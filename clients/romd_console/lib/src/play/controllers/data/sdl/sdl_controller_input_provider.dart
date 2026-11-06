import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:gamepads/gamepads.dart';

import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_gamepad_enumerator.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_native_dependency.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';

final class SdlControllerInputProvider
    implements
        ControllerInputProvider,
        RawControllerInputProvider,
        ControllerInputCaptureGate {
  SdlControllerInputProvider({
    required SdlGamepadApi api,
    Duration pollInterval = const Duration(milliseconds: 16),
  }) : _api = api,
       _subsystem = SdlGamepadSubsystem(api) {
    _pump = SdlControllerEventPump(
      api: api,
      subsystem: _subsystem,
      pollInterval: pollInterval,
    );
  }

  final SdlGamepadApi _api;
  final SdlGamepadSubsystem _subsystem;
  late final SdlControllerEventPump _pump;
  final Set<VoidCallback> _captureListeners = <VoidCallback>{};
  Object? _captureOwner;

  @override
  Future<List<ConnectedGamepad>> listGamepads() =>
      SdlGamepadEnumerator(_api, subsystem: _subsystem).listGamepads();

  @override
  Stream<NormalizedGamepadEvent> events() => _pump.normalizedEvents();

  @override
  Future<List<RawControllerDescriptor>> listRawControllers() {
    final api = _api;
    if (api is! SdlRawInputApi) {
      throw const SdlGamepadException('Raw SDL joystick input is unavailable.');
    }
    return SdlRawControllerEnumerator(
      api,
      subsystem: _subsystem,
    ).listControllers();
  }

  @override
  Stream<RawControllerEvent> rawEvents() => _pump.rawEvents();

  @override
  bool get isNavigationSuppressed => _captureOwner != null;

  @override
  void addCaptureStateListener(VoidCallback listener) =>
      _captureListeners.add(listener);

  @override
  void removeCaptureStateListener(VoidCallback listener) =>
      _captureListeners.remove(listener);

  @override
  Future<ControllerCaptureResult> acquireCapture(String controllerId) async {
    if (_api is! SdlRawInputApi) {
      return const ControllerCaptureUnsupported();
    }
    if (_captureOwner != null) {
      return const ControllerCaptureBusy();
    }
    final owner = Object();
    _captureOwner = owner;
    _notifyCaptureChanged();
    return ControllerCaptureStarted(
      _SdlControllerInputCaptureLease(
        controllerId: controllerId,
        events: rawEvents().where(
          (event) => event.controllerId == controllerId,
        ),
        onDispose: () => _releaseCapture(owner),
      ),
    );
  }

  void _releaseCapture(Object owner) {
    if (!identical(_captureOwner, owner)) {
      return;
    }
    _captureOwner = null;
    _notifyCaptureChanged();
  }

  void _notifyCaptureChanged() {
    for (final listener in List<VoidCallback>.of(_captureListeners)) {
      listener();
    }
  }
}

final class _SdlControllerInputCaptureLease
    implements ControllerInputCaptureLease {
  _SdlControllerInputCaptureLease({
    required this.controllerId,
    required this.events,
    required VoidCallback onDispose,
  }) : _onDispose = onDispose;

  @override
  final String controllerId;
  @override
  final Stream<RawControllerEvent> events;
  VoidCallback? _onDispose;

  @override
  void dispose() {
    final callback = _onDispose;
    _onDispose = null;
    callback?.call();
  }
}

final class SdlControllerEventPump {
  SdlControllerEventPump({
    required SdlGamepadApi api,
    SdlGamepadSubsystem? subsystem,
    this.pollInterval = const Duration(milliseconds: 16),
  }) : _api = api,
       _subsystem = subsystem ?? SdlGamepadSubsystem(api);

  final SdlGamepadApi _api;
  final SdlGamepadSubsystem _subsystem;
  final Duration pollInterval;

  Stream<NormalizedGamepadEvent>? _normalizedStream;
  Stream<RawControllerEvent>? _rawStream;

  Stream<NormalizedGamepadEvent> normalizedEvents() {
    _ensureStreams();
    return _normalizedStream!;
  }

  Stream<RawControllerEvent> rawEvents() {
    _ensureStreams();
    return _rawStream!;
  }

