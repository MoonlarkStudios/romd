import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/consumer_host_health.dart';
import 'package:romd_console/src/domain/device_authorization.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/catalog/discover_catalog_session.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';

void main() {
  test(
    'Featured respects audience placement and excludes unfeatured collections',
    () async {
      ConsoleCollection collection(String id, bool featured) =>
          ConsoleCollection(
            id: id,
            name: id,
            description: null,
            platformId: null,
            platformName: null,
            coverUrl: null,
            heroUrl: null,
            itemCount: 1,
            isFeatured: featured,
          );
      final api = _Api(
        catalogPages: [Future.value(_page([]))],
        collections: [
          collection('hidden', false),
          collection('second', true),
          collection('first', true),
        ],
        collectionPages: [
          _page([_game('b')]),
          _page([_game('a')]),
        ],
      );
      final session = DiscoverCatalogSession(
        consumerApiClient: api,
        installService: const _Install(),
        operation: _operation(),
      );
      addTearDown(session.dispose);
      await session.loadFeatured();
      expect(session.featured.shelves.map((shelf) => shelf.id), [
        'second',
        'first',
      ]);
      expect(session.featured.shelves.first.games.single.id, 'b');
    },
  );

  test('All Games never forwards release completeness', () async {
    final api = _Api(
      catalogPages: <Future<ConsoleGamePage>>[
        Future<ConsoleGamePage>.value(_page(<ConsoleGame>[_game('a')])),
        Future<ConsoleGamePage>.value(_page(<ConsoleGame>[_game('b')])),
      ],
    );
    final session = DiscoverCatalogSession(
      consumerApiClient: api,
      installService: const _Install(),
      operation: _operation(),
    );
    addTearDown(session.dispose);

    await session.loadAllGames();
    await session.configureAllGames(
      sort: DiscoverCatalogSort.rating,
      platformId: 'snes',
    );

    expect(api.catalogRequests, hasLength(2));
    expect(
      api.catalogRequests.map((request) => request.completeness),
      everyElement(isNull),
    );
    expect(api.catalogRequests.last.sortBy, 'rating');
    expect(api.catalogRequests.last.platformId, 'snes');
  });

  test(
    'operation invalidation synchronously clears every remote snapshot',
    () async {
      final changes = _Changes();
      var current = true;
      final api = _Api(
        catalogPages: <Future<ConsoleGamePage>>[
          Future<ConsoleGamePage>.value(_page(<ConsoleGame>[_game('a')])),
        ],
      );
      final session = DiscoverCatalogSession(
        consumerApiClient: api,
        installService: const _Install(),
        operation: _operation(isCurrent: () => current, changes: changes),
      );
      addTearDown(session.dispose);

      await session.loadAllGames();
      session.rememberFocus(
        DiscoverSection.allGames,
        itemId: 'a',
        scrollOffset: 120,
      );
      session.rememberSearchFocus(
        const DiscoverSearchFocusSnapshot(
          query: 'mario',
          zone: DiscoverSearchZone.results,
          keyboardKeyIndex: 4,
          resultId: 'a',
          resultScrollOffset: 96,
        ),
      );
      expect(session.allGames.items.single.id, 'a');

      current = false;
      changes.notify();

      expect(session.privacyCleared, isTrue);
      expect(session.allGames.items, isEmpty);
      expect(session.featured.shelves, isEmpty);
      expect(session.systems.items, isEmpty);
      expect(session.focusFor(DiscoverSection.allGames).itemId, isNull);
      expect(session.searchFocus.query, isEmpty);
      expect(session.searchFocus.resultId, isNull);
    },
  );

  test(
    'a response cannot publish after its operation is invalidated',
    () async {
      final changes = _Changes();
      var current = true;
      final pending = Completer<ConsoleGamePage>();
      final session = DiscoverCatalogSession(
        consumerApiClient: _Api(
          catalogPages: <Future<ConsoleGamePage>>[pending.future],
        ),
        installService: const _Install(),
        operation: _operation(isCurrent: () => current, changes: changes),
      );
      addTearDown(session.dispose);

      final request = session.loadAllGames();
      expect(session.allGames.state, DiscoverLoadState.loading);
      current = false;
      changes.notify();
      pending.complete(_page(<ConsoleGame>[_game('stale')]));
      await request;

      expect(session.privacyCleared, isTrue);
      expect(session.allGames.items, isEmpty);
    },
  );

  test(
    'session loss synchronously clears snapshots while authority stays current',
    () async {
      final executor = _SessionExecutor();
      final session = DiscoverCatalogSession(
        consumerApiClient: _Api(
          catalogPages: <Future<ConsoleGamePage>>[
            Future<ConsoleGamePage>.value(_page(<ConsoleGame>[_game('a')])),
          ],
        ),
        installService: const _Install(),
        operation: _sessionOperation(executor),
      );
      addTearDown(session.dispose);
      addTearDown(executor.dispose);

      await session.loadAllGames();
      expect(session.allGames.items.single.id, 'a');

      executor.loseSession();

      expect(session.privacyCleared, isTrue);
      expect(session.allGames.items, isEmpty);
      expect(session.searchResults.items, isEmpty);
    },
  );

  testWidgets(
    'privacy boundary shields on the same session-loss notification',
    (tester) async {
      final executor = _SessionExecutor();
      addTearDown(executor.dispose);
      await tester.pumpWidget(
        MaterialApp(
          theme: RomdSkins.baselineDark(),
          home: CatalogPrivacyBoundary(
            operation: _sessionOperation(executor),
            child: const Text('Former session metadata'),
          ),
        ),
      );
      expect(find.text('Former session metadata'), findsOneWidget);

      executor.loseSession();
      await tester.pump();

      expect(
        find.byKey(const ValueKey<String>('catalog-privacy-shield')),
        findsOneWidget,
      );
      expect(find.text('Former session metadata'), findsNothing);
    },
  );

  test(
    'paging deduplicates stable ids and retains the original focus key',
    () async {
      final session = DiscoverCatalogSession(
        consumerApiClient: _Api(
          catalogPages: <Future<ConsoleGamePage>>[
            Future<ConsoleGamePage>.value(
              ConsoleGamePage(
                items: <ConsoleGame>[_game('a'), _game('b')],
                nextCursor: 'next',
                hasNextPage: true,
              ),
            ),
            Future<ConsoleGamePage>.value(
              _page(<ConsoleGame>[_game('b'), _game('c')]),
            ),
          ],
        ),
        installService: const _Install(),
        operation: _operation(),
      );
      addTearDown(session.dispose);

      await session.loadAllGames();
      session.rememberFocus(
        DiscoverSection.allGames,
        itemId: 'b',
        scrollOffset: 48,
      );
      await session.loadMoreAllGames();

      expect(session.allGames.items.map((game) => game.id), <String>[
        'a',
        'b',
        'c',
      ]);
      expect(session.focusFor(DiscoverSection.allGames).itemId, 'b');
    },
  );

  test(
    'Featured shelves preserve first order while deduplicating ids and titles',
    () async {
      final session = DiscoverCatalogSession(
        consumerApiClient: _Api(
          catalogPages: <Future<ConsoleGamePage>>[
            Future<ConsoleGamePage>.value(
              _page(<ConsoleGame>[
                _game('first', title: 'Super Metroid'),
                _game('first', title: 'Renamed duplicate'),
                _game('alias', title: '  super   METROID  '),
                _game(
                  'other-platform',
                  platformId: 'other-platform',
                  title: 'Super Metroid',
                ),
                _game('last', title: 'Chrono Trigger'),
              ]),
            ),
          ],
        ),
        installService: const _Install(),
        operation: _operation(),
      );
      addTearDown(session.dispose);

      await session.loadFeatured();

      expect(
        session.featured.shelves.single.games.map((game) => game.id),
        <String>['first', 'other-platform', 'last'],
      );
    },
  );

  test('Featured deduplication is scoped to each collection shelf', () async {
    final repeated = _game('repeated', title: 'Shared Highlight');
    final session = DiscoverCatalogSession(
      consumerApiClient: _Api(
        catalogPages: <Future<ConsoleGamePage>>[
          Future<ConsoleGamePage>.value(_page(<ConsoleGame>[repeated])),
        ],
        collections: const <ConsoleCollection>[
          ConsoleCollection(
            id: 'action',
            name: 'Action',
            description: null,
            platformId: null,
            platformName: null,
            coverUrl: null,
            heroUrl: null,
            itemCount: 2,
          ),
          ConsoleCollection(
            id: 'essentials',
            name: 'Essentials',
            description: null,
            platformId: null,
            platformName: null,
            coverUrl: null,
            heroUrl: null,
            itemCount: 1,
          ),
        ],
        collectionPages: <ConsoleGamePage>[
          _page(<ConsoleGame>[
            repeated,
            _game('action-alias', title: 'shared highlight'),
          ]),
          _page(<ConsoleGame>[repeated]),
        ],
      ),
      installService: const _Install(),
      operation: _operation(),
    );
    addTearDown(session.dispose);

    await session.loadFeatured();

    expect(session.featured.shelves.map((shelf) => shelf.id), <String>[
      'top-rated',
      'action',
      'essentials',
    ]);
    for (final shelf in session.featured.shelves) {
      expect(shelf.games.map((game) => game.id), <String>[
        'repeated',
      ], reason: shelf.id);
    }
  });

  test('forced All Games refresh rejects an older cursor response', () async {
    final paging = Completer<ConsoleGamePage>();
    final refresh = Completer<ConsoleGamePage>();
    final session = DiscoverCatalogSession(
      consumerApiClient: _Api(
        catalogPages: <Future<ConsoleGamePage>>[
          Future<ConsoleGamePage>.value(
            ConsoleGamePage(
              items: <ConsoleGame>[_game('initial')],
              nextCursor: 'next',
              hasNextPage: true,
            ),
          ),
          paging.future,
          refresh.future,
        ],
      ),
      installService: const _Install(),
      operation: _operation(),
    );
    addTearDown(session.dispose);

    await session.loadAllGames();
    final olderPaging = session.loadMoreAllGames();
    final latestRefresh = session.loadAllGames(force: true);
    paging.complete(_page(<ConsoleGame>[_game('stale-page')]));
    await olderPaging;

    expect(session.allGames.state, DiscoverLoadState.loading);
    expect(session.allGames.items, isEmpty);

    refresh.complete(_page(<ConsoleGame>[_game('fresh-root')]));
    await latestRefresh;
    expect(session.allGames.items.single.id, 'fresh-root');
  });

  test(
    'latest composite search key wins an out-of-order response race',
    () async {
      final first = Completer<ConsoleGamePage>();
      final second = Completer<ConsoleGamePage>();
      final session = DiscoverCatalogSession(
        consumerApiClient: _Api(
          catalogPages: <Future<ConsoleGamePage>>[first.future, second.future],
        ),
        installService: const _Install(),
        operation: _operation(),
      );
      addTearDown(session.dispose);

      final oldRequest = session.search(query: 'old');
      final latestRequest = session.search(query: 'latest');
      first.complete(_page(<ConsoleGame>[_game('old')]));
      await oldRequest;
      expect(session.searchResults.state, DiscoverLoadState.loading);
      second.complete(_page(<ConsoleGame>[_game('latest')]));
      await latestRequest;

      expect(session.searchResults.items.single.id, 'latest');
      expect(session.searchResults.requestKey?.query, 'latest');
    },
  );

  test('forced Search refresh rejects an older cursor response', () async {
    final paging = Completer<ConsoleGamePage>();
    final refresh = Completer<ConsoleGamePage>();
    final session = DiscoverCatalogSession(
      consumerApiClient: _Api(
        catalogPages: <Future<ConsoleGamePage>>[
          Future<ConsoleGamePage>.value(
            ConsoleGamePage(
              items: <ConsoleGame>[_game('initial')],
              nextCursor: 'next',
              hasNextPage: true,
            ),
          ),
          paging.future,
          refresh.future,
        ],
      ),
      installService: const _Install(),
      operation: _operation(),
    );
    addTearDown(session.dispose);

    await session.search(query: 'mario');
    final olderPaging = session.loadMoreSearch();
    final latestRefresh = session.search(query: 'mario', force: true);
    paging.complete(_page(<ConsoleGame>[_game('stale-page')]));
    await olderPaging;

    expect(session.searchResults.state, DiscoverLoadState.loading);
    expect(session.searchResults.items.single.id, 'initial');

    refresh.complete(_page(<ConsoleGame>[_game('fresh-root')]));
    await latestRefresh;
    expect(session.searchResults.items.single.id, 'fresh-root');
  });

  test('composite request keys distinguish filters and cursors', () {
    final operation = DiscoverOperationKey.from(_operation());
    final base = DiscoverRequestKey(
      operation: operation,
      surface: DiscoverSurface.allGames,
    );

    expect(
      base,
      isNot(
        DiscoverRequestKey(
          operation: operation,
          surface: DiscoverSurface.allGames,
          completeness: DiscoverReleaseCompleteness.complete,
        ),
      ),
    );
    expect(
      base,
      isNot(
        DiscoverRequestKey(
          operation: operation,
          surface: DiscoverSurface.allGames,
          cursor: 'next',
        ),
      ),
    );
  });
}

