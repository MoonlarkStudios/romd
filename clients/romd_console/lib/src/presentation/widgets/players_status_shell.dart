import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/data/local_profiles/local_profile_repository.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/input/console_intents.dart';
import 'package:romd_console/src/input/gamepad_navigator.dart';
import 'package:romd_console/src/input/session_join_tracker.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';
import 'package:romd_console/src/play/controllers/domain/players_projection.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';

import '../theme/console_theme_context.dart';
import 'change_order_screen.dart';
import 'controller_display_names.dart';
import 'guest_profile_assignment_screen.dart';
import 'players_panel.dart';

/// Owns one canonical, read-only player projection for every shell consumer.
///
/// Claim mutations refresh immediately; device presence is polled because the
/// controller provider intentionally exposes input events, not a device-change
/// stream. Resolving this state never reads or writes mapping/profile storage.
final class PlayersProjectionController extends ChangeNotifier {
  PlayersProjectionController({
    required SessionControllerSlotClaims slotClaims,
    required GamepadLister gamepadLister,
    this.pollInterval = const Duration(seconds: 2),
  }) : _slotClaims = slotClaims,
       _gamepadLister = gamepadLister {
    _claimSubscription = _slotClaims.changes.listen((_) => refresh());
    _pollTimer = Timer.periodic(pollInterval, (_) => refresh());
    refresh();
  }

  final SessionControllerSlotClaims _slotClaims;
  final GamepadLister _gamepadLister;
  final Duration pollInterval;
  late final StreamSubscription<int> _claimSubscription;
  late final Timer _pollTimer;
  PlayersProjection _projection = PlayersProjection.resolve(
    claims: const <ControllerSlotClaim?>[],
    devices: const <ConnectedGamepad>[],
  );
  bool _available = true;
  Future<void>? _activeRefresh;
  bool _refreshAgain = false;
  bool _disposed = false;
  ReviewedLaunchSnapshot? _snapshot;

  PlayersProjection get projection => _projection;
  bool get available => _available;
  ReviewedLaunchSnapshot? get snapshot => _snapshot;

  PlayersLaunchDisposition get launchDisposition {
    if (!_available) {
      return PlayersLaunchDisposition.setupRequired;
    }
    var connected = 0;
    for (final slot in _projection.slots) {
      switch (slot.state) {
        case PlayersSlotState.reservedExact ||
            PlayersSlotState.blockedAmbiguous:
          return PlayersLaunchDisposition.setupRequired;
        case PlayersSlotState.connected:
          connected++;
        case PlayersSlotState.open:
          if (slot.claim != null) {
            return PlayersLaunchDisposition.setupRequired;
          }
      }
    }
    return connected > 1 || _projection.availableControllers.isNotEmpty
        ? PlayersLaunchDisposition.review
        : PlayersLaunchDisposition.direct;
  }

  Future<void> refresh() async {
    final active = _activeRefresh;
    if (active != null) {
      _refreshAgain = true;
      return active;
    }
    late final Future<void> operation;
    operation = _refreshLoop().whenComplete(() {
      if (identical(_activeRefresh, operation)) {
        _activeRefresh = null;
      }
    });
    _activeRefresh = operation;
    return operation;
  }

  Future<void> _refreshLoop() async {
    do {
      _refreshAgain = false;
      try {
        final claimRevision = _slotClaims.revision;
        final claims = _slotClaims.claimDetails;
        final devices = await _gamepadLister();
        if (_disposed) {
          return;
        }
        if (_slotClaims.revision != claimRevision) {
          _refreshAgain = true;
          continue;
        }
        _snapshot = ReviewedLaunchSnapshot.resolve(
          claimRevision: claimRevision,
          claims: claims,
          devices: devices,
        );
        _projection = PlayersProjection.resolve(
          claims: claims,
          devices: devices,
        );
        _available = true;
      } on Object {
        if (_disposed) {
          return;
        }
        // An unavailable inventory cannot safely retain previously connected
        // devices in an approved launch roster. Project the same empty-device
        // fallback that the reviewed snapshot will carry.
        final claims = _slotClaims.claimDetails;
        _snapshot = ReviewedLaunchSnapshot.resolve(
          claimRevision: _slotClaims.revision,
          claims: claims,
          devices: const <ConnectedGamepad>[],
          inventoryAvailable: false,
        );
        _projection = PlayersProjection.resolve(
          claims: claims,
          devices: const <ConnectedGamepad>[],
        );
        _available = false;
      }
      notifyListeners();
    } while (_refreshAgain);
  }

