import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

/// Owns the on-disk layout for downloaded content and per-title save data.
///
/// The layout deliberately separates **evictable content** from **durable
/// saves/states/config**: clearing a release's content must never touch a
/// player's saves. All roots are keyed by the stable `platformShortName` (e.g.
/// `snes`) rather than an instance-specific encoded id, so saves survive a ROMD
/// reinstall.
///
/// ```
/// <base>/content/servers/{instance}/{platform}/{title}/{release}/
/// <base>/profiles/{profile}/servers/{instance}/games/{platform}/{title}/saves
/// <base>/profiles/{profile}/servers/{instance}/games/{platform}/{title}/states
/// <base>/config/servers/{instance}/{platform}/{title}/
/// ```
///
/// Staging and backup are siblings of the final content root so materialization
/// is a rename within one directory (atomic on the same volume).
final class ContentFileStore {
  const ContentFileStore({
    required this.baseDir,
    required String? serverInstanceId,
    TargetFilesystemPathProbe pathProbe = const IoTargetFilesystemPathProbe(),
  }) : _serverInstanceId = serverInstanceId,
       _pathProbe = pathProbe;

  /// Application support directory in production
  /// (`getApplicationSupportDirectory()`); a temp dir under test.
  final Directory baseDir;
  final String? _serverInstanceId;
  final TargetFilesystemPathProbe _pathProbe;

  Directory get serverContentRoot =>
      Directory(p.join(baseDir.path, 'content', 'servers', _serverKey));

