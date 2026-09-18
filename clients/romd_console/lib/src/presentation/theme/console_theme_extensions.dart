import 'dart:ui' as ui show lerpDouble;

import 'package:flutter/material.dart';

double _lerpDouble(double a, double b, double t) => ui.lerpDouble(a, b, t) ?? b;

Duration _lerpDuration(Duration a, Duration b, double t) => Duration(
  microseconds: _lerpDouble(
    a.inMicroseconds.toDouble(),
    b.inMicroseconds.toDouble(),
    t,
  ).round(),
);

bool _unitInterval(double value) => value >= 0 && value <= 1;

bool _orderedStops(List<double> stops, int length) {
  if (stops.length != length || stops.any((value) => !_unitInterval(value))) {
    return false;
  }
  for (var index = 1; index < stops.length; index++) {
    if (stops[index] < stops[index - 1]) return false;
  }
  return true;
}

/// Console-specific color roles that do not have a precise [ColorScheme] home.
@immutable
final class ConsoleColors extends ThemeExtension<ConsoleColors> {
  const ConsoleColors({
    required this.focusBorder,
    required this.focusFill,
    required this.focusGlow,
    required this.selectionFill,
    required this.selectionBorder,
    required this.controlRestFill,
    required this.connected,
    required this.warning,
    required this.catalogAccent,
    required this.iconStrong,
    required this.textStrong,
    required this.textBody,
    required this.textMuted,
    required this.textFaint,
    required this.textDisabled,
    required this.panelSurface,
    required this.dialogSurface,
    required this.panelBorder,
    required this.hairline,
    required this.borderStrong,
    required this.scrim,
    required this.onMediaOverlay,
    required this.mediaBackdrop,
    required this.mediaChipSurface,
    required this.onMediaAccent,
    required this.keycapSurface,
    required this.keycapBorder,
    required this.keycapForeground,
    required this.hintText,
    required this.hintKeycapSurface,
    required this.footerSurface,
    required this.onWarning,
  });

  final Color focusBorder;
  final Color focusFill;
  final Color focusGlow;
  final Color selectionFill;
  final Color selectionBorder;
  final Color controlRestFill;
  final Color connected;
  final Color warning;
  final Color catalogAccent;
  final Color iconStrong;
  final Color textStrong;
  final Color textBody;
  final Color textMuted;
  final Color textFaint;
  final Color textDisabled;
  final Color panelSurface;
  final Color dialogSurface;
  final Color panelBorder;
  final Color hairline;
  final Color borderStrong;
  final Color scrim;
  final Color onMediaOverlay;
  final Color mediaBackdrop;

  /// Scrim pill behind chrome that floats directly on artwork. Media is
  /// dark-backed in every skin, so this stays scrim-dark regardless of
  /// brightness and must keep [onMediaAccent] readable over any art.
  final Color mediaChipSurface;

  /// Catalog-accent foreground for chrome floating on artwork. Paired with
  /// [mediaChipSurface], not with the skin's on-surface [catalogAccent].
  final Color onMediaAccent;
  final Color keycapSurface;
  final Color keycapBorder;
  final Color keycapForeground;
  final Color hintText;
  final Color hintKeycapSurface;
  final Color footerSurface;
  final Color onWarning;

  @override
  ConsoleColors copyWith({
    Color? focusBorder,
    Color? focusFill,
    Color? focusGlow,
    Color? selectionFill,
    Color? selectionBorder,
    Color? controlRestFill,
    Color? connected,
    Color? warning,
    Color? catalogAccent,
    Color? iconStrong,
    Color? textStrong,
    Color? textBody,
    Color? textMuted,
    Color? textFaint,
    Color? textDisabled,
    Color? panelSurface,
    Color? dialogSurface,
    Color? panelBorder,
    Color? hairline,
    Color? borderStrong,
    Color? scrim,
    Color? onMediaOverlay,
    Color? mediaBackdrop,
    Color? mediaChipSurface,
    Color? onMediaAccent,
    Color? keycapSurface,
    Color? keycapBorder,
    Color? keycapForeground,
    Color? hintText,
    Color? hintKeycapSurface,
    Color? footerSurface,
    Color? onWarning,
  }) => ConsoleColors(
    focusBorder: focusBorder ?? this.focusBorder,
    focusFill: focusFill ?? this.focusFill,
    focusGlow: focusGlow ?? this.focusGlow,
    selectionFill: selectionFill ?? this.selectionFill,
    selectionBorder: selectionBorder ?? this.selectionBorder,
    controlRestFill: controlRestFill ?? this.controlRestFill,
    connected: connected ?? this.connected,
    warning: warning ?? this.warning,
    catalogAccent: catalogAccent ?? this.catalogAccent,
    iconStrong: iconStrong ?? this.iconStrong,
    textStrong: textStrong ?? this.textStrong,
    textBody: textBody ?? this.textBody,
    textMuted: textMuted ?? this.textMuted,
    textFaint: textFaint ?? this.textFaint,
    textDisabled: textDisabled ?? this.textDisabled,
    panelSurface: panelSurface ?? this.panelSurface,
    dialogSurface: dialogSurface ?? this.dialogSurface,
    panelBorder: panelBorder ?? this.panelBorder,
    hairline: hairline ?? this.hairline,
    borderStrong: borderStrong ?? this.borderStrong,
    scrim: scrim ?? this.scrim,
    onMediaOverlay: onMediaOverlay ?? this.onMediaOverlay,
    mediaBackdrop: mediaBackdrop ?? this.mediaBackdrop,
    mediaChipSurface: mediaChipSurface ?? this.mediaChipSurface,
    onMediaAccent: onMediaAccent ?? this.onMediaAccent,
    keycapSurface: keycapSurface ?? this.keycapSurface,
    keycapBorder: keycapBorder ?? this.keycapBorder,
    keycapForeground: keycapForeground ?? this.keycapForeground,
    hintText: hintText ?? this.hintText,
    hintKeycapSurface: hintKeycapSurface ?? this.hintKeycapSurface,
    footerSurface: footerSurface ?? this.footerSurface,
    onWarning: onWarning ?? this.onWarning,
  );

  @override
  ConsoleColors lerp(ThemeExtension<ConsoleColors>? other, double t) {
    if (other is! ConsoleColors) return this;
    return ConsoleColors(
      focusBorder: Color.lerp(focusBorder, other.focusBorder, t)!,
      focusFill: Color.lerp(focusFill, other.focusFill, t)!,
      focusGlow: Color.lerp(focusGlow, other.focusGlow, t)!,
      selectionFill: Color.lerp(selectionFill, other.selectionFill, t)!,
      selectionBorder: Color.lerp(selectionBorder, other.selectionBorder, t)!,
      controlRestFill: Color.lerp(controlRestFill, other.controlRestFill, t)!,
      connected: Color.lerp(connected, other.connected, t)!,
      warning: Color.lerp(warning, other.warning, t)!,
      catalogAccent: Color.lerp(catalogAccent, other.catalogAccent, t)!,
      iconStrong: Color.lerp(iconStrong, other.iconStrong, t)!,
      textStrong: Color.lerp(textStrong, other.textStrong, t)!,
      textBody: Color.lerp(textBody, other.textBody, t)!,
      textMuted: Color.lerp(textMuted, other.textMuted, t)!,
      textFaint: Color.lerp(textFaint, other.textFaint, t)!,
      textDisabled: Color.lerp(textDisabled, other.textDisabled, t)!,
      panelSurface: Color.lerp(panelSurface, other.panelSurface, t)!,
      dialogSurface: Color.lerp(dialogSurface, other.dialogSurface, t)!,
      panelBorder: Color.lerp(panelBorder, other.panelBorder, t)!,
      hairline: Color.lerp(hairline, other.hairline, t)!,
      borderStrong: Color.lerp(borderStrong, other.borderStrong, t)!,
      scrim: Color.lerp(scrim, other.scrim, t)!,
      onMediaOverlay: Color.lerp(onMediaOverlay, other.onMediaOverlay, t)!,
      mediaBackdrop: Color.lerp(mediaBackdrop, other.mediaBackdrop, t)!,
      mediaChipSurface: Color.lerp(
        mediaChipSurface,
        other.mediaChipSurface,
        t,
      )!,
      onMediaAccent: Color.lerp(onMediaAccent, other.onMediaAccent, t)!,
      keycapSurface: Color.lerp(keycapSurface, other.keycapSurface, t)!,
      keycapBorder: Color.lerp(keycapBorder, other.keycapBorder, t)!,
      keycapForeground: Color.lerp(
        keycapForeground,
        other.keycapForeground,
        t,
      )!,
      hintText: Color.lerp(hintText, other.hintText, t)!,
      hintKeycapSurface: Color.lerp(
        hintKeycapSurface,
        other.hintKeycapSurface,
        t,
      )!,
      footerSurface: Color.lerp(footerSurface, other.footerSurface, t)!,
      onWarning: Color.lerp(onWarning, other.onWarning, t)!,
    );
  }
}

/// Metrics owned by the reusable bottom navigation dock component.
@immutable
final class ConsoleNavigationDockMetrics {
  const ConsoleNavigationDockMetrics({
    required this.itemExtent,
    required this.buttonSize,
    required this.iconSize,
    required this.buttonShape,
    required this.buttonRadius,
    required this.labelLaneHeight,
    required this.labelMaxWidth,
  });

  final double itemExtent;
  final double buttonSize;
  final double iconSize;
  final BoxShape buttonShape;
  final double buttonRadius;
  final double labelLaneHeight;
  final double labelMaxWidth;

  ConsoleNavigationDockMetrics lerp(
    ConsoleNavigationDockMetrics other,
    double t,
  ) => ConsoleNavigationDockMetrics(
    itemExtent: _lerpDouble(itemExtent, other.itemExtent, t),
    buttonSize: _lerpDouble(buttonSize, other.buttonSize, t),
    iconSize: _lerpDouble(iconSize, other.iconSize, t),
    buttonShape: t < 0.5 ? buttonShape : other.buttonShape,
    buttonRadius: _lerpDouble(buttonRadius, other.buttonRadius, t),
    labelLaneHeight: _lerpDouble(labelLaneHeight, other.labelLaneHeight, t),
    labelMaxWidth: _lerpDouble(labelMaxWidth, other.labelMaxWidth, t),
  );
}

