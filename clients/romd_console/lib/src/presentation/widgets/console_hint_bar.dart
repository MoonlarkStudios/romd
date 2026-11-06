import 'package:flutter/material.dart';

import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import '../../input/console_input_mode.dart';
import '../glyph_family_scope.dart';
import '../theme/console_theme_context.dart';

/// Canonical keyboard keycap glyphs. Every character here is verified to
/// exist in the shipped IBM Plex Mono face, so keycaps render deterministically
/// on every platform instead of showing tofu or a per-host fallback font.
/// (The face has no ↵, ⏎, ◂, or ▸ — use these tokens, not raw arrows.)
abstract final class ConsoleHintGlyphs {
  /// Full directional navigation.
  static const String navigate = '↕ ↔';

  /// Vertical-only navigation.
  static const String navigateVertical = '↕';

  /// Horizontal-only navigation (previous / next).
  static const String navigateHorizontal = '← →';

  /// Confirm / activate, worded like the physical key — consistent with
  /// `Esc`, `PgDn`, and the other named keycaps.
  static const String confirm = 'Enter';
}

/// A single key/action hint, e.g. (`Enter`, "Open").
final class ConsoleHint {
  const ConsoleHint({
    required this.glyph,
    required this.label,
    String? gamepadGlyph,
    this.reserveLabel,
    this.onPressed,
  }) : keyboardGlyph = glyph,
       gamepadGlyph = gamepadGlyph ?? glyph;

  const ConsoleHint.adaptive({
    required this.keyboardGlyph,
    required this.gamepadGlyph,
    required this.label,
    this.reserveLabel,
    this.onPressed,
  }) : glyph = keyboardGlyph;

  final String glyph;
  final String keyboardGlyph;
  final String gamepadGlyph;
  final String label;

  /// When set, the label sits in a fixed-width slot sized for this (widest)
  /// string and crossfades in place whenever [label] changes — so a contextual
  /// verb can swap without nudging the surrounding keycaps. Null = natural
  /// width, no animation (the common, static case).
  final String? reserveLabel;

  /// Optional pointer affordance: the hint becomes tappable without joining
  /// focus traversal, so mouse users can act on chrome the controller reaches
  /// through its physical button.
  final VoidCallback? onPressed;
}

/// A row of keycap-style hints reminding the user how to navigate. Controller
/// first, then keyboard, then pointer — the glyphs describe those inputs.
final class ConsoleHintBar extends StatelessWidget {
  const ConsoleHintBar({required this.hints, this.muted = false, super.key});

  final List<ConsoleHint> hints;

  /// Welcome-register tone: a fainter label so the row whispers rather than
  /// labels. Keycaps are unchanged — they anchor the shared vocabulary across
  /// the in-app footer and the threshold screens (profile select, login).
  final bool muted;

  @override
  Widget build(BuildContext context) {
    final mode = ConsoleInputModeScope.of(context);
    final family = GlyphFamilyScope.of(context);
    final layout = context.layout;
    return Wrap(
      spacing: layout.hints.itemGap,
      runSpacing: layout.xs,
      alignment: WrapAlignment.center,
      children: <Widget>[
        for (final hint in hints)
          _HintItem(
            hint: hint,
            muted: muted,
            glyph: _glyphFor(hint, mode, family),
            gamepad: mode == ConsoleInputMode.gamepad,
          ),
      ],
    );
  }

  // Hints are authored as Xbox-position tokens; the keycap renders the label
  // physically printed at that position for the active controller family.
  String _glyphFor(
    ConsoleHint hint,
    ConsoleInputMode mode,
    GlyphFamily family,
  ) => mode == ConsoleInputMode.gamepad
      ? faceButtonGlyph(hint.gamepadGlyph, family)
      : hint.keyboardGlyph;
}

final class _HintItem extends StatelessWidget {
  const _HintItem({
    required this.hint,
    required this.muted,
    required this.glyph,
    required this.gamepad,
  });

  final ConsoleHint hint;
  final bool muted;
  final String glyph;
  final bool gamepad;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final row = Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        _Keycap(glyph: glyph, gamepad: gamepad),
        SizedBox(width: layout.xs),
        _HintLabel(hint: hint, muted: muted),
      ],
    );
    final onPressed = hint.onPressed;
    if (onPressed == null) return row;
    return Semantics(
      button: true,
      label: hint.label,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: onPressed,
          child: row,
        ),
      ),
    );
  }
}

final class _HintLabel extends StatelessWidget {
  const _HintLabel({required this.hint, required this.muted});

  final ConsoleHint hint;
  final bool muted;

