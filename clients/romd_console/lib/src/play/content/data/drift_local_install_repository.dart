import 'dart:convert';

import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/local_install_repository.dart';

import '../../../domain/system_key.dart';
import 'content_file_store.dart';
import 'installed_artwork_store.dart';

final class DriftLocalInstallRepository implements LocalInstallRepository {
  DriftLocalInstallRepository({
    required AppDatabase database,
    required RomdServerInstanceId? serverInstanceId,
    required ContentFileStore fileStore,
  }) : _db = database,
       _serverInstanceId = serverInstanceId,
       _fileStore = fileStore;

  final AppDatabase _db;
  final RomdServerInstanceId? _serverInstanceId;
  final ContentFileStore _fileStore;

  @override
  Future<LocalInstall?> findByReleaseId(String releaseId) async {
    final scope = _serverInstanceId;
    if (scope == null || RomdPublicId.tryParse(releaseId) == null) {
      return null;
    }
    final row =
        await (_db.select(_db.localInstalls)..where(
              (t) =>
                  t.serverInstanceId.equals(scope.value) &
                  t.releaseId.equals(releaseId),
            ))
            .getSingleOrNull();
    return row == null ? null : parseStoredRow(row);
  }

  @override
  Future<List<LocalInstall>> listInstalled() async {
    if (_serverInstanceId == null) {
      return const <LocalInstall>[];
    }
    final rows = await _installedQuery().get();
    return _sortInstalled(rows.map(parseStoredRow).toList(growable: false));
  }

  @override
  Future<List<LocalInstall>> listInstalledByTitleId(String titleId) async {
    if (_serverInstanceId == null || RomdPublicId.tryParse(titleId) == null) {
      return const <LocalInstall>[];
    }
    final rows =
        await (_installedQuery()..where((t) => t.titleId.equals(titleId)))
            .get();
    return _sortInstalled(rows.map(parseStoredRow).toList(growable: false));
  }

  @override
  Stream<List<LocalInstall>> watchInstalled() => _serverInstanceId == null
      ? Stream<List<LocalInstall>>.value(const <LocalInstall>[])
      : _installedQuery().watch().map(
          (rows) =>
              _sortInstalled(rows.map(parseStoredRow).toList(growable: false)),
        );

  @override
  Stream<List<LocalInstall>> watchInstalledByTitleId(String titleId) =>
      _serverInstanceId == null || RomdPublicId.tryParse(titleId) == null
      ? Stream<List<LocalInstall>>.value(const <LocalInstall>[])
      : (_installedQuery()..where((t) => t.titleId.equals(titleId)))
            .watch()
            .map(
              (rows) => _sortInstalled(
                rows.map(parseStoredRow).toList(growable: false),
              ),
            );

  @override
  Stream<Set<String>> watchInstalledReleaseIds() {
    if (_serverInstanceId == null) {
      return Stream<Set<String>>.value(const <String>{});
    }
    return _installedQuery().watch().map(
      (rows) =>
          rows.map(parseStoredRow).map((install) => install.releaseId).toSet(),
    );
  }

  @override
  Future<void> upsert(LocalInstall install) => _validateWrite(install)
      ? _db
            .into(_db.localInstalls)
            .insertOnConflictUpdate(_toCompanion(install))
      : Future<void>.error(
          const LocalInstallProjectionException(
            'Install identity does not match the active server scope.',
          ),
        );

  @override
  Future<void> updateState(String releaseId, InstallState state) async {
    final scope = _serverInstanceId;
    if (scope == null || RomdPublicId.tryParse(releaseId) == null) {
      return;
    }
    await (_db.update(_db.localInstalls)..where(
          (t) =>
              t.serverInstanceId.equals(scope.value) &
              t.releaseId.equals(releaseId),
        ))
        .write(LocalInstallsCompanion(state: Value(state.name)));
  }

  @override
  Future<void> deleteByReleaseId(String releaseId) async {
    final scope = _serverInstanceId;
    if (scope == null || RomdPublicId.tryParse(releaseId) == null) {
      return;
    }
    await (_db.delete(_db.localInstalls)..where(
          (t) =>
              t.serverInstanceId.equals(scope.value) &
              t.releaseId.equals(releaseId),
        ))
        .go();
  }

  SimpleSelectStatement<$LocalInstallsTable, LocalInstallRow>
  _installedQuery() => _db.select(_db.localInstalls)
    ..where(
      (t) =>
          t.serverInstanceId.equals(_serverInstanceId!.value) &
          t.state.equals(InstallState.installed.name),
    );