/// Metrics owned by reusable horizontally scrolling content rails.
@immutable
final class ConsoleContentRailMetrics {
  const ConsoleContentRailMetrics({
    required this.tileWidth,
    required this.tileGap,
    required this.ringInset,
    required this.labelEdgeInset,
    required this.messageMinHeight,
    required this.skeletonOpacity,
    required this.fadeStart,
  });

  final double tileWidth;
  final double tileGap;
  final double ringInset;
  final double labelEdgeInset;
  final double messageMinHeight;
  final double skeletonOpacity;
  final double fadeStart;

  ConsoleContentRailMetrics lerp(ConsoleContentRailMetrics other, double t) =>
      ConsoleContentRailMetrics(
        tileWidth: _lerpDouble(tileWidth, other.tileWidth, t),
        tileGap: _lerpDouble(tileGap, other.tileGap, t),
        ringInset: _lerpDouble(ringInset, other.ringInset, t),
        labelEdgeInset: _lerpDouble(labelEdgeInset, other.labelEdgeInset, t),
        messageMinHeight: _lerpDouble(
          messageMinHeight,
          other.messageMinHeight,
          t,
        ),
        skeletonOpacity: _lerpDouble(skeletonOpacity, other.skeletonOpacity, t),
        fadeStart: _lerpDouble(fadeStart, other.fadeStart, t),
      );
}

/// Reusable width-and-gap contract for horizontally scrolling tile strips.
@immutable
final class ConsoleTileStripMetrics {
  const ConsoleTileStripMetrics({
    required this.tileWidth,
    required this.tileGap,
  });

  final double tileWidth;
  final double tileGap;

  ConsoleTileStripMetrics lerp(ConsoleTileStripMetrics other, double t) =>
      ConsoleTileStripMetrics(
        tileWidth: _lerpDouble(tileWidth, other.tileWidth, t),
        tileGap: _lerpDouble(tileGap, other.tileGap, t),
      );
}

/// Metrics shared by game grids and game rails.
@immutable
final class ConsoleGameRailMetrics {
  const ConsoleGameRailMetrics({
    required this.strip,
    required this.gridRunGap,
    required this.minimumHeight,
    required this.gridTileMaxWidth,
  });

  final ConsoleTileStripMetrics strip;
  final double gridRunGap;
  final double minimumHeight;

  /// Upper bound on a grid tile's width. Wide viewports gain columns instead
  /// of stretching covers past their designed presence; [strip.tileWidth]
  /// remains the matching lower bound.
  final double gridTileMaxWidth;

  ConsoleGameRailMetrics lerp(ConsoleGameRailMetrics other, double t) =>
      ConsoleGameRailMetrics(
        strip: strip.lerp(other.strip, t),
        gridRunGap: _lerpDouble(gridRunGap, other.gridRunGap, t),
        minimumHeight: _lerpDouble(minimumHeight, other.minimumHeight, t),
        gridTileMaxWidth: _lerpDouble(
          gridTileMaxWidth,
          other.gridTileMaxWidth,
          t,
        ),
      );
}

/// Metrics for the release strip, whose fixed lane height is part of its
/// reusable component contract.
@immutable
final class ConsoleReleaseRailMetrics {
  const ConsoleReleaseRailMetrics({required this.strip, required this.height});

  final ConsoleTileStripMetrics strip;
  final double height;

  ConsoleReleaseRailMetrics lerp(ConsoleReleaseRailMetrics other, double t) =>
      ConsoleReleaseRailMetrics(
        strip: strip.lerp(other.strip, t),
        height: _lerpDouble(height, other.height, t),
      );
}

/// Shared screen-edge chrome insets, including the navigation-aware clock.
@immutable
final class ConsoleScreenChromeMetrics {
  const ConsoleScreenChromeMetrics({
    required this.top,
    required this.side,
    required this.bottom,
    required this.clockInset,
    required this.navigationClockTop,
  });

  final double top;
  final double side;
  final double bottom;
  final double clockInset;
  final double navigationClockTop;

  ConsoleScreenChromeMetrics lerp(ConsoleScreenChromeMetrics other, double t) =>
      ConsoleScreenChromeMetrics(
        top: _lerpDouble(top, other.top, t),
        side: _lerpDouble(side, other.side, t),
        bottom: _lerpDouble(bottom, other.bottom, t),
        clockInset: _lerpDouble(clockInset, other.clockInset, t),
        navigationClockTop: _lerpDouble(
          navigationClockTop,
          other.navigationClockTop,
          t,
        ),
      );
}

/// Metrics owned by the reusable compact page band: the shared header that
/// carries the surface title, optional eyebrow trail, inline section
/// switcher, and right-aligned status cluster.
@immutable
final class ConsolePageBandMetrics {
  const ConsolePageBandMetrics({
    required this.height,
    required this.eyebrowGap,
    required this.switcherGap,
    required this.segmentInset,
    required this.segmentPadding,
    required this.clusterGap,
  });

  /// Minimum band height; large text grows the band instead of clipping.
  final double height;

  /// Vertical gap between the eyebrow trail and the title.
  final double eyebrowGap;

  /// Horizontal gap between the title block and the section switcher.
  final double switcherGap;

  /// Inset between the switcher's pill container and its segments.
  final double segmentInset;

  /// Padding inside each switcher segment.
  final EdgeInsets segmentPadding;

  /// Gap between items in the trailing status cluster.
  final double clusterGap;

  ConsolePageBandMetrics lerp(ConsolePageBandMetrics other, double t) =>
      ConsolePageBandMetrics(
        height: _lerpDouble(height, other.height, t),
        eyebrowGap: _lerpDouble(eyebrowGap, other.eyebrowGap, t),
        switcherGap: _lerpDouble(switcherGap, other.switcherGap, t),
        segmentInset: _lerpDouble(segmentInset, other.segmentInset, t),
        segmentPadding: EdgeInsets.lerp(
          segmentPadding,
          other.segmentPadding,
          t,
        )!,
        clusterGap: _lerpDouble(clusterGap, other.clusterGap, t),
      );
}

/// Metrics owned by the reusable active-session/player cluster.
@immutable
final class ConsoleSessionClusterMetrics {
  const ConsoleSessionClusterMetrics({
    required this.height,
    required this.emptyWidth,
    required this.leadingWidth,
    required this.iconSlot,
    required this.iconSize,
    required this.radius,
    required this.attentionBadgeSize,
    required this.attentionBadgeIconSize,
    required this.attentionBadgeShape,
    required this.attentionBadgeRadius,
  });

  final double height;
  final double emptyWidth;
  final double leadingWidth;
  final double iconSlot;
  final double iconSize;
  final double radius;
  final double attentionBadgeSize;
  final double attentionBadgeIconSize;
  final BoxShape attentionBadgeShape;
  final double attentionBadgeRadius;

  ConsoleSessionClusterMetrics lerp(
    ConsoleSessionClusterMetrics other,
    double t,
  ) => ConsoleSessionClusterMetrics(
    height: _lerpDouble(height, other.height, t),
    emptyWidth: _lerpDouble(emptyWidth, other.emptyWidth, t),
    leadingWidth: _lerpDouble(leadingWidth, other.leadingWidth, t),
    iconSlot: _lerpDouble(iconSlot, other.iconSlot, t),
    iconSize: _lerpDouble(iconSize, other.iconSize, t),
    radius: _lerpDouble(radius, other.radius, t),
    attentionBadgeSize: _lerpDouble(
      attentionBadgeSize,
      other.attentionBadgeSize,
      t,
    ),
    attentionBadgeIconSize: _lerpDouble(
      attentionBadgeIconSize,
      other.attentionBadgeIconSize,
      t,
    ),
    attentionBadgeShape: t < 0.5
        ? attentionBadgeShape
        : other.attentionBadgeShape,
    attentionBadgeRadius: _lerpDouble(
      attentionBadgeRadius,
      other.attentionBadgeRadius,
      t,
    ),
  );
}

/// Shared geometry for controller/emulator/profile forms and action rows.
@immutable
final class ConsoleSettingsMetrics {
  const ConsoleSettingsMetrics({
    required this.contentMaxWidth,
    required this.hierarchyMaxWidth,
    required this.hierarchyBreakpoint,
    required this.hierarchyTextScaleBreakpoint,
    required this.categoryRailWidth,
    required this.hierarchyPaneGap,
    required this.categoryRowHeight,
    required this.headerIconSize,
    required this.actionHeight,
    required this.compactActionHeight,
    required this.contextPanelWidth,
    required this.contextPanelBreakpoint,
  });

  final double contentMaxWidth;
  final double hierarchyMaxWidth;
  final double hierarchyBreakpoint;
  final double hierarchyTextScaleBreakpoint;
  final double categoryRailWidth;
  final double hierarchyPaneGap;
  final double categoryRowHeight;
  final double headerIconSize;
  final double actionHeight;
  final double compactActionHeight;

  /// Width of the optional contextual panel beside settings content.
  final double contextPanelWidth;

  /// Below this width the contextual panel hides entirely.
  final double contextPanelBreakpoint;

