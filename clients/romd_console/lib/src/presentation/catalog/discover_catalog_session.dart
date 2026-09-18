import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';

/// Operation-scoped remote state for Discover.
///
/// This is the only Discover object that calls the Consumer API. Feature
/// widgets receive immutable snapshots and callbacks from their owner.
final class DiscoverCatalogSession extends ChangeNotifier {
  DiscoverCatalogSession({
    required ConsumerApiClient consumerApiClient,
    required InstallService installService,
    required CatalogOperationContext? operation,
  }) : _consumerApiClient = consumerApiClient,
       _installService = installService,
       _operation = operation {
    _listenToOperation(operation);
    _watchInstalledReleases();
  }

  ConsumerApiClient _consumerApiClient;
  InstallService _installService;
  CatalogOperationContext? _operation;
  StreamSubscription<Set<String>>? _installedSubscription;
  int _generation = 0;
  bool _disposed = false;
  bool _privacyCleared = false;

  DiscoverFeaturedSnapshot _featured = const DiscoverFeaturedSnapshot();
  DiscoverGamePageSnapshot _allGames = const DiscoverGamePageSnapshot();
  DiscoverSystemsSnapshot _systems = const DiscoverSystemsSnapshot();
  DiscoverGamePageSnapshot _search = const DiscoverGamePageSnapshot();
  Set<String> _installedReleaseIds = const <String>{};
  final Map<DiscoverSection, DiscoverFocusSnapshot> _focus =
      <DiscoverSection, DiscoverFocusSnapshot>{};
  final Map<String, ConsoleGameDetail> _details = <String, ConsoleGameDetail>{};
  final Set<DiscoverRequestKey> _inFlight = <DiscoverRequestKey>{};
  DiscoverSearchFocusSnapshot _searchFocus =
      const DiscoverSearchFocusSnapshot();

  DiscoverCatalogSort _allGamesSort = DiscoverCatalogSort.title;
  String? _allGamesPlatformId;

  CatalogOperationContext? get operation => _operation;
  bool get privacyCleared => _privacyCleared;
  DiscoverFeaturedSnapshot get featured => _featured;
  DiscoverGamePageSnapshot get allGames => _allGames;
  DiscoverSystemsSnapshot get systems => _systems;
  DiscoverGamePageSnapshot get searchResults => _search;
  Set<String> get installedReleaseIds => _installedReleaseIds;
  DiscoverCatalogSort get allGamesSort => _allGamesSort;
  String? get allGamesPlatformId => _allGamesPlatformId;
  DiscoverSearchFocusSnapshot get searchFocus => _searchFocus;

  DiscoverFocusSnapshot focusFor(DiscoverSection section) =>
      _focus[section] ?? const DiscoverFocusSnapshot();

  ConsoleGameDetail? detailFor(String titleId) => _details[titleId];

  void rememberFocus(
    DiscoverSection section, {
    required String? itemId,
    required double scrollOffset,
  }) {
    _focus[section] = DiscoverFocusSnapshot(
      itemId: itemId,
      scrollOffset: scrollOffset,
    );
  }

  void rememberSearchFocus(DiscoverSearchFocusSnapshot focus) {
    _searchFocus = focus;
  }

  /// Rebinds the session to the launcher-owned live authority.
  ///
  /// A changed Consumer graph or Catalog operation clears all remote state
  /// synchronously before listeners can render the new authority.
  void updateDependencies({
    required ConsumerApiClient consumerApiClient,
    required InstallService installService,
    required CatalogOperationContext? operation,
  }) {
    final operationChanged = switch ((_operation, operation)) {
      (null, null) => false,
      (final CatalogOperationContext old, final CatalogOperationContext next) =>
        !old.hasSameOperation(next),
      _ => true,
    };
    final clientChanged = !identical(_consumerApiClient, consumerApiClient);
    final installChanged = !identical(_installService, installService);
    if (!operationChanged && !clientChanged && !installChanged) return;

    if (operationChanged) {
      _unlistenFromOperation(_operation);
      _operation = operation;
      _listenToOperation(operation);
    }
    _consumerApiClient = consumerApiClient;
    if (installChanged) {
      unawaited(_installedSubscription?.cancel());
      _installService = installService;
      _installedReleaseIds = const <String>{};
      _watchInstalledReleases();
    }
    if (operationChanged || clientChanged) {
      clearRemoteSynchronously(privacyCleared: false);
    } else {
      _notify();
    }
  }

