import 'package:flutter/material.dart';

import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';

/// System surfaces reachable from the home dock, in display order.
enum HomeDockAction {
  library,
  catalog,
  controllers,
  settings,
  switchProfile,
  exit,
}

String _dockLabel(HomeDockAction action) => switch (action) {
  HomeDockAction.library => 'Library',
  HomeDockAction.catalog => 'Catalog',
  HomeDockAction.controllers => 'Controllers',
  HomeDockAction.settings => 'Settings',
  HomeDockAction.switchProfile => 'Switch Profile',
  HomeDockAction.exit => 'Exit',
};

/// The home screen's icon dock: one centered pill of circular actions with a
/// reserved label lane underneath — the focused action names itself there, so
/// the dock stays icon-quiet until it's being navigated.
final class HomeDock extends StatefulWidget {
  const HomeDock({
    required this.nodes,
    required this.onActivate,
    required this.onMoveUp,
    required this.onMoveDown,
    super.key,
  });

  /// One node per [HomeDockAction], owned by the home screen so focus memory
  /// survives this widget rebuilding.
  final List<FocusNode> nodes;
  final ValueChanged<HomeDockAction> onActivate;
  final VoidCallback onMoveUp;
  final VoidCallback onMoveDown;

  @override
  State<HomeDock> createState() => _HomeDockState();
}

final class _HomeDockState extends State<HomeDock> {
  HomeDockAction? _focusedAction;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Semantics(
      container: true,
      label: 'Home dock',
      child: Actions(
        actions: launcherDirectionalActions(_handleDirection),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            DecoratedBox(
              decoration: BoxDecoration(
                color: colors.panelSurface,
                borderRadius: BorderRadius.circular(layout.pillRadius),
                border: Border.all(
                  color: colors.hairline,
                  width: layout.hairlineStroke,
                ),
              ),
              child: Padding(
                padding: EdgeInsets.symmetric(
                  horizontal: layout.md,
                  vertical: layout.xs,
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: <Widget>[
                    for (final action in HomeDockAction.values)
                      SizedBox(
                        width: layout.navigationDock.itemExtent,
                        child: Center(
                          child: _DockButton(
                            action: action,
                            focusNode: widget.nodes[action.index],
                            diameter: layout.navigationDock.buttonSize,
                            onPressed: () => widget.onActivate(action),
                            onFocusChange: (focused) =>
                                _handleFocusChange(action, focused),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
            ),
            SizedBox(
              height: layout.navigationDock.labelLaneHeight,
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  for (final action in HomeDockAction.values)
                    SizedBox(
                      width: layout.navigationDock.itemExtent,
                      child: _focusedAction == action
                          ? OverflowBox(
                              maxWidth: layout.navigationDock.labelMaxWidth,
                              child: Padding(
                                padding: EdgeInsets.only(top: layout.xxs),
                                child: Text(
                                  _dockLabel(action),
                                  maxLines: 1,
                                  softWrap: false,
                                  textAlign: TextAlign.center,
                                  style: context.text.sectionLabel.copyWith(
                                    color: context.consoleColors.focusBorder,
                                  ),
                                ),
                              ),
                            )
                          : null,
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  void _handleFocusChange(HomeDockAction action, bool focused) {
    setState(() {
      if (focused) {
        _focusedAction = action;
      } else if (_focusedAction == action) {
        _focusedAction = null;
      }
    });
  }

  void _handleDirection(TraversalDirection direction) {
    final index = widget.nodes.indexWhere((node) => node.hasFocus);
    switch (direction) {
      case TraversalDirection.up:
        widget.onMoveUp();
      case TraversalDirection.left:
        if (index > 0) widget.nodes[index - 1].requestFocus();
      case TraversalDirection.right:
        if (index >= 0 && index < widget.nodes.length - 1) {
          widget.nodes[index + 1].requestFocus();
        }
      case TraversalDirection.down:
        widget.onMoveDown();
    }
  }
}

final class _DockButton extends StatelessWidget {
  const _DockButton({
    required this.action,
    required this.focusNode,
    required this.diameter,
    required this.onPressed,
    required this.onFocusChange,
  });

  final HomeDockAction action;
  final FocusNode focusNode;
  final double diameter;
  final VoidCallback onPressed;
  final ValueChanged<bool> onFocusChange;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    return ConsoleFocusable(
      focusNode: focusNode,
      onPressed: onPressed,
      onFocusChange: onFocusChange,
      semanticLabel: _dockLabel(action),
      builder: (context, focused) => ConsoleFocusRing(
        focused: focused,
        shape: layout.navigationDock.buttonShape,
        borderRadius: layout.navigationDock.buttonRadius,
        restBorderColor: Colors.transparent,
        child: AnimatedContainer(
          key: ValueKey<String>('dock-${action.name}'),
          duration: motion.resolve(context, motion.focus),
          curve: motion.standardCurve,
          width: diameter,
          height: diameter,
          decoration: BoxDecoration(
            shape: layout.navigationDock.buttonShape,
            borderRadius:
                layout.navigationDock.buttonShape == BoxShape.rectangle
                ? BorderRadius.circular(layout.navigationDock.buttonRadius)
                : null,
            // Chromeless at rest: stacking panelSurface on the dock's own
            // panelSurface compounds translucency into a visible disc edge.
            color: focused ? colors.focusFill : Colors.transparent,
          ),
          child: Icon(
            _icon(action),
            size: layout.navigationDock.iconSize,
            color: focused ? colors.textStrong : colors.textMuted,
          ),
        ),
      ),
    );
  }

  static IconData _icon(HomeDockAction action) => switch (action) {
    HomeDockAction.library => Icons.grid_view_rounded,
    HomeDockAction.catalog => Icons.collections_bookmark_outlined,
    HomeDockAction.controllers => Icons.sports_esports_outlined,
    HomeDockAction.settings => Icons.settings_outlined,
    HomeDockAction.switchProfile => Icons.switch_account_outlined,
    HomeDockAction.exit => Icons.power_settings_new,
  };
}
