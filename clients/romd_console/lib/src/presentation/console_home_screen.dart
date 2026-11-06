import 'package:flutter/foundation.dart' show ValueListenable;
import 'package:flutter/material.dart';

import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/presentation/launcher/home_dock.dart';
import 'package:romd_console/src/presentation/launcher/home_rail.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/launcher/launcher_surface_screen.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/connection_status_indicator.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';
import 'package:romd_console/src/presentation/widgets/cover_art.dart';
import 'package:romd_console/src/presentation/widgets/game_detail.dart';
import 'package:romd_console/src/presentation/widgets/players_session_cluster.dart';
import 'package:romd_console/src/presentation/widgets/players_status_shell.dart';
import 'package:romd_console/src/presentation/widgets/profile_avatar.dart';
import 'package:romd_console/src/presentation/widgets/profile_connection_ring.dart';

/// The console home: one recently-played rail over an icon dock, with quiet
/// corner chrome — profile identity top-left, connection + clock top-right,
/// player seating bottom-left, input hints bottom-right.
///
/// Library and Catalog are full-screen surfaces pushed from the dock; the home
/// never scrolls and never stacks destinations. Back on home returns focus to
/// the rail — leaving the profile is an explicit dock action.
final class ConsoleHomeScreen extends StatefulWidget {
  const ConsoleHomeScreen({
    required this.consumerApiClient,
    required this.playServices,
    required this.localProfile,
    required this.session,
    required this.surfaceContext,
    required this.onSwitchProfile,
    required this.onExitApp,
    required this.onConnect,
    required this.onOpenControllers,
    required this.onOpenSettings,
    this.initialDockFocus,
    this.connecting = false,
    this.hideProfileAvatar = false,
    super.key,
  });

  final ConsumerApiClient consumerApiClient;
  final PlayServices playServices;
  final LocalProfile localProfile;
  final ConsumerLoginSession? session;
  final ValueListenable<HomeSurfaceContext?> surfaceContext;
  final bool connecting;

  /// Reserves the avatar's focus-ring geometry while the root's shared avatar
  /// is flying into place.
  final bool hideProfileAvatar;
  final VoidCallback onSwitchProfile;
  final VoidCallback onExitApp;
  final VoidCallback onConnect;
  final VoidCallback onOpenControllers;
  final VoidCallback onOpenSettings;
  final HomeDockAction? initialDockFocus;

  @override
  State<ConsoleHomeScreen> createState() => _ConsoleHomeScreenState();
}

final class _ConsoleHomeScreenState extends State<ConsoleHomeScreen> {
  static const double _clockClearance = 88;

  final GlobalKey<HomeRailState> _railKey = GlobalKey<HomeRailState>();
  final FocusNode _avatarNode = FocusNode(debugLabel: 'home-avatar');
  final FocusNode _connectNode = FocusNode(debugLabel: 'home-connect');
  final FocusNode _playersNode = FocusNode(debugLabel: 'home-players');
  final List<FocusNode> _dockNodes = <FocusNode>[
    for (final action in HomeDockAction.values)
      FocusNode(debugLabel: 'dock-${action.name}'),
  ];
  bool _anyControlFocused = false;