  /// Invalidates every remote snapshot and cache in the current stack frame.
  void clearRemoteSynchronously({required bool privacyCleared}) {
    _generation++;
    _privacyCleared = privacyCleared;
    _featured = const DiscoverFeaturedSnapshot();
    _allGames = const DiscoverGamePageSnapshot();
    _systems = const DiscoverSystemsSnapshot();
    _search = const DiscoverGamePageSnapshot();
    _focus.clear();
    _details.clear();
    _inFlight.clear();
    _searchFocus = const DiscoverSearchFocusSnapshot();
    _allGamesSort = DiscoverCatalogSort.title;
    _allGamesPlatformId = null;
    _notify();
  }

  Future<void> ensureLoaded(DiscoverSection section) => switch (section) {
    DiscoverSection.featured => loadFeatured(),
    DiscoverSection.allGames => loadAllGames(),
  };

  Future<void> loadFeatured({bool force = false}) async {
    final operation = _requestOperation();
    if (operation == null ||
        (!force && _featured.state != DiscoverLoadState.idle)) {
      return;
    }
    final key = _key(operation, DiscoverSurface.featured);
    final generation = _generation;
    _featured = DiscoverFeaturedSnapshot(
      state: DiscoverLoadState.loading,
      requestKey: key,
    );
    _notify();
    try {
      final top = await operation.executeConsumer(
        (token) => _consumerApiClient.searchCatalog(
          accessToken: token,
          sortBy: DiscoverCatalogSort.rating.apiValue,
          limit: 24,
        ),
      );
      if (!_isCurrent(operation, generation, key, _featured.requestKey)) return;
      final collections = await operation.executeConsumer(
        (token) => _consumerApiClient.listCollections(accessToken: token),
      );
      if (!_isCurrent(operation, generation, key, _featured.requestKey)) return;
      final visible = collections
          .where(
            (collection) => collection.itemCount > 0 && collection.isFeatured,
          )
          .take(5)
          .toList(growable: false);
      final pages = await Future.wait(
        visible.map(
          (collection) => operation.executeConsumer(
            (token) => _consumerApiClient.listCollectionTitles(
              accessToken: token,
              collectionId: collection.id,
              limit: 18,
            ),
          ),
        ),
      );
      if (!_isCurrent(operation, generation, key, _featured.requestKey)) return;
      _featured = DiscoverFeaturedSnapshot(
        state: DiscoverLoadState.ready,
        requestKey: key,
        shelves:
            List<DiscoverFeaturedShelf>.unmodifiable(<DiscoverFeaturedShelf>[
              if (top.items.any((game) => game.rating != null))
                DiscoverFeaturedShelf(
                  id: 'top-rated',
                  eyebrow: 'FEATURED',
                  title: 'Top Rated',
                  games: _deduplicateFeaturedShelfGames(
                    top.items.where((game) => game.rating != null),
                  ),
                ),
              for (var index = 0; index < visible.length; index++)
                if (pages[index].items.isNotEmpty)
                  DiscoverFeaturedShelf(
                    id: visible[index].id,
                    eyebrow: 'FEATURED COLLECTION',
                    title: visible[index].name,
                    games: _deduplicateFeaturedShelfGames(pages[index].items),
                  ),
            ]),
      );
      _notify();
    } on Object {
      if (_isCurrent(operation, generation, key, _featured.requestKey)) {
        _featured = DiscoverFeaturedSnapshot(
          state: DiscoverLoadState.failed,
          requestKey: key,
        );
        _notify();
      }
    }
  }

  Future<void> configureAllGames({
    DiscoverCatalogSort? sort,
    String? platformId,
    bool clearPlatform = false,
  }) async {
    final nextSort = sort ?? _allGamesSort;
    final nextPlatform = clearPlatform
        ? null
        : platformId ?? _allGamesPlatformId;
    if (nextSort == _allGamesSort && nextPlatform == _allGamesPlatformId) {
      return;
    }
    _allGamesSort = nextSort;
    _allGamesPlatformId = nextPlatform;
    _allGames = const DiscoverGamePageSnapshot();
    _notify();
    await loadAllGames();
  }