final class _Changes extends ChangeNotifier {
  void notify() => notifyListeners();
}

final _instanceId = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;

CatalogOperationContext _operation({
  bool Function()? isCurrent,
  Listenable? changes,
}) => CatalogOperationContext(
  authority: InstallAuthorityContext(
    localProfileId: 'profile',
    connection: ProfileServerConnection(
      instanceId: _instanceId,
      origin: Uri.parse('https://catalog.local'),
      firstSeenAt: DateTime.utc(2026),
      lastSeenAt: DateTime.utc(2026),
    ),
    generation: 1,
  ),
  accessToken: 'secret',
  epoch: 7,
  isCurrent: isCurrent ?? () => true,
  changes: changes,
);

CatalogOperationContext _sessionOperation(_SessionExecutor executor) =>
    CatalogOperationContext(
      authority: InstallAuthorityContext(
        localProfileId: 'profile',
        connection: ProfileServerConnection(
          instanceId: _instanceId,
          origin: Uri.parse('https://catalog.local'),
          firstSeenAt: DateTime.utc(2026),
          lastSeenAt: DateTime.utc(2026),
        ),
        generation: 1,
      ),
      authenticatedSession: executor,
      epoch: 7,
      isAuthorityCurrent: () => true,
      changes: executor,
    );