  /// Strict projection parser shared by launch preauthorization so a corrupt
  /// install is rejected before any access request is sent.
  LocalInstall parseStoredRow(LocalInstallRow row) {
    final serverInstanceId = RomdServerInstanceId.tryParse(
      row.serverInstanceId,
    );
    final releaseId = RomdPublicId.tryParse(row.releaseId);
    final titleId = RomdPublicId.tryParse(row.titleId);
    final state = InstallState.values.asNameMap()[row.state];
    final coverUrl = row.coverUrl == null ? null : Uri.tryParse(row.coverUrl!);
    if (serverInstanceId == null ||
        serverInstanceId != _serverInstanceId ||
        releaseId == null ||
        titleId == null ||
        state == null ||
        row.platformId.isEmpty ||
        !isSystemKey(row.platformId) ||
        row.contentRoot.isEmpty ||
        row.launchRelativePath.isEmpty ||
        row.sizeBytes < 0 ||
        (row.coverUrl != null && coverUrl == null)) {
      throw const LocalInstallProjectionException(
        'Stored install projection is inconsistent.',
      );
    }
    final canonicalRoot = _fileStore.contentRoot(
      platformShortName: row.platformShortName,
      titleId: row.titleId,
      releaseId: row.releaseId,
    );
    if (row.contentRoot != canonicalRoot.path) {
      throw const LocalInstallProjectionException(
        'Stored install root is not canonical for its server scope.',
      );
    }
    final items = _decodeItems(row.manifestSnapshot);
    final collisionKeys = items
        .map((item) => _portablePathKey(item.relativePath))
        .toSet();
    final launchKey = _portablePathKey(row.launchRelativePath);
    if (row.platformShortName.isEmpty ||
        row.platformShortName.trim() != row.platformShortName ||
        row.platformShortName.length > 50 ||
        !RegExp(
          r'^[a-z0-9]+(?:-[a-z0-9]+)*$',
        ).hasMatch(row.platformShortName) ||
        row.manifestFingerprint.isEmpty ||
        !_isSafeRelativePath(row.launchRelativePath) ||
        !items.any((item) => item.relativePath == row.launchRelativePath) ||
        collisionKeys.length != items.length ||
        _hasFileDirectoryCollision(collisionKeys) ||
        !collisionKeys.contains(launchKey)) {
      throw const LocalInstallProjectionException(
        'Stored install paths are inconsistent.',
      );
    }
    return LocalInstall(
      artwork: InstalledArtworkStore(
        _fileStore.baseDir,
      ).read(serverInstanceId.value, row.releaseId),
      serverInstanceId: serverInstanceId.value,
      releaseId: row.releaseId,
      titleId: row.titleId,
      titleName: row.titleName.isEmpty ? row.titleId : row.titleName,
      platformId: row.platformId,
      platformName: row.platformName,
      platformShortName: row.platformShortName,
      coverUrl: coverUrl,
      releaseName: row.releaseName.isEmpty ? row.releaseId : row.releaseName,
      releaseRevision: row.releaseRevision,
      contentRoot: row.contentRoot,
      launchRelativePath: row.launchRelativePath,
      sizeBytes: row.sizeBytes,
      primarySha256: row.primarySha256,
      manifestFingerprint: row.manifestFingerprint,
      state: state,
      installMode: row.installMode,
      items: items,
      installedAt: row.installedAt,
      lastPlayedAt: row.lastPlayedAt,
    );
  }

  LocalInstallsCompanion _toCompanion(LocalInstall i) => LocalInstallsCompanion(
    serverInstanceId: Value(i.serverInstanceId),
    releaseId: Value(i.releaseId),
    titleId: Value(i.titleId),
    titleName: Value(i.titleName),
    platformId: Value(i.platformId!),
    platformName: Value(i.platformName),
    platformShortName: Value(i.platformShortName),
    coverUrl: Value(i.coverUrl?.toString()),
    releaseName: Value(i.releaseName),
    releaseRevision: Value(i.releaseRevision),
    contentRoot: Value(i.contentRoot),
    launchRelativePath: Value(i.launchRelativePath),
    sizeBytes: Value(i.sizeBytes),
    primarySha256: Value(i.primarySha256),
    manifestFingerprint: Value(i.manifestFingerprint),
    state: Value(i.state.name),
    installMode: Value(i.installMode),
    manifestSnapshot: Value(_encodeItems(i.items)),
    installedAt: Value(i.installedAt),
    lastPlayedAt: Value(i.lastPlayedAt),
  );

  static List<LocalInstall> _sortInstalled(List<LocalInstall> installs) =>
      installs..sort((a, b) {
        final installed = b.installedAt.compareTo(a.installedAt);
        return installed != 0 ? installed : a.releaseId.compareTo(b.releaseId);
      });

