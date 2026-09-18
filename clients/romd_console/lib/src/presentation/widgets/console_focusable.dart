import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../theme/console_theme_context.dart';

/// The console's shared focusable-control shell.
///
/// Wraps the recurring Shortcuts → Actions → FocusableActionDetector →
/// GestureDetector sandwich so every interactive control activates on Enter,
/// numpad Enter, Select, gamepad A, and tap, and reports focus through one
/// path. Visuals stay with the caller: [builder] receives the live focus
/// state and draws whatever the control needs.
final class ConsoleFocusable extends StatefulWidget {
  const ConsoleFocusable({
    required this.onPressed,
    required this.builder,
    this.focusNode,
    this.autofocus = false,
    this.enabled = true,
    this.semanticLabel,
    this.selected = false,
    this.scrollIntoViewOnFocus = false,
    this.onFocusChange,
    super.key,
  });

  final VoidCallback onPressed;
  final Widget Function(BuildContext context, bool focused) builder;
  final FocusNode? focusNode;
  final bool autofocus;
  final bool enabled;
  final String? semanticLabel;
  final bool selected;

  /// Centers this control in every ancestor scrollable when it gains focus.
  /// Leave off when a shell owns the scrolling (e.g. the home rail).
  final bool scrollIntoViewOnFocus;
  final ValueChanged<bool>? onFocusChange;

  @override
  State<ConsoleFocusable> createState() => _ConsoleFocusableState();
}

final class _ConsoleFocusableState extends State<ConsoleFocusable> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final child = Shortcuts(
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
              if (widget.enabled) widget.onPressed();
              return null;
            },
          ),
        },
        child: FocusableActionDetector(
          focusNode: widget.focusNode,
          autofocus: widget.autofocus,
          enabled: widget.enabled,
          mouseCursor: widget.enabled
              ? SystemMouseCursors.click
              : SystemMouseCursors.basic,
          onFocusChange: _handleFocusChange,
          child: GestureDetector(
            onTap: widget.enabled ? _handleTap : null,
            child: widget.builder(context, _focused),
          ),
        ),
      ),
    );
    final label = widget.semanticLabel;
    if (label == null) return child;
    return Semantics(
      button: true,
      enabled: widget.enabled,
      selected: widget.selected,
      label: label,
      child: child,
    );
  }

  void _handleFocusChange(bool focused) {
    setState(() => _focused = focused);
    widget.onFocusChange?.call(focused);
    if (focused && widget.scrollIntoViewOnFocus) _scrollIntoView();
  }

  void _handleTap() {
    widget.focusNode?.requestFocus();
    widget.onPressed();
  }

  void _scrollIntoView() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      Scrollable.ensureVisible(
        context,
        alignment: 0.5,
        duration: context.motion.resolve(
          context,
          context.motion.contentTransition,
        ),
        curve: context.motion.emphasizedCurve,
      );
    });
  }
}