  @override
  void dispose() {
    _disposed = true;
    _pollTimer.cancel();
    _claimSubscription.cancel();
    super.dispose();
  }
}

enum PlayersLaunchDisposition { direct, review, setupRequired }

enum PlayersPanelDisposition { approved, dismissed }

final class PlayersPanelResult {
  const PlayersPanelResult._({required this.disposition, this.snapshot});

  const PlayersPanelResult.dismissed()
    : this._(disposition: PlayersPanelDisposition.dismissed);

  const PlayersPanelResult.approved(ReviewedLaunchSnapshot snapshot)
    : this._(disposition: PlayersPanelDisposition.approved, snapshot: snapshot);

  final PlayersPanelDisposition disposition;
  final ReviewedLaunchSnapshot? snapshot;
}

/// Makes the live session-only Players state available to Play surfaces.
final class PlayersStatusScope extends InheritedWidget {
  const PlayersStatusScope({
    required this.controller,
    required this.openPlayers,
    required super.child,
    super.key,
  });

  final PlayersProjectionController controller;
  final Future<PlayersPanelResult> Function({
    bool forLaunch,
    VoidCallback? onSwitchProfile,
    LocalProfile? activeProfile,
  })
  openPlayers;

  static PlayersStatusScope? maybeOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<PlayersStatusScope>();

  @override
  bool updateShouldNotify(PlayersStatusScope oldWidget) =>
      !identical(controller, oldWidget.controller);
}

/// Persistent shell chrome mounted above the app Navigator.
final class PlayersStatusShell extends StatefulWidget {
  const PlayersStatusShell({
    required this.child,
    required this.navigatorKey,
    required this.controllerInputProvider,
    this.slotClaims,
    this.gamepadLister,
    this.activeProfile,
    this.localProfileRepository,
    this.onSwitchProfile,
    this.activeLaunchSession,
    this.navigationGate,
    this.visible = true,
    this.pollInterval = const Duration(seconds: 2),
    super.key,
  });

  final Widget child;
  final GlobalKey<NavigatorState> navigatorKey;
  final ControllerInputProvider controllerInputProvider;
  final SessionControllerSlotClaims? slotClaims;
  final GamepadLister? gamepadLister;
  final LocalProfile? activeProfile;
  final LocalProfileRepository? localProfileRepository;
  final VoidCallback? onSwitchProfile;
  final ActiveLaunchSession? activeLaunchSession;
  final ControllerNavigationGate? navigationGate;
  final bool visible;
  final Duration pollInterval;

  @override
  State<PlayersStatusShell> createState() => _PlayersStatusShellState();
}

final class _PlayersStatusShellState extends State<PlayersStatusShell> {
  PlayersProjectionController? _controller;
  bool _playersRouteOpen = false;
  final SessionJoinTracker _joinTracker = SessionJoinTracker();
  final List<String> _joinQueue = <String>[];
  StreamSubscription<NormalizedGamepadEvent>? _joinEvents;
  StreamSubscription<bool>? _launchSubscription;
  String? _joiningControllerId;
  bool _processingJoin = false;

  @override
  void initState() {
    super.initState();
    _replaceController();
    _listenForJoins();
    _listenForLaunchOwnership();
  }