  ConsoleSettingsMetrics lerp(ConsoleSettingsMetrics other, double t) =>
      ConsoleSettingsMetrics(
        contentMaxWidth: _lerpDouble(contentMaxWidth, other.contentMaxWidth, t),
        hierarchyMaxWidth: _lerpDouble(
          hierarchyMaxWidth,
          other.hierarchyMaxWidth,
          t,
        ),
        hierarchyBreakpoint: _lerpDouble(
          hierarchyBreakpoint,
          other.hierarchyBreakpoint,
          t,
        ),
        hierarchyTextScaleBreakpoint: _lerpDouble(
          hierarchyTextScaleBreakpoint,
          other.hierarchyTextScaleBreakpoint,
          t,
        ),
        categoryRailWidth: _lerpDouble(
          categoryRailWidth,
          other.categoryRailWidth,
          t,
        ),
        hierarchyPaneGap: _lerpDouble(
          hierarchyPaneGap,
          other.hierarchyPaneGap,
          t,
        ),
        categoryRowHeight: _lerpDouble(
          categoryRowHeight,
          other.categoryRowHeight,
          t,
        ),
        headerIconSize: _lerpDouble(headerIconSize, other.headerIconSize, t),
        actionHeight: _lerpDouble(actionHeight, other.actionHeight, t),
        compactActionHeight: _lerpDouble(
          compactActionHeight,
          other.compactActionHeight,
          t,
        ),
        contextPanelWidth: _lerpDouble(
          contextPanelWidth,
          other.contextPanelWidth,
          t,
        ),
        contextPanelBreakpoint: _lerpDouble(
          contextPanelBreakpoint,
          other.contextPanelBreakpoint,
          t,
        ),
      );
}

/// Geometry owned by the reusable Players review/setup panel.
@immutable
final class ConsolePlayersPanelMetrics {
  const ConsolePlayersPanelMetrics({
    required this.seatHeight,
    required this.seatTextScaleGrowth,
    required this.actionTextScaleGrowth,
    required this.claimTileHeight,
  });

  final double seatHeight;
  final double seatTextScaleGrowth;
  final double actionTextScaleGrowth;
  final double claimTileHeight;

  ConsolePlayersPanelMetrics lerp(ConsolePlayersPanelMetrics other, double t) =>
      ConsolePlayersPanelMetrics(
        seatHeight: _lerpDouble(seatHeight, other.seatHeight, t),
        seatTextScaleGrowth: _lerpDouble(
          seatTextScaleGrowth,
          other.seatTextScaleGrowth,
          t,
        ),
        actionTextScaleGrowth: _lerpDouble(
          actionTextScaleGrowth,
          other.actionTextScaleGrowth,
          t,
        ),
        claimTileHeight: _lerpDouble(claimTileHeight, other.claimTileHeight, t),
      );
}

/// Shared geometry for shortcut-reference rows and their keycaps.
@immutable
final class ConsoleShortcutHelpMetrics {
  const ConsoleShortcutHelpMetrics({
    required this.sectionGap,
    required this.rowPadding,
    required this.keycapWidth,
    required this.keycapPadding,
  });

  final double sectionGap;
  final EdgeInsets rowPadding;
  final double keycapWidth;
  final EdgeInsets keycapPadding;

  ConsoleShortcutHelpMetrics lerp(ConsoleShortcutHelpMetrics other, double t) =>
      ConsoleShortcutHelpMetrics(
        sectionGap: _lerpDouble(sectionGap, other.sectionGap, t),
        rowPadding: EdgeInsets.lerp(rowPadding, other.rowPadding, t)!,
        keycapWidth: _lerpDouble(keycapWidth, other.keycapWidth, t),
        keycapPadding: EdgeInsets.lerp(keycapPadding, other.keycapPadding, t)!,
      );
}

/// Shared geometry for footer hint bars and input keycaps.
@immutable
final class ConsoleHintMetrics {
  const ConsoleHintMetrics({
    required this.itemGap,
    required this.footerMinHeight,
    required this.keycapDiameter,
    required this.keycapMinWidth,
    required this.keycapRadius,
    required this.gamepadKeycapRadius,
    required this.keycapTextHeight,
    required this.stackBreakpoint,
    required this.leadingStackBreakpoint,
    required this.stackTextScaleThreshold,
  });

  final double itemGap;
  final double footerMinHeight;
  final double keycapDiameter;
  final double keycapMinWidth;
  final double keycapRadius;
  final double gamepadKeycapRadius;
  final double keycapTextHeight;

  /// Below this width the hint bar stacks its rows vertically.
  final double stackBreakpoint;

  /// Below this width a hint bar with a leading cluster stacks its rows.
  final double leadingStackBreakpoint;

  /// At or above this text scale the hint bar stacks regardless of width.
  final double stackTextScaleThreshold;

  ConsoleHintMetrics lerp(ConsoleHintMetrics other, double t) =>
      ConsoleHintMetrics(
        itemGap: _lerpDouble(itemGap, other.itemGap, t),
        footerMinHeight: _lerpDouble(footerMinHeight, other.footerMinHeight, t),
        keycapDiameter: _lerpDouble(keycapDiameter, other.keycapDiameter, t),
        keycapMinWidth: _lerpDouble(keycapMinWidth, other.keycapMinWidth, t),
        keycapRadius: _lerpDouble(keycapRadius, other.keycapRadius, t),
        gamepadKeycapRadius: _lerpDouble(
          gamepadKeycapRadius,
          other.gamepadKeycapRadius,
          t,
        ),
        keycapTextHeight: _lerpDouble(
          keycapTextHeight,
          other.keycapTextHeight,
          t,
        ),
        stackBreakpoint: _lerpDouble(stackBreakpoint, other.stackBreakpoint, t),
        leadingStackBreakpoint: _lerpDouble(
          leadingStackBreakpoint,
          other.leadingStackBreakpoint,
          t,
        ),
        stackTextScaleThreshold: _lerpDouble(
          stackTextScaleThreshold,
          other.stackTextScaleThreshold,
          t,
        ),
      );
}

/// Shared geometry for the full on-screen text-entry keyboard.
@immutable
final class ConsoleKeyboardMetrics {
  const ConsoleKeyboardMetrics({
    required this.unitWidth,
    required this.keyHeight,
    required this.gap,
    required this.iconSize,
  });

  final double unitWidth;
  final double keyHeight;
  final double gap;
  final double iconSize;

  ConsoleKeyboardMetrics lerp(ConsoleKeyboardMetrics other, double t) =>
      ConsoleKeyboardMetrics(
        unitWidth: _lerpDouble(unitWidth, other.unitWidth, t),
        keyHeight: _lerpDouble(keyHeight, other.keyHeight, t),
        gap: _lerpDouble(gap, other.gap, t),
        iconSize: _lerpDouble(iconSize, other.iconSize, t),
      );
}

/// Shared surface-frame geometry used by route shells and dialogs.
@immutable
final class ConsoleSurfaceMetrics {
  const ConsoleSurfaceMetrics({
    required this.notificationWidth,
    required this.frameMargin,
    required this.headerIconSize,
    required this.iconActionSize,
    required this.emptyStateMinHeight,
    required this.compactControlPadding,
  });

  final double notificationWidth;
  final double frameMargin;
  final double headerIconSize;

  /// Square hit target for icon-only actions (search, sort, filter).
  final double iconActionSize;
  final double emptyStateMinHeight;
  final EdgeInsets compactControlPadding;

  ConsoleSurfaceMetrics lerp(ConsoleSurfaceMetrics other, double t) =>
      ConsoleSurfaceMetrics(
        notificationWidth: _lerpDouble(
          notificationWidth,
          other.notificationWidth,
          t,
        ),
        frameMargin: _lerpDouble(frameMargin, other.frameMargin, t),
        headerIconSize: _lerpDouble(headerIconSize, other.headerIconSize, t),
        iconActionSize: _lerpDouble(iconActionSize, other.iconActionSize, t),
        emptyStateMinHeight: _lerpDouble(
          emptyStateMinHeight,
          other.emptyStateMinHeight,
          t,
        ),
        compactControlPadding: EdgeInsets.lerp(
          compactControlPadding,
          other.compactControlPadding,
          t,
        )!,
      );
}

/// Reusable status/feedback sizing across connection, loading, and empty-state
/// components.
@immutable
final class ConsoleStatusMetrics {
  const ConsoleStatusMetrics({
    required this.panelMaxWidth,
    required this.metadataIconSize,
    required this.dotSize,
    required this.labelMaxWidth,
    required this.feedbackIconSize,
    required this.loadingIndicatorWidth,
  });

  final double panelMaxWidth;
  final double metadataIconSize;
  final double dotSize;
  final double labelMaxWidth;
  final double feedbackIconSize;
  final double loadingIndicatorWidth;

  ConsoleStatusMetrics lerp(
    ConsoleStatusMetrics other,
    double t,
  ) => ConsoleStatusMetrics(
    panelMaxWidth: _lerpDouble(panelMaxWidth, other.panelMaxWidth, t),
    metadataIconSize: _lerpDouble(metadataIconSize, other.metadataIconSize, t),
    dotSize: _lerpDouble(dotSize, other.dotSize, t),
    labelMaxWidth: _lerpDouble(labelMaxWidth, other.labelMaxWidth, t),
    feedbackIconSize: _lerpDouble(feedbackIconSize, other.feedbackIconSize, t),
    loadingIndicatorWidth: _lerpDouble(
      loadingIndicatorWidth,
      other.loadingIndicatorWidth,
      t,
    ),
  );
}

/// Reusable controller-summary bar sizing.
@immutable
final class ConsoleControllerBarMetrics {
  const ConsoleControllerBarMetrics({
    required this.height,
    required this.nameMaxWidth,
  });

  final double height;
  final double nameMaxWidth;

  ConsoleControllerBarMetrics lerp(
    ConsoleControllerBarMetrics other,
    double t,
  ) => ConsoleControllerBarMetrics(
    height: _lerpDouble(height, other.height, t),
    nameMaxWidth: _lerpDouble(nameMaxWidth, other.nameMaxWidth, t),
  );
}

/// Shared alpha treatment for semantically tinted surfaces.
@immutable
final class ConsoleTintMetrics {
  const ConsoleTintMetrics({
    required this.fillOpacity,
    required this.borderOpacity,
  });

  final double fillOpacity;
  final double borderOpacity;

  ConsoleTintMetrics lerp(ConsoleTintMetrics other, double t) =>
      ConsoleTintMetrics(
        fillOpacity: _lerpDouble(fillOpacity, other.fillOpacity, t),
        borderOpacity: _lerpDouble(borderOpacity, other.borderOpacity, t),
      );
}