  @override
  void initState() {
    super.initState();
    _avatarNode.addListener(_trackFocus);
    _connectNode.addListener(_trackFocus);
    _playersNode.addListener(_trackFocus);
    for (final node in _dockNodes) {
      node.addListener(_trackFocus);
    }
    final initialDockFocus = widget.initialDockFocus;
    if (initialDockFocus != null) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) {
          _dockNodes[initialDockFocus.index].requestFocus();
        }
      });
    }
  }

  @override
  void dispose() {
    _avatarNode.dispose();
    _connectNode.dispose();
    _playersNode.dispose();
    for (final node in _dockNodes) {
      node.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Scaffold(
      backgroundColor: Colors.transparent,
      // Scaffold installs its own DismissIntent action, so the home Back
      // handler must remain inside this boundary.
      body: Actions(
        actions: <Type, Action<Intent>>{
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              _handleBack();
              return null;
            },
          ),
        },
        child: SafeArea(
          child: LayoutBuilder(
            builder: (context, constraints) => Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: <Widget>[
                Padding(
                  padding: EdgeInsets.fromLTRB(
                    layout.screenGutter,
                    layout.lg,
                    layout.screenGutter,
                    0,
                  ),
                  child: Row(
                    children: <Widget>[
                      _ProfileCorner(
                        profile: widget.localProfile,
                        connectionStatus: _connectionStatus(),
                        focusNode: _avatarNode,
                        hideAvatar: widget.hideProfileAvatar,
                        onPressed: widget.onOpenSettings,
                        onMoveDown: _focusRailOrDock,
                        onMoveRight: _focusConnectIfActionable,
                      ),
                      const Spacer(),
                      Padding(
                        // Clearance for the shared clock the root overlays at the
                        // gutter's edge.
                        padding: const EdgeInsets.only(right: _clockClearance),
                        child: _buildStatusCorner(),
                      ),
                    ],
                  ),
                ),
                const Spacer(),
                HomeRail(
                  key: _railKey,
                  playServices: widget.playServices,
                  tileWidth: _railTileWidth(context, constraints.maxHeight),
                  onOpenGame: _openLocalGame,
                  onConnect: widget.onConnect,
                  onMoveUp: () => _avatarNode.requestFocus(),
                  onMoveDown: _focusDock,
                  onFirstResult: _placeInitialFocus,
                  onAnyFocus: () => _anyControlFocused = true,
                ),
                const Spacer(),
                Center(
                  child: HomeDock(
                    nodes: _dockNodes,
                    onActivate: _activateDock,
                    onMoveUp: _focusRailFromDock,
                    onMoveDown: () {
                      if (PlayersStatusScope.maybeOf(context) != null) {
                        _playersNode.requestFocus();
                      }
                    },
                  ),
                ),
                SizedBox(height: layout.md),
                Padding(
                  padding: EdgeInsets.fromLTRB(
                    layout.screenGutter,
                    0,
                    layout.screenGutter,
                    layout.md,
                  ),
                  child: Row(
                    children: <Widget>[
                      if (PlayersStatusScope.maybeOf(context) != null)
                        Actions(
                          actions: launcherDirectionalActions((direction) {
                            if (direction == TraversalDirection.up)
                              _focusDock();
                          }),
                          child: PlayersSessionCluster(
                            profile: widget.localProfile,
                            focusNode: _playersNode,
                            onSwitchProfile: widget.onSwitchProfile,
                          ),
                        ),
                      const Spacer(),
                      const ConsoleHintBar(
                        hints: <ConsoleHint>[
                          ConsoleHint(
                            glyph: ConsoleHintGlyphs.navigate,
                            gamepadGlyph: 'D-PAD',
                            label: 'Navigate',
                          ),
                          ConsoleHint(
                            glyph: ConsoleHintGlyphs.confirm,
                            gamepadGlyph: 'A',
                            label: 'Open',
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  /// Rail cover width for the available canvas height: the full 160 pt design
  /// size on the 720 canvas, shrinking on shorter canvases so the fixed home
  /// composition (corners, rail, dock, hints) never overflows.
  double _railTileWidth(BuildContext context, double maxHeight) {
    final fixedOverhead = 336 + HomeRail.labelLaneHeight(context);
    final coverHeight = (maxHeight - fixedOverhead).clamp(140.0, 240.0);
    return coverHeight * kCoverAspectRatio;
  }

  ConnectionStatus _connectionStatus() {
    if (widget.connecting) return ConnectionStatus.connecting;
    if (widget.session == null) {
      return widget.localProfile.hasServer
          ? ConnectionStatus.offline
          : ConnectionStatus.local;
    }
    return ConnectionStatus.connected;
  }

  /// The top-right corner is reserved for states that need action. Healthy and
  /// in-flight connection states live on the profile avatar instead.
  Widget _buildStatusCorner() {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final status = _connectionStatus();
    final indicator = ConnectionStatusIndicator(
      status: status,
      serverHost: widget.localProfile.romdServerOrigin?.host,
    );
    if (status == ConnectionStatus.connected ||
        status == ConnectionStatus.connecting) {
      return const SizedBox.shrink();
    }
    return Actions(
      actions: launcherDirectionalActions((direction) {
        if (direction == TraversalDirection.down) {
          _focusRailOrDock();
        } else if (direction == TraversalDirection.left) {
          _avatarNode.requestFocus();
        }
      }),
      child: ConsoleFocusable(
        focusNode: _connectNode,
        onPressed: widget.onConnect,
        semanticLabel: 'Connect',
        builder: (context, focused) => AnimatedContainer(
          duration: motion.resolve(context, motion.focus),
          curve: motion.standardCurve,
          padding: EdgeInsets.symmetric(
            horizontal: layout.sm,
            vertical: layout.xxs,
          ),
          decoration: BoxDecoration(
            color: focused ? colors.focusFill : Colors.transparent,
            borderRadius: BorderRadius.circular(layout.controlRadius),
            border: Border.all(
              color: focused ? colors.focusBorder : Colors.transparent,
              width: focused ? layout.focusStroke : layout.hairlineStroke,
            ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              indicator,
              SizedBox(width: layout.xs),
              Icon(
                Icons.link,
                size: layout.iconSm,
                color: focused ? colors.textStrong : colors.textMuted,
              ),
            ],
          ),
        ),
      ),
    );
  }

  void _focusConnectIfActionable() {
    final status = _connectionStatus();
    if (status != ConnectionStatus.connected &&
        status != ConnectionStatus.connecting) {
      _connectNode.requestFocus();
    }
  }

  void _trackFocus() {
    if (_avatarNode.hasFocus ||
        _playersNode.hasFocus ||
        _dockNodes.any((node) => node.hasFocus)) {
      _anyControlFocused = true;
    }
  }

  void _placeInitialFocus() {
    if (_anyControlFocused) return;
    final rail = _railKey.currentState;
    if (rail != null && rail.hasFocusWithin) return;
    if (rail == null || !rail.focusContent()) {
      // First run with an empty shelf: the rail is silent vessels, so focus
      // lands on the dock's Catalog action — the invitation to find games.
      _dockNodes[HomeDockAction.catalog.index].requestFocus();
    }
  }

  void _focusDock() {
    final focused = _dockNodes.indexWhere((node) => node.hasFocus);
    if (focused < 0) {
      _dockNodes[HomeDockAction.library.index].requestFocus();
    }
  }

  void _focusRailFromDock() {
    final rail = _railKey.currentState;
    if (rail == null || !rail.focusContent()) {
      _avatarNode.requestFocus();
    }
  }

  void _focusRailOrDock() {
    final rail = _railKey.currentState;
    if (rail == null || !rail.focusContent()) {
      _focusDock();
    }
  }

  void _handleBack() {
    final rail = _railKey.currentState;
    if (rail == null || rail.hasFocusWithin) return;
    rail.focusContent();
  }

  void _activateDock(HomeDockAction action) {
    switch (action) {
      case HomeDockAction.library:
        pushLibrarySurface(
          context,
          surfaceContext: widget.surfaceContext,
          onConnect: widget.onConnect,
        );
      case HomeDockAction.catalog:
        pushCatalogSurface(
          context,
          surfaceContext: widget.surfaceContext,
          onConnect: widget.onConnect,
        );
      case HomeDockAction.controllers:
        widget.onOpenControllers();
      case HomeDockAction.settings:
        widget.onOpenSettings();
      case HomeDockAction.switchProfile:
        widget.onSwitchProfile();
      case HomeDockAction.exit:
        widget.onExitApp();
    }
  }

  void _openLocalGame(ConsoleGame game, String heroTag) => pushGameDetail(
    context,
    game: game,
    heroTag: heroTag,
    consumerApiClient: widget.consumerApiClient,
    operation: null,
    playServices: widget.playServices,
    localProfileId: widget.localProfile.id,
  );
}

/// Top-left identity corner: avatar + display name, opening this profile's
/// settings (the profile page analog).
final class _ProfileCorner extends StatelessWidget {
  const _ProfileCorner({
    required this.profile,
    required this.connectionStatus,
    required this.focusNode,
    required this.hideAvatar,
    required this.onPressed,
    required this.onMoveDown,
    required this.onMoveRight,
  });

  final LocalProfile profile;
  final ConnectionStatus connectionStatus;
  final FocusNode focusNode;
  final bool hideAvatar;
  final VoidCallback onPressed;
  final VoidCallback onMoveDown;
  final VoidCallback onMoveRight;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    return Actions(
      actions: launcherDirectionalActions((direction) {
        if (direction == TraversalDirection.down) {
          onMoveDown();
        } else if (direction == TraversalDirection.right) {
          onMoveRight();
        }
      }),
      child: ConsoleFocusable(
        focusNode: focusNode,
        onPressed: onPressed,
        semanticLabel: switch (connectionStatus) {
          ConnectionStatus.connected =>
            '${profile.displayName}. Connected to ROMD. Open profile settings.',
          ConnectionStatus.connecting =>
            '${profile.displayName}. Connecting to ROMD. Open profile settings.',
          _ => '${profile.displayName}. Open profile settings.',
        },
        builder: (context, focused) => ExcludeSemantics(
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              ConsoleFocusRing(
                focused: focused,
                shape: BoxShape.circle,
                restBorderColor: Colors.transparent,
                child: hideAvatar
                    ? SizedBox.square(
                        key: const ValueKey<String>(
                          'home-profile-avatar-placeholder',
                        ),
                        dimension: layout.avatarSm,
                      )
                    : ProfileConnectionRing(
                        status: connectionStatus,
                        indicatorKey: ValueKey<String>(
                          'home-profile-connection-${connectionStatus.name}',
                        ),
                        child: ProfileAvatar(
                          key: const ValueKey<String>('home-profile-avatar'),
                          avatarKey: profile.avatarKey,
                          accentColor: Color(profile.accentColor),
                          size: layout.avatarSm,
                        ),
                      ),
              ),
              SizedBox(width: layout.sm),
              AnimatedDefaultTextStyle(
                key: const ValueKey<String>('home-profile-label'),
                duration: motion.resolve(context, motion.focus),
                style: context.text.sectionLabel.copyWith(
                  color: focused ? colors.textStrong : colors.textMuted,
                ),
                child: Text(
                  profile.displayName,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