  void _ensureStreams() {
    if (_normalizedStream != null) {
      return;
    }
    Timer? timer;
    var active = false;
    final openGamepads = <int, SdlGamepadHandle>{};
    final openJoysticks = <int, SdlJoystickHandle>{};

    late final StreamController<NormalizedGamepadEvent> normalizedController;
    late final StreamController<RawControllerEvent> rawController;

    void closeGamepad(int instanceId) {
      final handle = openGamepads.remove(instanceId);
      if (handle != null) {
        _api.closeGamepad(handle);
      }
    }

    void openGamepad(int instanceId) {
      if (openGamepads.containsKey(instanceId)) {
        return;
      }
      final handle = _api.openGamepad(instanceId);
      if (handle != null) {
        openGamepads[instanceId] = handle;
      }
    }

    void openConnectedGamepads() {
      for (final instanceId in _api.gamepadInstanceIds()) {
        openGamepad(instanceId);
      }
    }

    void closeJoystick(int instanceId) {
      final handle = openJoysticks.remove(instanceId);
      final api = _api;
      if (handle != null && api is SdlRawInputApi) {
        api.closeJoystick(handle);
      }
    }

    void openJoystick(int instanceId) {
      final api = _api;
      if (api is! SdlRawInputApi || openJoysticks.containsKey(instanceId)) {
        return;
      }
      final handle = api.openJoystick(instanceId);
      if (handle != null) {
        openJoysticks[instanceId] = handle;
      }
    }

    void openConnectedJoysticks() {
      final api = _api;
      if (api is SdlRawInputApi) {
        for (final instanceId in api.joystickInstanceIds()) {
          openJoystick(instanceId);
        }
      }
    }

    void onDeviceEvent(SdlGamepadDeviceInputEvent event) {
      switch (event.change) {
        case SdlGamepadDeviceChange.added:
          openGamepad(event.instanceId);
        case SdlGamepadDeviceChange.removed:
          closeGamepad(event.instanceId);
      }
    }

    void stop() {
      timer?.cancel();
      timer = null;
      for (final instanceId in List<int>.of(openGamepads.keys)) {
        closeGamepad(instanceId);
      }
      for (final instanceId in List<int>.of(openJoysticks.keys)) {
        closeJoystick(instanceId);
      }
      if (active) {
        active = false;
        _subsystem.release();
      }
    }

    void poll() {
      try {
        while (true) {
          final event = _api.pollEvent();
          if (event == null) {
            break;
          }
          switch (event) {
            case SdlGamepadButtonInputEvent():
              final normalized = _normalizeButton(event);
              if (normalized != null) {
                normalizedController.add(normalized);
              }
            case SdlGamepadDeviceInputEvent():
              onDeviceEvent(event);
            case SdlJoystickButtonInputEvent():
              rawController.add(
                RawControllerButtonEvent(
                  controllerId: event.instanceId.toString(),
                  timestampMillis: event.timestampMillis,
                  button: event.button,
                  pressed: event.pressed,
                ),
              );
            case SdlJoystickAxisInputEvent():
              rawController.add(
                RawControllerAxisEvent(
                  controllerId: event.instanceId.toString(),
                  timestampMillis: event.timestampMillis,
                  axis: event.axis,
                  value: event.value,
                ),
              );
            case SdlJoystickHatInputEvent():
              rawController.add(
                RawControllerHatEvent(
                  controllerId: event.instanceId.toString(),
                  timestampMillis: event.timestampMillis,
                  hat: event.hat,
                  value: event.value,
                ),
              );
            case SdlJoystickDeviceInputEvent():
              if (event.change == SdlJoystickDeviceChange.added) {
                openJoystick(event.instanceId);
              } else {
                closeJoystick(event.instanceId);
              }
              rawController.add(
                RawControllerDeviceEvent(
                  controllerId: event.instanceId.toString(),
                  timestampMillis: event.timestampMillis,
                  change: event.change == SdlJoystickDeviceChange.added
                      ? RawControllerDeviceChange.added
                      : RawControllerDeviceChange.removed,
                ),
              );
            case SdlIgnoredEvent():
              break;
          }
        }
      } on Object catch (error, stackTrace) {
        normalizedController.addError(error, stackTrace);
        rawController.addError(error, stackTrace);
        stop();
      }
    }

    void start() {
      if (active) {
        return;
      }
      if (!_subsystem.acquire()) {
        const error = SdlGamepadException(
          'Could not initialize SDL gamepad events.',
        );
        normalizedController.addError(error);
        rawController.addError(error);
        return;
      }
      active = true;
      _api.setGamepadEventsEnabled(true);
      final api = _api;
      if (api is SdlRawInputApi) {
        api.setJoystickEventsEnabled(true);
      }
      openConnectedGamepads();
      openConnectedJoysticks();
      poll();
      timer = Timer.periodic(pollInterval, (_) => poll());
    }

    void stopWhenUnused() {
      if (!normalizedController.hasListener && !rawController.hasListener) {
        stop();
      }
    }

    normalizedController = StreamController<NormalizedGamepadEvent>.broadcast(
      onListen: start,
      onCancel: stopWhenUnused,
    );
    rawController = StreamController<RawControllerEvent>.broadcast(
      onListen: start,
      onCancel: stopWhenUnused,
    );
    _normalizedStream = normalizedController.stream;
    _rawStream = rawController.stream;
  }