/// Discover-owned composition contract.
///
/// These values describe relationships within the full-screen destination;
/// colors, type, focus, and motion continue to come from their shared roles.
@immutable
final class ConsoleDiscoverMetrics {
  const ConsoleDiscoverMetrics({
    required this.frameMargin,
    required this.headerHeight,
    required this.footerHeight,
    required this.headerSectionGap,
    required this.contentTopInset,
    required this.featuredSpotlightMaxFraction,
    required this.featuredCardWidth,
    required this.featuredCardGap,
    required this.allGamesGridColumns,
    required this.allGamesInspectorWidth,
    required this.allGamesPaneGap,
    required this.systemsPlateWidth,
    required this.systemsPlateGap,
    required this.searchKeyboardFraction,
    required this.searchZoneGap,
    required this.compactBreakpoint,
    required this.largeTextScaleBreakpoint,
  });

  final double frameMargin;
  final double headerHeight;
  final double footerHeight;
  final double headerSectionGap;
  final double contentTopInset;
  final double featuredSpotlightMaxFraction;
  final double featuredCardWidth;
  final double featuredCardGap;
  final int allGamesGridColumns;
  final double allGamesInspectorWidth;
  final double allGamesPaneGap;
  final double systemsPlateWidth;
  final double systemsPlateGap;
  final double searchKeyboardFraction;
  final double searchZoneGap;
  final double compactBreakpoint;
  final double largeTextScaleBreakpoint;

  ConsoleDiscoverMetrics copyWith({
    double? frameMargin,
    double? headerHeight,
    double? footerHeight,
    double? headerSectionGap,
    double? contentTopInset,
    double? featuredSpotlightMaxFraction,
    double? featuredCardWidth,
    double? featuredCardGap,
    int? allGamesGridColumns,
    double? allGamesInspectorWidth,
    double? allGamesPaneGap,
    double? systemsPlateWidth,
    double? systemsPlateGap,
    double? searchKeyboardFraction,
    double? searchZoneGap,
    double? compactBreakpoint,
    double? largeTextScaleBreakpoint,
  }) => ConsoleDiscoverMetrics(
    frameMargin: frameMargin ?? this.frameMargin,
    headerHeight: headerHeight ?? this.headerHeight,
    footerHeight: footerHeight ?? this.footerHeight,
    headerSectionGap: headerSectionGap ?? this.headerSectionGap,
    contentTopInset: contentTopInset ?? this.contentTopInset,
    featuredSpotlightMaxFraction:
        featuredSpotlightMaxFraction ?? this.featuredSpotlightMaxFraction,
    featuredCardWidth: featuredCardWidth ?? this.featuredCardWidth,
    featuredCardGap: featuredCardGap ?? this.featuredCardGap,
    allGamesGridColumns: allGamesGridColumns ?? this.allGamesGridColumns,
    allGamesInspectorWidth:
        allGamesInspectorWidth ?? this.allGamesInspectorWidth,
    allGamesPaneGap: allGamesPaneGap ?? this.allGamesPaneGap,
    systemsPlateWidth: systemsPlateWidth ?? this.systemsPlateWidth,
    systemsPlateGap: systemsPlateGap ?? this.systemsPlateGap,
    searchKeyboardFraction:
        searchKeyboardFraction ?? this.searchKeyboardFraction,
    searchZoneGap: searchZoneGap ?? this.searchZoneGap,
    compactBreakpoint: compactBreakpoint ?? this.compactBreakpoint,
    largeTextScaleBreakpoint:
        largeTextScaleBreakpoint ?? this.largeTextScaleBreakpoint,
  );

  ConsoleDiscoverMetrics lerp(ConsoleDiscoverMetrics other, double t) =>
      ConsoleDiscoverMetrics(
        frameMargin: _lerpDouble(frameMargin, other.frameMargin, t),
        headerHeight: _lerpDouble(headerHeight, other.headerHeight, t),
        footerHeight: _lerpDouble(footerHeight, other.footerHeight, t),
        headerSectionGap: _lerpDouble(
          headerSectionGap,
          other.headerSectionGap,
          t,
        ),
        contentTopInset: _lerpDouble(contentTopInset, other.contentTopInset, t),
        featuredSpotlightMaxFraction: _lerpDouble(
          featuredSpotlightMaxFraction,
          other.featuredSpotlightMaxFraction,
          t,
        ),
        featuredCardWidth: _lerpDouble(
          featuredCardWidth,
          other.featuredCardWidth,
          t,
        ),
        featuredCardGap: _lerpDouble(featuredCardGap, other.featuredCardGap, t),
        allGamesGridColumns: _lerpDouble(
          allGamesGridColumns.toDouble(),
          other.allGamesGridColumns.toDouble(),
          t,
        ).round(),
        allGamesInspectorWidth: _lerpDouble(
          allGamesInspectorWidth,
          other.allGamesInspectorWidth,
          t,
        ),
        allGamesPaneGap: _lerpDouble(allGamesPaneGap, other.allGamesPaneGap, t),
        systemsPlateWidth: _lerpDouble(
          systemsPlateWidth,
          other.systemsPlateWidth,
          t,
        ),
        systemsPlateGap: _lerpDouble(systemsPlateGap, other.systemsPlateGap, t),
        searchKeyboardFraction: _lerpDouble(
          searchKeyboardFraction,
          other.searchKeyboardFraction,
          t,
        ),
        searchZoneGap: _lerpDouble(searchZoneGap, other.searchZoneGap, t),
        compactBreakpoint: _lerpDouble(
          compactBreakpoint,
          other.compactBreakpoint,
          t,
        ),
        largeTextScaleBreakpoint: _lerpDouble(
          largeTextScaleBreakpoint,
          other.largeTextScaleBreakpoint,
          t,
        ),
      );
}

/// Runtime layout, shape, stroke, and component-size roles.
@immutable
final class ConsoleLayoutTheme extends ThemeExtension<ConsoleLayoutTheme> {
  const ConsoleLayoutTheme({
    required this.xxs,
    required this.xs,
    required this.sm,
    required this.md,
    required this.lg,
    required this.xl,
    required this.xxl,
    required this.xxxl,
    required this.screenGutter,
    required this.sectionGap,
    required this.controlGap,
    required this.panelPadding,
    required this.surface,
    required this.discover,
    required this.status,
    required this.screenChrome,
    required this.circleButtonSize,
    required this.circleButtonShape,
    required this.circleButtonRadius,
    required this.controllerBar,
    required this.tint,
    required this.navigationDock,
    required this.contentRail,
    required this.pageBand,
    required this.shortcutHelp,
    required this.hints,
    required this.keyboard,
    required this.sessionCluster,
    required this.settings,
    required this.playersPanel,
    required this.disabledOpacity,
    required this.entryLockupOffset,
    required this.entryBrandLift,
    required this.entryPromptOffset,
    required this.entrySelectionTranslate,
    required this.entryLockupSpacing,
    required this.entrySupportingGap,
    required this.entryRompSize,
    required this.entryWordmarkWidth,
    required this.profileSelectionLockupRise,
    required this.avatarMd,
    required this.gameRail,
    required this.mediaRail,
    required this.releaseRail,
    required this.controlRadius,
    required this.chipRadius,
    required this.panelRadius,
    required this.dialogRadius,
    required this.pillRadius,
    required this.focusStroke,
    required this.focusRingStroke,
    required this.focusRingGap,
    required this.focusGlowBlur,
    required this.focusGlowSpread,
    required this.focusGlowInflate,
    required this.hairlineStroke,
    required this.iconSm,
    required this.iconMd,
    required this.iconLg,
    required this.avatarSm,
    required this.avatarLg,
    required this.avatarXl,
    required this.progressIndicatorSm,
    required this.progressIndicatorSize,
  });

  final double xxs;
  final double xs;
  final double sm;
  final double md;
  final double lg;
  final double xl;
  final double xxl;
  final double xxxl;
  final double screenGutter;
  final double sectionGap;
  final double controlGap;
  final EdgeInsets panelPadding;
  final ConsoleSurfaceMetrics surface;
  final ConsoleDiscoverMetrics discover;
  final ConsoleStatusMetrics status;
  final ConsoleScreenChromeMetrics screenChrome;
  final double circleButtonSize;
  final BoxShape circleButtonShape;
  final double circleButtonRadius;
  final ConsoleControllerBarMetrics controllerBar;
  final ConsoleTintMetrics tint;
  final ConsoleNavigationDockMetrics navigationDock;
  final ConsoleContentRailMetrics contentRail;
  final ConsolePageBandMetrics pageBand;
  final ConsoleShortcutHelpMetrics shortcutHelp;
  final ConsoleHintMetrics hints;
  final ConsoleKeyboardMetrics keyboard;
  final ConsoleSessionClusterMetrics sessionCluster;
  final ConsoleSettingsMetrics settings;
  final ConsolePlayersPanelMetrics playersPanel;
  final double disabledOpacity;
  final double entryLockupOffset;
  final double entryBrandLift;
  final double entryPromptOffset;
  final double entrySelectionTranslate;
  final double entryLockupSpacing;
  final double entrySupportingGap;
  final double entryRompSize;
  final double entryWordmarkWidth;
  final double profileSelectionLockupRise;
  final double avatarMd;
  final ConsoleGameRailMetrics gameRail;
  final ConsoleTileStripMetrics mediaRail;
  final ConsoleReleaseRailMetrics releaseRail;
  final double controlRadius;
  final double chipRadius;
  final double panelRadius;
  final double dialogRadius;
  final double pillRadius;
  final double focusStroke;
  final double focusRingStroke;
  final double focusRingGap;
  final double focusGlowBlur;
  final double focusGlowSpread;
  final double focusGlowInflate;
  final double hairlineStroke;
  final double iconSm;
  final double iconMd;
  final double iconLg;
  final double avatarSm;
  final double avatarLg;
  final double avatarXl;
  final double progressIndicatorSm;
  final double progressIndicatorSize;

