import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';

import '../../../domain/system_key.dart';
import 'content_file_store.dart';
import 'install_mutation_serializer.dart';
import 'installed_artwork_store.dart';

final class ProfileGameCleanupService {
  ProfileGameCleanupService({
    required AppDatabase database,
    required Directory baseDir,
    required InstallMutationSerializer serializer,
    required RefreshTokenStore refreshTokenStore,
    ContentVerifier verifier = const ContentVerifier(),
    Future<bool> Function(FileSystemEntity entity)? deleteEntity,
  }) : _database = database,
       _baseDir = baseDir,
       _serializer = serializer,
       _refreshTokens = refreshTokenStore is SerializedRefreshTokenStore
           ? refreshTokenStore
           : SerializedRefreshTokenStore(refreshTokenStore),
       _verifier = verifier,
       _deleteEntity = deleteEntity ?? _deleteEntityDefault;

  final AppDatabase _database;
  final Directory _baseDir;
  final InstallMutationSerializer _serializer;
  final RefreshTokenStore _refreshTokens;
  final ContentVerifier _verifier;
  final Future<bool> Function(FileSystemEntity entity) _deleteEntity;

  Future<void> removeProfileGame({
    required InstallAuthorityContext? authority,
    required String releaseId,
    InstallOperationLease? operation,
  }) async {
    final parsedRelease = RomdPublicId.tryParse(releaseId);
    if (authority == null ||
        parsedRelease == null ||
        (operation != null && !operation.isCurrent)) {
      return;
    }
    final key = InstallMutationKey(
      serverInstanceId: authority.connection.instanceId,
      releaseId: parsedRelease,
    );
    final lease = await _serializer.acquire(key);
    try {
      final removed = await _database.transaction(() async {
        if (operation != null && !operation.isCurrent) {
          throw const _CleanupOperationInvalidated();
        }
        if (!await _authorityIsCurrent(authority)) return false;
        await (_database.delete(_database.profileLocalGames)..where(
              (row) =>
                  row.localProfileId.equals(authority.localProfileId) &
                  row.serverInstanceId.equals(key.serverInstanceId.value) &
                  row.releaseId.equals(key.releaseId.value),
            ))
            .go();
        if (operation != null && !operation.isCurrent) {
          throw const _CleanupOperationInvalidated();
        }
        return true;
      });
      if (removed && (operation == null || operation.isCurrent)) {
        await _cleanupKeyLocked(key);
      }
    } on _CleanupOperationInvalidated {
      return;
    } finally {
      lease.release();
    }
  }

  Future<bool> deleteProfile(String localProfileId) async {
    while (true) {
      final initial = await _profileKeys(localProfileId);
      if (initial == null) return false;
      final keys = initial.toList()..sort();
      final leases = <InstallMutationLease>[];
      try {
        for (final key in keys) {
          leases.add(await _serializer.acquire(key));
        }
        if (!await _deleteProfileCredentials(localProfileId)) return false;
        final deleted = await _database.transaction(() async {
          final current = await _profileKeys(localProfileId);
          if (current == null || !_sameKeys(current, initial)) return null;
          await (_database.delete(
            _database.romdAccountLinks,
          )..where((row) => row.localProfileId.equals(localProfileId))).go();
          await (_database.delete(
            _database.controllerProfileBindingRules,
          )..where((row) => row.localProfileId.equals(localProfileId))).go();
          await (_database.delete(
            _database.controllerMappingProfiles,
          )..where((row) => row.localProfileId.equals(localProfileId))).go();
          await (_database.delete(
            _database.profileLocalGames,
          )..where((row) => row.localProfileId.equals(localProfileId))).go();
          await (_database.delete(
            _database.pendingServerLocators,
          )..where((row) => row.localProfileId.equals(localProfileId))).go();
          final count = await (_database.delete(
            _database.localProfiles,
          )..where((row) => row.id.equals(localProfileId))).go();
          return count == 1;
        });
        if (deleted == null) continue;
        if (!deleted) return false;
        for (final key in keys) {
          await _cleanupKeyLocked(key);
        }
        return true;
      } finally {
        for (final lease in leases.reversed) {
          lease.release();
        }
      }
    }
  }