final class _SessionExecutor extends ChangeNotifier
    implements AuthenticatedRequestExecutor {
  ConsumerLoginSession? _session = ConsumerLoginSession(
    token: 'secret',
    tokenType: 'Bearer',
    refreshToken: 'refresh',
    expiresAt: DateTime.utc(2027),
    account: const ConsumerAccount(
      id: 'account',
      username: 'player',
      email: 'player@example.com',
    ),
  );
  int _credentialGeneration = 1;

  @override
  ConsumerLoginSession? get session => _session;

  @override
  int get credentialGeneration => _credentialGeneration;

  void loseSession() {
    _session = null;
    _credentialGeneration++;
    notifyListeners();
  }

  @override
  Future<T> execute<T>({
    required Future<T> Function(String accessToken) request,
    AuthenticatedReplayPolicy replayPolicy = AuthenticatedReplayPolicy.never,
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  }) {
    final current = _session;
    if (current == null) {
      throw const SessionUnavailableException(
        SessionRefreshFailureKind.missingCredential,
      );
    }
    return request(current.token);
  }
}

ConsoleGame _game(String id, {String platformId = 'platform', String? title}) =>
    ConsoleGame(
      id: id,
      platformId: platformId,
      platformName: 'System',
      title: title ?? 'Game $id',
      releaseDate: DateTime.utc(1994),
      coverUrl: null,
      genre: 'Action',
      rating: 9,
      releaseCount: 1,
      defaultReleaseId: '$id-release',
    );

