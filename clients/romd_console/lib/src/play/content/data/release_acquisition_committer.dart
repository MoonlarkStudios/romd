import 'dart:io';

import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/server_bound_release_manifest.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/local_install_repository.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

import 'acquisition_checkpoint.dart';
import 'content_file_store.dart';
import 'installed_artwork_store.dart';

/// Owns publication of verified content and its atomic install/profile grant.
/// Acquisition planning and verification finish before this effect boundary;
/// rollback restores the previous files and projection on a failed commit.
final class ReleaseAcquisitionCommitter {
  ReleaseAcquisitionCommitter({
    required AppDatabase database,
    required LocalInstallRepository installs,
    required ContentFileStore fileStore,
    required DateTime Function() now,
    Future<void> Function(AcquisitionCheckpoint checkpoint)? checkpoint,
  }) : _database = database,
       _installs = installs,
       _fileStore = fileStore,
       _now = now,
       _checkpoint = checkpoint;

  final AppDatabase _database;
  final LocalInstallRepository _installs;
  final ContentFileStore _fileStore;
  final DateTime Function() _now;
  final Future<void> Function(AcquisitionCheckpoint checkpoint)? _checkpoint;

  Future<ReleaseAcquisitionOutcome> finalize({
    required Directory staging,
    required ResolvedPlayTarget resolved,
    required ServerBoundReleaseManifest manifest,
    required LocalInstall install,
    required LocalInstall? previous,
    required InstallAuthorityContext authority,
    required InstallOperationLease operation,
  }) async {
    _MaterializationSwap? swap;
    var deactivated = false;
    try {
      await _checkpoint?.call(AcquisitionCheckpoint.beforeDeactivation);
      if (!operation.isCurrent) {
        return ReleaseAcquisitionOutcome.authorityChanged;
      }
      final deleted =
          await (_database.delete(_database.localInstalls)..where(
                (row) =>
                    row.serverInstanceId.equals(
                      authority.connection.instanceId.value,
                    ) &
                    row.releaseId.equals(manifest.releaseId.value),
              ))
              .go();
      deactivated = deleted > 0;
      await _checkpoint?.call(AcquisitionCheckpoint.afterDeactivation);

      swap = await _materialize(
        staging: staging,
        finalRoot: Directory(resolved.contentRoot),
        backup: _fileStore.backupRoot(
          platformShortName: manifest.systemKey,
          titleId: manifest.titleId.value,
          releaseId: manifest.releaseId.value,
        ),
      );
      await _checkpoint?.call(AcquisitionCheckpoint.afterPromotion);
      await _checkpoint?.call(AcquisitionCheckpoint.beforeFinalCommit);
      if (!await commit(install, authority: authority, operation: operation)) {
        await swap.rollback();
        await _restorePrevious(previous, deactivated: deactivated);
        return ReleaseAcquisitionOutcome.authorityChanged;
      }
      await swap.commit();
      return ReleaseAcquisitionOutcome.completed;
    } on Object {
      await swap?.rollback();
      await _restorePrevious(previous, deactivated: deactivated);
      return ReleaseAcquisitionOutcome.ioError;
    }
  }

  Future<void> _restorePrevious(
    LocalInstall? previous, {
    required bool deactivated,
  }) async {
    if (!deactivated || previous == null) return;
    try {
      await _installs.upsert(previous);
    } on Object {
      // No active row is safer than a stale row pointing at replaced files.
    }
  }