  Future<void> cleanupOrphans() async {
    final candidates = <InstallMutationKey, Set<ContentArtifactIdentity>>{};
    for (final row in await _database.select(_database.localInstalls).get()) {
      final instance = RomdServerInstanceId.tryParse(row.serverInstanceId);
      final release = RomdPublicId.tryParse(row.releaseId);
      if (instance != null && release != null) {
        candidates.putIfAbsent(
          InstallMutationKey(serverInstanceId: instance, releaseId: release),
          () => <ContentArtifactIdentity>{},
        );
      }
    }
    for (final connection
        in await _database.select(_database.serverConnections).get()) {
      final instance = RomdServerInstanceId.tryParse(connection.instanceId);
      if (instance == null) continue;
      final store = _fileStore(instance);
      for (final artifact in await store.scanCanonicalArtifacts()) {
        final release = RomdPublicId.tryParse(artifact.releaseId)!;
        candidates
            .putIfAbsent(
              InstallMutationKey(
                serverInstanceId: instance,
                releaseId: release,
              ),
              () => <ContentArtifactIdentity>{},
            )
            .add(artifact);
      }
    }

    final keys = candidates.keys.toList()..sort();
    for (final key in keys) {
      final lease = await _serializer.acquire(key);
      try {
        await _cleanupKeyLocked(key, artifacts: candidates[key]);
      } finally {
        lease.release();
      }
    }
  }

  Future<void> _cleanupKeyLocked(
    InstallMutationKey key, {
    Set<ContentArtifactIdentity>? artifacts,
  }) async {
    final references = await _referenceCount(key);
    final rows =
        await (_database.select(_database.localInstalls)..where(
              (row) =>
                  row.serverInstanceId.equals(key.serverInstanceId.value) &
                  row.releaseId.equals(key.releaseId.value),
            ))
            .get();
    if (rows.length > 1) return;
    final row = rows.isEmpty ? null : rows.single;
    final activeArtifact = row == null ? null : _canonicalArtifact(key, row);
    if (row != null && activeArtifact == null) return;

    if (references > 0) {
      if (row != null && activeArtifact != null) {
        final store = _fileStore(key.serverInstanceId);
        await _deleteHiddenSiblings(store, activeArtifact);
        await _cleanupLegacyDuplicate(row, store);
      }
      return;
    }

    final toDelete = <ContentArtifactIdentity>{...?artifacts};
    if (activeArtifact != null) toDelete.add(activeArtifact);
    for (final artifact in toDelete) {
      if (!await _deleteAllArtifacts(
        _fileStore(key.serverInstanceId),
        artifact,
      )) {
        return;
      }
    }

    if (!await InstalledArtworkStore(
      _baseDir,
    ).remove(key.serverInstanceId.value, key.releaseId.value))
      return;

    if (row != null) {
      await _database.transaction(() async {
        if (await _referenceCount(key) != 0) return;
        await (_database.delete(_database.localInstalls)..where(
              (candidate) =>
                  candidate.serverInstanceId.equals(
                    key.serverInstanceId.value,
                  ) &
                  candidate.releaseId.equals(key.releaseId.value),
            ))
            .go();
      });
    }
  }

  ContentArtifactIdentity? _canonicalArtifact(
    InstallMutationKey key,
    LocalInstallRow row,
  ) {
    final title = RomdPublicId.tryParse(row.titleId);
    final platform = isSystemKey(row.platformId);
    if (title == null ||
        !platform ||
        !_platformKey.hasMatch(row.platformShortName) ||
        row.platformShortName.length > 50) {
      return null;
    }
    final artifact = ContentArtifactIdentity(
      platformShortName: row.platformShortName,
      titleId: title.value,
      releaseId: key.releaseId.value,
    );
    final canonical = _fileStore(key.serverInstanceId).contentRoot(
      platformShortName: artifact.platformShortName,
      titleId: artifact.titleId,
      releaseId: artifact.releaseId,
    );
    return row.contentRoot == canonical.path ? artifact : null;
  }

