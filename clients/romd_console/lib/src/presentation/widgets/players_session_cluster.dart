import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../domain/local_profile.dart';
import '../../play/controllers/domain/players_projection.dart';
import '../theme/console_theme_context.dart';
import 'players_status_shell.dart';

/// Compact profile-led entry point into the session Players surface.
///
/// This is a view over [PlayersProjectionController]; it never enumerates,
/// resolves, or persists controller state itself.
final class PlayersSessionCluster extends StatefulWidget {
  const PlayersSessionCluster({
    required this.profile,
    required this.onSwitchProfile,
    this.focusNode,
    super.key,
  });

  final LocalProfile profile;
  final VoidCallback onSwitchProfile;
  final FocusNode? focusNode;

  @override
  State<PlayersSessionCluster> createState() => _PlayersSessionClusterState();
}

final class _PlayersSessionClusterState extends State<PlayersSessionCluster> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final scope = PlayersStatusScope.maybeOf(context);
    if (scope == null) {
      return _ClusterBody(
        profile: widget.profile,
        projection: null,
        available: false,
        focused: _focused,
        focusNode: widget.focusNode,
        onFocusChange: _setFocused,
        onPressed: widget.onSwitchProfile,
      );
    }
    return AnimatedBuilder(
      animation: scope.controller,
      builder: (context, _) => _ClusterBody(
        profile: widget.profile,
        projection: scope.controller.projection,
        available: scope.controller.available,
        focused: _focused,
        focusNode: widget.focusNode,
        onFocusChange: _setFocused,
        onPressed: () => scope.openPlayers(
          onSwitchProfile: widget.onSwitchProfile,
          activeProfile: widget.profile,
        ),
      ),
    );
  }

  void _setFocused(bool focused) {
    if (_focused != focused) {
      setState(() => _focused = focused);
    }
  }
}

final class _ClusterBody extends StatelessWidget {
  const _ClusterBody({
    required this.profile,
    required this.projection,
    required this.available,
    required this.focused,
    required this.focusNode,
    required this.onFocusChange,
    required this.onPressed,
  });