  List<NormalizedGamepadEvent> pollPendingEvents({
    void Function(SdlGamepadDeviceInputEvent event)? onDeviceEvent,
  }) {
    final events = <NormalizedGamepadEvent>[];
    while (true) {
      final event = _api.pollEvent();
      if (event == null) {
        break;
      }
      switch (event) {
        case SdlGamepadButtonInputEvent():
          final normalized = _normalizeButton(event);
          if (normalized != null) {
            events.add(normalized);
          }
        case SdlGamepadDeviceInputEvent():
          onDeviceEvent?.call(event);
        case SdlJoystickAxisInputEvent() ||
            SdlJoystickButtonInputEvent() ||
            SdlJoystickHatInputEvent() ||
            SdlJoystickDeviceInputEvent():
          break;
        case SdlIgnoredEvent():
          break;
      }
    }
    return events;
  }

  NormalizedGamepadEvent? _normalizeButton(SdlGamepadButtonInputEvent event) {
    final button = _buttonFromSdl(event.button);
    if (button == null) {
      return null;
    }
    final value = event.pressed ? 1.0 : 0.0;
    final gamepadId = event.instanceId.toString();
    return NormalizedGamepadEvent(
      gamepadId: gamepadId,
      timestamp: event.timestampMillis,
      value: value,
      button: button,
      rawEvent: GamepadEvent(
        gamepadId: gamepadId,
        timestamp: event.timestampMillis,
        type: KeyType.button,
        key: 'sdl:${event.button}',
        value: value,
      ),
    );
  }

  GamepadButton? _buttonFromSdl(int button) => switch (button) {
    SdlGamepadButtonIds.south => GamepadButton.a,
    SdlGamepadButtonIds.east => GamepadButton.b,
    SdlGamepadButtonIds.west => GamepadButton.x,
    SdlGamepadButtonIds.north => GamepadButton.y,
    SdlGamepadButtonIds.back => GamepadButton.back,
    SdlGamepadButtonIds.guide => GamepadButton.home,
    SdlGamepadButtonIds.start => GamepadButton.start,
    SdlGamepadButtonIds.leftStick => GamepadButton.leftStick,
    SdlGamepadButtonIds.rightStick => GamepadButton.rightStick,
    SdlGamepadButtonIds.leftShoulder => GamepadButton.leftBumper,
    SdlGamepadButtonIds.rightShoulder => GamepadButton.rightBumper,
    SdlGamepadButtonIds.dpadUp => GamepadButton.dpadUp,
    SdlGamepadButtonIds.dpadDown => GamepadButton.dpadDown,
    SdlGamepadButtonIds.dpadLeft => GamepadButton.dpadLeft,
    SdlGamepadButtonIds.dpadRight => GamepadButton.dpadRight,
    SdlGamepadButtonIds.touchpad => GamepadButton.touchpad,
    _ => null,
  };
}

/// Compatibility facade for focused normalization tests. Production providers
/// own one [SdlControllerEventPump] and never construct competing pollers.
final class SdlGamepadEventPoller {
  SdlGamepadEventPoller({
    required SdlGamepadApi api,
    SdlGamepadSubsystem? subsystem,
    Duration pollInterval = const Duration(milliseconds: 16),
  }) : _pump = SdlControllerEventPump(
         api: api,
         subsystem: subsystem,
         pollInterval: pollInterval,
       );