  @override
  void didUpdateWidget(covariant PlayersStatusShell oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(oldWidget.slotClaims, widget.slotClaims) ||
        oldWidget.gamepadLister != widget.gamepadLister ||
        oldWidget.pollInterval != widget.pollInterval) {
      _replaceController();
    }
    if (!identical(
      oldWidget.controllerInputProvider,
      widget.controllerInputProvider,
    )) {
      _listenForJoins();
    }
    if (!identical(oldWidget.activeLaunchSession, widget.activeLaunchSession)) {
      _listenForLaunchOwnership();
    }
    _syncNavigationGate();
  }

  void _replaceController() {
    _controller?.removeListener(_handleProjectionChange);
    _controller?.dispose();
    final claims = widget.slotClaims;
    final lister = widget.gamepadLister;
    _controller = claims == null || lister == null
        ? null
        : PlayersProjectionController(
            slotClaims: claims,
            gamepadLister: lister,
            pollInterval: widget.pollInterval,
          );
    _controller?.addListener(_handleProjectionChange);
    _syncNavigationGate();
  }

  @override
  void dispose() {
    _joinEvents?.cancel();
    _launchSubscription?.cancel();
    _controller?.removeListener(_handleProjectionChange);
    _controller?.dispose();
    widget.navigationGate?.reset();
    super.dispose();
  }

  void _listenForLaunchOwnership() {
    _launchSubscription?.cancel();
    _launchSubscription = widget.activeLaunchSession?.changes.listen(
      (_) => _syncNavigationGate(),
    );
    _syncNavigationGate();
  }

  void _listenForJoins() {
    _joinEvents?.cancel();
    try {
      _joinEvents = widget.controllerInputProvider.events().listen(
        _onJoinEvent,
        onError: (_) {},
      );
    } on Object {
      // Controller joining remains unavailable when the provider has no stream.
    }
  }

  void _syncNavigationGate() {
    final gate = widget.navigationGate;
    if (gate == null) {
      return;
    }
    gate.configure(
      enabled: true,
      requireJoined: widget.visible,
      ownershipAllowed:
          !widget.visible || widget.activeLaunchSession?.hasSession != true,
      joiningControllerId: _joiningControllerId,
      joinedControllerIds: <String>{
        for (final slot
            in _controller?.projection.slots ?? const <PlayerSlotProjection>[])
          if (slot.controller != null) slot.controller!.id,
      },
    );
  }

  void _handleProjectionChange() {
    _syncNavigationGate();
    final joiningId = _joiningControllerId;
    if (joiningId != null && !_controller!.available) {
      _cancelUnavailableJoin();
    } else if (joiningId != null &&
        !_controller!.projection.availableControllers.any(
          (pad) => pad.id == joiningId,
        )) {
      _cancelDisconnectedJoin();
    }
  }

  void _onJoinEvent(NormalizedGamepadEvent event) {
    final Object provider = widget.controllerInputProvider;
    if (provider is ControllerInputCaptureGate &&
        provider.isNavigationSuppressed) {
      return;
    }
    final gamepadId = _joinTracker.onEvent(event);
    if (gamepadId == null || !_joinOwnershipIsSafe) {
      return;
    }
    final projection = _controller?.projection;
    if (projection == null ||
        !projection.availableControllers.any((pad) => pad.id == gamepadId) ||
        gamepadId == _joiningControllerId ||
        _joinQueue.contains(gamepadId)) {
      return;
    }
    _joinQueue.add(gamepadId);
    unawaited(_processJoinQueue());
  }

  bool get _joinOwnershipIsSafe {
    if (!widget.visible ||
        widget.activeProfile == null ||
        widget.activeLaunchSession?.hasSession == true ||
        _playersRouteOpen) {
      return false;
    }
    // A join chooser is the only pushed route under which another join may be
    // queued. Change Order and every other pushed route retain chord ownership.
    return _joiningControllerId != null ||
        widget.navigatorKey.currentState?.canPop() != true;
  }

  Future<void> _processJoinQueue() async {
    if (_processingJoin) {
      return;
    }
    _processingJoin = true;
    try {
      while (_joinQueue.isNotEmpty && mounted) {
        final id = _joinQueue.removeAt(0);
        await _joinController(id);
      }
    } finally {
      _processingJoin = false;
    }
  }

  Future<void> _joinController(String id) async {
    final controller = _controller;
    final claims = widget.slotClaims;
    final activeProfile = widget.activeProfile;
    if (controller == null || claims == null || activeProfile == null) {
      return;
    }
    await controller.refresh();
    if (!controller.available) {
      _showJoinFeedback('Controller status unavailable. Join cancelled.');
      return;
    }
    ConnectedGamepad? pad;
    for (final available in controller.projection.availableControllers) {
      if (available.id == id) {
        pad = available;
        break;
      }
    }
    if (pad == null) {
      return;
    }
    final playerSlot = claims.claimDetails.indexOf(null);
    if (playerSlot < 0) {
      _showJoinFeedback('All four player spots are already in use.');
      return;
    }

    String? localProfileId;
    if (playerSlot > 0) {
      var profiles = const <LocalProfile>[];
      try {
        profiles =
            await widget.localProfileRepository?.listProfiles() ??
            const <LocalProfile>[];
      } on Object {
        // Guest remains available when profile names cannot be loaded.
      }
      final assignedIds = <String>{
        for (final claim in claims.claimDetails)
          if (claim?.localProfileId case final id?) id,
      };
      final eligible = profiles
          .where(
            (profile) =>
                profile.id != activeProfile.id &&
                !assignedIds.contains(profile.id),
          )
          .toList(growable: false);
      if (eligible.isNotEmpty) {
        _joiningControllerId = id;
        _syncNavigationGate();
        final choice = await showWhoIsUsingController(
          widget.navigatorKey.currentContext!,
          playerNumber: playerSlot + 1,
          profiles: eligible,
          controllerDisplayName:
              controllerDisplayNamesById(
                controller.snapshot?.devices ?? <ConnectedGamepad>[pad],
              )[pad.id] ??
              'Controller',
        );
        _joiningControllerId = null;
        _syncNavigationGate();
        if (choice == null || !mounted) {
          return;
        }
        localProfileId = choice.localProfileId;
      }
    }
    final result = await claims.join(
      claim: ControllerSlotClaim.fromGamepad(pad),
      localProfileId: localProfileId,
    );
    await controller.refresh();
    if (result.disposition == ControllerJoinDisposition.joined) {
      _showJoinFeedback('Player ${result.playerSlot! + 1} joined.');
    } else if (result.disposition == ControllerJoinDisposition.rosterFull) {
      _showJoinFeedback('All four player spots are already in use.');
    }
  }

  void _cancelDisconnectedJoin() {
    final id = _joiningControllerId;
    if (id == null) {
      return;
    }
    _joiningControllerId = null;
    _joinTracker.forget(id);
    _syncNavigationGate();
    widget.navigatorKey.currentState?.maybePop();
    _showJoinFeedback('Controller disconnected. Join cancelled.');
  }

  void _cancelUnavailableJoin() {
    final id = _joiningControllerId;
    if (id == null) {
      return;
    }
    _joiningControllerId = null;
    _joinTracker.forget(id);
    _syncNavigationGate();
    widget.navigatorKey.currentState?.maybePop();
    _showJoinFeedback('Controller status unavailable. Join cancelled.');
  }

  void _showJoinFeedback(String message) {
    final context = widget.navigatorKey.currentContext;
    if (context != null) {
      ScaffoldMessenger.maybeOf(context)?.showSnackBar(
        SnackBar(content: Text(message), duration: const Duration(seconds: 2)),
      );
    }
  }

  Future<PlayersPanelResult> _openPlayers({
    bool forLaunch = false,
    VoidCallback? onSwitchProfile,
    LocalProfile? activeProfile,
  }) async {
    activeProfile ??= widget.activeProfile;
    onSwitchProfile ??= widget.onSwitchProfile;
    final navigator = widget.navigatorKey.currentState;
    final controller = _controller;
    final claims = widget.slotClaims;
    final lister = widget.gamepadLister;
    if (_playersRouteOpen ||
        navigator == null ||
        controller == null ||
        claims == null ||
        lister == null) {
      return const PlayersPanelResult.dismissed();
    }
    _playersRouteOpen = true;
    final previousFocus = FocusManager.instance.primaryFocus;
    final motion = context.motion;
    try {
      var localProfiles = const <LocalProfile>[];
      try {
        localProfiles =
            await widget.localProfileRepository?.listProfiles() ??
            const <LocalProfile>[];
      } on Object {
        // Seating remains usable when the read-only profile list is unavailable.
      }
      final result = await navigator.push<PlayersPanelDisposition>(
        PageRouteBuilder<PlayersPanelDisposition>(
          opaque: false,
          barrierDismissible: true,
          barrierColor: context.consoleColors.scrim,
          barrierLabel: 'Dismiss Players',
          transitionDuration: motion.resolve(context, motion.routeEnter),
          reverseTransitionDuration: motion.resolve(context, motion.routeExit),
          pageBuilder: (context, animation, secondaryAnimation) =>
              PlayersSetupScreen(
                projectionController: controller,
                slotClaims: claims,
                forLaunch: forLaunch,
                onSwitchProfile: onSwitchProfile,
                activeProfile: activeProfile,
                localProfiles: localProfiles,
                controllerInputProvider: CallbackControllerInputProvider(
                  listGamepads: lister,
                  events: widget.controllerInputProvider.events,
                ),
              ),
          transitionsBuilder: (context, animation, secondaryAnimation, child) =>
              FadeTransition(
                opacity: CurvedAnimation(
                  parent: animation,
                  curve: motion.standardCurve,
                ),
                child: ScaleTransition(
                  scale: Tween<double>(begin: motion.routeScaleBegin, end: 1)
                      .animate(
                        CurvedAnimation(
                          parent: animation,
                          curve: motion.emphasizedCurve,
                        ),
                      ),
                  child: child,
                ),
              ),
        ),
      );
      final snapshot = controller.snapshot;
      return result == PlayersPanelDisposition.approved && snapshot != null
          ? PlayersPanelResult.approved(snapshot)
          : const PlayersPanelResult.dismissed();
    } finally {
      _playersRouteOpen = false;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (previousFocus?.canRequestFocus == true) {
          previousFocus!.requestFocus();
        }
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final controller = _controller;
    final enabled = controller != null && widget.visible;
    final content = Shortcuts(
      shortcuts: enabled
          ? const <ShortcutActivator, Intent>{
              SingleActivator(LogicalKeyboardKey.f2): ShowPlayersIntent(),
            }
          : const <ShortcutActivator, Intent>{},
      child: Actions(
        actions: enabled
            ? <Type, Action<Intent>>{
                ShowPlayersIntent: CallbackAction<ShowPlayersIntent>(
                  onInvoke: (_) {
                    _openPlayers();
                    return null;
                  },
                ),
              }
            : const <Type, Action<Intent>>{},
        child: widget.child,
      ),
    );
    return controller == null
        ? content
        : PlayersStatusScope(
            controller: controller,
            openPlayers: _openPlayers,
            child: content,
          );
  }
}