  @override
  ConsoleLayoutTheme copyWith({
    double? xxs,
    double? xs,
    double? sm,
    double? md,
    double? lg,
    double? xl,
    double? xxl,
    double? xxxl,
    double? screenGutter,
    double? sectionGap,
    double? controlGap,
    EdgeInsets? panelPadding,
    ConsoleSurfaceMetrics? surface,
    ConsoleDiscoverMetrics? discover,
    ConsoleStatusMetrics? status,
    ConsoleScreenChromeMetrics? screenChrome,
    double? circleButtonSize,
    BoxShape? circleButtonShape,
    double? circleButtonRadius,
    ConsoleControllerBarMetrics? controllerBar,
    ConsoleTintMetrics? tint,
    ConsoleNavigationDockMetrics? navigationDock,
    ConsoleContentRailMetrics? contentRail,
    ConsolePageBandMetrics? pageBand,
    ConsoleShortcutHelpMetrics? shortcutHelp,
    ConsoleHintMetrics? hints,
    ConsoleKeyboardMetrics? keyboard,
    ConsoleSessionClusterMetrics? sessionCluster,
    ConsoleSettingsMetrics? settings,
    ConsolePlayersPanelMetrics? playersPanel,
    double? disabledOpacity,
    double? entryLockupOffset,
    double? entryBrandLift,
    double? entryPromptOffset,
    double? entrySelectionTranslate,
    double? entryLockupSpacing,
    double? entrySupportingGap,
    double? entryRompSize,
    double? entryWordmarkWidth,
    double? profileSelectionLockupRise,
    double? avatarMd,
    ConsoleGameRailMetrics? gameRail,
    ConsoleTileStripMetrics? mediaRail,
    ConsoleReleaseRailMetrics? releaseRail,
    double? controlRadius,
    double? chipRadius,
    double? panelRadius,
    double? dialogRadius,
    double? pillRadius,
    double? focusStroke,
    double? focusRingStroke,
    double? focusRingGap,
    double? focusGlowBlur,
    double? focusGlowSpread,
    double? focusGlowInflate,
    double? hairlineStroke,
    double? iconSm,
    double? iconMd,
    double? iconLg,
    double? avatarSm,
    double? avatarLg,
    double? avatarXl,
    double? progressIndicatorSm,
    double? progressIndicatorSize,
  }) => ConsoleLayoutTheme(
    xxs: xxs ?? this.xxs,
    xs: xs ?? this.xs,
    sm: sm ?? this.sm,
    md: md ?? this.md,
    lg: lg ?? this.lg,
    xl: xl ?? this.xl,
    xxl: xxl ?? this.xxl,
    xxxl: xxxl ?? this.xxxl,
    screenGutter: screenGutter ?? this.screenGutter,
    sectionGap: sectionGap ?? this.sectionGap,
    controlGap: controlGap ?? this.controlGap,
    panelPadding: panelPadding ?? this.panelPadding,
    surface: surface ?? this.surface,
    discover: discover ?? this.discover,
    status: status ?? this.status,
    screenChrome: screenChrome ?? this.screenChrome,
    circleButtonSize: circleButtonSize ?? this.circleButtonSize,
    circleButtonShape: circleButtonShape ?? this.circleButtonShape,
    circleButtonRadius: circleButtonRadius ?? this.circleButtonRadius,
    controllerBar: controllerBar ?? this.controllerBar,
    tint: tint ?? this.tint,
    navigationDock: navigationDock ?? this.navigationDock,
    contentRail: contentRail ?? this.contentRail,
    pageBand: pageBand ?? this.pageBand,
    shortcutHelp: shortcutHelp ?? this.shortcutHelp,
    hints: hints ?? this.hints,
    keyboard: keyboard ?? this.keyboard,
    sessionCluster: sessionCluster ?? this.sessionCluster,
    settings: settings ?? this.settings,
    playersPanel: playersPanel ?? this.playersPanel,
    disabledOpacity: disabledOpacity ?? this.disabledOpacity,
    entryLockupOffset: entryLockupOffset ?? this.entryLockupOffset,
    entryBrandLift: entryBrandLift ?? this.entryBrandLift,
    entryPromptOffset: entryPromptOffset ?? this.entryPromptOffset,
    entrySelectionTranslate:
        entrySelectionTranslate ?? this.entrySelectionTranslate,
    entryLockupSpacing: entryLockupSpacing ?? this.entryLockupSpacing,
    entrySupportingGap: entrySupportingGap ?? this.entrySupportingGap,
    entryRompSize: entryRompSize ?? this.entryRompSize,
    entryWordmarkWidth: entryWordmarkWidth ?? this.entryWordmarkWidth,
    profileSelectionLockupRise:
        profileSelectionLockupRise ?? this.profileSelectionLockupRise,
    avatarMd: avatarMd ?? this.avatarMd,
    gameRail: gameRail ?? this.gameRail,
    mediaRail: mediaRail ?? this.mediaRail,
    releaseRail: releaseRail ?? this.releaseRail,
    controlRadius: controlRadius ?? this.controlRadius,
    chipRadius: chipRadius ?? this.chipRadius,
    panelRadius: panelRadius ?? this.panelRadius,
    dialogRadius: dialogRadius ?? this.dialogRadius,
    pillRadius: pillRadius ?? this.pillRadius,
    focusStroke: focusStroke ?? this.focusStroke,
    focusRingStroke: focusRingStroke ?? this.focusRingStroke,
    focusRingGap: focusRingGap ?? this.focusRingGap,
    focusGlowBlur: focusGlowBlur ?? this.focusGlowBlur,
    focusGlowSpread: focusGlowSpread ?? this.focusGlowSpread,
    focusGlowInflate: focusGlowInflate ?? this.focusGlowInflate,
    hairlineStroke: hairlineStroke ?? this.hairlineStroke,
    iconSm: iconSm ?? this.iconSm,
    iconMd: iconMd ?? this.iconMd,
    iconLg: iconLg ?? this.iconLg,
    avatarSm: avatarSm ?? this.avatarSm,
    avatarLg: avatarLg ?? this.avatarLg,
    avatarXl: avatarXl ?? this.avatarXl,
    progressIndicatorSm: progressIndicatorSm ?? this.progressIndicatorSm,
    progressIndicatorSize: progressIndicatorSize ?? this.progressIndicatorSize,
  );

  @override
  ConsoleLayoutTheme lerp(ThemeExtension<ConsoleLayoutTheme>? other, double t) {
    if (other is! ConsoleLayoutTheme) return this;
    return ConsoleLayoutTheme(
      xxs: _lerpDouble(xxs, other.xxs, t),
      xs: _lerpDouble(xs, other.xs, t),
      sm: _lerpDouble(sm, other.sm, t),
      md: _lerpDouble(md, other.md, t),
      lg: _lerpDouble(lg, other.lg, t),
      xl: _lerpDouble(xl, other.xl, t),
      xxl: _lerpDouble(xxl, other.xxl, t),
      xxxl: _lerpDouble(xxxl, other.xxxl, t),
      screenGutter: _lerpDouble(screenGutter, other.screenGutter, t),
      sectionGap: _lerpDouble(sectionGap, other.sectionGap, t),
      controlGap: _lerpDouble(controlGap, other.controlGap, t),
      panelPadding: EdgeInsets.lerp(panelPadding, other.panelPadding, t)!,
      surface: surface.lerp(other.surface, t),
      discover: discover.lerp(other.discover, t),
      status: status.lerp(other.status, t),
      screenChrome: screenChrome.lerp(other.screenChrome, t),
      circleButtonSize: _lerpDouble(
        circleButtonSize,
        other.circleButtonSize,
        t,
      ),
      circleButtonShape: t < 0.5 ? circleButtonShape : other.circleButtonShape,
      circleButtonRadius: _lerpDouble(
        circleButtonRadius,
        other.circleButtonRadius,
        t,
      ),
      controllerBar: controllerBar.lerp(other.controllerBar, t),
      tint: tint.lerp(other.tint, t),
      navigationDock: navigationDock.lerp(other.navigationDock, t),
      contentRail: contentRail.lerp(other.contentRail, t),
      pageBand: pageBand.lerp(other.pageBand, t),
      shortcutHelp: shortcutHelp.lerp(other.shortcutHelp, t),
      hints: hints.lerp(other.hints, t),
      keyboard: keyboard.lerp(other.keyboard, t),
      sessionCluster: sessionCluster.lerp(other.sessionCluster, t),
      settings: settings.lerp(other.settings, t),
      playersPanel: playersPanel.lerp(other.playersPanel, t),
      disabledOpacity: _lerpDouble(disabledOpacity, other.disabledOpacity, t),
      entryLockupOffset: _lerpDouble(
        entryLockupOffset,
        other.entryLockupOffset,
        t,
      ),
      entryBrandLift: _lerpDouble(entryBrandLift, other.entryBrandLift, t),
      entryPromptOffset: _lerpDouble(
        entryPromptOffset,
        other.entryPromptOffset,
        t,
      ),
      entrySelectionTranslate: _lerpDouble(
        entrySelectionTranslate,
        other.entrySelectionTranslate,
        t,
      ),
      entryLockupSpacing: _lerpDouble(
        entryLockupSpacing,
        other.entryLockupSpacing,
        t,
      ),
      entrySupportingGap: _lerpDouble(
        entrySupportingGap,
        other.entrySupportingGap,
        t,
      ),
      entryRompSize: _lerpDouble(entryRompSize, other.entryRompSize, t),
      entryWordmarkWidth: _lerpDouble(
        entryWordmarkWidth,
        other.entryWordmarkWidth,
        t,
      ),
      profileSelectionLockupRise: _lerpDouble(
        profileSelectionLockupRise,
        other.profileSelectionLockupRise,
        t,
      ),
      avatarMd: _lerpDouble(avatarMd, other.avatarMd, t),
      gameRail: gameRail.lerp(other.gameRail, t),
      mediaRail: mediaRail.lerp(other.mediaRail, t),
      releaseRail: releaseRail.lerp(other.releaseRail, t),
      controlRadius: _lerpDouble(controlRadius, other.controlRadius, t),
      chipRadius: _lerpDouble(chipRadius, other.chipRadius, t),
      panelRadius: _lerpDouble(panelRadius, other.panelRadius, t),
      dialogRadius: _lerpDouble(dialogRadius, other.dialogRadius, t),
      pillRadius: _lerpDouble(pillRadius, other.pillRadius, t),
      focusStroke: _lerpDouble(focusStroke, other.focusStroke, t),
      focusRingStroke: _lerpDouble(focusRingStroke, other.focusRingStroke, t),
      focusRingGap: _lerpDouble(focusRingGap, other.focusRingGap, t),
      focusGlowBlur: _lerpDouble(focusGlowBlur, other.focusGlowBlur, t),
      focusGlowSpread: _lerpDouble(focusGlowSpread, other.focusGlowSpread, t),
      focusGlowInflate: _lerpDouble(
        focusGlowInflate,
        other.focusGlowInflate,
        t,
      ),
      hairlineStroke: _lerpDouble(hairlineStroke, other.hairlineStroke, t),
      iconSm: _lerpDouble(iconSm, other.iconSm, t),
      iconMd: _lerpDouble(iconMd, other.iconMd, t),
      iconLg: _lerpDouble(iconLg, other.iconLg, t),
      avatarSm: _lerpDouble(avatarSm, other.avatarSm, t),
      avatarLg: _lerpDouble(avatarLg, other.avatarLg, t),
      avatarXl: _lerpDouble(avatarXl, other.avatarXl, t),
      progressIndicatorSm: _lerpDouble(
        progressIndicatorSm,
        other.progressIndicatorSm,
        t,
      ),
      progressIndicatorSize: _lerpDouble(
        progressIndicatorSize,
        other.progressIndicatorSize,
        t,
      ),
    );
  }
}