ConsoleGamePage _page(List<ConsoleGame> items) =>
    ConsoleGamePage(items: items, nextCursor: null, hasNextPage: false);

final class _Api implements ConsumerApiClient {
  @override
  Future<void> revokeSession({required String refreshToken}) async {}

  _Api({
    required this.catalogPages,
    this.collections = const <ConsoleCollection>[],
    this.collectionPages = const <ConsoleGamePage>[],
  });

  final List<Future<ConsoleGamePage>> catalogPages;
  final List<ConsoleCollection> collections;
  final List<ConsoleGamePage> collectionPages;
  final List<
    ({
      String? query,
      String? platformId,
      String? completeness,
      String? sortBy,
      String? cursor,
    })
  >
  catalogRequests =
      <
        ({
          String? query,
          String? platformId,
          String? completeness,
          String? sortBy,
          String? cursor,
        })
      >[];
  int _catalogIndex = 0;
  int _collectionPageIndex = 0;

  @override
  Future<ConsoleGamePage> searchCatalog({
    required String accessToken,
    String? query,
    String? platformId,
    String? completeness,
    String? sortBy,
    String? cursor,
    int limit = 48,
  }) {
    catalogRequests.add((
      query: query,
      platformId: platformId,
      completeness: completeness,
      sortBy: sortBy,
      cursor: cursor,
    ));
    return catalogPages[_catalogIndex++];
  }

