import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:gamepads/gamepads.dart';

import '../data/local_profiles/local_profile_repository.dart';
import '../domain/local_profile.dart';
import '../input/claim_ceremony.dart';
import '../play/controllers/data/gamepad_devices.dart';
import '../play/controllers/domain/controller_assignments.dart';
import 'local_profile_selection_screen.dart';
import 'theme/console_theme_context.dart';
import 'widgets/ottercade_brand.dart';

/// The combined attract + profile-selection stage.
///
/// A single controller drives the Quiet Handoff: the Ottercade identity lifts
/// and clears before profile selection enters. Backing out (Esc) reverses the
/// same choreography without introducing a separate timed splash screen.
final class EntryStage extends StatefulWidget {
  const EntryStage({
    required this.repository,
    required this.defaultRomdServerOrigin,
    required this.onSelected,
    required this.onStartedChanged,
    this.controllerInputProvider = const GamepadsControllerInputProvider(),
    this.onControllerStarted,
    this.initialStarted = false,
    super.key,
  });

  final LocalProfileRepository repository;
  final Uri defaultRomdServerOrigin;
  final ValueChanged<LocalProfile> onSelected;

  /// Reports whether the user has begun (left the attract state). The root uses
  /// this to reopen the stage directly in the selection state when the user
  /// backs out of login.
  final ValueChanged<bool> onStartedChanged;
  final ControllerInputProvider controllerInputProvider;
  final ValueChanged<ControllerSlotClaim>? onControllerStarted;

  /// When true, the stage opens already in the selection state (no morph),
  /// used when returning from the login flow.
  final bool initialStarted;

  @override
  State<EntryStage> createState() => _EntryStageState();
}