/// Semantic shadow and glow roles.
@immutable
final class ConsoleElevationTheme
    extends ThemeExtension<ConsoleElevationTheme> {
  const ConsoleElevationTheme({
    required this.none,
    required this.panel,
    required this.dialog,
    required this.focusGlow,
    required this.selectionGlow,
    required this.hero,
    required this.identityFocusGlowAlpha,
  });

  final List<BoxShadow> none;
  final List<BoxShadow> panel;
  final List<BoxShadow> dialog;
  final List<BoxShadow> focusGlow;
  final List<BoxShadow> selectionGlow;
  final List<BoxShadow> hero;
  final double identityFocusGlowAlpha;

  @override
  ConsoleElevationTheme copyWith({
    List<BoxShadow>? none,
    List<BoxShadow>? panel,
    List<BoxShadow>? dialog,
    List<BoxShadow>? focusGlow,
    List<BoxShadow>? selectionGlow,
    List<BoxShadow>? hero,
    double? identityFocusGlowAlpha,
  }) => ConsoleElevationTheme(
    none: none ?? this.none,
    panel: panel ?? this.panel,
    dialog: dialog ?? this.dialog,
    focusGlow: focusGlow ?? this.focusGlow,
    selectionGlow: selectionGlow ?? this.selectionGlow,
    hero: hero ?? this.hero,
    identityFocusGlowAlpha:
        identityFocusGlowAlpha ?? this.identityFocusGlowAlpha,
  );

  @override
  ConsoleElevationTheme lerp(
    ThemeExtension<ConsoleElevationTheme>? other,
    double t,
  ) {
    if (other is! ConsoleElevationTheme) return this;
    return ConsoleElevationTheme(
      none: BoxShadow.lerpList(none, other.none, t) ?? const <BoxShadow>[],
      panel: BoxShadow.lerpList(panel, other.panel, t) ?? const <BoxShadow>[],
      dialog:
          BoxShadow.lerpList(dialog, other.dialog, t) ?? const <BoxShadow>[],
      focusGlow:
          BoxShadow.lerpList(focusGlow, other.focusGlow, t) ??
          const <BoxShadow>[],
      selectionGlow:
          BoxShadow.lerpList(selectionGlow, other.selectionGlow, t) ??
          const <BoxShadow>[],
      hero: BoxShadow.lerpList(hero, other.hero, t) ?? const <BoxShadow>[],
      identityFocusGlowAlpha: _lerpDouble(
        identityFocusGlowAlpha,
        other.identityFocusGlowAlpha,
        t,
      ),
    );
  }
}

/// Skin-owned spatial character for focus, rest, and subtle entrances.
@immutable
final class ConsoleInteractionSpec {
  const ConsoleInteractionSpec({
    required this.focusedScale,
    required this.restScale,
    required this.subtleEnterScale,
    required this.rowActiveOpacity,
    required this.rowInactiveOpacity,
  });

  final double focusedScale;
  final double restScale;
  final double subtleEnterScale;
  final double rowActiveOpacity;
  final double rowInactiveOpacity;

  ConsoleInteractionSpec lerp(
    ConsoleInteractionSpec other,
    double t,
  ) => ConsoleInteractionSpec(
    focusedScale: _lerpDouble(focusedScale, other.focusedScale, t),
    restScale: _lerpDouble(restScale, other.restScale, t),
    subtleEnterScale: _lerpDouble(subtleEnterScale, other.subtleEnterScale, t),
    rowActiveOpacity: _lerpDouble(rowActiveOpacity, other.rowActiveOpacity, t),
    rowInactiveOpacity: _lerpDouble(
      rowInactiveOpacity,
      other.rowInactiveOpacity,
      t,
    ),
  );
}

/// Visual motion roles and the shared reduced-motion policy.
@immutable
final class ConsoleMotionTheme extends ThemeExtension<ConsoleMotionTheme> {
  const ConsoleMotionTheme({
    required this.focus,
    required this.selection,
    required this.contentTransition,
    required this.routeEnter,
    required this.routeExit,
    required this.intro,
    required this.pulse,
    required this.focusRotation,
    required this.screenTransition,
    required this.chromeTransition,
    required this.hintLabelTransition,
    required this.entryTransition,
    required this.entryAttractFadeFraction,
    required this.entrySelectionDelayFraction,
    required this.entryPromptDelayFraction,
    required this.entryPulseMinimumOpacity,
    required this.routeScaleBegin,
    required this.interaction,
    required this.standardCurve,
    required this.emphasizedCurve,
    required this.spatialCurve,
  });

  final Duration focus;
  final Duration selection;
  final Duration contentTransition;
  final Duration routeEnter;
  final Duration routeExit;
  final Duration intro;
  final Duration pulse;
  final Duration focusRotation;
  final Duration screenTransition;
  final Duration chromeTransition;
  final Duration hintLabelTransition;
  final Duration entryTransition;
  final double entryAttractFadeFraction;
  final double entrySelectionDelayFraction;
  final double entryPromptDelayFraction;
  final double entryPulseMinimumOpacity;
  final double routeScaleBegin;
  final ConsoleInteractionSpec interaction;
  final Curve standardCurve;
  final Curve emphasizedCurve;
  final Curve spatialCurve;

  Duration resolve(BuildContext context, Duration duration) =>
      (MediaQuery.maybeOf(context)?.disableAnimations ?? false)
      ? Duration.zero
      : duration;

  @override
  ConsoleMotionTheme copyWith({
    Duration? focus,
    Duration? selection,
    Duration? contentTransition,
    Duration? routeEnter,
    Duration? routeExit,
    Duration? intro,
    Duration? pulse,
    Duration? focusRotation,
    Duration? screenTransition,
    Duration? chromeTransition,
    Duration? hintLabelTransition,
    Duration? entryTransition,
    double? entryAttractFadeFraction,
    double? entrySelectionDelayFraction,
    double? entryPromptDelayFraction,
    double? entryPulseMinimumOpacity,
    double? routeScaleBegin,
    ConsoleInteractionSpec? interaction,
    Curve? standardCurve,
    Curve? emphasizedCurve,
    Curve? spatialCurve,
  }) => ConsoleMotionTheme(
    focus: focus ?? this.focus,
    selection: selection ?? this.selection,
    contentTransition: contentTransition ?? this.contentTransition,
    routeEnter: routeEnter ?? this.routeEnter,
    routeExit: routeExit ?? this.routeExit,
    intro: intro ?? this.intro,
    pulse: pulse ?? this.pulse,
    focusRotation: focusRotation ?? this.focusRotation,
    screenTransition: screenTransition ?? this.screenTransition,
    chromeTransition: chromeTransition ?? this.chromeTransition,
    hintLabelTransition: hintLabelTransition ?? this.hintLabelTransition,
    entryTransition: entryTransition ?? this.entryTransition,
    entryAttractFadeFraction:
        entryAttractFadeFraction ?? this.entryAttractFadeFraction,
    entrySelectionDelayFraction:
        entrySelectionDelayFraction ?? this.entrySelectionDelayFraction,
    entryPromptDelayFraction:
        entryPromptDelayFraction ?? this.entryPromptDelayFraction,
    entryPulseMinimumOpacity:
        entryPulseMinimumOpacity ?? this.entryPulseMinimumOpacity,
    routeScaleBegin: routeScaleBegin ?? this.routeScaleBegin,
    interaction: interaction ?? this.interaction,
    standardCurve: standardCurve ?? this.standardCurve,
    emphasizedCurve: emphasizedCurve ?? this.emphasizedCurve,
    spatialCurve: spatialCurve ?? this.spatialCurve,
  );

