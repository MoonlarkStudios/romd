import 'console_artwork.dart';

export 'console_artwork.dart';

final class ConsoleGame {
  const ConsoleGame({
    required this.id,
    required this.platformId,
    required this.platformName,
    required this.title,
    required this.releaseDate,
    required this.coverUrl,
    required this.genre,
    required this.rating,
    required this.releaseCount,
    required this.defaultReleaseId,
    this.artwork = const [],
  });

  final String id;
  final String platformId;
  final String platformName;
  final String title;
  final DateTime? releaseDate;
  final Uri? coverUrl;
  final String? genre;
  final double? rating;
  final int releaseCount;
  final String? defaultReleaseId;
  final List<ConsoleArtwork> artwork;
  ConsoleArtwork? get poster => artwork.forRole('Poster');
  ConsoleArtwork? get hero => artwork.forRole('Hero');

  int? get releaseYear => releaseDate?.year;

  bool get canLaunch => defaultReleaseId != null;

  String get availabilityLabel => canLaunch ? 'Ready' : 'Unavailable';
}

final class ConsoleGamePage {
  const ConsoleGamePage({
    required this.items,
    required this.nextCursor,
    required this.hasNextPage,
  });

  final List<ConsoleGame> items;
  final String? nextCursor;
  final bool hasNextPage;
}

final class ConsoleCollection {
  const ConsoleCollection({
    required this.id,
    required this.name,
    required this.description,
    required this.platformId,
    required this.platformName,
    required this.coverUrl,
    required this.heroUrl,
    required this.itemCount,
    this.isFeatured = true,
  });

  final String id;
  final String name;
  final String? description;
  final String? platformId;
  final String? platformName;
  final Uri? coverUrl;
  final Uri? heroUrl;
  final int itemCount;
  final bool isFeatured;
}

final class ConsolePlatform {
  const ConsolePlatform({
    required this.id,
    required this.name,
    required this.shortName,
    required this.manufacturer,
    required this.titleCount,
    required this.coverUrl,
  });

  final String id;
  final String name;
  final String shortName;
  final String? manufacturer;
  final int titleCount;
  final Uri? coverUrl;
}

final class ConsolePlatformPage {
  const ConsolePlatformPage({
    required this.items,
    required this.nextCursor,
    required this.hasNextPage,
  });

  final List<ConsolePlatform> items;
  final String? nextCursor;
  final bool hasNextPage;
}

final class ConsoleGameDetail {
  const ConsoleGameDetail({
    required this.id,
    required this.platformId,
    required this.platformName,
    required this.title,
    required this.description,
    required this.publisher,
    required this.developer,
    required this.genre,
    required this.releaseDate,
    required this.players,
    required this.rating,
    required this.media,
    required this.releases,
    required this.defaultReleaseId,
    this.artwork = const [],
  });

  final String id;
  final String platformId;
  final String platformName;
  final String title;
  final String? description;
  final String? publisher;
  final String? developer;
  final String? genre;
  final DateTime? releaseDate;
  final int? players;
  final double? rating;
  final List<ConsoleMediaRef> media;
  final List<ConsoleRelease> releases;
  final String? defaultReleaseId;
  final List<ConsoleArtwork> artwork;
  ConsoleArtwork? get poster => artwork.forRole('Poster');
  ConsoleArtwork? get hero => artwork.forRole('Hero');
}

final class ConsoleReleaseManifest {
  const ConsoleReleaseManifest({
    required this.releaseId,
    required this.titleId,
    required this.systemKey,
    required this.name,
    required this.revision,
    required this.isComplete,
    required this.runtime,
    required this.items,
  });

  final String releaseId;
  final String titleId;
  final String systemKey;

  /// Stable platform identifier (e.g. `snes`) — the key the local runtime uses
  /// to select a play adapter. Unlike [platformId] (an instance-specific
  /// encoded id), this is stable across ROMD deployments.
  final String name;
  final String? revision;
  final bool isComplete;
  final ConsoleReleaseRuntime runtime;
  final List<ConsoleReleaseManifestItem> items;

  int get availableItemCount => items.where((item) => item.isAvailable).length;
}

final class ConsoleReleaseRuntime {
  const ConsoleReleaseRuntime({
    required this.contentType,
    required this.launch,
    required this.packaging,
    required this.minimumInstallBytes,
  });

  final String contentType;
  final ConsoleLaunchTarget? launch;
  final String packaging;
  final int minimumInstallBytes;
}

final class ConsoleLaunchTarget {
  const ConsoleLaunchTarget({required this.type, required this.relativePath});

  final String type;
  final String relativePath;
}

final class ConsoleReleaseManifestItem {
  const ConsoleReleaseManifestItem({
    required this.relativePath,
    required this.role,
    required this.sizeBytes,
    required this.sha256,
    required this.isAvailable,
    required this.downloadUrl,
  });

  final String relativePath;
  final String role;
  final int sizeBytes;
  final String? sha256;
  final bool isAvailable;
  final Uri? downloadUrl;
}

final class ConsoleMediaRef {
  const ConsoleMediaRef({
    required this.id,
    required this.type,
    required this.url,
    required this.isPrimary,
  });

  final String id;
  final String type;
  final Uri url;
  final bool isPrimary;
}

final class ConsoleRelease {
  const ConsoleRelease({
    required this.id,
    required this.name,
    required this.revision,
    required this.regions,
    required this.languages,
    required this.sizeBytes,
    required this.isComplete,
  });

  final String id;
  final String name;
  final String? revision;
  final List<String> regions;
  final List<String> languages;
  final int sizeBytes;
  final bool isComplete;
}
