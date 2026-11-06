import 'package:flutter/foundation.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';

enum DiscoverSection { featured, allGames }

enum DiscoverCatalogSort { title, rating }

enum DiscoverReleaseCompleteness { any, complete, partial }

enum DiscoverSurface { featured, allGames, systems, search, detail }

enum DiscoverLoadState { idle, loading, ready, failed }

enum DiscoverSearchZone { keyboard, results }

extension DiscoverSectionLabel on DiscoverSection {
  String get label => switch (this) {
    DiscoverSection.featured => 'Featured',
    DiscoverSection.allGames => 'Browse',
  };
}

extension DiscoverCatalogSortRequest on DiscoverCatalogSort {
  String? get apiValue => switch (this) {
    DiscoverCatalogSort.title => null,
    DiscoverCatalogSort.rating => 'rating',
  };
}

extension DiscoverReleaseCompletenessRequest on DiscoverReleaseCompleteness {
  String? get apiValue => switch (this) {
    DiscoverReleaseCompleteness.any => null,
    DiscoverReleaseCompleteness.complete => 'complete',
    DiscoverReleaseCompleteness.partial => 'partial',
  };
}

/// Stable identity for one Catalog authority operation.
///
/// Request keys keep this value rather than a bearer token. Tokens are
/// deliberately absent from presentation snapshots and logs.
@immutable
final class DiscoverOperationKey {
  const DiscoverOperationKey({
    required this.localProfileId,
    required this.serverInstanceId,
    required this.serverOrigin,
    required this.authorityGeneration,
    required this.epoch,
    required this.sessionIdentity,
  });

  factory DiscoverOperationKey.from(CatalogOperationContext operation) {
    final authority = operation.authority;
    return DiscoverOperationKey(
      localProfileId: authority.localProfileId,
      serverInstanceId: authority.connection.instanceId.value,
      serverOrigin: authority.connection.origin.toString(),
      authorityGeneration: authority.generation,
      epoch: operation.epoch,
      sessionIdentity: identityHashCode(operation.authenticatedSession),
    );
  }

  final String localProfileId;
  final String serverInstanceId;
  final String serverOrigin;
  final int authorityGeneration;
  final int epoch;
  final int sessionIdentity;

  @override
  bool operator ==(Object other) =>
      other is DiscoverOperationKey &&
      localProfileId == other.localProfileId &&
      serverInstanceId == other.serverInstanceId &&
      serverOrigin == other.serverOrigin &&
      authorityGeneration == other.authorityGeneration &&
      epoch == other.epoch &&
      sessionIdentity == other.sessionIdentity;

  @override
  int get hashCode => Object.hash(
    localProfileId,
    serverInstanceId,
    serverOrigin,
    authorityGeneration,
    epoch,
    sessionIdentity,
  );
}

/// Complete identity of a remote Catalog request.
///
/// A response is publishable only while this exact key remains current.
@immutable
final class DiscoverRequestKey {
  const DiscoverRequestKey({
    required this.operation,
    required this.surface,
    this.query = '',
    this.sort = DiscoverCatalogSort.title,
    this.completeness = DiscoverReleaseCompleteness.any,
    this.platformId,
    this.cursor,
    this.resourceId,
  });

  final DiscoverOperationKey operation;
  final DiscoverSurface surface;
  final String query;
  final DiscoverCatalogSort sort;
  final DiscoverReleaseCompleteness completeness;
  final String? platformId;
  final String? cursor;
  final String? resourceId;

  @override
  bool operator ==(Object other) =>
      other is DiscoverRequestKey &&
      operation == other.operation &&
      surface == other.surface &&
      query == other.query &&
      sort == other.sort &&
      completeness == other.completeness &&
      platformId == other.platformId &&
      cursor == other.cursor &&
      resourceId == other.resourceId;

  @override
  int get hashCode => Object.hash(
    operation,
    surface,
    query,
    sort,
    completeness,
    platformId,
    cursor,
    resourceId,
  );
}

@immutable
final class DiscoverFocusSnapshot {
  const DiscoverFocusSnapshot({this.itemId, this.scrollOffset = 0});

  final String? itemId;
  final double scrollOffset;

  DiscoverFocusSnapshot copyWith({
    String? itemId,
    bool clearItemId = false,
    double? scrollOffset,
  }) => DiscoverFocusSnapshot(
    itemId: clearItemId ? null : itemId ?? this.itemId,
    scrollOffset: scrollOffset ?? this.scrollOffset,
  );
}

@immutable
final class DiscoverSearchFocusSnapshot {
  const DiscoverSearchFocusSnapshot({
    this.query = '',
    this.zone = DiscoverSearchZone.keyboard,
    this.keyboardKeyIndex = 0,
    this.resultId,
    this.resultScrollOffset = 0,
  });

  final String query;
  final DiscoverSearchZone zone;
  final int keyboardKeyIndex;
  final String? resultId;
  final double resultScrollOffset;
}

@immutable
final class DiscoverFeaturedShelf {
  const DiscoverFeaturedShelf({
    required this.id,
    required this.eyebrow,
    required this.title,
    required this.games,
  });

  final String id;
  final String eyebrow;
  final String title;
  final List<ConsoleGame> games;
}

@immutable
final class DiscoverFeaturedSnapshot {
  const DiscoverFeaturedSnapshot({
    this.state = DiscoverLoadState.idle,
    this.shelves = const <DiscoverFeaturedShelf>[],
    this.requestKey,
  });

  final DiscoverLoadState state;
  final List<DiscoverFeaturedShelf> shelves;
  final DiscoverRequestKey? requestKey;
}

@immutable
final class DiscoverGamePageSnapshot {
  const DiscoverGamePageSnapshot({
    this.state = DiscoverLoadState.idle,
    this.items = const <ConsoleGame>[],
    this.nextCursor,
    this.hasNextPage = false,
    this.paging = false,
    this.pagingFailed = false,
    this.pagingRequestKey,
    this.requestKey,
  });

  final DiscoverLoadState state;
  final List<ConsoleGame> items;
  final String? nextCursor;
  final bool hasNextPage;
  final bool paging;
  final bool pagingFailed;
  final DiscoverRequestKey? pagingRequestKey;
  final DiscoverRequestKey? requestKey;
}

@immutable
final class DiscoverSystemsSnapshot {
  const DiscoverSystemsSnapshot({
    this.state = DiscoverLoadState.idle,
    this.items = const <ConsolePlatform>[],
    this.requestKey,
  });

  final DiscoverLoadState state;
  final List<ConsolePlatform> items;
  final DiscoverRequestKey? requestKey;
}

/// Immutable route callbacks shared by the retained Catalog features.
@immutable
final class DiscoverFeatureNavigation {
  const DiscoverFeatureNavigation({
    required this.onSelectSection,
    required this.onSearch,
    required this.onConnect,
  });

  final ValueChanged<DiscoverSection> onSelectSection;
  final VoidCallback onSearch;
  final VoidCallback onConnect;
}