  @override
  ConsoleMotionTheme lerp(ThemeExtension<ConsoleMotionTheme>? other, double t) {
    if (other is! ConsoleMotionTheme) return this;
    return ConsoleMotionTheme(
      focus: _lerpDuration(focus, other.focus, t),
      selection: _lerpDuration(selection, other.selection, t),
      contentTransition: _lerpDuration(
        contentTransition,
        other.contentTransition,
        t,
      ),
      routeEnter: _lerpDuration(routeEnter, other.routeEnter, t),
      routeExit: _lerpDuration(routeExit, other.routeExit, t),
      intro: _lerpDuration(intro, other.intro, t),
      pulse: _lerpDuration(pulse, other.pulse, t),
      focusRotation: _lerpDuration(focusRotation, other.focusRotation, t),
      screenTransition: _lerpDuration(
        screenTransition,
        other.screenTransition,
        t,
      ),
      chromeTransition: _lerpDuration(
        chromeTransition,
        other.chromeTransition,
        t,
      ),
      hintLabelTransition: _lerpDuration(
        hintLabelTransition,
        other.hintLabelTransition,
        t,
      ),
      entryTransition: _lerpDuration(entryTransition, other.entryTransition, t),
      entryAttractFadeFraction: _lerpDouble(
        entryAttractFadeFraction,
        other.entryAttractFadeFraction,
        t,
      ),
      entrySelectionDelayFraction: _lerpDouble(
        entrySelectionDelayFraction,
        other.entrySelectionDelayFraction,
        t,
      ),
      entryPromptDelayFraction: _lerpDouble(
        entryPromptDelayFraction,
        other.entryPromptDelayFraction,
        t,
      ),
      entryPulseMinimumOpacity: _lerpDouble(
        entryPulseMinimumOpacity,
        other.entryPulseMinimumOpacity,
        t,
      ),
      routeScaleBegin: _lerpDouble(routeScaleBegin, other.routeScaleBegin, t),
      interaction: interaction.lerp(other.interaction, t),
      standardCurve: t < 0.5 ? standardCurve : other.standardCurve,
      emphasizedCurve: t < 0.5 ? emphasizedCurve : other.emphasizedCurve,
      spatialCurve: t < 0.5 ? spatialCurve : other.spatialCurve,
    );
  }
}

/// Named wave-color roles in an ambient palette.
enum ConsoleAmbientWaveTone { primary, secondary, tertiary }

/// Typed color contract for one ambient artwork concept.
///
/// Named roles replace the former positional 12-color list, so a skin author
/// cannot accidentally swap a backdrop, particle, wave, or spotlight slot.
@immutable
final class ConsoleAmbientPalette {
  const ConsoleAmbientPalette({
    required this.backdropTop,
    required this.backdropMiddle,
    required this.backdropBottom,
    required this.particlePrimary,
    required this.particleSecondary,
    required this.particleWarm,
    required this.mesh,
    required this.star,
    required this.wavePrimary,
    required this.waveSecondary,
    required this.waveTertiary,
    required this.spotlight,
  });

  final Color backdropTop;
  final Color backdropMiddle;
  final Color backdropBottom;
  final Color particlePrimary;
  final Color particleSecondary;
  final Color particleWarm;
  final Color mesh;
  final Color star;
  final Color wavePrimary;
  final Color waveSecondary;
  final Color waveTertiary;
  final Color spotlight;
}

/// One wave band in a skin-owned ambient artwork concept.
@immutable
final class ConsoleAmbientWaveSpec {
  const ConsoleAmbientWaveSpec({
    required this.tone,
    required this.baseY,
    required this.amplitude,
    required this.secondaryAmplitude,
    required this.frequency,
    required this.secondaryFrequency,
    required this.phase,
    required this.speed,
    required this.spread,
    required this.alpha,
    required this.glowWidth,
    required this.lineWidth,
    required this.hazeBlur,
  });

  final ConsoleAmbientWaveTone tone;
  final double baseY;
  final double amplitude;
  final double secondaryAmplitude;
  final double frequency;
  final double secondaryFrequency;
  final double phase;
  final double speed;
  final double spread;
  final double alpha;
  final double glowWidth;
  final double lineWidth;
  final double hazeBlur;

  List<String> validate(String path) => <String>[
    if (!_unitInterval(alpha)) '$path.alpha must be within 0...1',
    if (frequency <= 0) '$path.frequency must be positive',
    if (secondaryFrequency <= 0) '$path.secondaryFrequency must be positive',
    if (glowWidth <= 0) '$path.glowWidth must be positive',
    if (lineWidth <= 0) '$path.lineWidth must be positive',
    if (hazeBlur < 0) '$path.hazeBlur cannot be negative',
  ];

  ConsoleAmbientWaveSpec copyWith({
    ConsoleAmbientWaveTone? tone,
    double? baseY,
    double? amplitude,
    double? secondaryAmplitude,
    double? frequency,
    double? secondaryFrequency,
    double? phase,
    double? speed,
    double? spread,
    double? alpha,
    double? glowWidth,
    double? lineWidth,
    double? hazeBlur,
  }) => ConsoleAmbientWaveSpec(
    tone: tone ?? this.tone,
    baseY: baseY ?? this.baseY,
    amplitude: amplitude ?? this.amplitude,
    secondaryAmplitude: secondaryAmplitude ?? this.secondaryAmplitude,
    frequency: frequency ?? this.frequency,
    secondaryFrequency: secondaryFrequency ?? this.secondaryFrequency,
    phase: phase ?? this.phase,
    speed: speed ?? this.speed,
    spread: spread ?? this.spread,
    alpha: alpha ?? this.alpha,
    glowWidth: glowWidth ?? this.glowWidth,
    lineWidth: lineWidth ?? this.lineWidth,
    hazeBlur: hazeBlur ?? this.hazeBlur,
  );
}

/// Topology and density for one ambient artwork concept.
@immutable
final class ConsoleAmbientGeometrySpec {
  const ConsoleAmbientGeometrySpec({
    required this.seed,
    required this.backdropCenter,
    required this.particleAlpha,
    required this.meshAlpha,
    required this.starAlpha,
    required this.warmParticleChance,
    required this.warmAccentAlpha,
    required this.motionScale,
    required this.particleCount,
    required this.falloutCount,
    required this.starCount,
    required this.waves,
  });

  final int seed;
  final Alignment backdropCenter;
  final double particleAlpha;
  final double meshAlpha;
  final double starAlpha;
  final double warmParticleChance;
  final double warmAccentAlpha;
  final double motionScale;
  final int particleCount;
  final int falloutCount;
  final int starCount;
  final List<ConsoleAmbientWaveSpec> waves;

  List<String> validate(String path) => <String>[
    if (!_unitInterval(particleAlpha))
      '$path.particleAlpha must be within 0...1',
    if (!_unitInterval(meshAlpha)) '$path.meshAlpha must be within 0...1',
    if (!_unitInterval(starAlpha)) '$path.starAlpha must be within 0...1',
    if (!_unitInterval(warmParticleChance))
      '$path.warmParticleChance must be within 0...1',
    if (!_unitInterval(warmAccentAlpha))
      '$path.warmAccentAlpha must be within 0...1',
    if (motionScale < 0) '$path.motionScale cannot be negative',
    if (particleCount < 0 || particleCount > 600)
      '$path.particleCount must be within 0...600',
    if (falloutCount < 0 || falloutCount > 200)
      '$path.falloutCount must be within 0...200',
    if (starCount < 0 || starCount > 150)
      '$path.starCount must be within 0...150',
    if (waves.isEmpty) '$path requires at least one wave',
    for (var index = 0; index < waves.length; index++)
      ...waves[index].validate('$path.waves[$index]'),
  ];

  ConsoleAmbientGeometrySpec copyWith({
    int? seed,
    Alignment? backdropCenter,
    double? particleAlpha,
    double? meshAlpha,
    double? starAlpha,
    double? warmParticleChance,
    double? warmAccentAlpha,
    double? motionScale,
    int? particleCount,
    int? falloutCount,
    int? starCount,
    List<ConsoleAmbientWaveSpec>? waves,
  }) => ConsoleAmbientGeometrySpec(
    seed: seed ?? this.seed,
    backdropCenter: backdropCenter ?? this.backdropCenter,
    particleAlpha: particleAlpha ?? this.particleAlpha,
    meshAlpha: meshAlpha ?? this.meshAlpha,
    starAlpha: starAlpha ?? this.starAlpha,
    warmParticleChance: warmParticleChance ?? this.warmParticleChance,
    warmAccentAlpha: warmAccentAlpha ?? this.warmAccentAlpha,
    motionScale: motionScale ?? this.motionScale,
    particleCount: particleCount ?? this.particleCount,
    falloutCount: falloutCount ?? this.falloutCount,
    starCount: starCount ?? this.starCount,
    waves: waves ?? this.waves,
  );
}

/// Shared renderer tuning for the procedural ambient artwork.
///
/// This deliberately groups implementation-level visual knobs under one
/// artwork contract instead of flattening them into screen-prefixed tokens.
@immutable
final class ConsoleAmbientRenderSpec {
  const ConsoleAmbientRenderSpec({
    required this.backdropRadius,
    required this.backdropMiddleStop,
    required this.pathSegments,
    required this.particleBandOffsetScale,
    required this.particleRadius,
    required this.particleAlpha,
    required this.particleSpeed,
    required this.particleShimmerFrequency,
    required this.particleShimmerBase,
    required this.particleShimmerAmplitude,
    required this.primaryParticleThreshold,
    required this.layerThresholds,
    required this.falloutWarmChanceScale,
    required this.falloutDrop,
    required this.falloutRadius,
    required this.falloutAlpha,
    required this.falloutSpeed,
    required this.falloutSwayFrequency,
    required this.falloutSwayScale,
    required this.falloutFadeScale,
    required this.starY,
    required this.starRadius,
    required this.starAlpha,
    required this.starTwinkleFrequency,
    required this.starWarmChance,
    required this.starTwinkleBase,
    required this.starTwinkleAmplitude,
    required this.starDriftFrequency,
    required this.starDriftScale,
    required this.hazeAlphaScale,
    required this.meshMinimumStroke,
    required this.meshStrokeScale,
    required this.meshOffsets,
    required this.ribbonGlowAlphaScale,
    required this.ribbonGlowBlur,
    required this.ribbonGlowMinimumStroke,
    required this.ribbonGlowWidthScale,
    required this.ribbonLineMinimumStroke,
    required this.ribbonLineAlphaScale,
    required this.ribbonHighlightAlphaScale,
    required this.ribbonTrailingAlphaScale,
    required this.ribbonGradientStops,
    required this.warmPathVerticalOffsetScale,
    required this.warmPathStart,
    required this.warmPathEnd,
    required this.warmGlowBlur,
    required this.warmMinimumStroke,
    required this.warmStrokeScale,
    required this.warmGradientStops,
    required this.waveSecondaryTimeScale,
    required this.atmosphereSpotlightCenter,
    required this.atmosphereSpotlightRadius,
    required this.atmosphereSpotlightAlpha,
    required this.atmosphereWarmAlphaScale,
    required this.atmosphereSpotlightStops,
    required this.atmosphereShadowTopAlpha,
    required this.atmosphereShadowBottomAlpha,
    required this.atmosphereShadowMiddleStop,
  });

