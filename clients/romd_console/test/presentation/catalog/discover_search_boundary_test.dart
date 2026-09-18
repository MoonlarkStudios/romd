import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/input/console_intents.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/catalog/discover_catalog_session.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/search/discover_search_boundary.dart';
import 'package:romd_console/src/presentation/catalog/search/discover_search_route.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';

void main() {
  testWidgets(
    'hardware typing debounces for 300ms and filters submit immediately',
    (tester) async {
      final searches = <DiscoverSearchRequest>[];
      await _pumpSearch(
        tester,
        results: const DiscoverGamePageSnapshot(),
        focus: const DiscoverSearchFocusSnapshot(),
        onSearch: searches.add,
      );

      await tester.sendKeyDownEvent(LogicalKeyboardKey.keyM, character: 'm');
      await tester.sendKeyUpEvent(LogicalKeyboardKey.keyM);
      await tester.pump(const Duration(milliseconds: 299));
      expect(searches, isEmpty);

      await tester.pump(const Duration(milliseconds: 1));
      expect(searches, hasLength(1));
      expect(searches.single.query, 'm');
      expect(searches.single.platformId, isNull);
      expect(searches.single.completeness, DiscoverReleaseCompleteness.any);

      await tester.tap(
        find.byKey(const ValueKey<String>('search-system-filter')),
      );
      await tester.pump();
      expect(searches.last.platformId, 'snes');

      await tester.tap(
        find.byKey(const ValueKey<String>('search-release-filter')),
      );
      await tester.pump();
      expect(searches.last.completeness, DiscoverReleaseCompleteness.complete);
    },
  );

  testWidgets(
    'arrows traverse keyboard, filters, and results without a shortcut',
    (tester) async {
      await _pumpSearch(
        tester,
        results: _readyResults,
        focus: const DiscoverSearchFocusSnapshot(
          query: 'star',
          keyboardKeyIndex: 6,
        ),
      );

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-key-G',
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-filter-system',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-filter-release',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-result-star-1',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-key-U',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.pageDown);
      await tester.pump();
      final resultFocus = tester.binding.focusManager.primaryFocus?.debugLabel;
      expect(resultFocus, startsWith('search-result-'));

      await tester.sendKeyEvent(LogicalKeyboardKey.pageDown);
      await tester.pump();
      expect(tester.binding.focusManager.primaryFocus?.debugLabel, resultFocus);

      Actions.invoke(
        tester.element(
          find.byKey(const ValueKey<String>('search-result-star-1')),
        ),
        const ShowDetailsIntent(),
      );
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-filter-system',
      );
    },
  );

  testWidgets(
    'controller directional intents cross directly into the spatially aligned '
    'result row',
    (tester) async {
      await _pumpSearch(
        tester,
        results: _readyResults,
        focus: const DiscoverSearchFocusSnapshot(
          query: 'star',
          keyboardKeyIndex: 20,
        ),
      );

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-key-U',
      );
      Actions.invoke(
        tester.element(find.byKey(const ValueKey<String>('search-key-U'))),
        const DirectionalFocusIntent(TraversalDirection.right),
      );
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-result-star-1',
      );

      Actions.invoke(
        tester.element(
          find.byKey(const ValueKey<String>('search-result-star-1')),
        ),
        const DirectionalFocusIntent(TraversalDirection.up),
      );
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-filter-system',
      );
    },
  );

  testWidgets('spatial traversal ignores unattached lazy-grid result nodes', (
    tester,
  ) async {
    final manyGames = List<ConsoleGame>.generate(
      60,
      (index) => _replacementGame('lazy-${index + 1}'),
    );
    await _pumpSearch(
      tester,
      results: DiscoverGamePageSnapshot(
        state: DiscoverLoadState.ready,
        items: manyGames,
      ),
      focus: const DiscoverSearchFocusSnapshot(
        query: 'lazy',
        keyboardKeyIndex: 6,
      ),
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();

    expect(tester.takeException(), isNull);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'search-filter-system',
    );
  });

  testWidgets('restores result focus and exposes an honest paged count', (
    tester,
  ) async {
    await _pumpSearch(
      tester,
      results: _readyResults,
      focus: const DiscoverSearchFocusSnapshot(
        query: 'star',
        zone: DiscoverSearchZone.results,
        resultId: 'star-2',
      ),
    );

    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'search-result-star-2',
    );
    expect(find.text('6+ RESULTS'), findsOneWidget);
  });

  testWidgets(
    'hardware typing and disposal preserve locally focused result state',
    (tester) async {
      final remembered = <DiscoverSearchFocusSnapshot>[];
      await _pumpSearch(
        tester,
        results: _readyResults,
        focus: const DiscoverSearchFocusSnapshot(
          query: 'star',
          zone: DiscoverSearchZone.results,
          resultId: 'star-2',
        ),
        onRememberFocus: remembered.add,
      );

      await tester.sendKeyDownEvent(LogicalKeyboardKey.keyX, character: 'x');
      await tester.sendKeyUpEvent(LogicalKeyboardKey.keyX);
      await tester.pump();
      expect(remembered.last.query, 'starx');
      expect(remembered.last.zone, DiscoverSearchZone.results);
      expect(remembered.last.resultId, 'star-2');

      await tester.pumpWidget(const SizedBox.shrink());
      expect(remembered.last.zone, DiscoverSearchZone.results);
      expect(remembered.last.resultId, 'star-2');
    },
  );

  testWidgets(
    'completed result replacement recovers focus through cards and keyboard',
    (tester) async {
      final remembered = <DiscoverSearchFocusSnapshot>[];
      await _pumpSearch(
        tester,
        results: _readyResults,
        focus: const DiscoverSearchFocusSnapshot(
          query: 'star',
          zone: DiscoverSearchZone.results,
          resultId: 'star-2',
        ),
        onRememberFocus: remembered.add,
      );

      final replacements = <ConsoleGame>[
        _replacementGame('replacement-1'),
        _replacementGame('replacement-2'),
      ];
      await _pumpSearch(
        tester,
        results: DiscoverGamePageSnapshot(
          state: DiscoverLoadState.ready,
          items: replacements,
        ),
        focus: remembered.last,
        onRememberFocus: remembered.add,
      );
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-result-replacement-2',
      );
      expect(remembered.last.zone, DiscoverSearchZone.results);
      expect(remembered.last.resultId, 'replacement-2');

      await _pumpSearch(
        tester,
        results: const DiscoverGamePageSnapshot(state: DiscoverLoadState.ready),
        focus: remembered.last,
        onRememberFocus: remembered.add,
      );
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        startsWith('search-key-'),
      );
      expect(remembered.last.zone, DiscoverSearchZone.keyboard);
      expect(remembered.last.resultId, isNull);

      await _pumpSearch(
        tester,
        results: DiscoverGamePageSnapshot(
          state: DiscoverLoadState.ready,
          items: replacements,
        ),
        focus: remembered.last,
        onRememberFocus: remembered.add,
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.pageDown);
      await tester.pump();
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        startsWith('search-result-replacement-'),
      );
    },
  );

  testWidgets('removed selected system resubmits against All Systems', (
    tester,
  ) async {
    final searches = <DiscoverSearchRequest>[];
    await _pumpSearch(
      tester,
      results: _readyResults,
      focus: const DiscoverSearchFocusSnapshot(query: 'star'),
      onSearch: searches.add,
    );
    await tester.tap(
      find.byKey(const ValueKey<String>('search-system-filter')),
    );
    await tester.pump();
    expect(searches.last.platformId, 'snes');

    await _pumpSearch(
      tester,
      results: _readyResults,
      systems: <ConsolePlatform>[_systems[1]],
      focus: const DiscoverSearchFocusSnapshot(query: 'star'),
      onSearch: searches.add,
    );
    await tester.pump();
    expect(searches.last.platformId, isNull);
  });

  testWidgets('preserves visible cards while loading and wires paging/retry', (
    tester,
  ) async {
    var loadMore = 0;
    var retries = 0;
    await _pumpSearch(
      tester,
      results: DiscoverGamePageSnapshot(
        state: DiscoverLoadState.loading,
        items: _games,
        hasNextPage: true,
      ),
      focus: const DiscoverSearchFocusSnapshot(
        query: 'star',
        zone: DiscoverSearchZone.results,
        resultId: 'star-1',
      ),
      onLoadMore: () => loadMore++,
      onRetry: (_) => retries++,
    );

    expect(
      find.byKey(const ValueKey<String>('search-result-star-1')),
      findsOneWidget,
    );
    expect(find.byType(LinearProgressIndicator), findsOneWidget);
    expect(find.text('6 SHOWN'), findsOneWidget);
    expect(find.text('6 RESULTS'), findsNothing);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'search-result-star-1',
    );
    final focusedRing = find.descendant(
      of: find.byKey(const ValueKey<String>('search-result-star-1')),
      matching: find.byType(ConsoleFocusRing),
    );
    expect(focusedRing, findsOneWidget);
    expect(
      find.ancestor(of: focusedRing, matching: find.byType(AnimatedOpacity)),
      findsNothing,
    );
    await tester.tap(find.byKey(const ValueKey<String>('search-load-more')));
    expect(loadMore, 1);

    await _pumpSearch(
      tester,
      results: DiscoverGamePageSnapshot(
        state: DiscoverLoadState.failed,
        items: _games,
      ),
      focus: const DiscoverSearchFocusSnapshot(query: 'star'),
      onRetry: (_) => retries++,
    );
    expect(find.text('6 SHOWN'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey<String>('search-inline-retry')));
    expect(retries, 1);

    await _pumpSearch(
      tester,
      results: const DiscoverGamePageSnapshot(state: DiscoverLoadState.failed),
      focus: const DiscoverSearchFocusSnapshot(query: 'star'),
      onRetry: (_) => retries++,
    );
    await tester.tap(find.byKey(const ValueKey<String>('search-retry')));
    expect(retries, 2);
  });

  testWidgets('route closes after its session rebounds to another operation', (
    tester,
  ) async {
    tester.view
      ..devicePixelRatio = 1
      ..physicalSize = const Size(1280, 720);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final api = _RouteApi();
    const install = _Install();
    final openingOperation = _routeOperation(epoch: 1);
    final session = DiscoverCatalogSession(
      consumerApiClient: api,
      installService: install,
      operation: openingOperation,
    );
    addTearDown(session.dispose);

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: _PushedRouteHarness(session: session),
      ),
    );
    await tester.pump();
    await tester.pump();
    expect(find.byType(DiscoverSearchBoundary), findsOneWidget);

    final replacement = CatalogOperationContext(
      authority: openingOperation.authority,
      epoch: openingOperation.epoch + 1,
      accessToken: 'replacement-token',
      isCurrent: () => true,
    );
    session.updateDependencies(
      consumerApiClient: api,
      installService: install,
      operation: replacement,
    );
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    await tester.pump();

    expect(find.byType(DiscoverSearchBoundary), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('search-route-host')),
      findsOneWidget,
    );
  });

  testWidgets('route closes when its opening operation is invalidated', (
    tester,
  ) async {
    tester.view
      ..devicePixelRatio = 1
      ..physicalSize = const Size(1280, 720);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final changes = ChangeNotifier();
    addTearDown(changes.dispose);
    var operationCurrent = true;
    final operation = CatalogOperationContext(
      authority: _routeAuthority(),
      epoch: 1,
      accessToken: 'opening-token',
      isCurrent: () => operationCurrent,
      changes: changes,
    );
    final session = DiscoverCatalogSession(
      consumerApiClient: _RouteApi(),
      installService: const _Install(),
      operation: operation,
    );
    addTearDown(session.dispose);

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: _PushedRouteHarness(session: session),
      ),
    );
    await tester.pump();
    await tester.pump();
    expect(find.byType(DiscoverSearchBoundary), findsOneWidget);

    operationCurrent = false;
    changes.notifyListeners();
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    await tester.pump();

    expect(find.byType(DiscoverSearchBoundary), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('search-route-host')),
      findsOneWidget,
    );
  });

  testWidgets('route defers opening loads until after its parent build', (
    tester,
  ) async {
    tester.view
      ..devicePixelRatio = 1
      ..physicalSize = const Size(1280, 720);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final api = _RouteApi();
    const install = _Install();
    final session = DiscoverCatalogSession(
      consumerApiClient: api,
      installService: install,
      operation: _routeOperation(epoch: 1),
    );
    addTearDown(session.dispose);

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: _RouteOpenHarness(session: session),
      ),
    );
    expect(tester.takeException(), isNull);
    expect(api.listPlatformCalls, 1);

    await tester.pump();
    expect(tester.takeException(), isNull);
    expect(find.byType(DiscoverSearchBoundary), findsOneWidget);
  });
}

