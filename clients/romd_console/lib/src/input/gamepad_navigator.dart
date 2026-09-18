import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:gamepads/gamepads.dart';

import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';

import 'console_input_mode.dart';
import 'console_intents.dart';

enum GamepadNavigationAction {
  direction,
  analogDirection,
  activate,
  dismiss,
  details,
  controls,
  players,
  catalogSection,
}

typedef GamepadNavigationGate =
    bool Function(NormalizedGamepadEvent event, GamepadNavigationAction action);

/// Mutable shell-owned policy shared with the navigator. Keyboard and pointer
/// input bypass this gate by design.
final class ControllerNavigationGate {
  bool _enabled = true;
  bool _requireJoined = false;
  bool _ownershipAllowed = true;
  String? _joiningId;
  Set<String> _joinedIds = const <String>{};
  final Set<VoidCallback> _listeners = <VoidCallback>{};

  void addPolicyChangeListener(VoidCallback listener) =>
      _listeners.add(listener);

  void removePolicyChangeListener(VoidCallback listener) =>
      _listeners.remove(listener);

  void configure({
    required bool enabled,
    required bool requireJoined,
    required bool ownershipAllowed,
    required String? joiningControllerId,
    required Set<String> joinedControllerIds,
  }) {
    final normalizedIds = Set<String>.unmodifiable(joinedControllerIds);
    if (_enabled == enabled &&
        _requireJoined == requireJoined &&
        _ownershipAllowed == ownershipAllowed &&
        _joiningId == joiningControllerId &&
        setEquals(_joinedIds, normalizedIds)) {
      return;
    }
    _enabled = enabled;
    _requireJoined = requireJoined;
    _ownershipAllowed = ownershipAllowed;
    _joiningId = joiningControllerId;
    _joinedIds = normalizedIds;
    for (final listener in List<VoidCallback>.of(_listeners)) {
      listener();
    }
  }

  void reset() => configure(
    enabled: true,
    requireJoined: false,
    ownershipAllowed: true,
    joiningControllerId: null,
    joinedControllerIds: const <String>{},
  );

  bool allows(NormalizedGamepadEvent event, GamepadNavigationAction action) {
    if (!_enabled || !_ownershipAllowed) {
      return false;
    }
    final joining = _joiningId;
    if (joining == null) {
      if (!_requireJoined) {
        return true;
      }
      return _joinedIds.contains(event.gamepadId);
    }
    if (event.gamepadId != joining) {
      return false;
    }
    return action == GamepadNavigationAction.direction ||
        action == GamepadNavigationAction.activate ||
        action == GamepadNavigationAction.dismiss;
  }
}

/// Bridges a physical gamepad into the console's input abstraction.
///
/// Input is unified on Flutter's `Intent`/`Action` system: the keyboard maps to
/// [DirectionalFocusIntent] / [ActivateIntent] / [DismissIntent] by default (and
/// we bind Y to [ShowDetailsIntent]). This listens to normalized gamepad events
/// and dispatches the *same* intents to the focused element — so both inputs
/// flow through one path and every screen handles them identically.
///
/// Mapping (Xbox-normalized): d-pad + left stick → directional focus, A → activate,
/// B → dismiss/back, Y → details, X → controls, Start/Menu → Players.
/// Held directions auto-repeat.
final class GamepadNavigator {
  GamepadNavigator({
    required ValueChanged<ConsoleInputMode> onInputModeChanged,
    ControllerInputProvider controllerInputProvider =
        const GamepadsControllerInputProvider(),
    GamepadNavigationGate? navigationGate,
    ControllerNavigationGate? controllerNavigationGate,
  }) : _onInputModeChanged = onInputModeChanged,
       _controllerInputProvider = controllerInputProvider,
       _navigationGate = controllerNavigationGate?.allows ?? navigationGate,
       _controllerNavigationGate = controllerNavigationGate,
       _captureGate = controllerInputProvider is ControllerInputCaptureGate
           ? controllerInputProvider as ControllerInputCaptureGate
           : null {
    _controllerNavigationGate?.addPolicyChangeListener(_resetNavigationState);
    _captureGate?.addCaptureStateListener(_resetNavigationState);
    try {
      _subscription = _controllerInputProvider.events().listen(
        _onEvent,
        onError: (_) {},
      );
      _seedConnectedGamepad();
    } on Object {
      // No gamepad backend (e.g. unit tests / unsupported host) — the app stays
      // fully keyboard/mouse driven.
    }
  }

  final ValueChanged<ConsoleInputMode> _onInputModeChanged;
  final ControllerInputProvider _controllerInputProvider;
  final GamepadNavigationGate? _navigationGate;
  final ControllerNavigationGate? _controllerNavigationGate;
  final ControllerInputCaptureGate? _captureGate;
  StreamSubscription<NormalizedGamepadEvent>? _subscription;