  @override
  Widget build(BuildContext context) {
    final style = context.text.utilityLabel.copyWith(
      color: muted
          ? context.consoleColors.textFaint
          : context.consoleColors.hintText,
    );

    final reserve = hint.reserveLabel;
    if (reserve == null) {
      return Text(hint.label, style: style);
    }

    // Pin the slot to the widest label's true width so neighbouring keycaps
    // never shift; the live label then crossfades in place within it.
    final painter = TextPainter(
      text: TextSpan(text: reserve, style: style),
      textDirection: TextDirection.ltr,
      textScaler: MediaQuery.textScalerOf(context),
    )..layout();

    return SizedBox(
      width: painter.width,
      child: Align(
        alignment: Alignment.centerLeft,
        child: AnimatedSwitcher(
          duration: context.motion.resolve(
            context,
            context.motion.hintLabelTransition,
          ),
          child: Text(
            hint.label,
            key: ValueKey<String>(hint.label),
            style: style,
          ),
        ),
      ),
    );
  }
}

/// The reserved hint row that sits at the bottom of a screen column — never an
/// overlay on the content above it. Pairs an optional left-aligned mono context
/// caption with right-aligned keycap hints; with no caption the hints center.
final class ConsoleFooterBar extends StatelessWidget {
  const ConsoleFooterBar({
    required this.hints,
    this.context,
    this.leading,
    this.horizontalPadding,
    super.key,
  });

  final List<ConsoleHint> hints;
  final String? context;
  final Widget? leading;
  final double? horizontalPadding;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final hintBar = ConsoleHintBar(hints: hints);

    return DecoratedBox(
      decoration: BoxDecoration(
        color: colors.footerSurface,
        border: Border(
          top: BorderSide(color: colors.hairline, width: layout.hairlineStroke),
        ),
      ),
      child: Padding(
        padding: EdgeInsets.symmetric(
          horizontal: horizontalPadding ?? layout.screenGutter,
          vertical: layout.sm,
        ),
        child: ConstrainedBox(
          constraints: BoxConstraints(minHeight: layout.hints.footerMinHeight),
          child: LayoutBuilder(
            builder: (context, constraints) {
              final caption = this.context;
              final leading = this.leading;
              if (caption == null && leading == null) {
                return Center(child: hintBar);
              }
              final captionText = caption == null
                  ? null
                  : Text(
                      caption,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: context.text.utilityLabel.copyWith(
                        color: colors.textDisabled,
                      ),
                    );
              final contextWidget = leading ?? captionText!;
              final textScale = MediaQuery.textScalerOf(context).scale(1);
              final stackBreakpoint = leading == null
                  ? layout.hints.stackBreakpoint
                  : layout.hints.leadingStackBreakpoint;
              if (constraints.maxWidth < stackBreakpoint ||
                  textScale > layout.hints.stackTextScaleThreshold) {
                return Column(
                  mainAxisSize: MainAxisSize.min,
                  children: <Widget>[
                    contextWidget,
                    SizedBox(height: layout.xs),
                    hintBar,
                  ],
                );
              }
              if (leading != null) {
                return Row(
                  children: <Widget>[
                    contextWidget,
                    SizedBox(width: layout.lg),
                    Expanded(
                      child: Align(
                        alignment: Alignment.centerRight,
                        child: hintBar,
                      ),
                    ),
                  ],
                );
              }
              return Row(
                children: <Widget>[
                  Expanded(child: contextWidget),
                  SizedBox(width: layout.md),
                  Flexible(child: hintBar),
                ],
              );
            },
          ),
        ),
      ),
    );
  }
}

final class _Keycap extends StatelessWidget {
  const _Keycap({required this.glyph, required this.gamepad});

  final String glyph;
  final bool gamepad;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final isFaceButton = gamepad && glyph.length == 1;
    return Container(
      constraints: BoxConstraints(
        minWidth: isFaceButton
            ? layout.hints.keycapDiameter
            : layout.hints.keycapMinWidth,
        minHeight: isFaceButton ? layout.hints.keycapDiameter : 0,
      ),
      padding: EdgeInsets.symmetric(
        horizontal: isFaceButton ? 0 : layout.xs,
        vertical: layout.xxs,
      ),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(
          isFaceButton
              ? layout.hints.gamepadKeycapRadius
              : layout.hints.keycapRadius,
        ),
        color: colors.hintKeycapSurface,
        border: Border.all(
          color: colors.borderStrong,
          width: layout.hairlineStroke,
        ),
      ),
      child: Text(
        glyph,
        textAlign: TextAlign.center,
        style: context.text.metadata.copyWith(
          height: layout.hints.keycapTextHeight,
          color: colors.iconStrong,
        ),
      ),
    );
  }
}