const List<ConsolePlatform> _systems = <ConsolePlatform>[
  ConsolePlatform(
    id: 'snes',
    name: 'Super Nintendo Entertainment System',
    shortName: 'SNES',
    manufacturer: 'Nintendo',
    titleCount: 3,
    coverUrl: null,
  ),
  ConsolePlatform(
    id: 'genesis',
    name: 'Sega Genesis',
    shortName: 'GEN',
    manufacturer: 'Sega',
    titleCount: 3,
    coverUrl: null,
  ),
];

final List<ConsoleGame> _games = List<ConsoleGame>.generate(
  6,
  (index) => ConsoleGame(
    id: 'star-${index + 1}',
    platformId: index.isEven ? 'snes' : 'genesis',
    platformName: index.isEven ? 'SNES' : 'Genesis',
    title: 'Star Title ${index + 1}',
    releaseDate: DateTime(1990 + index),
    coverUrl: null,
    genre: 'Action',
    rating: 8,
    releaseCount: 1,
    defaultReleaseId: 'release-${index + 1}',
  ),
);

ConsoleGame _replacementGame(String id) => ConsoleGame(
  id: id,
  platformId: 'snes',
  platformName: 'SNES',
  title: id,
  releaseDate: DateTime(1999),
  coverUrl: null,
  genre: 'Action',
  rating: 8,
  releaseCount: 1,
  defaultReleaseId: '$id-release',
);

