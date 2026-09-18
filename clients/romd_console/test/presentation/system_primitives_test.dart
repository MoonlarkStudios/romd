import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_action_button.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';
import 'package:romd_console/src/presentation/widgets/console_page_band.dart';
import 'package:romd_console/src/presentation/widgets/console_status_state.dart';

Future<void> pumpThemed(
  WidgetTester tester,
  Widget child, {
  bool disableAnimations = false,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: RomdSkins.baselineDark(),
      themeAnimationDuration: Duration.zero,
      builder: (context, appChild) => MediaQuery(
        data: MediaQuery.of(
          context,
        ).copyWith(disableAnimations: disableAnimations),
        child: appChild!,
      ),
      home: Scaffold(body: Center(child: child)),
    ),
  );
  await tester.pump();
}

void main() {
  setUp(() {
    FocusManager.instance.highlightStrategy =
        FocusHighlightStrategy.alwaysTraditional;
  });
  tearDown(() {
    FocusManager.instance.highlightStrategy = FocusHighlightStrategy.automatic;
  });

  group('ConsolePageBand', () {
    testWidgets('renders eyebrow trail, title, and trailing cluster', (
      tester,
    ) async {
      await pumpThemed(
        tester,
        const ConsolePageBand(
          title: 'Storage',
          eyebrow: 'SETTINGS',
          trailing: <Widget>[Text('cluster')],
        ),
      );

      expect(find.text('SETTINGS'), findsOneWidget);
      expect(find.text('Storage'), findsOneWidget);
      expect(find.text('cluster'), findsOneWidget);
    });

    testWidgets('anchoring fades in the chrome fill', (tester) async {
      Color fillOf() {
        final container = tester.widget<AnimatedContainer>(
          find.byType(AnimatedContainer).first,
        );
        return (container.decoration! as BoxDecoration).color!;
      }

      await pumpThemed(tester, const ConsolePageBand(title: 'Catalog'));
      expect(fillOf(), Colors.transparent);

      await pumpThemed(
        tester,
        const ConsolePageBand(title: 'Catalog', anchored: true),
      );
      final colors = RomdSkins.baselineDark().extension<ConsoleColors>()!;
      expect(fillOf(), colors.footerSurface);
    });
  });

  group('ConsoleSegmentedSwitcher', () {
    testWidgets('activating an unselected segment reports its index', (
      tester,
    ) async {
      final selections = <int>[];
      await pumpThemed(
        tester,
        ConsoleSegmentedSwitcher(
          segments: const <ConsoleSegment>[
            ConsoleSegment(label: 'Featured'),
            ConsoleSegment(label: 'Browse'),
          ],
          selectedIndex: 0,
          onSelected: selections.add,
        ),
      );

      await tester.tap(find.text('Browse'));
      await tester.pump();
      expect(selections, <int>[1]);
    });

    testWidgets('segments join focus traversal and activate on Enter', (
      tester,
    ) async {
      final selections = <int>[];
      final node = FocusNode(debugLabel: 'browse-segment');
      addTearDown(node.dispose);
      await pumpThemed(
        tester,
        ConsoleSegmentedSwitcher(
          segments: <ConsoleSegment>[
            const ConsoleSegment(label: 'Featured'),
            ConsoleSegment(label: 'Browse', focusNode: node),
          ],
          selectedIndex: 0,
          onSelected: selections.add,
        ),
      );

      node.requestFocus();
      await tester.pump();
      expect(node.hasFocus, isTrue);

      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pump();
      expect(selections, <int>[1]);
    });
  });

  group('ConsoleActionButton', () {
    testWidgets('kinds map to the themed Material button families', (
      tester,
    ) async {
      await pumpThemed(
        tester,
        Row(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            ConsoleActionButton(
              label: 'Play',
              kind: ConsoleActionKind.primary,
              onPressed: () {},
            ),
            ConsoleActionButton(label: 'Manage', onPressed: () {}),
            ConsoleActionButton(
              label: 'Media',
              kind: ConsoleActionKind.quiet,
              onPressed: () {},
            ),
          ],
        ),
      );

      expect(find.widgetWithText(FilledButton, 'Play'), findsOneWidget);
      expect(find.widgetWithText(OutlinedButton, 'Manage'), findsOneWidget);
      expect(find.widgetWithText(TextButton, 'Media'), findsOneWidget);
    });

    testWidgets('busy replaces the icon slot with a progress indicator', (
      tester,
    ) async {
      await pumpThemed(
        tester,
        const ConsoleActionButton(
          label: 'Preparing',
          kind: ConsoleActionKind.primary,
          icon: Icons.play_arrow,
          busy: true,
          onPressed: null,
        ),
      );

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.byIcon(Icons.play_arrow), findsNothing);
      // Busy without an icon still presents the indicator.
      await pumpThemed(
        tester,
        const ConsoleActionButton(
          label: 'Working',
          busy: true,
          onPressed: null,
        ),
      );
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('press fires and disabled ignores input', (tester) async {
      var presses = 0;
      await pumpThemed(
        tester,
        Row(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            ConsoleActionButton(
              label: 'Play',
              kind: ConsoleActionKind.primary,
              onPressed: () => presses++,
            ),
            const ConsoleActionButton(label: 'Install', onPressed: null),
          ],
        ),
      );

      await tester.tap(find.text('Play'));
      await tester.tap(find.text('Install'), warnIfMissed: false);
      await tester.pump();
      expect(presses, 1);
    });

    testWidgets('destructive secondary borrows the warning role', (
      tester,
    ) async {
      await pumpThemed(
        tester,
        ConsoleActionButton(
          label: 'Remove',
          destructive: true,
          onPressed: () {},
        ),
      );

      final button = tester.widget<OutlinedButton>(find.byType(OutlinedButton));
      final colors = RomdSkins.baselineDark().extension<ConsoleColors>()!;
      expect(
        button.style?.foregroundColor?.resolve(const <WidgetState>{}),
        colors.warning,
      );
      expect(
        button.style?.side?.resolve(const <WidgetState>{})?.color,
        colors.warning,
      );
    });
  });

  group('ConsoleStatusState', () {
    testWidgets('loading holds a static arc under reduced motion', (
      tester,
    ) async {
      await pumpThemed(
        tester,
        const ConsoleStatusState(loading: true, title: 'Fetching'),
        disableAnimations: true,
      );

      final indicator = tester.widget<CircularProgressIndicator>(
        find.byType(CircularProgressIndicator),
      );
      expect(indicator.value, isNotNull);
    });

    testWidgets('loading animates when motion is allowed', (tester) async {
      await pumpThemed(
        tester,
        const ConsoleStatusState(loading: true, title: 'Fetching'),
      );

      final indicator = tester.widget<CircularProgressIndicator>(
        find.byType(CircularProgressIndicator),
      );
      expect(indicator.value, isNull);
    });

    testWidgets('invitation tints the icon with the catalog accent', (
      tester,
    ) async {
      await pumpThemed(
        tester,
        const ConsoleStatusState(
          icon: Icons.auto_awesome_rounded,
          invitation: true,
          title: 'Nothing here yet',
          message: 'Games you install appear here.',
        ),
      );

      final icon = tester.widget<Icon>(find.byIcon(Icons.auto_awesome_rounded));
      final colors = RomdSkins.baselineDark().extension<ConsoleColors>()!;
      expect(icon.color, colors.catalogAccent);
      expect(find.text('Nothing here yet'), findsOneWidget);
      expect(find.text('Games you install appear here.'), findsOneWidget);
    });
  });

  group('ConsoleHint onPressed', () {
    testWidgets('tappable hints act without joining focus traversal', (
      tester,
    ) async {
      var presses = 0;
      await pumpThemed(
        tester,
        ConsoleHintBar(
          hints: <ConsoleHint>[
            ConsoleHint(
              glyph: 'Esc',
              gamepadGlyph: 'B',
              label: 'Back',
              onPressed: () => presses++,
            ),
          ],
        ),
      );

      await tester.tap(find.text('Back'));
      expect(presses, 1);

      // The pointer affordance must not add a stop to controller traversal:
      // nothing in this tree becomes focusable because a hint is tappable.
      // (Framework scopes remain; a tappable hint adds no focusable node.)
      expect(
        tester.binding.focusManager.rootScope.traversalDescendants.where(
          (node) => node is! FocusScopeNode,
        ),
        isEmpty,
      );
    });
  });
}
