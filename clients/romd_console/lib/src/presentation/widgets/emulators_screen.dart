import 'package:flutter/material.dart';
import 'package:romd_console/src/play/session/domain/emulator_settings_launcher.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';

import '../theme/console_theme_context.dart';
import 'console_hint_bar.dart';
import 'console_settings.dart';

/// Settings → Emulation: advanced entry points into managed emulator UIs.
/// Runtime selection remains game-specific and is not configured here.
final class EmulatorsScreen extends StatefulWidget {
  const EmulatorsScreen({required this.launcher, this.clockNow, super.key});

  final EmulatorSettingsLauncher? launcher;
  final DateTime Function()? clockNow;

  @override
  State<EmulatorsScreen> createState() => _EmulatorsScreenState();
}

final class _EmulatorsScreenState extends State<EmulatorsScreen> {
  final FocusNode _dolphinFocus = FocusNode(
    debugLabel: 'open-dolphin-settings',
  );
  bool _busy = false;
  String? _status;
  bool _statusIsError = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _dolphinFocus.requestFocus();
    });
  }

  @override
  void dispose() {
    _dolphinFocus.dispose();
    super.dispose();
  }

  void _back() {
    if (!_busy) Navigator.of(context).maybePop();
  }

  Future<void> _openDolphin() async {
    final launcher = widget.launcher;
    if (launcher == null || _busy) return;
    setState(() {
      _busy = true;
      _statusIsError = false;
      _status = 'Preparing or running Dolphin settings…';
    });
    final result = await launcher.openDolphin();
    if (!mounted) return;
    setState(() {
      _busy = false;
      switch (result) {
        case LaunchExited(exitCode: 0):
          _status = 'Dolphin settings closed.';
          _statusIsError = false;
        case LaunchExited():
          _status = 'Dolphin settings closed unexpectedly.';
          _statusIsError = true;
        case LaunchFailed(:final message):
          _status = message;
          _statusIsError = true;
        case LaunchRuntimeMissing(:final runtimeName):
          _status = '${runtimeName ?? 'Dolphin'} is not available.';
          _statusIsError = true;
        case LaunchUnsupportedPlatform() ||
            LaunchControllerReviewRequired() ||
            LaunchAccessNotGranted() ||
            LaunchAccessRevoked():
          _status = 'Dolphin settings are not available on this device.';
          _statusIsError = true;
      }
    });
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _dolphinFocus.requestFocus();
    });
  }

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final enabled = widget.launcher != null && !_busy;
    return ConsoleSettingsShell(
      eyebrow: 'Settings',
      title: 'Emulation',
      description:
          'Ottercade picks a compatible runtime for each game. Open an '
          'emulator here only when you need its advanced native settings.',
      onBack: _back,
      clockNow: widget.clockNow,
      hints: <ConsoleHint>[
        const ConsoleHint(
          glyph: ConsoleHintGlyphs.confirm,
          gamepadGlyph: 'A',
          label: 'Open',
        ),
        ConsoleHint(
          glyph: 'Esc',
          gamepadGlyph: 'B',
          label: _busy ? 'Wait for Dolphin' : 'Back',
        ),
      ],
      child: ConsoleSettingsSection(
        title: 'Advanced emulator settings',
        children: <Widget>[
          ConsoleSettingsActionRow(
            key: const ValueKey<String>('open-dolphin-settings-row'),
            icon: Icons.sports_esports,
            title: _busy ? 'Dolphin settings are running' : 'Dolphin settings',
            subtitle:
                'Advanced settings for GameCube and Wii games. Opens in its '
                'own window; keyboard or mouse may be required.',
            value: 'GameCube · Wii',
            focusNode: _dolphinFocus,
            autofocus: true,
            enabled: enabled,
            semanticLabel: _busy
                ? 'Dolphin settings are running'
                : 'Open Dolphin settings',
            onPressed: _openDolphin,
          ),
          if (widget.launcher == null) ...<Widget>[
            SizedBox(height: layout.sm),
            Text(
              'Advanced Dolphin settings are unavailable in this build.',
              style: context.text.metadata.copyWith(color: colors.textMuted),
            ),
          ],
          if (_status case final status?) ...<Widget>[
            SizedBox(height: layout.md),
            Semantics(
              liveRegion: true,
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Icon(
                    _statusIsError
                        ? Icons.error_outline
                        : _busy
                        ? Icons.hourglass_top
                        : Icons.check_circle_outline,
                    color: _statusIsError ? colors.warning : colors.connected,
                  ),
                  SizedBox(width: layout.sm),
                  Expanded(
                    child: Text(
                      status,
                      style: context.text.metadata.copyWith(
                        color: _statusIsError
                            ? colors.warning
                            : colors.textMuted,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}