final DiscoverGamePageSnapshot _readyResults = DiscoverGamePageSnapshot(
  state: DiscoverLoadState.ready,
  items: _games,
  nextCursor: 'next',
  hasNextPage: true,
);

Future<void> _pumpSearch(
  WidgetTester tester, {
  required DiscoverGamePageSnapshot results,
  required DiscoverSearchFocusSnapshot focus,
  ValueChanged<DiscoverSearchRequest>? onSearch,
  ValueChanged<DiscoverSearchRequest>? onRetry,
  VoidCallback? onLoadMore,
  ValueChanged<DiscoverSearchFocusSnapshot>? onRememberFocus,
  List<ConsolePlatform> systems = _systems,
}) async {
  tester.view
    ..devicePixelRatio = 1
    ..physicalSize = const Size(1280, 720);
  addTearDown(tester.view.resetDevicePixelRatio);
  addTearDown(tester.view.resetPhysicalSize);
  await tester.pumpWidget(
    MaterialApp(
      theme: RomdSkins.baselineDark(),
      home: Scaffold(
        body: DiscoverSearchBoundary(
          input: DiscoverSearchInput(
            results: results,
            systems: DiscoverSystemsSnapshot(
              state: DiscoverLoadState.ready,
              items: systems,
            ),
            focus: focus,
          ),
          callbacks: DiscoverSearchCallbacks(
            onSearch: onSearch ?? (_) {},
            onRetry: onRetry ?? (_) {},
            onLoadMore: onLoadMore ?? () {},
            onOpenGame: (_, _) {},
            onBack: () {},
            onRememberFocus: onRememberFocus ?? (_) {},
          ),
        ),
      ),
    ),
  );
  await tester.pump();
}