  Future<void> loadAllGames({bool force = false}) async {
    final operation = _requestOperation();
    if (operation == null ||
        (!force && _allGames.state != DiscoverLoadState.idle)) {
      return;
    }
    final key = _key(
      operation,
      DiscoverSurface.allGames,
      sort: _allGamesSort,
      platformId: _allGamesPlatformId,
    );
    final generation = _generation;
    _allGames = DiscoverGamePageSnapshot(
      state: DiscoverLoadState.loading,
      requestKey: key,
    );
    _notify();
    try {
      final page = await operation.executeConsumer(
        (token) => _consumerApiClient.searchCatalog(
          accessToken: token,
          sortBy: key.sort.apiValue,
          completeness: key.completeness.apiValue,
          platformId: key.platformId,
          limit: 60,
        ),
      );
      if (!_isCurrent(operation, generation, key, _allGames.requestKey)) return;
      _allGames = _pageSnapshot(page, key);
      _notify();
    } on Object {
      if (_isCurrent(operation, generation, key, _allGames.requestKey)) {
        _allGames = DiscoverGamePageSnapshot(
          state: DiscoverLoadState.failed,
          requestKey: key,
        );
        _notify();
      }
    }
  }

  Future<void> loadMoreAllGames() async {
    final operation = _requestOperation();
    final cursor = _allGames.nextCursor;
    if (operation == null ||
        cursor == null ||
        !_allGames.hasNextPage ||
        _allGames.paging) {
      return;
    }
    final rootKey = _allGames.requestKey;
    if (rootKey == null) return;
    final key = DiscoverRequestKey(
      operation: rootKey.operation,
      surface: rootKey.surface,
      sort: rootKey.sort,
      completeness: rootKey.completeness,
      platformId: rootKey.platformId,
      cursor: cursor,
    );
    final generation = _generation;
    _allGames = DiscoverGamePageSnapshot(
      state: _allGames.state,
      items: _allGames.items,
      nextCursor: _allGames.nextCursor,
      hasNextPage: _allGames.hasNextPage,
      paging: true,
      pagingRequestKey: key,
      requestKey: rootKey,
    );
    _notify();
    try {
      final page = await operation.executeConsumer(
        (token) => _consumerApiClient.searchCatalog(
          accessToken: token,
          sortBy: key.sort.apiValue,
          completeness: key.completeness.apiValue,
          platformId: key.platformId,
          cursor: cursor,
          limit: 60,
        ),
      );
      if (!_isCurrent(operation, generation, key, _allGames.pagingRequestKey)) {
        return;
      }
      final ids = _allGames.items.map((game) => game.id).toSet();
      final items = <ConsoleGame>[
        ..._allGames.items,
        for (final game in page.items)
          if (ids.add(game.id)) game,
      ];
      _allGames = DiscoverGamePageSnapshot(
        state: DiscoverLoadState.ready,
        items: List<ConsoleGame>.unmodifiable(items),
        nextCursor: page.nextCursor,
        hasNextPage:
            page.hasNextPage &&
            page.nextCursor != null &&
            page.nextCursor != cursor,
        requestKey: rootKey,
      );
      _notify();
    } on Object {
      if (_isCurrent(operation, generation, key, _allGames.pagingRequestKey)) {
        _allGames = DiscoverGamePageSnapshot(
          state: _allGames.state,
          items: _allGames.items,
          nextCursor: _allGames.nextCursor,
          hasNextPage: _allGames.hasNextPage,
          pagingFailed: true,
          requestKey: rootKey,
        );
        _notify();
      }
    }
  }

  Future<void> loadSystems({bool force = false}) async {
    final operation = _requestOperation();
    if (operation == null ||
        (!force && _systems.state != DiscoverLoadState.idle)) {
      return;
    }
    final key = _key(operation, DiscoverSurface.systems);
    final generation = _generation;
    _systems = DiscoverSystemsSnapshot(
      state: DiscoverLoadState.loading,
      requestKey: key,
    );
    _notify();
    try {
      final systems = <ConsolePlatform>[];
      final ids = <String>{};
      final cursors = <String>{};
      String? cursor;
      while (true) {
        final page = await operation.executeConsumer(
          (token) => _consumerApiClient.listPlatforms(
            accessToken: token,
            cursor: cursor,
            limit: 100,
          ),
        );
        if (!_isCurrent(operation, generation, key, _systems.requestKey))
          return;
        for (final platform in page.items) {
          if (ids.add(platform.id)) systems.add(platform);
        }
        if (!page.hasNextPage) break;
        final next = page.nextCursor;
        if (next == null || next.isEmpty || !cursors.add(next)) {
          throw StateError('Invalid Systems pagination cursor.');
        }
        cursor = next;
      }
      _systems = DiscoverSystemsSnapshot(
        state: DiscoverLoadState.ready,
        items: List<ConsolePlatform>.unmodifiable(systems),
        requestKey: key,
      );
      _notify();
    } on Object {
      if (_isCurrent(operation, generation, key, _systems.requestKey)) {
        _systems = DiscoverSystemsSnapshot(
          state: DiscoverLoadState.failed,
          requestKey: key,
        );
        _notify();
      }
    }
  }