  Future<bool> commit(
    LocalInstall install, {
    required InstallAuthorityContext authority,
    required InstallOperationLease operation,
  }) async {
    try {
      final committed = await _database.transaction(() async {
        if (!operation.isCurrent) {
          throw const _InstallOperationInvalidated();
        }
        final profiles = await (_database.select(
          _database.localProfiles,
        )..where((row) => row.id.equals(authority.localProfileId))).get();
        if (profiles.length != 1) {
          return false;
        }
        final profile = profiles.single;
        if (profile.selectedServerInstanceId !=
                authority.connection.instanceId.value ||
            profile.serverSelectionGeneration != authority.generation) {
          return false;
        }
        final connections =
            await (_database.select(_database.serverConnections)..where(
                  (row) => row.instanceId.equals(
                    authority.connection.instanceId.value,
                  ),
                ))
                .get();
        if (connections.length != 1 ||
            connections.single.lastKnownOrigin !=
                authority.connection.origin.toString()) {
          return false;
        }

        if (!operation.isCurrent) {
          throw const _InstallOperationInvalidated();
        }

        final existingGames =
            await (_database.select(_database.profileLocalGames)..where(
                  (row) =>
                      row.localProfileId.equals(authority.localProfileId) &
                      row.serverInstanceId.equals(
                        authority.connection.instanceId.value,
                      ) &
                      row.releaseId.equals(install.releaseId),
                ))
                .get();
        if (existingGames.length > 1 ||
            (existingGames.isNotEmpty &&
                existingGames.single.titleId != install.titleId)) {
          return false;
        }

        await _installs.upsert(install);
        if (!operation.isCurrent) {
          throw const _InstallOperationInvalidated();
        }
        final checkedAt = _now();
        final acquiredAt = existingGames.isEmpty
            ? checkedAt
            : existingGames.single.acquiredAt;
        await _database
            .into(_database.profileLocalGames)
            .insertOnConflictUpdate(
              ProfileLocalGamesCompanion.insert(
                localProfileId: authority.localProfileId,
                serverInstanceId: authority.connection.instanceId.value,
                releaseId: install.releaseId,
                titleId: install.titleId,
                authorizationState: ProfileGameAuthorization.authorized.name,
                acquiredAt: acquiredAt,
                lastCheckedAt: checkedAt,
              ),
            );
        if (!operation.isCurrent) {
          throw const _InstallOperationInvalidated();
        }
        return true;
      });
      if (!committed) return false;
      // Only committed installs retain artwork. A cache failure after this point
      // must never trigger ROM rollback or undo the successful acquisition.
      try {
        if (install.artwork.isNotEmpty) {
          await InstalledArtworkStore(_fileStore.baseDir).retain(
            server: install.serverInstanceId,
            release: install.releaseId,
            origin: authority.connection.origin,
            artwork: install.artwork,
          );
          // Re-emit the unchanged projection so installed rails read the new
          // manifest. The caller still owns the release mutation lease.
          await _installs.upsert(install);
        }
      } on Object {
        // Verified installed content remains usable without artwork.
      }
      return true;
    } on Object {
      return false;
    }
  }

  /// Atomic replace with rollback: back up any existing final root, move staging
  /// into place, then drop the backup. On failure, restore the backup.
  Future<_MaterializationSwap> _materialize({
    required Directory staging,
    required Directory finalRoot,
    required Directory backup,
  }) async {
    await _safeDelete(backup);
    final finalExisted = await finalRoot.exists();
    if (finalExisted) {
      await finalRoot.rename(backup.path);
    }
    try {
      await staging.rename(finalRoot.path);
    } on Object {
      if (finalExisted) {
        await _safeDelete(finalRoot);
        await backup.rename(finalRoot.path);
      }
      rethrow;
    }
    return _MaterializationSwap(
      finalRoot: finalRoot,
      backup: backup,
      hadPrevious: finalExisted,
      safeDelete: _safeDelete,
    );
  }

  Future<void> _safeDelete(FileSystemEntity entity) async {
    try {
      if (await entity.exists()) await entity.delete(recursive: true);
    } on Object {
      // Content publication/rollback remains authoritative if cleanup fails.
    }
  }
}

enum ReleaseAcquisitionOutcome { completed, authorityChanged, ioError }

final class _InstallOperationInvalidated implements Exception {
  const _InstallOperationInvalidated();
}

final class _MaterializationSwap {
  const _MaterializationSwap({
    required this.finalRoot,
    required this.backup,
    required this.hadPrevious,
    required this.safeDelete,
  });

  final Directory finalRoot;
  final Directory backup;
  final bool hadPrevious;
  final Future<void> Function(FileSystemEntity) safeDelete;

  Future<void> commit() => safeDelete(backup);

  Future<void> rollback() async {
    await safeDelete(finalRoot);
    if (hadPrevious && await backup.exists()) {
      await backup.rename(finalRoot.path);
    }
  }
}