InstallAuthorityContext _routeAuthority() => InstallAuthorityContext(
  localProfileId: 'search-route-profile',
  connection: ProfileServerConnection(
    instanceId: RomdServerInstanceId.tryParse(
      '11111111-1111-4111-8111-111111111111',
    )!,
    origin: Uri.parse('https://search-route.invalid'),
    firstSeenAt: DateTime.utc(2026),
    lastSeenAt: DateTime.utc(2026),
  ),
  generation: 1,
);

CatalogOperationContext _routeOperation({required int epoch}) =>
    CatalogOperationContext(
      authority: _routeAuthority(),
      epoch: epoch,
      accessToken: 'search-route-token-$epoch',
      isCurrent: () => true,
    );

final class _RouteApi implements ConsumerApiClient {
  int listPlatformCalls = 0;

  @override
  Future<ConsolePlatformPage> listPlatforms({
    required String accessToken,
    String? cursor,
    int limit = 50,
  }) async {
    listPlatformCalls++;
    return const ConsolePlatformPage(
      items: <ConsolePlatform>[],
      nextCursor: null,
      hasNextPage: false,
    );
  }

  @override
  Future<ConsoleGamePage> searchCatalog({
    required String accessToken,
    String? query,
    String? platformId,
    String? completeness,
    String? sortBy,
    String? cursor,
    int limit = 48,
  }) async => const ConsoleGamePage(
    items: <ConsoleGame>[],
    nextCursor: null,
    hasNextPage: false,
  );

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Install implements InstallService {
  const _Install();

  @override
  Future<LocalInstall?> findInstall(String releaseId) async => null;

  @override
  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  }) => const Stream<InstallProgress>.empty();

  @override
  Future<List<LocalInstall>> listInstalled() async => const <LocalInstall>[];

  @override
  Future<List<LocalInstall>> listInstalledForTitle(String titleId) async =>
      const <LocalInstall>[];

  @override
  Future<void> uninstall(
    String releaseId, {
    InstallOperationLease? operation,
  }) async {}

  @override
  Stream<List<LocalInstall>> watchInstalled() =>
      Stream<List<LocalInstall>>.value(const <LocalInstall>[]);

  @override
  Stream<List<LocalInstall>> watchInstalledForTitle(String titleId) =>
      Stream<List<LocalInstall>>.value(const <LocalInstall>[]);

  @override
  Stream<Set<String>> watchInstalledReleaseIds() =>
      Stream<Set<String>>.value(const <String>{});
}