  final SdlControllerEventPump _pump;

  Stream<NormalizedGamepadEvent> events() => _pump.normalizedEvents();

  List<NormalizedGamepadEvent> pollPendingEvents({
    void Function(SdlGamepadDeviceInputEvent event)? onDeviceEvent,
  }) => _pump.pollPendingEvents(onDeviceEvent: onDeviceEvent);
}

typedef SdlControllerProviderFactory =
    ControllerInputProvider Function(SdlGamepadApi api, Duration pollInterval);

final class ProvisionedSdlControllerInputProvider
    implements
        ControllerInputProvider,
        RawControllerInputProvider,
        ControllerInputCaptureGate {
  ProvisionedSdlControllerInputProvider({
    required Sdl3Provision ensureSdl3,
    required SdlGamepadApiFactory apiFactory,
    required ControllerInputProvider fallback,
    Duration pollInterval = const Duration(milliseconds: 16),
    SdlControllerProviderFactory? sdlProviderFactory,
  }) : _ensureSdl3 = ensureSdl3,
       _apiFactory = apiFactory,
       _fallback = fallback,
       _pollInterval = pollInterval,
       _sdlProviderFactory = sdlProviderFactory;

  final Sdl3Provision _ensureSdl3;
  final SdlGamepadApiFactory _apiFactory;
  final ControllerInputProvider _fallback;
  final Duration _pollInterval;
  final SdlControllerProviderFactory? _sdlProviderFactory;
  Future<SdlNativeProvisionResult>? _sdl3Provision;
  Future<ControllerInputProvider>? _provider;
  Stream<NormalizedGamepadEvent>? _events;
  Future<void>? _fallbackTransition;
  Future<void> Function()? _switchEventsToFallback;
  ControllerInputCaptureGate? _captureGate;
  final Set<VoidCallback> _captureListeners = <VoidCallback>{};

  @override
  Future<List<ConnectedGamepad>> listGamepads() async {
    final activeTransition = _fallbackTransition;
    if (activeTransition != null) {
      await activeTransition;
      return _fallback.listGamepads();
    }

    late final List<ConnectedGamepad> sdlGamepads;
    try {
      sdlGamepads = await (await _openProvider()).listGamepads();
    } on Object {
      await _activateFallback();
      return await _fallback.listGamepads();
    }

    final transitionAfterListing = _fallbackTransition;
    if (transitionAfterListing != null) {
      await transitionAfterListing;
      return _fallback.listGamepads();
    }
    return sdlGamepads;
  }

  @override
  Stream<NormalizedGamepadEvent> events() => _events ??= _createEventStream();

  @override
  Future<List<RawControllerDescriptor>> listRawControllers() async {
    try {
      final provider = await _openProvider();
      if (provider case final RawControllerInputProvider rawProvider) {
        return await rawProvider.listRawControllers();
      }
    } on Object {
      // Raw attribution has no safe plugin fallback.
    }
    throw const SdlGamepadException('Raw controller input is unavailable.');
  }

  @override
  Stream<RawControllerEvent> rawEvents() {
    late final StreamController<RawControllerEvent> controller;
    StreamSubscription<RawControllerEvent>? subscription;
    controller = StreamController<RawControllerEvent>.broadcast(
      onListen: () async {
        try {
          final provider = await _openProvider();
          if (provider case final RawControllerInputProvider rawProvider) {
            subscription = rawProvider.rawEvents().listen(
              controller.add,
              onError: controller.addError,
            );
            return;
          }
          controller.addError(
            const SdlGamepadException('Raw controller input is unavailable.'),
          );
        } on Object catch (error, stackTrace) {
          controller.addError(error, stackTrace);
        }
      },
      onCancel: () async {
        if (!controller.hasListener) {
          await subscription?.cancel();
          subscription = null;
        }
      },
    );
    return controller.stream;
  }

  @override
  Future<ControllerCaptureResult> acquireCapture(String controllerId) async {
    try {
      final provider = await _openProvider();
      if (provider case final RawControllerInputProvider rawProvider) {
        return await rawProvider.acquireCapture(controllerId);
      }
    } on Object {
      // The fallback cannot attribute raw controls to the selected device.
    }
    return const ControllerCaptureUnsupported();
  }

  @override
  bool get isNavigationSuppressed =>
      _captureGate?.isNavigationSuppressed ?? false;

  @override
  void addCaptureStateListener(VoidCallback listener) =>
      _captureListeners.add(listener);

  @override
  void removeCaptureStateListener(VoidCallback listener) =>
      _captureListeners.remove(listener);

  void _observeCaptureGate(ControllerInputProvider provider) {
    if (provider case final ControllerInputCaptureGate gate) {
      if (identical(_captureGate, gate)) {
        return;
      }
      _captureGate?.removeCaptureStateListener(_notifyCaptureChanged);
      _captureGate = gate;
      gate.addCaptureStateListener(_notifyCaptureChanged);
    }
  }

  void _notifyCaptureChanged() {
    for (final listener in List<VoidCallback>.of(_captureListeners)) {
      listener();
    }
  }

  Stream<NormalizedGamepadEvent> _createEventStream() {
    StreamSubscription<NormalizedGamepadEvent>? subscription;
    Future<void>? subscriptionCancellation;
    late final StreamController<NormalizedGamepadEvent> controller;

    Future<void> cancelSubscription() {
      final activeCancellation = subscriptionCancellation;
      if (activeCancellation != null) {
        return activeCancellation;
      }
      final activeSubscription = subscription;
      subscription = null;
      if (activeSubscription == null) {
        return Future<void>.value();
      }
      final cancellation = activeSubscription.cancel();
      subscriptionCancellation = cancellation;
      return cancellation.whenComplete(() {
        if (identical(subscriptionCancellation, cancellation)) {
          subscriptionCancellation = null;
        }
      });
    }

    Future<void> startFallback() async {
      await cancelSubscription();
      if (!controller.hasListener) {
        return;
      }
      subscription = _fallback.events().listen(
        controller.add,
        onError: controller.addError,
      );
    }

    _switchEventsToFallback = startFallback;

    Future<void> start() async {
      if (subscription != null) {
        return;
      }
      final activeTransition = _fallbackTransition;
      if (activeTransition != null) {
        await activeTransition;
        if (subscription == null) {
          await startFallback();
        }
        return;
      }
      try {
        final provider = await _openProvider();
        final transitionAfterOpen = _fallbackTransition;
        if (transitionAfterOpen != null) {
          await transitionAfterOpen;
          if (subscription == null) {
            await startFallback();
          }
          return;
        }
        if (!controller.hasListener) {
          return;
        }
        subscription = provider.events().listen(
          controller.add,
          onError: (Object error, StackTrace stackTrace) {
            unawaited(_activateFallback());
          },
        );
      } on Object {
        await _activateFallback();
      }
    }

    controller = StreamController<NormalizedGamepadEvent>.broadcast(
      onListen: () => unawaited(start()),
      onCancel: () {
        if (!controller.hasListener) {
          unawaited(cancelSubscription());
        }
      },
    );
    return controller.stream;
  }

  Future<void> _activateFallback() {
    final activeTransition = _fallbackTransition;
    if (activeTransition != null) {
      return activeTransition;
    }

    final completer = Completer<void>();
    _fallbackTransition = completer.future;
    Future.sync(() async {
      await _switchEventsToFallback?.call();
    }).then(completer.complete, onError: completer.completeError);
    return completer.future;
  }

  Future<ControllerInputProvider> _openProvider() =>
      _provider ??= _createProvider();

  Future<ControllerInputProvider> _createProvider() async {
    final result = await (_sdl3Provision ??= _ensureSdl3());
    if (result case SdlNativeReady(:final libraryPath)) {
      final api = _apiFactory(libraryPath);
      final providerFactory = _sdlProviderFactory;
      if (providerFactory != null) {
        final provider = providerFactory(api, _pollInterval);
        _observeCaptureGate(provider);
        return provider;
      }
      final provider = SdlControllerInputProvider(
        api: api,
        pollInterval: _pollInterval,
      );
      _observeCaptureGate(provider);
      return provider;
    }
    throw const SdlGamepadException('SDL gamepad events are unavailable.');
  }
}
