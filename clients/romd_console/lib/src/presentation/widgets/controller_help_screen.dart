import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

import '../glyph_family_scope.dart';
import '../theme/console_theme_context.dart';
import 'console_hint_bar.dart';

/// Full-screen reference for the in-game shortcuts ROMD generates into
/// RetroArch's session config (`SELECT` is the hotkey modifier). Pushed from the
/// game detail screen via `C`. Controller-navigable — Esc / B closes it.
///
/// This exists because the in-game menu is RetroArch's own (a ROMD-rendered
/// overlay can't sit over RetroArch on macOS); ROMD makes the combos
/// discoverable here, out of game.
final class ControllerHelpScreen extends StatelessWidget {
  const ControllerHelpScreen({super.key});

  /// The chord table, labeled for the player's glyph family. The screenshot
  /// chord is bound to the *north* face button — printed Y on Xbox pads, X on
  /// Nintendo-style pads, △ on PlayStation — so its label is resolved from
  /// the Xbox token 'Y' rather than hardcoded.
  static List<({String combo, String action, String note})> _scheme(
    GlyphFamily family,
  ) => <({String combo, String action, String note})>[
    (
      combo: 'SELECT + START',
      action: 'Menu',
      note: 'Open the in-game menu — reset, quit, and more',
    ),
    (
      combo: 'SELECT + R',
      action: 'Save State',
      note: 'Snapshot your exact spot',
    ),
    (
      combo: 'SELECT + L',
      action: 'Load State',
      note: 'Jump back to the last snapshot',
    ),
    (
      combo: 'SELECT + ← →',
      action: 'State Slot',
      note: 'Choose which save-state slot',
    ),
    (
      combo: 'SELECT + ${faceButtonGlyph('Y', family)}',
      action: 'Screenshot',
      note: 'Capture the screen',
    ),
  ];

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Shortcuts(
      shortcuts: const <ShortcutActivator, Intent>{
        SingleActivator(LogicalKeyboardKey.keyB): DismissIntent(),
      },
      child: Actions(
        actions: <Type, Action<Intent>>{
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              Navigator.of(context).maybePop();
              return null;
            },
          ),
        },
        child: Focus(
          autofocus: true,
          child: Scaffold(
            backgroundColor: context.theme.scaffoldBackgroundColor,
            body: SafeArea(
              child: Column(
                children: <Widget>[
                  Expanded(
                    child: SingleChildScrollView(
                      padding: EdgeInsets.fromLTRB(
                        layout.screenGutter,
                        layout.xxl,
                        layout.screenGutter,
                        layout.xs,
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: <Widget>[
                          Text(
                            'IN-GAME',
                            style: context.text.eyebrow.copyWith(
                              color: colors.textFaint,
                            ),
                          ),
                          SizedBox(height: layout.xs),
                          Text(
                            'In-game shortcuts',
                            style: context.text.pageHeading.copyWith(
                              color: colors.textStrong,
                            ),
                          ),
                          SizedBox(height: layout.xs),
                          Text(
                            'Hold SELECT, then press:',
                            style: context.text.bodyCompact.copyWith(
                              color: colors.textMuted,
                            ),
                          ),
                          SizedBox(height: layout.shortcutHelp.sectionGap),
                          for (final row in _scheme(
                            GlyphFamilyScope.of(context),
                          ))
                            _ComboRow(
                              combo: row.combo,
                              action: row.action,
                              note: row.note,
                            ),
                        ],
                      ),
                    ),
                  ),
                  const ConsoleFooterBar(
                    context: 'IN-GAME SHORTCUTS',
                    hints: <ConsoleHint>[
                      ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: 'B',
                        label: 'Back',
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

final class _ComboRow extends StatelessWidget {
  const _ComboRow({
    required this.combo,
    required this.action,
    required this.note,
  });

  final String combo;
  final String action;
  final String note;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Padding(
      padding: layout.shortcutHelp.rowPadding,
      child: Row(
        children: <Widget>[
          Container(
            width: layout.shortcutHelp.keycapWidth,
            padding: layout.shortcutHelp.keycapPadding,
            decoration: BoxDecoration(
              color: colors.keycapSurface,
              borderRadius: BorderRadius.circular(layout.controlRadius),
              border: Border.all(
                color: colors.keycapBorder,
                width: layout.hairlineStroke,
              ),
            ),
            child: Text(
              combo,
              style: context.text.metadataStrong.copyWith(
                color: colors.keycapForeground,
              ),
            ),
          ),
          SizedBox(width: layout.lg),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Text(
                  action,
                  style: context.text.action.copyWith(color: colors.textStrong),
                ),
                SizedBox(height: layout.hairlineStroke * 2),
                Text(
                  note,
                  style: context.text.bodyCompact.copyWith(
                    color: colors.textFaint,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
