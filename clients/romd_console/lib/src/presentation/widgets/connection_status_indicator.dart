import 'package:flutter/material.dart';

import '../../domain/consumer_host_health.dart';
import '../theme/console_theme_context.dart';

/// Coarse connection state for the home status indicator. Distinguishes a
/// purely local profile (no server attached) from one whose server is simply
/// unreachable, so the launcher never implies a connection is required.
enum ConnectionStatus { local, offline, connecting, connected, unavailable }

/// Quiet top-corner connection readout: a status dot plus a short mono label.
/// Display-only — connecting is an explicit act that lives in Settings and in
/// the empty-state panels, never behind ambient chrome.
final class ConnectionStatusIndicator extends StatelessWidget {
  const ConnectionStatusIndicator({
    required this.status,
    this.health,
    this.serverHost,
    super.key,
  });

  final ConnectionStatus status;
  final ConsumerHostHealth? health;
  final String? serverHost;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final color = switch (status) {
      ConnectionStatus.connected => colors.connected,
      ConnectionStatus.connecting => colors.catalogAccent,
      ConnectionStatus.offline ||
      ConnectionStatus.unavailable => colors.warning,
      ConnectionStatus.local => colors.textFaint,
    };
    final label = switch (status) {
      ConnectionStatus.connected => 'Connected',
      ConnectionStatus.connecting => 'Connecting…',
      ConnectionStatus.offline => 'Offline',
      ConnectionStatus.unavailable => 'Server unavailable',
      ConnectionStatus.local => 'No server',
    };
    return Semantics(
      // A distinct node even when embedded in the Connect affordance, and the
      // label fully replaces the child text so the honest long-form status is
      // what assistive tech reads.
      container: true,
      excludeSemantics: true,
      label: switch (status) {
        ConnectionStatus.offline => 'Offline — saved access applies',
        _ => label,
      },
      child: Tooltip(
        message: <String>[
          health?.reason ?? label,
          if (serverHost case final host?) host,
        ].join(' · '),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            Container(
              key: ValueKey<String>('connection-status-${status.name}'),
              width: layout.status.dotSize,
              height: layout.status.dotSize,
              decoration: BoxDecoration(shape: BoxShape.circle, color: color),
            ),
            SizedBox(width: layout.xs),
            ConstrainedBox(
              constraints: BoxConstraints(
                maxWidth: layout.status.labelMaxWidth,
              ),
              child: Text(
                label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: context.text.metadataStrong.copyWith(color: color),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