  Future<void> _cleanupLegacyDuplicate(
    LocalInstallRow active,
    ContentFileStore store,
  ) async {
    final rows = await (_database.select(
      _database.legacyLocalInstalls,
    )..where((row) => row.releaseId.equals(active.releaseId))).get();
    if (rows.length != 1) return;
    final legacy = rows.single;
    final canonical = store.legacyContentRoot(
      platformShortName: active.platformShortName,
      titleId: active.titleId,
      releaseId: active.releaseId,
    );
    if (legacy.titleId != active.titleId ||
        legacy.platformId != active.platformId ||
        legacy.platformShortName != active.platformShortName ||
        legacy.contentRoot != canonical.path ||
        active.state != InstallState.installed.name ||
        legacy.state != InstallState.installed.name ||
        active.installMode != 'permanent' ||
        legacy.installMode != 'permanent' ||
        legacy.manifestSnapshot != active.manifestSnapshot ||
        legacy.manifestFingerprint != active.manifestFingerprint) {
      return;
    }
    if (!await _isSafeLegacyRoot(canonical)) return;
    final legacyType = await FileSystemEntity.type(
      canonical.path,
      followLinks: false,
    );
    if (legacyType == FileSystemEntityType.notFound) {
      await _deleteExactLegacyRow(active.releaseId);
      return;
    }
    if (legacyType != FileSystemEntityType.directory ||
        !await _legacyFilesMatchActiveSnapshot(active, legacy, canonical) ||
        !await _deleteLegacyRoot(canonical)) {
      return;
    }
    await _deleteExactLegacyRow(active.releaseId);
  }

  Future<void> _deleteExactLegacyRow(String releaseId) async {
    await (_database.delete(
      _database.legacyLocalInstalls,
    )..where((row) => row.releaseId.equals(releaseId))).go();
  }

  Future<bool> _deleteAllArtifacts(
    ContentFileStore store,
    ContentArtifactIdentity artifact,
  ) async {
    final roots = _artifactRoots(store, artifact);
    // Hidden scratch roots go first so a failure cannot unnecessarily remove
    // the still-playable final content while its active row is retained.
    for (final root in <Directory>[...roots.skip(1), roots.first]) {
      if (!await _deleteManagedRoot(store, root)) return false;
    }
    return true;
  }

  Future<void> _deleteHiddenSiblings(
    ContentFileStore store,
    ContentArtifactIdentity artifact,
  ) async {
    final roots = _artifactRoots(store, artifact);
    for (final root in roots.skip(1)) {
      await _deleteManagedRoot(store, root);
    }
  }

  Future<bool> _legacyFilesMatchActiveSnapshot(
    LocalInstallRow active,
    LegacyLocalInstallRow legacy,
    Directory legacyRoot,
  ) async {
    final items = _decodeSnapshot(active.manifestSnapshot);
    if (items == null || items.isEmpty) return false;
    final fingerprint = _verifier.manifestFingerprint(
      items.map(
        (item) => (
          relativePath: item.relativePath,
          sizeBytes: item.sizeBytes,
          sha256: item.sha256,
        ),
      ),
    );
    if (fingerprint != active.manifestFingerprint ||
        fingerprint != legacy.manifestFingerprint) {
      return false;
    }
    final store = _fileStore(
      RomdServerInstanceId.tryParse(active.serverInstanceId)!,
    );
    for (final item in items) {
      final sha256 = item.sha256;
      if (sha256 == null ||
          !await _verifier.verify(
            file: store.resolveWithin(legacyRoot, item.relativePath),
            expectedSize: item.sizeBytes,
            expectedSha256Hex: sha256,
          )) {
        return false;
      }
    }
    return true;
  }