Future<ReviewedLaunchSnapshot?> reviewPlayersBeforePlay(
  BuildContext context, {
  SessionControllerSlotClaims? slotClaims,
  GamepadLister? gamepadLister,
}) async {
  final scope = PlayersStatusScope.maybeOf(context);
  if (scope == null) {
    if (slotClaims == null || gamepadLister == null) {
      return null;
    }
    return captureReviewedLaunchSnapshot(
      slotClaims: slotClaims,
      gamepadLister: gamepadLister,
    );
  }
  await scope.controller.refresh();
  if (!context.mounted) {
    return null;
  }
  switch (scope.controller.launchDisposition) {
    case PlayersLaunchDisposition.direct:
      return scope.controller.snapshot;
    case PlayersLaunchDisposition.setupRequired ||
        PlayersLaunchDisposition.review:
      final result = await scope.openPlayers(forLaunch: true);
      return result.disposition == PlayersPanelDisposition.approved
          ? result.snapshot
          : null;
  }
}

final class PlayersSetupScreen extends StatelessWidget {
  const PlayersSetupScreen({
    required this.projectionController,
    required this.slotClaims,
    required this.controllerInputProvider,
    this.forLaunch = false,
    this.onSwitchProfile,
    this.activeProfile,
    this.localProfiles = const <LocalProfile>[],
    super.key,
  });