  final Set<GamepadButton> _dpadHeld = <GamepadButton>{};
  double _stickX = 0;
  double _stickY = 0;
  bool _disposed = false;
  TraversalDirection? _activeDirection;
  Timer? _repeatTimer;
  NormalizedGamepadEvent? _directionSourceEvent;
  GamepadNavigationAction? _directionSourceAction;
  final Map<String, _ShoulderState> _shoulders = <String, _ShoulderState>{};

  static const double _engageThreshold = 0.55;
  static const Duration _initialRepeatDelay = Duration(milliseconds: 350);
  static const Duration _repeatInterval = Duration(milliseconds: 130);

  void dispose() {
    _disposed = true;
    _subscription?.cancel();
    _controllerNavigationGate?.removePolicyChangeListener(
      _resetNavigationState,
    );
    _captureGate?.removeCaptureStateListener(_resetNavigationState);
    _resetNavigationState();
  }

  void _markGamepadInput() {
    if (!_disposed) {
      _onInputModeChanged(ConsoleInputMode.gamepad);
    }
  }

  void _dispatch(Intent intent) {
    final context = FocusManager.instance.primaryFocus?.context;
    if (context != null) {
      Actions.maybeInvoke(context, intent);
    }
  }

  void _onEvent(NormalizedGamepadEvent event) {
    final button = event.button;
    if (button == GamepadButton.leftBumper ||
        button == GamepadButton.rightBumper) {
      // Shoulder releases must always reach the recognizer so capture/policy
      // changes cannot strand a pending press. Dispatch itself re-checks every
      // gate below.
      _onShoulder(event, button!, event.value >= 0.5);
      return;
    }
    if (_captureGate?.isNavigationSuppressed ?? false) {
      return;
    }
    final axis = event.axis;
    if (button != null) {
      _onButton(event, button, event.value >= 0.5);
    } else if (axis != null) {
      _onAxis(event, axis, event.value);
    }
  }

  void _onButton(
    NormalizedGamepadEvent event,
    GamepadButton button,
    bool pressed,
  ) {
    final action = switch (button) {
      GamepadButton.a => GamepadNavigationAction.activate,
      GamepadButton.b => GamepadNavigationAction.dismiss,
      GamepadButton.y => GamepadNavigationAction.details,
      GamepadButton.x => GamepadNavigationAction.controls,
      GamepadButton.start => GamepadNavigationAction.players,
      GamepadButton.dpadUp ||
      GamepadButton.dpadDown ||
      GamepadButton.dpadLeft ||
      GamepadButton.dpadRight => GamepadNavigationAction.direction,
      GamepadButton.leftBumper ||
      GamepadButton.rightBumper => GamepadNavigationAction.catalogSection,
      _ => null,
    };
    if (action == null || _navigationGate?.call(event, action) == false) {
      return;
    }
    _markGamepadInput();
    switch (button) {
      case GamepadButton.a:
        if (pressed) {
          _dispatch(const ActivateIntent());
        }
      case GamepadButton.b:
        if (pressed) {
          _dispatch(const DismissIntent());
        }
      case GamepadButton.y:
        if (pressed) {
          _dispatch(const ShowDetailsIntent());
        }
      case GamepadButton.x:
        if (pressed) {
          _dispatch(const ShowControlsIntent());
        }
      case GamepadButton.start:
        if (pressed) {
          _dispatch(const ShowPlayersIntent());
        }
      case GamepadButton.dpadUp:
      case GamepadButton.dpadDown:
      case GamepadButton.dpadLeft:
      case GamepadButton.dpadRight:
        if (pressed) {
          _dpadHeld.add(button);
          _directionSourceEvent = event;
          _directionSourceAction = GamepadNavigationAction.direction;
        } else {
          _dpadHeld.remove(button);
        }
        _recomputeDirection();
      case GamepadButton.leftBumper:
      case GamepadButton.rightBumper:
        // Handled before the capture gate in [_onEvent].
        break;
      // Triggers, back/home, stick clicks, and touchpad are intentionally
      // unmapped for now.
      case GamepadButton.leftTrigger:
      case GamepadButton.rightTrigger:
      case GamepadButton.back:
      case GamepadButton.home:
      case GamepadButton.leftStick:
      case GamepadButton.rightStick:
      case GamepadButton.touchpad:
        break;
    }
  }

