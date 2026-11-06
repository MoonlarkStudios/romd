import 'package:flutter/material.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';

import '../theme/console_theme_context.dart';
import 'controller_display_names.dart';

/// Quiet, non-seat presentation for recognized controllers that have not
/// joined. Every item uses a generic controller icon; no family is inferred.
final class AvailableControllersPanel extends StatelessWidget {
  const AvailableControllersPanel({
    required this.controllers,
    this.attentionControllers = const <ConnectedGamepad>[],
    this.showControllerNames = false,
    super.key,
  });

  final List<ConnectedGamepad> controllers;
  final List<ConnectedGamepad> attentionControllers;
  final bool showControllerNames;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final names = controllerDisplayNamesById(<ConnectedGamepad>[
      ...controllers,
      ...attentionControllers,
    ]);
    if (!showControllerNames) {
      final count = controllers.length;
      return Semantics(
        container: true,
        excludeSemantics: true,
        label:
            '$count ${count == 1 ? 'controller is' : 'controllers are'} ready. Hold L plus R to join.',
        child: SizedBox(
          height: layout.controllerBar.height,
          child: Row(
            children: <Widget>[
              for (var index = 0; index < count; index++) ...<Widget>[
                if (index > 0) SizedBox(width: layout.xs),
                SizedBox(
                  width: layout.lg,
                  child: Icon(
                    Icons.sports_esports_outlined,
                    size: layout.iconMd,
                    color: colors.textFaint,
                  ),
                ),
              ],
              SizedBox(width: layout.sm),
              Flexible(
                child: Text(
                  '${count == 1 ? 'Controller' : 'Controllers'} ready — Hold L + R to join',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: context.text.metadata,
                ),
              ),
            ],
          ),
        ),
      );
    }
    return Semantics(
      container: true,
      excludeSemantics: true,
      label: <String>[
        if (controllers.isNotEmpty)
          '${controllers.length} available ${controllers.length == 1 ? 'controller' : 'controllers'}: '
              '${controllers.map((controller) => names[controller.id] ?? 'Controller').join(', ')}.',
        if (attentionControllers.isNotEmpty)
          '${attentionControllers.length} connected ${attentionControllers.length == 1 ? 'controller needs' : 'controllers need'} identity attention: '
              '${attentionControllers.map((controller) => names[controller.id] ?? 'Controller').join(', ')}.',
      ].join(' '),
      child: Wrap(
        spacing: layout.md,
        runSpacing: layout.xs,
        children: <Widget>[
          for (final controller in controllers)
            _DiagnosticController(name: names[controller.id] ?? 'Controller'),
          for (final controller in attentionControllers)
            _DiagnosticController(
              name: names[controller.id] ?? 'Controller',
              needsAttention: true,
            ),
        ],
      ),
    );
  }
}

final class _DiagnosticController extends StatelessWidget {
  const _DiagnosticController({
    required this.name,
    this.needsAttention = false,
  });

  final String name;
  final bool needsAttention;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        Icon(
          Icons.sports_esports_outlined,
          size: layout.progressIndicatorSm,
          color: needsAttention ? colors.warning : colors.textFaint,
        ),
        SizedBox(width: layout.xs),
        ConstrainedBox(
          constraints: BoxConstraints(
            maxWidth: layout.controllerBar.nameMaxWidth,
          ),
          child: Text(
            needsAttention ? '$name · Check identity' : name,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: context.text.metadata.copyWith(
              color: needsAttention ? colors.warning : null,
            ),
          ),
        ),
      ],
    );
  }
}