  Future<void> search({
    required String query,
    String? platformId,
    DiscoverReleaseCompleteness completeness = DiscoverReleaseCompleteness.any,
    bool force = false,
  }) async {
    final operation = _requestOperation();
    if (operation == null) return;
    final normalizedQuery = query.trim();
    final key = _key(
      operation,
      DiscoverSurface.search,
      query: normalizedQuery,
      completeness: completeness,
      platformId: platformId,
    );
    if (!force &&
        _search.requestKey == key &&
        _search.state != DiscoverLoadState.idle &&
        _search.state != DiscoverLoadState.failed) {
      return;
    }
    final generation = _generation;
    _search = DiscoverGamePageSnapshot(
      state: DiscoverLoadState.loading,
      // Preserve the previous page while a new query is in flight. Gate 2 may
      // dim it, but never needs to collapse Search to a blank screen.
      items: _search.items,
      requestKey: key,
    );
    _notify();
    try {
      final page = await operation.executeConsumer(
        (token) => _consumerApiClient.searchCatalog(
          accessToken: token,
          query: normalizedQuery,
          completeness: completeness.apiValue,
          platformId: platformId,
          limit: 60,
        ),
      );
      if (!_isCurrent(operation, generation, key, _search.requestKey)) return;
      _search = _pageSnapshot(page, key);
      _notify();
    } on Object {
      if (_isCurrent(operation, generation, key, _search.requestKey)) {
        _search = DiscoverGamePageSnapshot(
          state: DiscoverLoadState.failed,
          items: _search.items,
          requestKey: key,
        );
        _notify();
      }
    }
  }

  Future<void> loadMoreSearch() async {
    final operation = _requestOperation();
    final rootKey = _search.requestKey;
    final cursor = _search.nextCursor;
    if (operation == null ||
        rootKey == null ||
        cursor == null ||
        !_search.hasNextPage ||
        _search.paging) {
      return;
    }
    final pageKey = DiscoverRequestKey(
      operation: rootKey.operation,
      surface: rootKey.surface,
      query: rootKey.query,
      sort: rootKey.sort,
      completeness: rootKey.completeness,
      platformId: rootKey.platformId,
      cursor: cursor,
    );
    final generation = _generation;
    _search = DiscoverGamePageSnapshot(
      state: _search.state,
      items: _search.items,
      nextCursor: cursor,
      hasNextPage: true,
      paging: true,
      pagingRequestKey: pageKey,
      requestKey: rootKey,
    );
    _notify();
    try {
      final page = await operation.executeConsumer(
        (token) => _consumerApiClient.searchCatalog(
          accessToken: token,
          query: pageKey.query,
          completeness: pageKey.completeness.apiValue,
          platformId: pageKey.platformId,
          cursor: cursor,
          limit: 60,
        ),
      );
      if (!_isCurrent(
        operation,
        generation,
        pageKey,
        _search.pagingRequestKey,
      )) {
        return;
      }
      final ids = _search.items.map((game) => game.id).toSet();
      _search = DiscoverGamePageSnapshot(
        state: DiscoverLoadState.ready,
        items: List<ConsoleGame>.unmodifiable(<ConsoleGame>[
          ..._search.items,
          for (final game in page.items)
            if (ids.add(game.id)) game,
        ]),
        nextCursor: page.nextCursor,
        hasNextPage:
            page.hasNextPage &&
            page.nextCursor != null &&
            page.nextCursor != cursor,
        requestKey: rootKey,
      );
      _notify();
    } on Object {
      if (_isCurrent(
        operation,
        generation,
        pageKey,
        _search.pagingRequestKey,
      )) {
        _search = DiscoverGamePageSnapshot(
          state: _search.state,
          items: _search.items,
          nextCursor: cursor,
          hasNextPage: true,
          pagingFailed: true,
          requestKey: rootKey,
        );
        _notify();
      }
    }
  }