  void _onShoulder(
    NormalizedGamepadEvent event,
    GamepadButton button,
    bool pressed,
  ) {
    final state = pressed
        ? _shoulders.putIfAbsent(event.gamepadId, _ShoulderState.new)
        : _shoulders[event.gamepadId];
    if (state == null) return;
    final isLeft = button == GamepadButton.leftBumper;
    final wasPressed = isLeft ? state.leftDown : state.rightDown;
    if (pressed) {
      if (wasPressed) return;
      final eligible =
          !(_captureGate?.isNavigationSuppressed ?? false) &&
          _navigationGate?.call(
                event,
                GamepadNavigationAction.catalogSection,
              ) !=
              false;
      if (isLeft) {
        state
          ..leftDown = true
          ..leftEligible = eligible;
      } else {
        state
          ..rightDown = true
          ..rightEligible = eligible;
      }
      if (state.leftDown && state.rightDown) {
        // The join chord dominates for as long as either bumper remains held.
        // There is deliberately no timer: an arbitrarily slow overlap still
        // cancels both single-bumper candidates.
        state
          ..chordLatched = true
          ..leftEligible = false
          ..rightEligible = false;
      }
      if (eligible) _markGamepadInput();
      return;
    }

    if (!wasPressed) return;
    final eligibleAtPress = isLeft ? state.leftEligible : state.rightEligible;
    if (isLeft) {
      state
        ..leftDown = false
        ..leftEligible = false;
    } else {
      state
        ..rightDown = false
        ..rightEligible = false;
    }
    if (state.chordLatched) {
      if (!state.leftDown && !state.rightDown) {
        _shoulders.remove(event.gamepadId);
      }
      return;
    }

    final allowedNow =
        !(_captureGate?.isNavigationSuppressed ?? false) &&
        _navigationGate?.call(event, GamepadNavigationAction.catalogSection) !=
            false;
    if (eligibleAtPress && allowedNow) {
      _markGamepadInput();
      _dispatch(
        isLeft
            ? const PreviousCatalogSectionIntent()
            : const NextCatalogSectionIntent(),
      );
    }
    if (!state.leftDown && !state.rightDown) {
      _shoulders.remove(event.gamepadId);
    }
  }

  void _onAxis(NormalizedGamepadEvent event, GamepadAxis axis, double value) {
    if (_navigationGate?.call(event, GamepadNavigationAction.analogDirection) ==
        false) {
      return;
    }
    _markGamepadInput();
    if (axis == GamepadAxis.leftStickX) {
      _stickX = value;
    } else if (axis == GamepadAxis.leftStickY) {
      _stickY = value;
    } else {
      return;
    }
    _directionSourceEvent = event;
    _directionSourceAction = GamepadNavigationAction.analogDirection;
    _recomputeDirection();
  }

  void _recomputeDirection() {
    final next = _resolveDirection();
    if (next == _activeDirection) {
      return;
    }
    _activeDirection = next;
    _repeatTimer?.cancel();
    if (next == null) {
      return;
    }
    _dispatch(DirectionalFocusIntent(next));
    _repeatTimer = Timer(_initialRepeatDelay, () {
      _repeatTimer = Timer.periodic(_repeatInterval, (_) {
        final direction = _activeDirection;
        final sourceEvent = _directionSourceEvent;
        final sourceAction = _directionSourceAction;
        if (direction != null &&
            sourceEvent != null &&
            sourceAction != null &&
            _navigationGate?.call(sourceEvent, sourceAction) != false) {
          _dispatch(DirectionalFocusIntent(direction));
        } else {
          _resetDirectionalState();
        }
      });
    });
  }

  void _resetDirectionalState() {
    _dpadHeld.clear();
    _stickX = 0;
    _stickY = 0;
    _activeDirection = null;
    _directionSourceEvent = null;
    _directionSourceAction = null;
    _repeatTimer?.cancel();
    _repeatTimer = null;
  }

  void _resetNavigationState() {
    _resetDirectionalState();
    _shoulders.clear();
  }

  TraversalDirection? _resolveDirection() {
    // D-pad takes precedence over the stick.
    if (_dpadHeld.contains(GamepadButton.dpadUp)) {
      return TraversalDirection.up;
    }
    if (_dpadHeld.contains(GamepadButton.dpadDown)) {
      return TraversalDirection.down;
    }
    if (_dpadHeld.contains(GamepadButton.dpadLeft)) {
      return TraversalDirection.left;
    }
    if (_dpadHeld.contains(GamepadButton.dpadRight)) {
      return TraversalDirection.right;
    }

    if (_stickX.abs() < _engageThreshold && _stickY.abs() < _engageThreshold) {
      return null;
    }
    if (_stickX.abs() >= _stickY.abs()) {
      return _stickX > 0 ? TraversalDirection.right : TraversalDirection.left;
    }
    // leftStickY is +1.0 at the top, -1.0 at the bottom.
    return _stickY > 0 ? TraversalDirection.up : TraversalDirection.down;
  }

  Future<void> _seedConnectedGamepad() async {
    try {
      final controllers = await _controllerInputProvider.listGamepads();
      if (controllers.isNotEmpty) {
        _markGamepadInput();
      }
    } on Object {
      // Presence detection is best-effort; the event stream still flips mode on
      // first input if listing is unsupported on the host.
    }
  }
}

final class _ShoulderState {
  bool leftDown = false;
  bool rightDown = false;
  bool leftEligible = false;
  bool rightEligible = false;
  bool chordLatched = false;
}