  /// `<base>/content/servers/{instance}/{platform}/{title}/{release}/` — evictable.
  Directory contentRoot({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) => Directory(
    p.join(
      baseDir.path,
      'content',
      'servers',
      _serverKey,
      _key(platformShortName),
      _key(titleId),
      _key(releaseId),
    ),
  );

  /// Exact release-only v10-v11 content location. This path is used only as a
  /// read-only candidate during explicit online adoption; it never establishes
  /// server authority by itself.
  Directory legacyContentRoot({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) => Directory(
    p.join(
      baseDir.path,
      'content',
      _key(platformShortName),
      _key(titleId),
      _key(releaseId),
    ),
  );

  /// In-flight download target, sibling of [contentRoot] (same volume → the
  /// final swap is a rename, not a copy).
  Directory stagingRoot({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) => Directory(
    '${contentRoot(platformShortName: platformShortName, titleId: titleId, releaseId: releaseId).path}.staging',
  );

  /// Scratch location holding the previous content during an atomic replace.
  Directory backupRoot({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) => Directory(
    '${contentRoot(platformShortName: platformShortName, titleId: titleId, releaseId: releaseId).path}.backup',
  );

  Directory preflightRoot({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) => Directory(
    '${contentRoot(platformShortName: platformShortName, titleId: titleId, releaseId: releaseId).path}.preflight',
  );

  /// Exercises candidate paths on the target volume before staging is
  /// created. Exclusive file creation makes the target filesystem itself
  /// decide case and Unicode-normalization equivalence.
  Future<bool> pathsAreDistinctOnTarget({
    required Directory scratch,
    required Iterable<String> relativePaths,
  }) => _pathProbe.pathsAreDistinct(
    scratch: scratch,
    relativePaths: relativePaths,
    resolveWithin: resolveWithin,
  );

  Directory _profileGameRoot({
    required String localProfileId,
    required String platformShortName,
    required String titleId,
  }) => Directory(
    p.join(
      baseDir.path,
      'profiles',
      _key(localProfileId),
      'servers',
      _serverKey,
      'games',
      _key(platformShortName),
      _key(titleId),
    ),
  );

  /// Durable per-profile, per-title save root (SRAM etc.) — never evicted with
  /// content. Releases of the same title intentionally share this root.
  Directory saveRoot({
    required String localProfileId,
    required String platformShortName,
    required String titleId,
  }) => Directory(
    p.join(
      _profileGameRoot(
        localProfileId: localProfileId,
        platformShortName: platformShortName,
        titleId: titleId,
      ).path,
      'saves',
    ),
  );

  /// Durable per-profile, per-title save-state root — never evicted with
  /// content. Releases of the same title intentionally share this root.
  Directory stateRoot({
    required String localProfileId,
    required String platformShortName,
    required String titleId,
  }) => Directory(
    p.join(
      _profileGameRoot(
        localProfileId: localProfileId,
        platformShortName: platformShortName,
        titleId: titleId,
      ).path,
      'states',
    ),
  );

  /// Durable per-title emulator-config root (isolated runtime config) — never
  /// evicted with content.
  Directory configRoot({
    required String platformShortName,
    required String titleId,
  }) => Directory(
    p.join(
      baseDir.path,
      'config',
      'servers',
      _serverKey,
      _key(platformShortName),
      _key(titleId),
    ),
  );

  /// Resolves a manifest item's `relativePath` to a [File] under [root],
  /// rejecting any path that would escape it (absolute, or containing `..`).
  /// Manifest paths are posix (`/`-separated); the result uses the host
  /// separator.
  File resolveWithin(Directory root, String relativePath) {
    final segments = p.posix
        .split(relativePath)
        .where((segment) => segment.isNotEmpty && segment != '.')
        .toList(growable: false);

    if (segments.isEmpty ||
        segments.contains('..') ||
        p.posix.isAbsolute(relativePath)) {
      throw ArgumentError.value(relativePath, 'relativePath', 'unsafe path');
    }

    return File(p.join(root.path, p.joinAll(segments)));
  }

  Future<void> ensureDir(Directory dir) async {
    if (!await dir.exists()) {
      await dir.create(recursive: true);
    }
  }

  /// Finds only structurally canonical instance-scoped content artifacts.
  /// Arbitrary persisted paths and symlinks are never followed.
  Future<List<ContentArtifactIdentity>> scanCanonicalArtifacts() async {
    final root = serverContentRoot;
    if (await FileSystemEntity.type(root.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      return const <ContentArtifactIdentity>[];
    }
    final found = <ContentArtifactIdentity>{};
    await for (final platform in root.list(followLinks: false)) {
      if (platform is! Directory ||
          await FileSystemEntity.type(platform.path, followLinks: false) !=
              FileSystemEntityType.directory) {
        continue;
      }
      final platformKey = p.basename(platform.path);
      if (!_platformKey.hasMatch(platformKey)) continue;
      await for (final title in platform.list(followLinks: false)) {
        if (title is! Directory ||
            await FileSystemEntity.type(title.path, followLinks: false) !=
                FileSystemEntityType.directory) {
          continue;
        }
        final titleId = p.basename(title.path);
        if (RomdPublicId.tryParse(titleId) == null) continue;
        await for (final artifact in title.list(followLinks: false)) {
          if (artifact is! Directory ||
              await FileSystemEntity.type(artifact.path, followLinks: false) !=
                  FileSystemEntityType.directory) {
            continue;
          }
          var releaseId = p.basename(artifact.path);
          for (final suffix in const <String>[
            '.staging',
            '.backup',
            '.preflight',
          ]) {
            if (releaseId.endsWith(suffix)) {
              releaseId = releaseId.substring(
                0,
                releaseId.length - suffix.length,
              );
              break;
            }
          }
          if (RomdPublicId.tryParse(releaseId) == null) continue;
          found.add(
            ContentArtifactIdentity(
              platformShortName: platformKey,
              titleId: titleId,
              releaseId: releaseId,
            ),
          );
        }
      }
    }
    final result = found.toList()
      ..sort((a, b) => a.sortKey.compareTo(b.sortKey));
    return List<ContentArtifactIdentity>.unmodifiable(result);
  }

  /// Guards a path-segment key (platform / title / release). These come from
  /// the manifest (short names + encoded ids) and must never contain
  /// separators or traversal.
  String _key(String value) {
    if (value.isEmpty ||
        value.contains('/') ||
        value.contains(r'\') ||
        value == '.' ||
        value == '..') {
      throw ArgumentError.value(value, 'key', 'unsafe path segment');
    }
    return value;
  }

  static final RegExp _platformKey = RegExp(r'^[a-z0-9]+(?:[-_][a-z0-9]+)*$');

  String get _serverKey {
    final value = _serverInstanceId;
    if (value == null) {
      throw StateError('No ROMD server instance is selected.');
    }
    if (RomdServerInstanceId.tryParse(value) == null) {
      throw StateError('The selected ROMD server instance is invalid.');
    }
    return _key(value);
  }
}

final class ContentArtifactIdentity {
  const ContentArtifactIdentity({
    required this.platformShortName,
    required this.titleId,
    required this.releaseId,
  });

  final String platformShortName;
  final String titleId;
  final String releaseId;

  String get sortKey => '$platformShortName\u0000$titleId\u0000$releaseId';

  @override
  bool operator ==(Object other) =>
      other is ContentArtifactIdentity &&
      platformShortName == other.platformShortName &&
      titleId == other.titleId &&
      releaseId == other.releaseId;

  @override
  int get hashCode => Object.hash(platformShortName, titleId, releaseId);
}

abstract interface class TargetFilesystemPathProbe {
  Future<bool> pathsAreDistinct({
    required Directory scratch,
    required Iterable<String> relativePaths,
    required File Function(Directory root, String relativePath) resolveWithin,
  });
}

final class IoTargetFilesystemPathProbe implements TargetFilesystemPathProbe {
  const IoTargetFilesystemPathProbe();

  @override
  Future<bool> pathsAreDistinct({
    required Directory scratch,
    required Iterable<String> relativePaths,
    required File Function(Directory root, String relativePath) resolveWithin,
  }) async {
    try {
      if (await scratch.exists()) {
        await scratch.delete(recursive: true);
      }
      await scratch.create(recursive: true);
      for (final relativePath in relativePaths) {
        final file = resolveWithin(scratch, relativePath);
        await file.parent.create(recursive: true);
        await file.create(exclusive: true);
      }
      return true;
    } on FileSystemException {
      return false;
    } finally {
      try {
        if (await scratch.exists()) {
          await scratch.delete(recursive: true);
        }
      } on FileSystemException {
        // Acquisition will fail separately if the target cannot be prepared.
      }
    }
  }
}