  final PlayersProjectionController projectionController;
  final SessionControllerSlotClaims slotClaims;
  final ControllerInputProvider controllerInputProvider;
  final bool forLaunch;
  final VoidCallback? onSwitchProfile;
  final LocalProfile? activeProfile;
  final List<LocalProfile> localProfiles;

  Future<void> _openChangeOrder(BuildContext context) async {
    await ChangeOrderScreen.show(
      context,
      slotClaims: slotClaims,
      controllerInputProvider: controllerInputProvider,
      profileNamesById: <String, String>{
        for (final profile in localProfiles) profile.id: profile.displayName,
      },
    );
    await projectionController.refresh();
  }

  Future<void> _openGuestProfiles(BuildContext context) {
    final profile = activeProfile;
    if (profile == null) {
      return Future<void>.value();
    }
    return Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => GuestProfileAssignmentScreen(
          projectionListenable: projectionController,
          readProjection: () => projectionController.projection,
          refreshProjection: projectionController.refresh,
          slotClaims: slotClaims,
          profiles: localProfiles,
          activeProfile: profile,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) => CallbackShortcuts(
    bindings: <ShortcutActivator, VoidCallback>{
      const SingleActivator(LogicalKeyboardKey.escape): () =>
          Navigator.of(context).maybePop(),
    },
    child: Scaffold(
      key: const ValueKey<String>('players-overlay'),
      backgroundColor: Colors.transparent,
      body: Actions(
        actions: <Type, Action<Intent>>{
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              Navigator.of(context).maybePop();
              return null;
            },
          ),
        },
        child: _PlayersRouteFocus(
          child: AnimatedBuilder(
            animation: projectionController,
            builder: (context, _) {
              final projection = projectionController.projection;
              final needsAttention =
                  projectionController.launchDisposition ==
                  PlayersLaunchDisposition.setupRequired;
              return PlayersPanel(
                projection: projection,
                activeProfile: activeProfile,
                issueCopy: _playersIssueCopy(projectionController),
                profileNamesBySlot: <int, String?>{
                  for (final slot in projection.slots)
                    slot.playerSlot: _profileNameForSlot(slot),
                },
                actions: <Widget>[
                  if (forLaunch)
                    PlayersPanelAction(
                      icon: Icons.play_arrow,
                      label: needsAttention
                          ? 'Play anyway'
                          : 'Continue to game',
                      detail:
                          'Game controller order may differ from Ottercade Players',
                      autofocus: !needsAttention,
                      onPressed: () => Navigator.of(
                        context,
                      ).pop(PlayersPanelDisposition.approved),
                    ),
                  PlayersPanelAction(
                    icon: Icons.swap_horiz,
                    label: 'Change player order',
                    detail: 'Choose who is P1, P2, P3, and P4',
                    autofocus: !forLaunch || needsAttention,
                    onPressed: () => _openChangeOrder(context),
                  ),
                  if (activeProfile != null &&
                      localProfiles.any(
                        (profile) => profile.id != activeProfile!.id,
                      ))
                    PlayersPanelAction(
                      icon: Icons.group_outlined,
                      label: 'Assign guest profiles',
                      detail:
                          'Choose a local profile for P2–P4, or leave them as guests',
                      onPressed: () => _openGuestProfiles(context),
                    ),
                  if (onSwitchProfile != null)
                    PlayersPanelAction(
                      icon: Icons.switch_account_outlined,
                      label: 'Switch primary profile',
                      detail: 'Leave this library and choose who is playing',
                      onPressed: () {
                        Navigator.of(context).pop();
                        onSwitchProfile!();
                      },
                    ),
                ],
              );
            },
          ),
        ),
      ),
    ),
  );

  String? _profileNameForSlot(PlayerSlotProjection slot) {
    if (slot.playerSlot == 0) {
      return activeProfile?.displayName;
    }
    final profileId = slot.claim?.localProfileId;
    if (profileId == null) {
      return slot.state == PlayersSlotState.open ? null : 'Guest';
    }
    for (final profile in localProfiles) {
      if (profile.id == profileId) {
        return profile.displayName;
      }
    }
    return 'Guest';
  }
}

