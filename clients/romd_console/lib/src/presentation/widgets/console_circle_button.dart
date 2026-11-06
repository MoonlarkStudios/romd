import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';

/// The circular "back" affordance at the top-left of the detail and record
/// screens. Visual only — the owning screen drives selection and key handling —
/// but still tappable by mouse. Shows a teal ring when [selected].
final class ConsoleCircleButton extends StatelessWidget {
  const ConsoleCircleButton({
    required this.onTap,
    this.icon = Icons.chevron_left,
    this.selected = false,
    super.key,
  });

  final VoidCallback onTap;
  final IconData icon;
  final bool selected;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    return Semantics(
      button: true,
      label: 'Back',
      child: GestureDetector(
        onTap: onTap,
        child: AnimatedContainer(
          duration: motion.resolve(context, motion.selection),
          curve: motion.standardCurve,
          width: layout.circleButtonSize,
          height: layout.circleButtonSize,
          decoration: BoxDecoration(
            shape: layout.circleButtonShape,
            borderRadius: layout.circleButtonShape == BoxShape.rectangle
                ? BorderRadius.circular(layout.circleButtonRadius)
                : null,
            color: selected ? colors.selectionFill : colors.panelSurface,
            border: Border.all(
              color: selected ? colors.selectionBorder : colors.panelBorder,
              width: selected ? layout.focusStroke : layout.hairlineStroke,
            ),
            boxShadow: selected ? context.elevation.selectionGlow : const [],
          ),
          alignment: Alignment.center,
          child: Icon(icon, size: layout.iconMd, color: colors.iconStrong),
        ),
      ),
    );
  }
}
