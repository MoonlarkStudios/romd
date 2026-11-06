import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/discover_shell.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';

void main() {
  test('Featured chrome progress starts immersive and clamps updates', () {
    final controller = DiscoverShellController();
    addTearDown(controller.dispose);

    expect(controller.featuredChromeProgress.value, 0);
    controller.updateFeaturedChromeProgress(0.54);
    expect(controller.featuredChromeProgress.value, 0.54);
    controller.updateFeaturedChromeProgress(2);
    expect(controller.featuredChromeProgress.value, 1);
    controller.updateFeaturedChromeProgress(-1);
    expect(controller.featuredChromeProgress.value, 0);
  });

  testWidgets('section accelerators cycle Featured and Browse', (tester) async {
    final controller = DiscoverShellController();
    final handoff = DiscoverFocusHandoff();
    addTearDown(controller.dispose);
    var searches = 0;
    var filters = 0;

    await tester.pumpWidget(
      _shell(
        controller: controller,
        handoff: handoff,
        onSearch: () => searches++,
        onFilters: () => filters++,
      ),
    );
    await tester.pump();
    handoff.focusHeader(DiscoverSection.featured);
    await tester.pump();

    expect(find.text('Featured content'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.pageDown);
    await tester.pump();
    expect(controller.section, DiscoverSection.allGames);
    expect(find.text('Browse content'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.pageDown);
    await tester.pump();
    expect(controller.section, DiscoverSection.featured);

    await tester.sendKeyEvent(LogicalKeyboardKey.keyX);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyY);
    expect(searches, 1);
    expect(filters, 1);
  });

  testWidgets('shell exposes Featured and Browse beside the band title', (
    tester,
  ) async {
    tester.view
      ..devicePixelRatio = 1
      ..physicalSize = const Size(1280, 720);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final controller = DiscoverShellController(
      initialSection: DiscoverSection.allGames,
    );
    final handoff = DiscoverFocusHandoff();
    addTearDown(controller.dispose);
    var backs = 0;

    await tester.pumpWidget(
      _shell(
        controller: controller,
        handoff: handoff,
        onBack: () => backs++,
        theme: RomdSkins.baselineLight(),
      ),
    );
    await tester.pump();

    expect(find.text('Catalog'), findsOneWidget);
    expect(find.text('Featured'), findsOneWidget);
    expect(find.text('Browse'), findsOneWidget);
    expect(find.text('Systems'), findsNothing);
    expect(find.text('Browse content'), findsOneWidget);
    expect(find.byKey(const ValueKey<String>('discover-back')), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('discover-search')),
      findsOneWidget,
    );

    final title = tester.getRect(find.text('Catalog'));
    final navigation = tester.getRect(
      find.byKey(const ValueKey<String>('discover-section-navigation')),
    );
    final search = tester.getRect(
      find.byKey(const ValueKey<String>('discover-search')),
    );
    expect(title.left, closeTo(64, 0.01));
    expect(navigation.left, greaterThan(title.right));
    expect(search.left, greaterThan(navigation.right));
    expect(search.right, closeTo(1280 - 64, 0.01));

    handoff.focusHeader(DiscoverSection.allGames);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    expect(backs, 1);
    expect(tester.takeException(), isNull);
  });

  testWidgets('Featured remains immersive while Browse starts below header', (
    tester,
  ) async {
    tester.view
      ..devicePixelRatio = 1
      ..physicalSize = const Size(1280, 720);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final controller = DiscoverShellController();
    final handoff = DiscoverFocusHandoff();
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Scaffold(
          body: DiscoverShell(
            controller: controller,
            focusHandoff: handoff,
            onSearch: () {},
            onBack: () {},
            slots: DiscoverFeatureSlots(
              featured: (_) => const ColoredBox(
                key: ValueKey<String>('featured-layer'),
                color: Colors.red,
              ),
              allGames: (_) => const ColoredBox(
                key: ValueKey<String>('browse-layer'),
                color: Colors.green,
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    final header = tester.getRect(
      find.byKey(const ValueKey<String>('discover-header-chrome')),
    );
    final featured = tester.getRect(
      find.byKey(const ValueKey<String>('featured-layer')),
    );
    expect(featured.top, 0);
    expect(featured.top, lessThan(header.bottom));

    controller.select(DiscoverSection.allGames);
    await tester.pump();
    final browse = tester.getRect(
      find.byKey(const ValueKey<String>('browse-layer')),
    );
    expect(browse.top, greaterThan(header.bottom));
  });

  testWidgets('compact shell preserves non-overlapping header zones', (
    tester,
  ) async {
    tester.view
      ..devicePixelRatio = 1
      ..physicalSize = const Size(800, 600);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final controller = DiscoverShellController();
    final handoff = DiscoverFocusHandoff();
    addTearDown(controller.dispose);

    await tester.pumpWidget(_shell(controller: controller, handoff: handoff));
    await tester.pump();

    final navigation = tester.getRect(
      find.byKey(const ValueKey<String>('discover-section-navigation')),
    );
    final search = tester.getRect(
      find.byKey(const ValueKey<String>('discover-search')),
    );
    expect(find.text('Catalog'), findsNothing);
    expect(navigation.right, lessThan(search.left));
  });

  testWidgets('focused inactive Browse does not change active Featured tab', (
    tester,
  ) async {
    final controller = DiscoverShellController();
    final handoff = DiscoverFocusHandoff();
    addTearDown(controller.dispose);
    await tester.pumpWidget(_shell(controller: controller, handoff: handoff));
    await tester.pump();

    handoff.focusHeader(DiscoverSection.allGames);
    await tester.pump(const Duration(milliseconds: 200));

    expect(controller.section, DiscoverSection.featured);
    final activeDecoration = _sectionDecoration(
      tester,
      DiscoverSection.featured,
    );
    final focusedDecoration = _sectionDecoration(
      tester,
      DiscoverSection.allGames,
    );
    final activeBorder = activeDecoration.border! as Border;
    final focusedBorder = focusedDecoration.border! as Border;
    expect(activeBorder.bottom.color, isNot(Colors.transparent));
    expect(focusedBorder.bottom.color, Colors.transparent);
    expect(focusedDecoration.color, isNot(Colors.transparent));
  });

  testWidgets('header traverses Featured, Browse, and Search explicitly', (
    tester,
  ) async {
    final controller = DiscoverShellController();
    final handoff = DiscoverFocusHandoff();
    final featuredContent = FocusNode(debugLabel: 'featured-content');
    final browseContent = FocusNode(debugLabel: 'browse-content');
    addTearDown(controller.dispose);
    addTearDown(featuredContent.dispose);
    addTearDown(browseContent.dispose);
    handoff
      ..attachContent(DiscoverSection.featured, featuredContent.requestFocus)
      ..attachContent(DiscoverSection.allGames, browseContent.requestFocus);

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Scaffold(
          body: DiscoverShell(
            controller: controller,
            focusHandoff: handoff,
            onSearch: () {},
            onBack: () {},
            slots: DiscoverFeatureSlots(
              featured: (_) => Focus(
                focusNode: featuredContent,
                autofocus: true,
                child: const Text('Featured content'),
              ),
              allGames: (_) => Focus(
                focusNode: browseContent,
                child: const Text('Browse content'),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    handoff.focusHeader(DiscoverSection.featured);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(controller.section, DiscoverSection.featured);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'discover-tab-browse',
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(controller.section, DiscoverSection.allGames);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'discover-search-action',
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'discover-tab-browse',
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'browse-content',
    );
  });

  testWidgets('Tab order reaches Browse and Search', (tester) async {
    final controller = DiscoverShellController();
    final handoff = DiscoverFocusHandoff();
    addTearDown(controller.dispose);
    await tester.pumpWidget(_shell(controller: controller, handoff: handoff));
    await tester.pump();

    handoff.focusHeader(DiscoverSection.featured);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'discover-tab-browse',
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'discover-search-action',
    );
  });
}

Widget _shell({
  required DiscoverShellController controller,
  required DiscoverFocusHandoff handoff,
  ThemeData? theme,
  VoidCallback? onSearch,
  VoidCallback? onFilters,
  VoidCallback? onBack,
}) => MaterialApp(
  theme: theme ?? RomdSkins.baselineDark(),
  home: Scaffold(
    body: DiscoverShell(
      controller: controller,
      focusHandoff: handoff,
      onSearch: onSearch ?? () {},
      onFilters: onFilters,
      onBack: onBack ?? () {},
      slots: DiscoverFeatureSlots(
        featured: (_) => const Text('Featured content'),
        allGames: (_) => const Text('Browse content'),
      ),
    ),
  ),
);

BoxDecoration _sectionDecoration(
  WidgetTester tester,
  DiscoverSection section,
) =>
    tester
            .widget<AnimatedContainer>(
              find.descendant(
                of: find.byKey(
                  ValueKey<String>('discover-section-${section.name}'),
                ),
                matching: find.byType(AnimatedContainer),
              ),
            )
            .decoration
        as BoxDecoration;
