import 'package:flutter/material.dart';

import '../../play/controllers/domain/canonical_controller_mapping.dart';
import '../theme/console_theme_context.dart';
import '../theme/console_theme_extensions.dart';

/// Shared inline feedback surface for controller setup states.
final class ControllerSetupMessagePanel extends StatelessWidget {
  const ControllerSetupMessagePanel({
    required this.icon,
    required this.text,
    super.key,
  });

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;

    return Semantics(
      container: true,
      child: Padding(
        padding: EdgeInsets.only(top: layout.xs),
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: colors.panelSurface,
            borderRadius: BorderRadius.circular(layout.controlRadius),
          ),
          child: Padding(
            padding: EdgeInsets.symmetric(
              horizontal: layout.md,
              vertical: layout.sm,
            ),
            child: Row(
              children: <Widget>[
                Icon(icon, color: colors.textMuted, size: layout.iconMd),
                SizedBox(width: layout.sm),
                Expanded(
                  child: Text(
                    text,
                    style: context.text.metadata.copyWith(
                      color: colors.textMuted,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Vendor-neutral controller map. Fixed coordinates define control topology;
/// all visual treatment is supplied by the active console skin.
final class CanonicalControllerDiagram extends StatelessWidget {
  const CanonicalControllerDiagram({
    required this.mapping,
    required this.selected,
    required this.active,
    required this.enabled,
    required this.focusNodes,
    required this.onSelected,
    required this.controlLabel,
    super.key,
  });

  final Map<CanonicalGamepadControl, RawGamepadInput> mapping;
  final CanonicalGamepadControl? selected;
  final Set<CanonicalGamepadControl> active;
  final bool enabled;
  final Map<CanonicalGamepadControl, FocusNode> focusNodes;
  final ValueChanged<CanonicalGamepadControl> onSelected;
  final String Function(CanonicalGamepadControl) controlLabel;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;

    return Semantics(
      container: true,
      label: 'Neutral Ottercade controller map',
      child: FittedBox(
        fit: BoxFit.scaleDown,
        child: SizedBox(
          width: 760,
          height: 330,
          child: DecoratedBox(
            key: const ValueKey<String>('controller-diagram-frame'),
            decoration: BoxDecoration(
              color: colors.controlRestFill,
              border: Border.all(
                color: colors.panelBorder,
                width: layout.hairlineStroke,
              ),
              borderRadius: BorderRadius.circular(layout.panelRadius),
            ),
            child: Stack(
              children: <Widget>[
                Positioned.fill(
                  child: IgnorePointer(
                    child: CustomPaint(
                      key: const ValueKey<String>(
                        'controller-silhouette-painter',
                      ),
                      painter: _ControllerSilhouettePainter(
                        surface: colors.panelSurface,
                        border: colors.borderStrong,
                        hairline: colors.hairline,
                        borderWidth: layout.focusStroke,
                        hairlineWidth: layout.hairlineStroke,
                      ),
                    ),
                  ),
                ),
                _control(
                  context,
                  CanonicalGamepadControl.leftTrigger,
                  70,
                  14,
                  104,
                  38,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.rightTrigger,
                  586,
                  14,
                  104,
                  38,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.leftShoulder,
                  86,
                  58,
                  112,
                  38,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.rightShoulder,
                  562,
                  58,
                  112,
                  38,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.dpadUp,
                  105,
                  130,
                  64,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.dpadLeft,
                  42,
                  176,
                  64,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.dpadRight,
                  168,
                  176,
                  64,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.dpadDown,
                  105,
                  222,
                  64,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.select,
                  286,
                  124,
                  58,
                  36,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.guide,
                  351,
                  116,
                  58,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.start,
                  416,
                  124,
                  58,
                  36,
                ),
                _stickControl(context, left: true, leftPosition: 245, top: 218),
                _control(
                  context,
                  CanonicalGamepadControl.leftStickPress,
                  324,
                  222,
                  58,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.rightStickPress,
                  386,
                  222,
                  58,
                  44,
                ),
                _stickControl(
                  context,
                  left: false,
                  leftPosition: 452,
                  top: 218,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.faceNorth,
                  598,
                  126,
                  72,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.faceWest,
                  526,
                  174,
                  72,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.faceEast,
                  670,
                  174,
                  72,
                  44,
                ),
                _control(
                  context,
                  CanonicalGamepadControl.faceSouth,
                  598,
                  222,
                  72,
                  44,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _control(
    BuildContext context,
    CanonicalGamepadControl control,
    double left,
    double top,
    double width,
    double height,
  ) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final isSelected = selected == control;
    final isActive = active.contains(control);
    final isMapped = mapping.containsKey(control);

    return Positioned(
      left: left,
      top: top,
      width: width,
      height: height,
      child: Semantics(
        button: enabled,
        selected: isSelected,
        label:
            '${controlLabel(control)}. '
            '${isMapped ? 'Mapped' : 'Not mapped'}${isActive ? '. Active' : ''}',
        excludeSemantics: true,
        child: OutlinedButton(
          key: ValueKey<String>('control-${control.name}'),
          focusNode: focusNodes.putIfAbsent(control, FocusNode.new),
          onPressed: enabled ? () => onSelected(control) : null,
          style: OutlinedButton.styleFrom(
            padding: EdgeInsets.symmetric(horizontal: layout.xxs),
            backgroundColor: _controlFill(
              colors,
              layout,
              active: isActive,
              selected: isSelected,
            ),
            side: _controlSide(
              colors,
              layout,
              active: isActive,
              selected: isSelected,
            ),
          ),
          child: FittedBox(
            fit: BoxFit.scaleDown,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Icon(
                  isActive
                      ? Icons.bolt
                      : isMapped
                      ? Icons.check
                      : Icons.remove,
                  size: layout.iconSm,
                ),
                SizedBox(width: layout.xxs),
                Text(_shortLabel(control)),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _stickControl(
    BuildContext context, {
    required bool left,
    required double leftPosition,
    required double top,
  }) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final representative = left
        ? CanonicalGamepadControl.leftStickX
        : CanonicalGamepadControl.rightStickX;
    final controls = _stickControls(representative);
    final isSelected = selected == representative;
    final isActive = controls.any(active.contains);
    final mappedCount = controls.where(mapping.containsKey).length;
    final mappingState = switch (mappedCount) {
      0 => 'Not mapped',
      1 => 'Partially mapped',
      _ => 'Mapped',
    };
    final label = left ? 'Left stick' : 'Right stick';

    return Positioned(
      left: leftPosition,
      top: top,
      width: 76,
      height: 50,
      child: Semantics(
        button: enabled,
        selected: isSelected,
        label: '$label. $mappingState${isActive ? '. Active' : ''}',
        excludeSemantics: true,
        child: OutlinedButton(
          key: ValueKey<String>(
            left ? 'control-leftStick' : 'control-rightStick',
          ),
          focusNode: focusNodes.putIfAbsent(representative, FocusNode.new),
          onPressed: enabled ? () => onSelected(representative) : null,
          style: OutlinedButton.styleFrom(
            padding: EdgeInsets.symmetric(horizontal: layout.xxs),
            backgroundColor: _controlFill(
              colors,
              layout,
              active: isActive,
              selected: isSelected,
            ),
            side: _controlSide(
              colors,
              layout,
              active: isActive,
              selected: isSelected,
            ),
          ),
          child: FittedBox(
            fit: BoxFit.scaleDown,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Icon(
                  isActive
                      ? Icons.bolt
                      : mappedCount == 2
                      ? Icons.check
                      : Icons.remove,
                  size: layout.iconSm,
                ),
                Text(left ? 'LEFT STICK' : 'RIGHT STICK'),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Color _controlFill(
    ConsoleColors colors,
    ConsoleLayoutTheme layout, {
    required bool active,
    required bool selected,
  }) => active
      ? colors.connected.withValues(alpha: layout.tint.borderOpacity)
      : selected
      ? colors.focusFill
      : colors.controlRestFill;

  BorderSide _controlSide(
    ConsoleColors colors,
    ConsoleLayoutTheme layout, {
    required bool active,
    required bool selected,
  }) => BorderSide(
    width: active || selected ? layout.focusStroke : layout.hairlineStroke,
    color: active
        ? colors.connected
        : selected
        ? colors.focusBorder
        : colors.panelBorder,
  );
}

final class _ControllerSilhouettePainter extends CustomPainter {
  const _ControllerSilhouettePainter({
    required this.surface,
    required this.border,
    required this.hairline,
    required this.borderWidth,
    required this.hairlineWidth,
  });

  final Color surface;
  final Color border;
  final Color hairline;
  final double borderWidth;
  final double hairlineWidth;

  @override
  void paint(Canvas canvas, Size size) {
    final body = Path()
      ..moveTo(size.width * 0.19, size.height * 0.17)
      ..cubicTo(
        size.width * 0.12,
        size.height * 0.16,
        size.width * 0.08,
        size.height * 0.28,
        size.width * 0.07,
        size.height * 0.45,
      )
      ..lineTo(size.width * 0.035, size.height * 0.84)
      ..cubicTo(
        size.width * 0.02,
        size.height * 0.98,
        size.width * 0.10,
        size.height * 1.02,
        size.width * 0.16,
        size.height * 0.91,
      )
      ..lineTo(size.width * 0.235, size.height * 0.72)
      ..cubicTo(
        size.width * 0.27,
        size.height * 0.64,
        size.width * 0.31,
        size.height * 0.65,
        size.width * 0.35,
        size.height * 0.69,
      )
      ..cubicTo(
        size.width * 0.42,
        size.height * 0.76,
        size.width * 0.58,
        size.height * 0.76,
        size.width * 0.65,
        size.height * 0.69,
      )
      ..cubicTo(
        size.width * 0.69,
        size.height * 0.65,
        size.width * 0.73,
        size.height * 0.64,
        size.width * 0.765,
        size.height * 0.72,
      )
      ..lineTo(size.width * 0.84, size.height * 0.91)
      ..cubicTo(
        size.width * 0.90,
        size.height * 1.02,
        size.width * 0.98,
        size.height * 0.98,
        size.width * 0.965,
        size.height * 0.84,
      )
      ..lineTo(size.width * 0.93, size.height * 0.45)
      ..cubicTo(
        size.width * 0.92,
        size.height * 0.28,
        size.width * 0.88,
        size.height * 0.16,
        size.width * 0.81,
        size.height * 0.17,
      )
      ..cubicTo(
        size.width * 0.64,
        size.height * 0.08,
        size.width * 0.36,
        size.height * 0.08,
        size.width * 0.19,
        size.height * 0.17,
      )
      ..close();

    canvas.drawPath(
      body,
      Paint()
        ..style = PaintingStyle.fill
        ..color = surface,
    );
    canvas.drawPath(
      body,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = borderWidth
        ..color = border,
    );

    final centerLine = Path()
      ..moveTo(size.width * 0.33, size.height * 0.69)
      ..quadraticBezierTo(
        size.width * 0.5,
        size.height * 0.79,
        size.width * 0.67,
        size.height * 0.69,
      );
    canvas.drawPath(
      centerLine,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = hairlineWidth
        ..color = hairline,
    );
  }

  @override
  bool shouldRepaint(_ControllerSilhouettePainter oldDelegate) =>
      surface != oldDelegate.surface ||
      border != oldDelegate.border ||
      hairline != oldDelegate.hairline ||
      borderWidth != oldDelegate.borderWidth ||
      hairlineWidth != oldDelegate.hairlineWidth;
}

List<CanonicalGamepadControl> _stickControls(
  CanonicalGamepadControl representative,
) => representative == CanonicalGamepadControl.leftStickX
    ? const <CanonicalGamepadControl>[
        CanonicalGamepadControl.leftStickX,
        CanonicalGamepadControl.leftStickY,
      ]
    : const <CanonicalGamepadControl>[
        CanonicalGamepadControl.rightStickX,
        CanonicalGamepadControl.rightStickY,
      ];

String _shortLabel(CanonicalGamepadControl control) => switch (control) {
  CanonicalGamepadControl.faceSouth => 'SOUTH',
  CanonicalGamepadControl.faceEast => 'EAST',
  CanonicalGamepadControl.faceWest => 'WEST',
  CanonicalGamepadControl.faceNorth => 'NORTH',
  CanonicalGamepadControl.dpadUp => 'UP',
  CanonicalGamepadControl.dpadDown => 'DOWN',
  CanonicalGamepadControl.dpadLeft => 'LEFT',
  CanonicalGamepadControl.dpadRight => 'RIGHT',
  CanonicalGamepadControl.leftShoulder => 'L SHOULDER',
  CanonicalGamepadControl.rightShoulder => 'R SHOULDER',
  CanonicalGamepadControl.leftTrigger => 'L TRIGGER',
  CanonicalGamepadControl.rightTrigger => 'R TRIGGER',
  CanonicalGamepadControl.select => 'BACK',
  CanonicalGamepadControl.start => 'START',
  CanonicalGamepadControl.guide => 'HOME',
  CanonicalGamepadControl.leftStickPress => 'L STICK',
  CanonicalGamepadControl.rightStickPress => 'R STICK',
  CanonicalGamepadControl.leftStickX ||
  CanonicalGamepadControl.leftStickY => 'LEFT STICK',
  CanonicalGamepadControl.rightStickX ||
  CanonicalGamepadControl.rightStickY => 'RIGHT STICK',
};
