import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../theme/console_theme_context.dart';

enum OnScreenKeyboardLayout { text, url }

/// A D-pad / keyboard navigable on-screen keyboard for the search screen. Keys
/// are focusable cells (controller path); the search screen also captures raw
/// hardware keystrokes, so both inputs feed the same query.
final class OnScreenKeyboard extends StatelessWidget {
  const OnScreenKeyboard({
    required this.onChar,
    required this.onBackspace,
    required this.onClear,
    this.layout = OnScreenKeyboardLayout.text,
    this.initialFocusNode,
    this.onMoveBelow,
    super.key,
  });

  final ValueChanged<String> onChar;
  final VoidCallback onBackspace;
  final VoidCallback onClear;
  final OnScreenKeyboardLayout layout;
  final FocusNode? initialFocusNode;
  final VoidCallback? onMoveBelow;

  static const List<List<String>> _textRows = <List<String>>[
    <String>['A', 'B', 'C', 'D', 'E', 'F', 'G'],
    <String>['H', 'I', 'J', 'K', 'L', 'M', 'N'],
    <String>['O', 'P', 'Q', 'R', 'S', 'T', 'U'],
    <String>['V', 'W', 'X', 'Y', 'Z', '1', '2'],
    <String>['3', '4', '5', '6', '7', '8', '9'],
  ];

  static const List<List<String>> _urlRows = <List<String>>[
    <String>['A', 'B', 'C', 'D', 'E', 'F', 'G'],
    <String>['H', 'I', 'J', 'K', 'L', 'M', 'N'],
    <String>['O', 'P', 'Q', 'R', 'S', 'T', 'U'],
    <String>['V', 'W', 'X', 'Y', 'Z', '1', '2'],
    <String>['3', '4', '5', '6', '7', '8', '9'],
    <String>['.', '-', ':', '/', '0'],
  ];

  List<List<String>> get _rows => switch (layout) {
    OnScreenKeyboardLayout.text => _textRows,
    OnScreenKeyboardLayout.url => _urlRows,
  };

  @override
  Widget build(BuildContext context) {
    final themeLayout = context.layout;
    final rows = _rows;

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        for (var r = 0; r < rows.length; r++) ...<Widget>[
          if (r > 0) SizedBox(height: themeLayout.keyboard.gap),
          Row(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              for (final key in rows[r]) ...<Widget>[
                _Key(
                  label: key,
                  focusNode: r == 0 && key == 'A' ? initialFocusNode : null,
                  autofocus: r == 0 && key == 'A',
                  onTap: () => onChar(key.toLowerCase()),
                ),
                SizedBox(width: themeLayout.keyboard.gap),
              ],
            ],
          ),
        ],
        SizedBox(height: themeLayout.keyboard.gap),
        Row(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            if (layout == OnScreenKeyboardLayout.text) ...<Widget>[
              _Key(
                label: '0',
                onTap: () => onChar('0'),
                onArrowDown: onMoveBelow,
              ),
              SizedBox(width: themeLayout.keyboard.gap),
              _Key(
                flex: 3,
                label: 'SPACE',
                onTap: () => onChar(' '),
                onArrowDown: onMoveBelow,
              ),
              SizedBox(width: themeLayout.keyboard.gap),
            ],
            _Key(
              icon: Icons.backspace_outlined,
              onTap: onBackspace,
              onArrowDown: onMoveBelow,
            ),
            SizedBox(width: themeLayout.keyboard.gap),
            _Key(
              label: 'CLEAR',
              flex: 2,
              onTap: onClear,
              onArrowDown: onMoveBelow,
            ),
          ],
        ),
      ],
    );
  }
}

final class _Key extends StatefulWidget {
  const _Key({
    required this.onTap,
    this.label,
    this.icon,
    this.flex = 1,
    this.autofocus = false,
    this.focusNode,
    this.onArrowDown,
  });

  final VoidCallback onTap;
  final String? label;
  final IconData? icon;
  final int flex;
  final bool autofocus;
  final FocusNode? focusNode;
  final VoidCallback? onArrowDown;

  @override
  State<_Key> createState() => _KeyState();
}

final class _KeyState extends State<_Key> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final keyboard = layout.keyboard;
    final width =
        keyboard.unitWidth * widget.flex + keyboard.gap * (widget.flex - 1);
    final onArrowDown = widget.onArrowDown;

    return Semantics(
      button: true,
      label: widget.label ?? 'Backspace',
      child: Shortcuts(
        shortcuts: <ShortcutActivator, Intent>{
          const SingleActivator(LogicalKeyboardKey.enter):
              const ActivateIntent(),
          const SingleActivator(LogicalKeyboardKey.select):
              const ActivateIntent(),
          const SingleActivator(LogicalKeyboardKey.gameButtonA):
              const ActivateIntent(),
          if (onArrowDown != null)
            const SingleActivator(LogicalKeyboardKey.arrowDown):
                const _MoveBelowIntent(),
        },
        child: Actions(
          actions: <Type, Action<Intent>>{
            ActivateIntent: CallbackAction<ActivateIntent>(
              onInvoke: (_) {
                widget.onTap();
                return null;
              },
            ),
            if (onArrowDown != null)
              _MoveBelowIntent: CallbackAction<_MoveBelowIntent>(
                onInvoke: (_) {
                  onArrowDown();
                  return null;
                },
              ),
          },
          child: FocusableActionDetector(
            focusNode: widget.focusNode,
            autofocus: widget.autofocus,
            mouseCursor: SystemMouseCursors.click,
            onFocusChange: (focused) => setState(() => _focused = focused),
            child: GestureDetector(
              onTap: widget.onTap,
              child: AnimatedContainer(
                duration: motion.resolve(context, motion.focus),
                curve: motion.standardCurve,
                width: width,
                height: keyboard.keyHeight,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(layout.controlRadius),
                  color: _focused ? colors.focusFill : colors.panelSurface,
                  border: Border.all(
                    color: _focused ? colors.focusBorder : colors.panelBorder,
                    width: _focused
                        ? layout.focusStroke
                        : layout.hairlineStroke,
                  ),
                  boxShadow: _focused
                      ? context.elevation.focusGlow
                      : context.elevation.none,
                ),
                child: widget.icon != null
                    ? Icon(
                        widget.icon,
                        size: keyboard.iconSize,
                        color: _focused ? colors.textStrong : colors.textMuted,
                      )
                    : Text(
                        widget.label!,
                        style:
                            (widget.label!.length > 1
                                    ? context.text.metadataStrong
                                    : context.text.supportingTitle)
                                .copyWith(
                                  color: _focused
                                      ? colors.textStrong
                                      : colors.textMuted,
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

final class _MoveBelowIntent extends Intent {
  const _MoveBelowIntent();
}