final class _PlayersRouteFocus extends StatefulWidget {
  const _PlayersRouteFocus({required this.child});

  final Widget child;

  @override
  State<_PlayersRouteFocus> createState() => _PlayersRouteFocusState();
}

final class _PlayersRouteFocusState extends State<_PlayersRouteFocus> {
  final FocusScopeNode _scope = FocusScopeNode(debugLabel: 'players-route');

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        _scope.requestFocus();
      }
    });
  }

  @override
  void dispose() {
    _scope.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      FocusScope(node: _scope, child: widget.child);
}

String? _playersIssueCopy(PlayersProjectionController controller) {
  if (!controller.available) {
    return 'Controller status is temporarily unavailable. Confirm player order '
        'before playing.';
  }
  for (final slot in controller.projection.slots) {
    final player = slot.playerSlot + 1;
    switch (slot.state) {
      case PlayersSlotState.reservedExact:
        return 'P$player is waiting for '
            '${slot.claim?.displayName ?? 'a controller'}. Reconnect it or '
            'change player order. Other controllers won’t take P$player.';
      case PlayersSlotState.blockedAmbiguous:
        return 'Ottercade cannot safely tell these controllers apart for P$player. '
            'Change player order before playing.';
      case PlayersSlotState.connected:
        break;
      case PlayersSlotState.open:
        if (slot.claim != null) {
          return 'P$player\'s previous controller is unavailable. Confirm player '
              'order before playing.';
        }
    }
  }
  return null;
}