  List<InstalledItem>? _decodeSnapshot(String encoded) {
    try {
      final decoded = jsonDecode(encoded);
      if (decoded is! List<Object?> || decoded.isEmpty) return null;
      final paths = <String>{};
      final items = <InstalledItem>[];
      for (final value in decoded) {
        if (value is! Map<String, Object?> ||
            value.length != 3 ||
            !value.containsKey('relativePath') ||
            !value.containsKey('sizeBytes') ||
            !value.containsKey('sha256')) {
          return null;
        }
        final relativePath = value['relativePath'];
        final sizeBytes = value['sizeBytes'];
        final sha256 = value['sha256'];
        if (relativePath is! String ||
            relativePath.isEmpty ||
            !_isSafeRelativePath(relativePath) ||
            !paths.add(relativePath) ||
            sizeBytes is! int ||
            sizeBytes < 0 ||
            sha256 is! String ||
            !_sha256.hasMatch(sha256)) {
          return null;
        }
        items.add(
          InstalledItem(
            relativePath: relativePath,
            sizeBytes: sizeBytes,
            sha256: sha256,
          ),
        );
      }
      return items;
    } on Object {
      return null;
    }
  }

  Future<bool> _deleteManagedRoot(
    ContentFileStore store,
    Directory root,
  ) async {
    if (!await _isSafeRootWithin(store.serverContentRoot, root)) return false;
    return _deleteEntity(root);
  }

  Future<bool> _deleteLegacyRoot(Directory root) async {
    if (!await _isSafeLegacyRoot(root)) return false;
    return _deleteEntity(root);
  }

  Future<bool> _isSafeLegacyRoot(Directory root) =>
      _isSafeRootWithin(Directory(p.join(_baseDir.path, 'content')), root);

  Future<bool> _isSafeRootWithin(
    Directory root,
    Directory candidateRoot,
  ) async {
    final instanceRoot = p.normalize(p.absolute(root.path));
    final candidate = p.normalize(p.absolute(candidateRoot.path));
    if (!p.isWithin(instanceRoot, candidate)) return false;

    var current = instanceRoot;
    final relative = p.relative(candidate, from: instanceRoot);
    for (final segment in <String>['', ...p.split(relative)]) {
      if (segment.isNotEmpty) current = p.join(current, segment);
      final type = await FileSystemEntity.type(current, followLinks: false);
      if (type == FileSystemEntityType.link ||
          (type != FileSystemEntityType.notFound &&
              type != FileSystemEntityType.directory)) {
        return false;
      }
    }
    return true;
  }

  List<Directory> _artifactRoots(
    ContentFileStore store,
    ContentArtifactIdentity artifact,
  ) => <Directory>[
    store.contentRoot(
      platformShortName: artifact.platformShortName,
      titleId: artifact.titleId,
      releaseId: artifact.releaseId,
    ),
    store.stagingRoot(
      platformShortName: artifact.platformShortName,
      titleId: artifact.titleId,
      releaseId: artifact.releaseId,
    ),
    store.backupRoot(
      platformShortName: artifact.platformShortName,
      titleId: artifact.titleId,
      releaseId: artifact.releaseId,
    ),
    store.preflightRoot(
      platformShortName: artifact.platformShortName,
      titleId: artifact.titleId,
      releaseId: artifact.releaseId,
    ),
  ];

  Future<int> _referenceCount(InstallMutationKey key) async {
    final count = _database.profileLocalGames.localProfileId.count();
    final query = _database.selectOnly(_database.profileLocalGames)
      ..addColumns(<Expression<Object>>[count])
      ..where(
        _database.profileLocalGames.serverInstanceId.equals(
              key.serverInstanceId.value,
            ) &
            _database.profileLocalGames.releaseId.equals(key.releaseId.value),
      );
    return (await query.getSingle()).read(count) ?? 0;
  }

  Future<bool> _authorityIsCurrent(InstallAuthorityContext authority) async {
    final profiles = await (_database.select(
      _database.localProfiles,
    )..where((row) => row.id.equals(authority.localProfileId))).get();
    if (profiles.length != 1) return false;
    final profile = profiles.single;
    if (profile.selectedServerInstanceId !=
            authority.connection.instanceId.value ||
        profile.serverSelectionGeneration != authority.generation) {
      return false;
    }
    final connections =
        await (_database.select(_database.serverConnections)..where(
              (row) =>
                  row.instanceId.equals(authority.connection.instanceId.value),
            ))
            .get();
    return connections.length == 1 &&
        connections.single.lastKnownOrigin ==
            authority.connection.origin.toString();
  }