  final LocalProfile profile;
  final PlayersProjection? projection;
  final bool available;
  final bool focused;
  final FocusNode? focusNode;
  final ValueChanged<bool> onFocusChange;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final state = _ClusterState.from(projection, available: available);
    return Semantics(
      button: true,
      label: '${state.semanticLabel} Open Players.',
      child: Shortcuts(
        shortcuts: const <ShortcutActivator, Intent>{
          SingleActivator(LogicalKeyboardKey.enter): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.numpadEnter): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.select): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.gameButtonA): ActivateIntent(),
        },
        child: Actions(
          actions: <Type, Action<Intent>>{
            ActivateIntent: CallbackAction<ActivateIntent>(
              onInvoke: (_) {
                onPressed();
                return null;
              },
            ),
          },
          child: FocusableActionDetector(
            focusNode: focusNode,
            onFocusChange: onFocusChange,
            mouseCursor: SystemMouseCursors.click,
            child: GestureDetector(
              onTap: onPressed,
              child: SizedBox(
                height: layout.sessionCluster.height,
                width: !available
                    ? layout.sessionCluster.emptyWidth
                    : (state.controllerCount == 0
                          ? layout.sessionCluster.emptyWidth
                          : layout.sessionCluster.leadingWidth +
                                state.controllerCount *
                                    layout.sessionCluster.iconSlot +
                                (state.controllerCount - 1) * layout.xs),
                child: AnimatedContainer(
                  key: const ValueKey<String>('players-session-cluster'),
                  duration: motion.resolve(context, motion.focus),
                  curve: motion.standardCurve,
                  padding: EdgeInsets.symmetric(horizontal: layout.xs),
                  decoration: BoxDecoration(
                    color: focused ? colors.focusFill : Colors.transparent,
                    borderRadius: BorderRadius.circular(
                      layout.sessionCluster.radius,
                    ),
                    border: Border.all(
                      color: focused
                          ? colors.focusBorder
                          : state.attention
                          ? colors.warning
                          : Colors.transparent,
                      width: focused
                          ? layout.focusStroke
                          : layout.hairlineStroke,
                    ),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: <Widget>[
                      if (!available)
                        SizedBox(
                          width: layout.sessionCluster.iconSlot,
                          child: Icon(
                            Icons.warning_amber_rounded,
                            size: layout.sessionCluster.iconSize,
                            color: colors.warning,
                          ),
                        )
                      else
                        _ControllerRosterIcons(
                          joinedCount: state.joinedCount,
                          availableCount: state.availableCount,
                          attentionCount: state.attentionCount,
                        ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

final class _ControllerRosterIcons extends StatelessWidget {
  const _ControllerRosterIcons({
    required this.joinedCount,
    required this.availableCount,
    required this.attentionCount,
  });

  final int joinedCount;
  final int availableCount;
  final int attentionCount;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        if (joinedCount + availableCount + attentionCount == 0)
          SizedBox(
            width: layout.sessionCluster.iconSlot,
            child: Icon(
              Icons.group_outlined,
              key: const ValueKey<String>('empty-players-icon'),
              size: layout.sessionCluster.iconSize,
              color: colors.textFaint,
            ),
          ),
        for (var index = 0; index < joinedCount; index++)
          Padding(
            padding: EdgeInsets.only(
              right: index == joinedCount + availableCount + attentionCount - 1
                  ? 0
                  : layout.xs,
            ),
            child: SizedBox(
              width: layout.sessionCluster.iconSlot,
              child: Icon(
                Icons.sports_esports,
                key: ValueKey<String>('joined-controller-icon-$index'),
                size: layout.sessionCluster.iconSize,
                color: colors.connected,
              ),
            ),
          ),
        for (var index = 0; index < availableCount; index++)
          Padding(
            padding: EdgeInsets.only(
              right: index == availableCount + attentionCount - 1
                  ? 0
                  : layout.xs,
            ),
            child: SizedBox(
              width: layout.sessionCluster.iconSlot,
              child: Icon(
                Icons.sports_esports_outlined,
                key: ValueKey<String>('available-controller-icon-$index'),
                size: layout.sessionCluster.iconSize,
                color: colors.textFaint,
              ),
            ),
          ),
        for (var index = 0; index < attentionCount; index++)
          Padding(
            padding: EdgeInsets.only(
              right: index == attentionCount - 1 ? 0 : layout.xs,
            ),
            child: SizedBox(
              width: layout.sessionCluster.iconSlot,
              child: Stack(
                clipBehavior: Clip.none,
                alignment: Alignment.center,
                children: <Widget>[
                  Icon(
                    Icons.sports_esports_outlined,
                    key: ValueKey<String>('attention-controller-icon-$index'),
                    size: layout.sessionCluster.iconSize,
                    color: colors.warning,
                  ),
                  Positioned(
                    top: layout.hairlineStroke,
                    right: 0,
                    child: Container(
                      key: ValueKey<String>(
                        'attention-controller-badge-$index',
                      ),
                      width: layout.sessionCluster.attentionBadgeSize,
                      height: layout.sessionCluster.attentionBadgeSize,
                      decoration: BoxDecoration(
                        color: colors.warning,
                        shape: layout.sessionCluster.attentionBadgeShape,
                        borderRadius:
                            layout.sessionCluster.attentionBadgeShape ==
                                BoxShape.rectangle
                            ? BorderRadius.circular(
                                layout.sessionCluster.attentionBadgeRadius,
                              )
                            : null,
                      ),
                      child: Icon(
                        Icons.priority_high_rounded,
                        size: layout.sessionCluster.attentionBadgeIconSize,
                        color: colors.onWarning,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
      ],
    );
  }
}

final class _ClusterState {
  const _ClusterState({
    required this.joinedCount,
    required this.attention,
    required this.kind,
    required this.semanticLabel,
    required this.availableCount,
    required this.attentionCount,
  });

  factory _ClusterState.from(
    PlayersProjection? projection, {
    required bool available,
  }) {
    if (!available || projection == null) {
      return const _ClusterState(
        joinedCount: 0,
        attention: true,
        kind: _ClusterKind.unavailable,
        semanticLabel: 'Players status is unavailable.',
        availableCount: 0,
        attentionCount: 0,
      );
    }
    final joinedCount = <Object?>[
      for (final slot in projection.slots)
        if (slot.state == PlayersSlotState.connected) null,
    ].length;
    final availableCount = projection.availableControllers.length;
    final attentionCount = projection.attentionControllers.length;
    if (joinedCount == 0) {
      return _ClusterState(
        joinedCount: 0,
        attention: false,
        kind: _ClusterKind.empty,
        semanticLabel: availableCount == 0 && attentionCount == 0
            ? 'No controllers are connected or joined.'
            : 'No controllers are joined.'
                  '${_availableSemantic(availableCount)}'
                  '${_attentionSemantic(attentionCount)}',
        availableCount: availableCount,
        attentionCount: attentionCount,
      );
    }
    return _ClusterState(
      joinedCount: joinedCount,
      attention: false,
      kind: joinedCount == 1 ? _ClusterKind.single : _ClusterKind.multiplayer,
      semanticLabel: joinedCount == 1
          ? 'One controller is joined.${_availableSemantic(availableCount)}${_attentionSemantic(attentionCount)}'
          : '$joinedCount controllers are joined.${_availableSemantic(availableCount)}${_attentionSemantic(attentionCount)}',
      availableCount: availableCount,
      attentionCount: attentionCount,
    );
  }

  final int joinedCount;
  final bool attention;
  final _ClusterKind kind;
  final String semanticLabel;
  final int availableCount;
  final int attentionCount;
  int get controllerCount => joinedCount + availableCount + attentionCount;

  String label(String profileName) => switch (kind) {
    _ClusterKind.single => '$profileName · 1 player',
    _ClusterKind.multiplayer => '$profileName · $joinedCount players',
    _ClusterKind.empty => 'Set up players',
    _ClusterKind.unavailable => 'Players unavailable',
  };

  static String _availableSemantic(int count) => count == 0
      ? ''
      : ' $count ${count == 1 ? 'controller is' : 'controllers are'} available to join.';

  static String _attentionSemantic(int count) => count == 0
      ? ''
      : ' $count ${count == 1 ? 'connected controller needs' : 'connected controllers need'} identity attention.';
}

enum _ClusterKind { single, multiplayer, empty, unavailable }