  final double backdropRadius;
  final double backdropMiddleStop;
  final int pathSegments;
  final double particleBandOffsetScale;
  final RangeValues particleRadius;
  final RangeValues particleAlpha;
  final RangeValues particleSpeed;
  final RangeValues particleShimmerFrequency;
  final double particleShimmerBase;
  final double particleShimmerAmplitude;
  final double primaryParticleThreshold;
  final List<double> layerThresholds;
  final double falloutWarmChanceScale;
  final RangeValues falloutDrop;
  final RangeValues falloutRadius;
  final RangeValues falloutAlpha;
  final RangeValues falloutSpeed;
  final double falloutSwayFrequency;
  final double falloutSwayScale;
  final double falloutFadeScale;
  final RangeValues starY;
  final RangeValues starRadius;
  final RangeValues starAlpha;
  final RangeValues starTwinkleFrequency;
  final double starWarmChance;
  final double starTwinkleBase;
  final double starTwinkleAmplitude;
  final double starDriftFrequency;
  final double starDriftScale;
  final double hazeAlphaScale;
  final double meshMinimumStroke;
  final double meshStrokeScale;
  final List<double> meshOffsets;
  final double ribbonGlowAlphaScale;
  final double ribbonGlowBlur;
  final double ribbonGlowMinimumStroke;
  final double ribbonGlowWidthScale;
  final double ribbonLineMinimumStroke;
  final double ribbonLineAlphaScale;
  final double ribbonHighlightAlphaScale;
  final double ribbonTrailingAlphaScale;
  final List<double> ribbonGradientStops;
  final double warmPathVerticalOffsetScale;
  final double warmPathStart;
  final double warmPathEnd;
  final double warmGlowBlur;
  final double warmMinimumStroke;
  final double warmStrokeScale;
  final List<double> warmGradientStops;
  final double waveSecondaryTimeScale;
  final Alignment atmosphereSpotlightCenter;
  final double atmosphereSpotlightRadius;
  final double atmosphereSpotlightAlpha;
  final double atmosphereWarmAlphaScale;
  final List<double> atmosphereSpotlightStops;
  final double atmosphereShadowTopAlpha;
  final double atmosphereShadowBottomAlpha;
  final double atmosphereShadowMiddleStop;

  List<String> validate(String path) => <String>[
    if (pathSegments < 2 || pathSegments > 512)
      '$path.pathSegments must be within 2...512',
    if (!_orderedStops(layerThresholds, 2))
      '$path.layerThresholds must contain 2 ascending unit stops',
    if (!_orderedStops(ribbonGradientStops, 5))
      '$path.ribbonGradientStops must contain 5 ascending unit stops',
    if (!_orderedStops(warmGradientStops, 3))
      '$path.warmGradientStops must contain 3 ascending unit stops',
    if (!_orderedStops(atmosphereSpotlightStops, 3))
      '$path.atmosphereSpotlightStops must contain 3 ascending unit stops',
    if (!_unitInterval(backdropMiddleStop))
      '$path.backdropMiddleStop must be within 0...1',
    if (!_unitInterval(primaryParticleThreshold))
      '$path.primaryParticleThreshold must be within 0...1',
    if (!_unitInterval(starWarmChance))
      '$path.starWarmChance must be within 0...1',
    if (!_unitInterval(atmosphereSpotlightAlpha))
      '$path.atmosphereSpotlightAlpha must be within 0...1',
  ];
}

/// Rendering contract for placeholder cover plates and detail backdrops.
@immutable
final class ConsoleCoverArtworkSpec {
  const ConsoleCoverArtworkSpec({
    required this.plateGradientMiddleStop,
    required this.plateRingSizeFactor,
    required this.plateRingAlpha,
    required this.plateMonogramInsetFactor,
    required this.plateMonogramSizeFactor,
    required this.plateMonogramAlpha,
    required this.hatchAlpha,
    required this.hatchStrokeWidth,
    required this.hatchSpacing,
    required this.backdropGradientMiddleStop,
    required this.backdropMonogramAlignment,
    required this.backdropMonogramAlpha,
    required this.shadeCenter,
    required this.shadeRadius,
    required this.shadeToneAlpha,
    required this.shadeOuterAlpha,
    required this.shadeRadialMiddleStop,
    required this.shadeTopAlpha,
    required this.shadeMiddleAlpha,
    required this.shadeBottomAlpha,
    required this.shadeLinearStops,
  });

  final double plateGradientMiddleStop;
  final double plateRingSizeFactor;
  final double plateRingAlpha;
  final double plateMonogramInsetFactor;
  final double plateMonogramSizeFactor;
  final double plateMonogramAlpha;
  final double hatchAlpha;
  final double hatchStrokeWidth;
  final double hatchSpacing;
  final double backdropGradientMiddleStop;
  final Alignment backdropMonogramAlignment;
  final double backdropMonogramAlpha;
  final Alignment shadeCenter;
  final double shadeRadius;
  final double shadeToneAlpha;
  final double shadeOuterAlpha;
  final double shadeRadialMiddleStop;
  final double shadeTopAlpha;
  final double shadeMiddleAlpha;
  final double shadeBottomAlpha;
  final List<double> shadeLinearStops;
}

/// Skin-owned palettes and specifications used by custom-painted artwork.
///
/// Artwork configuration deliberately snaps at the midpoint. Interpolating
/// particle topology and content-tone families is not a shipping requirement.
@immutable
final class ConsoleArtworkTheme extends ThemeExtension<ConsoleArtworkTheme> {
  const ConsoleArtworkTheme({
    required this.ambientPalettes,
    required this.ambientGeometry,
    required this.ambientRender,
    required this.ambientIntensity,
    required this.focusGradient,
    required this.coverToneFamilies,
    required this.coverRendering,
    required this.coverPlateBackdrop,
    required this.coverMonogramStyle,
    required this.highlight,
    required this.shadow,
    required this.avatarRim,
    required this.verificationCodeStyle,
    required this.entryBackdrop,
    required this.entryPromptAccent,
    required this.entryStartPromptStyle,
  });

  /// Per-concept color mappings consumed by the ambient painter. Each palette
  /// owns backdrop, particle, mesh, star, wave, and spotlight roles.
  final List<ConsoleAmbientPalette> ambientPalettes;
  final List<ConsoleAmbientGeometrySpec> ambientGeometry;
  final ConsoleAmbientRenderSpec ambientRender;
  final double ambientIntensity;
  final List<Color> focusGradient;
  final List<List<Color>> coverToneFamilies;
  final ConsoleCoverArtworkSpec coverRendering;
  final Color coverPlateBackdrop;
  final TextStyle coverMonogramStyle;
  final Color highlight;
  final Color shadow;
  final Color avatarRim;
  final TextStyle verificationCodeStyle;
  final Color entryBackdrop;
  final Color entryPromptAccent;
  final TextStyle entryStartPromptStyle;

  /// Returns authoring errors without partially applying an invalid artwork
  /// definition. The future external-theme resolver uses this same contract.
  List<String> validate() => <String>[
    if (ambientPalettes.isEmpty) 'ambientPalettes cannot be empty',
    if (ambientPalettes.length != ambientGeometry.length)
      'ambientPalettes and ambientGeometry must have equal lengths',
    if (coverToneFamilies.isEmpty) 'coverToneFamilies cannot be empty',
    if (coverToneFamilies.any((family) => family.length < 3))
      'each coverToneFamily requires at least 3 colors',
    ...ambientRender.validate('ambientRender'),
    for (var index = 0; index < ambientGeometry.length; index++)
      ...ambientGeometry[index].validate('ambientGeometry[$index]'),
  ];

  @override
  ConsoleArtworkTheme copyWith({
    List<ConsoleAmbientPalette>? ambientPalettes,
    List<ConsoleAmbientGeometrySpec>? ambientGeometry,
    ConsoleAmbientRenderSpec? ambientRender,
    double? ambientIntensity,
    List<Color>? focusGradient,
    List<List<Color>>? coverToneFamilies,
    ConsoleCoverArtworkSpec? coverRendering,
    Color? coverPlateBackdrop,
    TextStyle? coverMonogramStyle,
    Color? highlight,
    Color? shadow,
    Color? avatarRim,
    TextStyle? verificationCodeStyle,
    Color? entryBackdrop,
    Color? entryPromptAccent,
    TextStyle? entryStartPromptStyle,
  }) => ConsoleArtworkTheme(
    ambientPalettes: ambientPalettes ?? this.ambientPalettes,
    ambientGeometry: ambientGeometry ?? this.ambientGeometry,
    ambientRender: ambientRender ?? this.ambientRender,
    ambientIntensity: ambientIntensity ?? this.ambientIntensity,
    focusGradient: focusGradient ?? this.focusGradient,
    coverToneFamilies: coverToneFamilies ?? this.coverToneFamilies,
    coverRendering: coverRendering ?? this.coverRendering,
    coverPlateBackdrop: coverPlateBackdrop ?? this.coverPlateBackdrop,
    coverMonogramStyle: coverMonogramStyle ?? this.coverMonogramStyle,
    highlight: highlight ?? this.highlight,
    shadow: shadow ?? this.shadow,
    avatarRim: avatarRim ?? this.avatarRim,
    verificationCodeStyle: verificationCodeStyle ?? this.verificationCodeStyle,
    entryBackdrop: entryBackdrop ?? this.entryBackdrop,
    entryPromptAccent: entryPromptAccent ?? this.entryPromptAccent,
    entryStartPromptStyle: entryStartPromptStyle ?? this.entryStartPromptStyle,
  );

  @override
  ConsoleArtworkTheme lerp(
    ThemeExtension<ConsoleArtworkTheme>? other,
    double t,
  ) {
    if (other is! ConsoleArtworkTheme) return this;
    return t < 0.5 ? this : other;
  }
}