  Future<Set<InstallMutationKey>?> _profileKeys(String profileId) async {
    final profiles = await (_database.select(
      _database.localProfiles,
    )..where((row) => row.id.equals(profileId))).get();
    if (profiles.length != 1) return null;
    final rows = await (_database.select(
      _database.profileLocalGames,
    )..where((row) => row.localProfileId.equals(profileId))).get();
    final keys = <InstallMutationKey>{};
    for (final row in rows) {
      final instance = RomdServerInstanceId.tryParse(row.serverInstanceId);
      final release = RomdPublicId.tryParse(row.releaseId);
      if (instance == null || release == null) return null;
      keys.add(
        InstallMutationKey(serverInstanceId: instance, releaseId: release),
      );
    }
    return keys;
  }

  Future<bool> _deleteProfileCredentials(String profileId) async {
    try {
      // ServerConnections is append-only discovery history. Every v2 refresh
      // token key is profile+instance, and a token can only be written after
      // its instance has been discovered into this table. Iterating every row
      // is therefore the exact enumerable namespace for this profile.
      final instances = <RomdServerInstanceId>[];
      for (final row
          in await _database.select(_database.serverConnections).get()) {
        final instance = RomdServerInstanceId.tryParse(row.instanceId);
        if (instance == null) return false;
        instances.add(instance);
      }
      instances.sort((left, right) => left.value.compareTo(right.value));
      for (final instance in instances) {
        await _refreshTokens.delete(
          profileId: profileId,
          serverInstanceId: instance,
        );
      }

      final origins = <Uri>{};
      final profiles = await (_database.select(
        _database.localProfiles,
      )..where((row) => row.id.equals(profileId))).get();
      if (profiles.length != 1) return false;
      final profileOrigin = RomdServerOrigins.tryParse(
        profiles.single.romdServerOrigin,
      );
      if (profileOrigin != null) origins.add(profileOrigin);
      final pending = await (_database.select(
        _database.pendingServerLocators,
      )..where((row) => row.localProfileId.equals(profileId))).get();
      if (pending.length > 1) return false;
      if (pending.isNotEmpty) {
        final pendingOrigin = RomdServerOrigins.tryParse(
          pending.single.normalizedOrigin,
        );
        if (pendingOrigin == null) return false;
        origins.add(pendingOrigin);
      }
      final sortedOrigins = origins.toList()
        ..sort((left, right) => left.toString().compareTo(right.toString()));
      for (final origin in sortedOrigins) {
        await _refreshTokens.deleteLegacyOriginToken(
          profileId: profileId,
          serverOrigin: origin,
        );
      }
      return true;
    } on Object {
      return false;
    }
  }

  bool _sameKeys(Set<InstallMutationKey> left, Set<InstallMutationKey> right) =>
      left.length == right.length && left.containsAll(right);

  ContentFileStore _fileStore(RomdServerInstanceId instance) =>
      ContentFileStore(baseDir: _baseDir, serverInstanceId: instance.value);

  static Future<bool> _deleteEntityDefault(FileSystemEntity entity) async {
    try {
      if (await entity.exists()) await entity.delete(recursive: true);
      return true;
    } on Object {
      return false;
    }
  }

  static final RegExp _platformKey = RegExp(r'^[a-z0-9]+(?:[-_][a-z0-9]+)*$');
  static final RegExp _sha256 = RegExp(r'^[a-f0-9]{64}$');

  static bool _isSafeRelativePath(String value) {
    if (value.startsWith('/') || value.contains(r'\')) return false;
    return value
        .split('/')
        .every(
          (segment) =>
              segment.isNotEmpty &&
              segment != '.' &&
              segment != '..' &&
              segment.trimRight() == segment &&
              !RegExp(r'[<>:"|?*\x00-\x1f]').hasMatch(segment),
        );
  }
}

final class _CleanupOperationInvalidated implements Exception {
  const _CleanupOperationInvalidated();
}