  static String _encodeItems(List<InstalledItem> items) => jsonEncode(
    items
        .map(
          (it) => <String, Object?>{
            'relativePath': it.relativePath,
            'sizeBytes': it.sizeBytes,
            'sha256': it.sha256,
          },
        )
        .toList(),
  );

  static List<InstalledItem> _decodeItems(String json) {
    try {
      final decoded = jsonDecode(json);
      if (decoded is! List<Object?>) {
        throw const FormatException('Manifest snapshot is not a list.');
      }
      final items = <InstalledItem>[];
      final paths = <String>{};
      for (final item in decoded) {
        const exactKeys = <String>{'relativePath', 'sizeBytes', 'sha256'};
        if (item is! Map<String, Object?> ||
            item.keys.toSet().difference(exactKeys).isNotEmpty ||
            exactKeys.difference(item.keys.toSet()).isNotEmpty ||
            item['relativePath'] is! String ||
            item['sizeBytes'] is! int ||
            item['sha256'] is! String) {
          throw const FormatException('Malformed manifest snapshot item.');
        }
        final relativePath = item['relativePath']! as String;
        final sizeBytes = item['sizeBytes']! as int;
        final sha256 = item['sha256']! as String;
        if (!_isSafeRelativePath(relativePath) ||
            sizeBytes < 0 ||
            !RegExp(r'^[0-9a-f]{64}$').hasMatch(sha256) ||
            !paths.add(relativePath)) {
          throw const FormatException('Invalid manifest snapshot item.');
        }
        items.add(
          InstalledItem(
            relativePath: relativePath,
            sizeBytes: sizeBytes,
            sha256: sha256,
          ),
        );
      }
      if (items.isEmpty) {
        throw const FormatException('Manifest snapshot is empty.');
      }
      return List<InstalledItem>.unmodifiable(items);
    } on LocalInstallProjectionException {
      rethrow;
    } on Object {
      throw const LocalInstallProjectionException(
        'Stored install manifest is inconsistent.',
      );
    }
  }

  bool _validateWrite(LocalInstall install) {
    final paths = install.items.map((item) => item.relativePath).toList();
    final collisionKeys = paths.map(_portablePathKey).toSet();
    return _serverInstanceId != null &&
        install.serverInstanceId == _serverInstanceId.value &&
        RomdPublicId.tryParse(install.releaseId) != null &&
        RomdPublicId.tryParse(install.titleId) != null &&
        isSystemKey(install.platformId) &&
        install.platformShortName.isNotEmpty &&
        install.platformShortName.trim() == install.platformShortName &&
        install.platformShortName.length <= 50 &&
        RegExp(
          r'^[a-z0-9]+(?:-[a-z0-9]+)*$',
        ).hasMatch(install.platformShortName) &&
        install.contentRoot ==
            _fileStore
                .contentRoot(
                  platformShortName: install.platformShortName,
                  titleId: install.titleId,
                  releaseId: install.releaseId,
                )
                .path &&
        install.manifestFingerprint.isNotEmpty &&
        install.items.isNotEmpty &&
        paths.contains(install.launchRelativePath) &&
        collisionKeys.length == paths.length &&
        !_hasFileDirectoryCollision(collisionKeys) &&
        install.items.every(
          (item) =>
              _isSafeRelativePath(item.relativePath) &&
              item.sha256 != null &&
              RegExp(r'^[0-9a-f]{64}$').hasMatch(item.sha256!),
        );
  }

  static bool _isSafeRelativePath(String value) {
    if (value.isEmpty || value.startsWith('/') || value.contains(r'\')) {
      return false;
    }
    final segments = value.split('/');
    return segments.every(
      (segment) =>
          segment.isNotEmpty &&
          segment != '.' &&
          segment != '..' &&
          segment.trimRight() == segment &&
          !RegExp(r'[<>:"|?*\x00-\x1f]').hasMatch(segment),
    );
  }

  static String _portablePathKey(String value) => value.toLowerCase();

  static bool _hasFileDirectoryCollision(Set<String> paths) {
    for (final candidate in paths) {
      final segments = candidate.split('/');
      for (var i = 1; i < segments.length; i++) {
        if (paths.contains(segments.take(i).join('/'))) {
          return true;
        }
      }
    }
    return false;
  }
}

final class LocalInstallProjectionException implements Exception {
  const LocalInstallProjectionException(this.message);

  final String message;

  @override
  String toString() => 'LocalInstallProjectionException: $message';
}
