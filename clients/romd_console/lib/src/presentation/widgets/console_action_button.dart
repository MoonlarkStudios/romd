import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';
import 'console_focus_ring.dart';

/// Semantic weight of a [ConsoleActionButton]. A view presents at most one
/// [primary] action; [secondary] carries alternatives; [quiet] carries
/// low-commitment utilities.
enum ConsoleActionKind { primary, secondary, quiet }

/// The console's one button vocabulary. Wraps the themed Material buttons
/// (which own geometry, type, and disabled treatment) and adds the shared
/// focus ring plus the destructive tint rule: warning color marks actions
/// that remove things — never resting content.
final class ConsoleActionButton extends StatefulWidget {
  const ConsoleActionButton({
    required this.label,
    required this.onPressed,
    this.kind = ConsoleActionKind.secondary,
    this.icon,
    this.busy = false,
    this.destructive = false,
    this.autofocus = false,
    this.focusNode,
    super.key,
  });

  final String label;

  /// Null renders the themed disabled state.
  final VoidCallback? onPressed;
  final ConsoleActionKind kind;
  final IconData? icon;

  /// Replaces the icon slot with a small progress indicator while an
  /// operation started by this action is in flight. Callers decide whether
  /// the button also disables (usually yes, via a null [onPressed]).
  final bool busy;

  /// Tints the action with the warning role. Reserved for actions that
  /// destroy or remove; a destructive [ConsoleActionKind.primary] belongs
  /// only on an explicit confirmation surface.
  final bool destructive;
  final bool autofocus;
  final FocusNode? focusNode;

  @override
  State<ConsoleActionButton> createState() => _ConsoleActionButtonState();
}

final class _ConsoleActionButtonState extends State<ConsoleActionButton> {
  bool _focused = false;

  void _handleFocusChange(bool focused) {
    if (_focused != focused) setState(() => _focused = focused);
  }

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;

    final ButtonStyle? style = switch ((widget.kind, widget.destructive)) {
      (ConsoleActionKind.primary, true) => FilledButton.styleFrom(
        backgroundColor: colors.warning,
        foregroundColor: colors.onWarning,
      ),
      (ConsoleActionKind.primary, false) => null,
      (ConsoleActionKind.secondary, true) => OutlinedButton.styleFrom(
        foregroundColor: colors.warning,
        side: BorderSide(color: colors.warning, width: layout.hairlineStroke),
      ),
      // Secondary stays neutral: accent foregrounds are the primary action's
      // signal, so alternatives read in the ordinary text ramp.
      (ConsoleActionKind.secondary, false) => OutlinedButton.styleFrom(
        foregroundColor: colors.textStrong,
      ),
      (ConsoleActionKind.quiet, true) => TextButton.styleFrom(
        foregroundColor: colors.warning,
      ),
      (ConsoleActionKind.quiet, false) => TextButton.styleFrom(
        foregroundColor: colors.textMuted,
      ),
    };

    final label = Text(widget.label);
    final Widget? leading = widget.busy
        ? SizedBox.square(
            dimension: layout.progressIndicatorSm,
            child: CircularProgressIndicator(strokeWidth: layout.focusStroke),
          )
        : widget.icon != null
        ? Icon(widget.icon)
        : null;
    final Widget button = switch (widget.kind) {
      ConsoleActionKind.primary when leading != null => FilledButton.icon(
        onPressed: widget.onPressed,
        autofocus: widget.autofocus,
        focusNode: widget.focusNode,
        onFocusChange: _handleFocusChange,
        style: style,
        icon: leading,
        label: label,
      ),
      ConsoleActionKind.primary => FilledButton(
        onPressed: widget.onPressed,
        autofocus: widget.autofocus,
        focusNode: widget.focusNode,
        onFocusChange: _handleFocusChange,
        style: style,
        child: label,
      ),
      ConsoleActionKind.secondary when leading != null => OutlinedButton.icon(
        onPressed: widget.onPressed,
        autofocus: widget.autofocus,
        focusNode: widget.focusNode,
        onFocusChange: _handleFocusChange,
        style: style,
        icon: leading,
        label: label,
      ),
      ConsoleActionKind.secondary => OutlinedButton(
        onPressed: widget.onPressed,
        autofocus: widget.autofocus,
        focusNode: widget.focusNode,
        onFocusChange: _handleFocusChange,
        style: style,
        child: label,
      ),
      ConsoleActionKind.quiet when leading != null => TextButton.icon(
        onPressed: widget.onPressed,
        autofocus: widget.autofocus,
        focusNode: widget.focusNode,
        onFocusChange: _handleFocusChange,
        style: style,
        icon: leading,
        label: label,
      ),
      ConsoleActionKind.quiet => TextButton(
        onPressed: widget.onPressed,
        autofocus: widget.autofocus,
        focusNode: widget.focusNode,
        onFocusChange: _handleFocusChange,
        style: style,
        child: label,
      ),
    };

    return ConsoleFocusRing(
      focused: _focused,
      borderRadius: layout.controlRadius,
      restBorderColor: Colors.transparent,
      child: button,
    );
  }
}