final class _RouteOpenHarness extends StatefulWidget {
  const _RouteOpenHarness({required this.session});

  final DiscoverCatalogSession session;

  @override
  State<_RouteOpenHarness> createState() => _RouteOpenHarnessState();
}

final class _RouteOpenHarnessState extends State<_RouteOpenHarness> {
  @override
  void initState() {
    super.initState();
    widget.session.addListener(_onSessionChanged);
  }

  @override
  void dispose() {
    widget.session.removeListener(_onSessionChanged);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      DiscoverSearchRoute(session: widget.session, onOpenGame: (_, _) {});

  void _onSessionChanged() => setState(() {});
}

final class _PushedRouteHarness extends StatefulWidget {
  const _PushedRouteHarness({required this.session});

  final DiscoverCatalogSession session;

  @override
  State<_PushedRouteHarness> createState() => _PushedRouteHarnessState();
}

final class _PushedRouteHarnessState extends State<_PushedRouteHarness> {
  bool _opened = false;

  @override
  Widget build(BuildContext context) {
    if (!_opened) {
      _opened = true;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted) return;
        Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) => DiscoverSearchRoute(
              session: widget.session,
              onOpenGame: (_, _) {},
            ),
          ),
        );
      });
    }
    return const Scaffold(
      key: ValueKey<String>('search-route-host'),
      body: SizedBox.expand(),
    );
  }
}
