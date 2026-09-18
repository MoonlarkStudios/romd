import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';
import 'connection_status_indicator.dart';
import 'console_clock.dart';
import 'console_focus_ring.dart';
import 'console_focusable.dart';

/// The compact "Waterline" page band: the one-line chrome row every non-home
/// surface mounts above its content. Transparent at rest so the ambient stage
/// runs uninterrupted; [anchored] fades in chrome fill and a bottom hairline
/// when content scrolls beneath it.
///
/// Anatomy, left to right along the shared [ConsoleLayoutTheme.screenGutter]
/// anchor: eyebrow-trailed title, optional inline section [switcher], then
/// right-aligned [trailing] status items (see [ConsoleBandStatusCluster]).
/// The band carries no back affordance by design — Back lives in the footer
/// hint bar, which is the controller-first vocabulary for leaving a surface.
final class ConsolePageBand extends StatelessWidget {
  const ConsolePageBand({
    required this.title,
    this.eyebrow,
    this.switcher,
    this.trailing = const <Widget>[],
    this.anchored = false,
    super.key,
  });

  /// Surface name, set in [ConsoleTextRoles.sectionHeading].
  final String title;

  /// Optional parent-context trail rendered above the title (for example
  /// `SETTINGS` over `Storage`). Uppercase mono eyebrow, never a masthead.
  final String? eyebrow;

  /// Optional inline section switcher; see [ConsoleSegmentedSwitcher].
  final Widget? switcher;

  /// Right-aligned status items, laid out with the band's cluster gap.
  final List<Widget> trailing;

  /// Whether content is currently scrolled beneath the band.
  final bool anchored;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final band = layout.pageBand;

    final titleCluster = eyebrow == null
        ? Text(title, style: context.text.sectionHeading)
        : Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Text(
                eyebrow!,
                style: context.text.eyebrow.copyWith(color: colors.textFaint),
              ),
              SizedBox(height: band.eyebrowGap),
              Text(title, style: context.text.sectionHeading),
            ],
          );

    return AnimatedContainer(
      duration: motion.resolve(context, motion.chromeTransition),
      curve: motion.standardCurve,
      // Fixed at the band height for ordinary text; grows for large text
      // scales instead of clipping the title.
      constraints: BoxConstraints(minHeight: band.height),
      alignment: Alignment.centerLeft,
      padding: EdgeInsets.symmetric(
        horizontal: layout.screenGutter,
        vertical: layout.xs,
      ),
      decoration: BoxDecoration(
        color: anchored ? colors.footerSurface : Colors.transparent,
        border: Border(
          bottom: BorderSide(
            color: anchored ? colors.hairline : Colors.transparent,
            width: layout.hairlineStroke,
          ),
        ),
      ),
      child: Row(
        children: <Widget>[
          titleCluster,
          if (switcher case final switcher?) ...<Widget>[
            SizedBox(width: band.switcherGap),
            switcher,
          ],
          const Spacer(),
          for (var index = 0; index < trailing.length; index++) ...<Widget>[
            if (index != 0) SizedBox(width: band.clusterGap),
            trailing[index],
          ],
        ],
      ),
    );
  }
}

/// The band's canonical trailing cluster: connection status plus the quiet
/// clock, in that order everywhere, so time and connectivity have one home
/// instead of wandering per screen.
final class ConsoleBandStatusCluster extends StatelessWidget {
  const ConsoleBandStatusCluster({
    required this.connectionStatus,
    this.serverHost,
    this.showClock = true,
    this.clockNow,
    super.key,
  });

  final ConnectionStatus connectionStatus;
  final String? serverHost;
  final bool showClock;

  /// Overridable clock source for tests and goldens.
  final DateTime Function()? clockNow;

  @override
  Widget build(BuildContext context) {
    final band = context.layout.pageBand;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        ConnectionStatusIndicator(
          status: connectionStatus,
          serverHost: serverHost,
        ),
        if (showClock) ...<Widget>[
          SizedBox(width: band.clusterGap),
          ConsoleClock(now: clockNow),
        ],
      ],
    );
  }
}

/// One selectable section in a [ConsoleSegmentedSwitcher].
final class ConsoleSegment {
  const ConsoleSegment({required this.label, this.focusNode});

  final String label;
  final FocusNode? focusNode;
}

/// Pill-shaped segmented section switcher for the page band. Selection is a
/// persistent fill ([ConsoleColors.selectionFill]); focus is the shared teal
/// ring. Gold plays no role here — it is reserved for eyebrows and
/// invitations.
final class ConsoleSegmentedSwitcher extends StatelessWidget {
  const ConsoleSegmentedSwitcher({
    required this.segments,
    required this.selectedIndex,
    required this.onSelected,
    super.key,
  });

  final List<ConsoleSegment> segments;
  final int selectedIndex;
  final ValueChanged<int> onSelected;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final band = layout.pageBand;
    final pill = BorderRadius.circular(layout.pillRadius);

    return Container(
      padding: EdgeInsets.all(band.segmentInset),
      decoration: BoxDecoration(
        borderRadius: pill,
        border: Border.all(
          color: colors.hairline,
          width: layout.hairlineStroke,
        ),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          for (var index = 0; index < segments.length; index++)
            ConsoleFocusable(
              focusNode: segments[index].focusNode,
              selected: index == selectedIndex,
              semanticLabel: segments[index].label,
              onPressed: () => onSelected(index),
              builder: (context, focused) => ConsoleFocusRing(
                focused: focused,
                borderRadius: layout.pillRadius,
                restBorderColor: Colors.transparent,
                child: AnimatedContainer(
                  duration: motion.resolve(context, motion.selection),
                  curve: motion.standardCurve,
                  padding: band.segmentPadding,
                  decoration: BoxDecoration(
                    color: index == selectedIndex
                        ? colors.selectionFill
                        : Colors.transparent,
                    borderRadius: pill,
                    border: Border.all(
                      color: index == selectedIndex
                          ? colors.selectionBorder
                          : Colors.transparent,
                      width: layout.hairlineStroke,
                    ),
                  ),
                  child: Text(
                    segments[index].label,
                    style: context.text.action.copyWith(
                      color: index == selectedIndex
                          ? colors.textStrong
                          : colors.textMuted,
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}