final class _EntryStageState extends State<EntryStage>
    with TickerProviderStateMixin {
  // 0 = attract, 1 = profile selection.
  late final AnimationController _transition = AnimationController(
    vsync: this,
    value: widget.initialStarted ? 1 : 0,
  );

  late final AnimationController _pulse = AnimationController(vsync: this);

  // Focus is managed explicitly at every transition: only the active layer is
  // focus-eligible (see the ExcludeFocus gates in build), and the active layer
  // is asked for focus on begin, on back, and on initial mount.
  final FocusNode _attractFocus = FocusNode(debugLabel: 'attract');
  final FocusScopeNode _selectionScope = FocusScopeNode(
    debugLabel: 'selection',
  );
  ClaimCeremony _controllerStartCeremony = ClaimCeremony(maxSlots: 1);

  late bool _started = widget.initialStarted;
  List<LocalProfile>? _profiles;
  List<ConnectedGamepad> _pads = const <ConnectedGamepad>[];
  StreamSubscription<NormalizedGamepadEvent>? _controllerEvents;
  bool _controllerStartInFlight = false;
  String? _starterControllerName;

  @override
  void initState() {
    super.initState();
    _transition.addStatusListener(_handleTransitionStatus);
    if (widget.onControllerStarted != null) {
      try {
        _controllerEvents = widget.controllerInputProvider.events().listen(
          _onControllerEvent,
          onError: (_) {},
        );
      } on Object {
        // No gamepad backend — keyboard/mouse start remains available.
      }
    }
    widget.repository.listProfiles().then((profiles) {
      if (mounted) {
        setState(() => _profiles = profiles);
      }
    });
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) {
        return;
      }
      if (_started) {
        _selectionScope.requestFocus();
      } else {
        _attractFocus.requestFocus();
      }
    });
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final motion = context.motion;
    _transition.duration = motion.resolve(context, motion.entryTransition);
    final pulseDuration = motion.resolve(context, motion.pulse);
    if (pulseDuration == Duration.zero) {
      _pulse.stop();
      _pulse.value = 1;
    } else {
      _pulse.duration = pulseDuration;
      if (!_pulse.isAnimating) {
        _pulse.repeat(reverse: true);
      }
    }
  }

  @override
  void dispose() {
    _transition.dispose();
    _pulse.dispose();
    _controllerEvents?.cancel();
    _attractFocus.dispose();
    _selectionScope.dispose();
    super.dispose();
  }

  void _begin() {
    if (_started) {
      return;
    }
    setState(() => _started = true);
    _transition.forward();
    widget.onStartedChanged(true);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        _selectionScope.requestFocus();
      }
    });
  }

  void _back() {
    if (!_started) {
      return;
    }
    _resetControllerStartCeremony();
    _starterControllerName = null;
    setState(() => _started = false);
    _transition.reverse();
    widget.onStartedChanged(false);
    _attractFocus.requestFocus();
  }

  void _resetControllerStartCeremony() {
    _controllerStartInFlight = false;
    _controllerStartCeremony = ClaimCeremony(maxSlots: 1);
  }

  void _handleTransitionStatus(AnimationStatus status) {
    // Once fully back at attract, rebuild to unmount the selection layer and
    // make sure the attract layer holds focus for the next key press.
    if (status == AnimationStatus.dismissed && mounted) {
      setState(() {});
      _attractFocus.requestFocus();
    } else if (status == AnimationStatus.completed && mounted) {
      // The final animation-value notification precedes the completed status,
      // so rebuild once more to make the selection layer interactive.
      setState(() {});
      _selectionScope.requestFocus();
    }
  }

  Future<void> _refreshPads() async {
    try {
      final pads = await widget.controllerInputProvider.listGamepads();
      if (mounted) {
        _pads = pads;
      }
    } on Object {
      // Keep the last known list; a start chord can still begin the app.
    }
  }

  void _onControllerEvent(NormalizedGamepadEvent event) {
    if (_started || _controllerStartInFlight) {
      return;
    }
    if (!_controllerStartCeremony.onEvent(event)) {
      return;
    }
    _controllerStartInFlight = true;
    unawaited(_beginFromController(event.gamepadId));
  }

  Future<void> _beginFromController(String gamepadId) async {
    var pad = _padById(gamepadId);
    if (pad == null) {
      await _refreshPads();
      pad = _padById(gamepadId);
    }
    if (!mounted || _started) {
      return;
    }
    widget.onControllerStarted?.call(
      pad == null
          ? ControllerSlotClaim(
              displayName: 'Controller',
              providerId: gamepadId,
            )
          : ControllerSlotClaim.fromGamepad(pad),
    );
    _starterControllerName = pad?.name ?? 'Controller';
    _begin();
  }

  ConnectedGamepad? _padById(String gamepadId) {
    for (final pad in _pads) {
      if (pad.id == gamepadId) {
        return pad;
      }
    }
    return null;
  }

  String get _prompt {
    if (!(_profiles?.isNotEmpty ?? true)) {
      return 'Add a profile to get started';
    }
    final controllerName = _starterControllerName;
    return controllerName == null
        ? "Who's playing?"
        : 'Who’s using $controllerName?';
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final motion = context.motion;
    return Scaffold(
      // The root owns one continuous ambient field across attract and profile
      // selection. Native first paint still uses entryBackdrop as its safeguard.
      backgroundColor: Colors.transparent,
      body: SafeArea(
        child: AnimatedBuilder(
          animation: _transition,
          builder: (context, _) {
            final t = _transition.value;
            // Keep the selection layer mounted while the morph is in flight so it
            // can fade out on reverse, not just blink away at t == 0.
            final showSelection = _started || t > 0;

            final brandLiftProgress = motion.spatialCurve.transform(
              (t / motion.entryAttractFadeFraction).clamp(0.0, 1.0),
            );
            final brandOpacity =
                1 -
                motion.standardCurve.transform(
                  (t / motion.entryAttractFadeFraction).clamp(0.0, 1.0),
                );
            final selectionOpacity = motion.standardCurve.transform(
              ((t - motion.entrySelectionDelayFraction) /
                      (1 - motion.entrySelectionDelayFraction))
                  .clamp(0.0, 1.0),
            );
            final promptOpacity =
                1 -
                motion.standardCurve.transform(
                  (t / motion.entryPromptDelayFraction).clamp(0.0, 1.0),
                );
            final selectionInteractive = _started && _transition.isCompleted;

            return Stack(
              children: <Widget>[
                if (showSelection)
                  Positioned.fill(
                    child: IgnorePointer(
                      ignoring: !selectionInteractive,
                      child: Opacity(
                        opacity: selectionOpacity,
                        child: Transform.translate(
                          offset: Offset(
                            0,
                            layout.entrySelectionTranslate *
                                (1 - selectionOpacity),
                          ),
                          child: FocusScope(
                            node: _selectionScope,
                            child: ExcludeFocus(
                              excluding: !_started,
                              child: LocalProfileSelectionScreen(
                                repository: widget.repository,
                                defaultRomdServerOrigin:
                                    widget.defaultRomdServerOrigin,
                                showLockup: false,
                                showClock: false,
                                animateIntro: false,
                                onSelected: widget.onSelected,
                                onBack: _back,
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                if (showSelection)
                  IgnorePointer(
                    child: Center(
                      child: Opacity(
                        opacity: selectionOpacity,
                        child: Transform.translate(
                          key: const ValueKey<String>(
                            'entry-profile-prompt-transform',
                          ),
                          offset: Offset(
                            0,
                            -layout.profileSelectionLockupRise -
                                layout.md * (1 - selectionOpacity),
                          ),
                          child: _ProfilePrompt(
                            prompt: _prompt,
                            supportingPrompt: _starterControllerName == null
                                ? null
                                : 'This controller will be Player 1.',
                          ),
                        ),
                      ),
                    ),
                  ),
                Positioned.fill(
                  child: IgnorePointer(
                    ignoring: _started,
                    child: ExcludeFocus(
                      excluding: _started,
                      child: _AttractChrome(
                        focusNode: _attractFocus,
                        pulse: _pulse,
                        brandOpacity: brandOpacity,
                        promptOpacity: promptOpacity,
                        brandLiftProgress: brandLiftProgress,
                        onBegin: _begin,
                      ),
                    ),
                  ),
                ),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// The full-screen attract target with the approved stacked lockup and prompt.
final class _AttractChrome extends StatelessWidget {
  const _AttractChrome({
    required this.focusNode,
    required this.pulse,
    required this.brandOpacity,
    required this.promptOpacity,
    required this.brandLiftProgress,
    required this.onBegin,
  });

  final FocusNode focusNode;
  final Animation<double> pulse;
  final double brandOpacity;
  final double promptOpacity;
  final double brandLiftProgress;
  final VoidCallback onBegin;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final motion = context.motion;
    return Focus(
      focusNode: focusNode,
      autofocus: true,
      onKeyEvent: (_, event) {
        if (event is KeyDownEvent && _isKeyboardStartKey(event.logicalKey)) {
          onBegin();
          return KeyEventResult.handled;
        }
        return KeyEventResult.ignored;
      },
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: onBegin,
        child: Stack(
          children: <Widget>[
            Center(
              child: Opacity(
                opacity: brandOpacity,
                child: Transform.translate(
                  key: const ValueKey<String>('entry-brand-transform'),
                  offset: Offset(
                    0,
                    layout.entryLockupOffset -
                        layout.entryBrandLift * brandLiftProgress,
                  ),
                  child: OttercadeStackedLockup(
                    rompSize: layout.entryRompSize,
                    wordmarkWidth: layout.entryWordmarkWidth,
                    spacing: layout.entryLockupSpacing,
                  ),
                ),
              ),
            ),
            Center(
              child: Transform.translate(
                offset: Offset(0, layout.entryPromptOffset),
                child: Opacity(
                  opacity: promptOpacity,
                  child: FadeTransition(
                    opacity: pulse.drive(
                      Tween<double>(
                        begin: motion.entryPulseMinimumOpacity,
                        end: 1,
                      ),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: <Widget>[
                        Container(
                          width: layout.xs,
                          height: layout.xs,
                          decoration: BoxDecoration(
                            color: context.artwork.entryPromptAccent,
                            shape: BoxShape.circle,
                          ),
                        ),
                        SizedBox(width: layout.sm),
                        Text(
                          'Press L + R to Start',
                          key: const ValueKey<String>('entry-start-prompt'),
                          style: context.artwork.entryStartPromptStyle,
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  bool _isKeyboardStartKey(LogicalKeyboardKey key) =>
      !_isGamepadKey(key) &&
      key != LogicalKeyboardKey.arrowUp &&
      key != LogicalKeyboardKey.arrowDown &&
      key != LogicalKeyboardKey.arrowLeft &&
      key != LogicalKeyboardKey.arrowRight;

  bool _isGamepadKey(LogicalKeyboardKey key) =>
      key.keyId >= LogicalKeyboardKey.gameButton1.keyId &&
      key.keyId <= LogicalKeyboardKey.gameButtonZ.keyId;
}

final class _ProfilePrompt extends StatelessWidget {
  const _ProfilePrompt({required this.prompt, required this.supportingPrompt});

  final String prompt;
  final String? supportingPrompt;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        Text(
          prompt,
          key: const ValueKey<String>('entry-profile-prompt'),
          style: context.text.action.copyWith(
            color: context.consoleColors.textStrong,
          ),
        ),
        if (supportingPrompt case final supporting?) ...<Widget>[
          SizedBox(height: layout.entrySupportingGap),
          Text(
            supporting,
            style: context.text.metadata.copyWith(
              color: context.consoleColors.textFaint,
            ),
          ),
        ],
      ],
    );
  }
}