  Future<ConsoleGameDetail?> loadDetail(String titleId) async {
    final cached = _details[titleId];
    if (cached != null) return cached;
    final operation = _requestOperation();
    if (operation == null) return null;
    final key = _key(operation, DiscoverSurface.detail, resourceId: titleId);
    if (!_inFlight.add(key)) return null;
    final generation = _generation;
    try {
      final detail = await operation.executeConsumer(
        (token) =>
            _consumerApiClient.getTitle(accessToken: token, titleId: titleId),
      );
      if (!_isCurrent(operation, generation, key, key)) return null;
      _details[titleId] = detail;
      _notify();
      return detail;
    } on Object {
      return null;
    } finally {
      _inFlight.remove(key);
    }
  }

  CatalogOperationContext? _requestOperation() {
    final operation = _operation;
    if (operation == null || !operation.isRequestCurrent || _privacyCleared) {
      return null;
    }
    return operation;
  }

  DiscoverRequestKey _key(
    CatalogOperationContext operation,
    DiscoverSurface surface, {
    String query = '',
    DiscoverCatalogSort sort = DiscoverCatalogSort.title,
    DiscoverReleaseCompleteness completeness = DiscoverReleaseCompleteness.any,
    String? platformId,
    String? cursor,
    String? resourceId,
  }) => DiscoverRequestKey(
    operation: DiscoverOperationKey.from(operation),
    surface: surface,
    query: query,
    sort: sort,
    completeness: completeness,
    platformId: platformId,
    cursor: cursor,
    resourceId: resourceId,
  );

  bool _isCurrent(
    CatalogOperationContext operation,
    int generation,
    DiscoverRequestKey expected,
    DiscoverRequestKey? current,
  ) {
    final live = _operation;
    return !_disposed &&
        generation == _generation &&
        !_privacyCleared &&
        current == expected &&
        operation.isRequestCurrent &&
        live != null &&
        live.hasSameOperation(operation);
  }

  DiscoverGamePageSnapshot _pageSnapshot(
    ConsoleGamePage page,
    DiscoverRequestKey key,
  ) => DiscoverGamePageSnapshot(
    state: DiscoverLoadState.ready,
    items: List<ConsoleGame>.unmodifiable(page.items),
    nextCursor: page.nextCursor,
    hasNextPage: page.hasNextPage && page.nextCursor != null,
    requestKey: key,
  );

  void _onOperationChanged() {
    final operation = _operation;
    if (operation != null && !operation.isRequestCurrent) {
      clearRemoteSynchronously(privacyCleared: true);
    }
  }

  void _listenToOperation(CatalogOperationContext? operation) =>
      operation?.changes?.addListener(_onOperationChanged);

  void _unlistenFromOperation(CatalogOperationContext? operation) =>
      operation?.changes?.removeListener(_onOperationChanged);

  void _watchInstalledReleases() {
    _installedSubscription = _installService.watchInstalledReleaseIds().listen((
      ids,
    ) {
      if (_disposed || setEquals(ids, _installedReleaseIds)) return;
      _installedReleaseIds = Set<String>.unmodifiable(ids);
      _notify();
    });
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _generation++;
    _unlistenFromOperation(_operation);
    unawaited(_installedSubscription?.cancel());
    super.dispose();
  }
}

List<ConsoleGame> _deduplicateFeaturedShelfGames(Iterable<ConsoleGame> games) {
  final ids = <String>{};
  final platformTitles = <(String, String)>{};
  final unique = <ConsoleGame>[];
  for (final game in games) {
    final platformTitle = (
      _normalizeFeaturedIdentity(game.platformId),
      _normalizeFeaturedIdentity(game.title),
    );
    if (ids.contains(game.id) || platformTitles.contains(platformTitle)) {
      continue;
    }
    ids.add(game.id);
    platformTitles.add(platformTitle);
    unique.add(game);
  }
  return List<ConsoleGame>.unmodifiable(unique);
}

String _normalizeFeaturedIdentity(String value) =>
    value.trim().toLowerCase().replaceAll(RegExp(r'\s+'), ' ');