  @override
  Future<List<ConsoleCollection>> listCollections({
    required String accessToken,
    String? platformId,
  }) async => collections;

  @override
  Future<ConsoleGamePage> listCollectionTitles({
    required String accessToken,
    required String collectionId,
    String? cursor,
    int limit = 24,
  }) async => collectionPages.isEmpty
      ? _page(const <ConsoleGame>[])
      : collectionPages[_collectionPageIndex++];

  @override
  Future<ConsolePlatformPage> listPlatforms({
    required String accessToken,
    String? cursor,
    int limit = 50,
  }) async => const ConsolePlatformPage(
    items: <ConsolePlatform>[],
    nextCursor: null,
    hasNextPage: false,
  );

  @override
  Future<ConsoleGameDetail> getTitle({
    required String accessToken,
    required String titleId,
  }) => throw UnimplementedError();

  @override
  Future<ConsumerHostHealth> getHealth() async =>
      const ConsumerHostHealth(status: 'Healthy');

  @override
  Future<ConsoleReleaseManifest> issueReleaseManifest({
    required String accessToken,
    required String releaseId,
  }) => throw UnimplementedError();

  @override
  Future<List<BiosFileListing>> listPlatformBios({
    required String accessToken,
    required String platformShortName,
  }) async => const <BiosFileListing>[];

  @override
  Future<DeviceAuthorizationResult> requestDeviceAuthorization() async =>
      const DeviceAuthorizationFailure('unused');

  @override
  Future<DeviceTokenResult> redeemDeviceCode({
    required String deviceCode,
  }) async => const DeviceTokenFailure('unused');

  @override
  Future<ConsumerLoginResult> refreshSession({
    required String refreshToken,
  }) async => const ConsumerLoginFailure('unused');

  @override
  void close() {}
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
