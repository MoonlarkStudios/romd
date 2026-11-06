import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_failure.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';

import 'disc_set_api.dart';
import 'disc_set_store.dart';
import 'domain/disc_set_library.dart';
import 'domain/disc_set_models.dart';

/// Serialized independently of release mutation keys: a group is a distinct
/// content kind, never a synthetic release id.
final class DiscSetMutations {
  final Map<String, Future<void>> _tails = {};
  Future<T> run<T>(
    String server,
    String set,
    Future<T> Function() action,
  ) async {
    final key = '$server\u0000$set';
    final prior = _tails[key] ?? Future<void>.value();
    final done = Completer<void>();
    _tails[key] = done.future;
    await prior;
    try {
      return await action();
    } finally {
      done.complete();
      if (identical(_tails[key], done.future)) {
        unawaited(_tails.remove(key));
      }
    }
  }
}

final class DiscSetService implements DiscSetLibrary {
  DiscSetService({
    required this.database,
    required this.api,
    required this.authority,
    required this.authenticated,
    required this.downloads,
    required this.store,
    required this.mutations,
    required this.activeSession,
    this.verifier = const ContentVerifier(),
    this.checkpoint,
  });
  final AppDatabase database;
  final DiscSetApi api;
  final InstallAuthorityContext? authority;
  final AuthenticatedRequestExecutor? authenticated;
  final DownloadClient downloads;
  final DiscSetFileStore store;
  final DiscSetMutations mutations;
  final ActiveLaunchSession activeSession;
  final ContentVerifier verifier;
  final Future<void> Function(String phase)? checkpoint;
  bool _closed = false;
  static final _random = Random.secure();
  String _nextAuthorityRevision() => List.generate(
    24,
    (_) => _random.nextInt(256).toRadixString(16).padLeft(2, '0'),
  ).join();
  Future<T> request<T>(Future<T> Function(String token) action) async {
    if (_closed || !await current() || authenticated == null)
      throw const DiscSetException(DiscSetFailureKind.unavailable);
    final result = await authenticated!.execute(request: action);
    if (_closed || !await current())
      throw const DiscSetException(DiscSetFailureKind.unavailable);
    return result;
  }

  @override
  Future<bool> current() async {
    try {
      final a = authority;
      if (_closed || a == null) return false;
      final profile = await (database.select(
        database.localProfiles,
      )..where((r) => r.id.equals(a.localProfileId))).getSingleOrNull();
      final connection =
          await (database.select(database.serverConnections)..where(
                (r) => r.instanceId.equals(a.connection.instanceId.value),
              ))
              .getSingleOrNull();
      return profile != null &&
          connection != null &&
          profile.selectedServerInstanceId == a.connection.instanceId.value &&
          profile.serverSelectionGeneration == a.generation &&
          connection.lastKnownOrigin == a.connection.origin.toString();
    } on Object {
      return false;
    }
  }

  @override
  Future<List<DiscSetStatus>> list(String titleId) =>
      request((token) => api.list(token, titleId));
  Future<DiscSetStatus> status(String set) =>
      request((token) => api.status(token, set));
  @override
  Future<DiscSetPlan> select(
    DiscSetStatus status, {
    required bool partial,
  }) async {
    final plan = await request(
      (token) => api.select(token, status.discSetId, {
        'contractVersion': 1,
        'revision': status.revision,
        'observationId': status.observationId,
        'selectedReleaseIds': status.readyReleaseIds,
        'allowPartial': partial,
        'requiredSnapshotId': null,
      }),
    );
    if (plan.serverInstanceId != status.serverInstanceId ||
        plan.titleId != status.titleId ||
        plan.systemKey != status.systemKey)
      throw const DiscSetException(DiscSetFailureKind.malformed);
    return plan;
  }

  @override
  Future<List<LocalDiscSet>> installed({String? titleId}) async {
    final a = authority;
    if (a == null || !await current()) return [];
    final refs =
        await (database.select(database.profileDiscSets)..where(
              (r) =>
                  r.localProfileId.equals(a.localProfileId) &
                  r.serverInstanceId.equals(a.connection.instanceId.value),
            ))
            .get();
    final result = <LocalDiscSet>[];
    for (final ref in refs) {
      final row =
          await (database.select(database.discSetInstalls)..where(
                (r) =>
                    r.serverInstanceId.equals(ref.serverInstanceId) &
                    r.discSetId.equals(ref.discSetId) &
                    r.snapshotId.equals(ref.snapshotId),
              ))
              .getSingleOrNull();
      if (row == null || (titleId != null && row.titleId != titleId)) continue;
      try {
        final plan = DiscSetPlan.parse(
          jsonDecode(row.planJson),
          origin: a.connection.origin,
          persisted: true,
        );
        if (plan.serverInstanceId != ref.serverInstanceId ||
            plan.discSetId != ref.discSetId ||
            plan.snapshotId != ref.snapshotId ||
            row.contentRoot != store.root(plan).path)
          continue;
        result.add(
          LocalDiscSet(
            plan,
            row.titleName,
            row.contentRoot,
            ref.authorizationState == 'authorized',
            ref.lastCheckedAt,
            ref.authorityRevision,
          ),
        );
      } on FormatException {
        continue;
      }
    }
    return await current() ? result : [];
  }

  @override
  Future<DiscSetPlan?> pending(String set) async {
    final a = authority;
    if (a == null || !await current()) return null;
    final row =
        await (database.select(database.discSetInstallJournals)..where(
              (r) =>
                  r.localProfileId.equals(a.localProfileId) &
                  r.serverInstanceId.equals(a.connection.instanceId.value) &
                  r.discSetId.equals(set),
            ))
            .getSingleOrNull();
    if (row == null) return null;
    final plan = DiscSetPlan.parse(
      jsonDecode(row.planJson),
      origin: a.connection.origin,
      persisted: true,
    );
    if (plan.discSetId != set ||
        plan.serverInstanceId != a.connection.instanceId.value)
      return null;
    return plan;
  }

  Future<DiscSetPlan> renew(DiscSetPlan plan) => request((token) async {
    final status = await api.status(token, plan.discSetId);
    if (status.serverInstanceId != plan.serverInstanceId ||
        status.discSetId != plan.discSetId ||
        status.titleId != plan.titleId ||
        status.systemKey != plan.systemKey ||
        status.revision != plan.revision)
      throw const DiscSetException(DiscSetFailureKind.stale);
    return api.select(token, plan.discSetId, {
      ...plan.renewalRequest,
      'observationId': status.observationId,
    });
  });
  Future<bool> intact(DiscSetPlan plan) async {
    final root = store.root(plan);
    await store.assertSafe(root, allowFiles: true);
    for (final item in plan.files) {
      if (!await verifier.verify(
        file: store.file(root, item.relativePath),
        expectedSize: item.sizeBytes,
        expectedSha256Hex: item.sha256,
      ))
        return false;
    }
    return store.hasOnlyExpectedFiles(
      root,
      plan.files.map((file) => file.relativePath),
    );
  }

  @override
  Future<void> install(
    DiscSetPlan selected, {
    required String titleName,
    required InstallOperationLease operation,
    void Function(int, int)? progress,
  }) => mutations.run(selected.serverInstanceId, selected.discSetId, () async {
    final a = authority;
    if (a == null ||
        selected.serverInstanceId != a.connection.instanceId.value ||
        !operation.isCurrent ||
        !await current() ||
        activeSession.hasSession)
      throw const DiscSetException(DiscSetFailureKind.unavailable);
    // Always issue fresh capabilities against the exact desired snapshot.
    var plan = await renew(selected);
    if (!operation.isCurrent || !await current())
      throw const DiscSetException(DiscSetFailureKind.unavailable);
    if (!plan.hasSameSelectedSnapshot(selected))
      throw const DiscSetException(DiscSetFailureKind.stale);
    await database
        .into(database.discSetInstallJournals)
        .insertOnConflictUpdate(
          DiscSetInstallJournalsCompanion.insert(
            localProfileId: a.localProfileId,
            serverInstanceId: plan.serverInstanceId,
            discSetId: plan.discSetId,
            planJson: plan.encode(),
            titleName: titleName,
            updatedAt: DateTime.now(),
          ),
        );
    final root = store.root(plan), staging = store.staging(plan);
    await store.assertSafe(root, allowFiles: true);
    await store.assertSafe(staging, allowFiles: true);
    await store.assertSafe(store.scratch(plan));
    if (!await store.distinct(plan))
      throw const FormatException('Colliding target paths.');
    await staging.create(recursive: true);
    var received = 0;
    final total = plan.files.fold<int>(0, (sum, f) => sum + f.sizeBytes);
    for (var index = 0; index < plan.files.length; index++) {
      if (!operation.isCurrent || !await current())
        throw const DiscSetException(DiscSetFailureKind.unavailable);
      final item = plan.files[index];
      final file = store.file(staging, item.relativePath);
      await store.assertSafe(staging, allowFiles: true);
      if (!await verifier.verify(
        file: file,
        expectedSize: item.sizeBytes,
        expectedSha256Hex: item.sha256,
      )) {
        if (item.relativePath == plan.playlistPath) {
          await file.parent.create(recursive: true);
          await file.writeAsBytes(utf8.encode(plan.playlistText), flush: true);
        } else {
          var retried = false;
          while (true) {
            try {
              final grant = plan.files[index].url;
              if (grant == null) throw const FormatException('Missing grant.');
              await for (final count in downloads.download(
                url: grant,
                destination: file,
              )) {
                if (!operation.isCurrent || !await current())
                  throw const DiscSetException(
                    DiscSetFailureKind.unavailable,
                  );
                if (count > item.sizeBytes)
                  throw const FormatException('Oversize content.');
                progress?.call(received + count, total);
              }
              break;
            } on DownloadGrantExpired {
              if (retried) rethrow;
              retried = true;
              final refreshed = await renew(plan);
              if (!refreshed.hasSameSelectedSnapshot(plan))
                throw const DiscSetException(DiscSetFailureKind.stale);
              plan = refreshed;
            }
          }
        }
      }
      if (!await verifier.verify(
        file: file,
        expectedSize: item.sizeBytes,
        expectedSha256Hex: item.sha256,
      ))
        throw const FormatException('Content hash mismatch.');
      received += item.sizeBytes;
      progress?.call(received, total);
    }
    if (!await store.hasOnlyExpectedFiles(
      staging,
      plan.files.map((file) => file.relativePath),
    ))
      throw const FormatException('Unverified extra group content.');
    await checkpoint?.call('verified');
    if (!operation.isCurrent || !await current())
      throw const DiscSetException(DiscSetFailureKind.unavailable);
    // A last online check closes the download-time policy window. Capability
    // expiry and having bytes on disk never establish offline authority.
    final acquired = (await installed())
        .where((install) => install.plan.hasSameSelectedSnapshot(plan))
        .firstOrNull;
    if (!await request((token) => api.access(token, plan))) {
      if (acquired != null)
        await _publishAuthorization(plan, acquired, allowed: false);
      throw const DiscSetException(DiscSetFailureKind.denied);
    }
    if (!operation.isCurrent || !await current() || activeSession.hasSession)
      throw const DiscSetException(DiscSetFailureKind.unavailable);
    await store.assertSafe(staging, allowFiles: true);
    final backup = Directory('${root.path}.backup');
    await store.assertSafe(backup, allowFiles: true);
    var swapped = false;
    var hadPrevious = false;
    if (await root.exists()) {
      if (await intact(plan)) {
        await staging.delete(recursive: true);
      } else {
        if (await backup.exists()) await backup.delete(recursive: true);
        await root.rename(backup.path);
        hadPrevious = true;
        try {
          await staging.rename(root.path);
          swapped = true;
        } on Object {
          await backup.rename(root.path);
          rethrow;
        }
      }
    } else {
      await staging.rename(root.path);
      swapped = true;
    }
    try {
      await checkpoint?.call('published');
      await database.transaction(() async {
        if (!operation.isCurrent || !await current())
          throw const DiscSetException(DiscSetFailureKind.unavailable);
        await database
            .into(database.discSetInstalls)
            .insertOnConflictUpdate(
              DiscSetInstallsCompanion.insert(
                serverInstanceId: plan.serverInstanceId,
                discSetId: plan.discSetId,
                snapshotId: plan.snapshotId,
                titleId: plan.titleId,
                titleName: titleName,
                planJson: plan.encode(),
                contentRoot: root.path,
                installedAt: DateTime.now(),
              ),
            );
        await database
            .into(database.profileDiscSets)
            .insertOnConflictUpdate(
              ProfileDiscSetsCompanion.insert(
                localProfileId: a.localProfileId,
                serverInstanceId: plan.serverInstanceId,
                discSetId: plan.discSetId,
                snapshotId: plan.snapshotId,
                authorizationState: 'authorized',
                authorityRevision: _nextAuthorityRevision(),
                lastCheckedAt: DateTime.now(),
              ),
            );
        await (database.delete(database.discSetInstallJournals)..where(
              (r) =>
                  r.localProfileId.equals(a.localProfileId) &
                  r.serverInstanceId.equals(plan.serverInstanceId) &
                  r.discSetId.equals(plan.discSetId),
            ))
            .go();
        if (!operation.isCurrent || !await current())
          throw const DiscSetException(DiscSetFailureKind.unavailable);
      });
    } on Object {
      if (swapped && hadPrevious) {
        if (await root.exists()) await root.delete(recursive: true);
        if (await backup.exists()) await backup.rename(root.path);
      }
      rethrow;
    }
    if (await backup.exists()) await backup.delete(recursive: true);
  });

  /// Authorizes only an existing profile-owned exact snapshot. Unknown/stale
  /// responses never write cached policy; fallback re-reads identical state.
  Future<bool> authorize(LocalDiscSet install) => mutations.run(
    install.plan.serverInstanceId,
    install.plan.discSetId,
    () => _authorizeLocked(install),
  );

  /// Serializes all allow/revoke publications with install publication and the
  /// launch reservation or process-start boundary. The callback may await
  /// process creation, and must return before the process lifetime is awaited.
  /// A later explicit deny cannot be replaced by an install's earlier allow.
  Future<T?> withAuthorizedSnapshot<T>(
    LocalDiscSet install,
    FutureOr<T> Function() action,
  ) => mutations.run(
    install.plan.serverInstanceId,
    install.plan.discSetId,
    () async {
      if (!await _authorizeLocked(install)) return null;
      return await action();
    },
  );

  Future<bool> _authorizeLocked(LocalDiscSet install) async {
    final plan = install.plan;
    final before = (await installed())
        .where(
          (i) =>
              i.plan.discSetId == plan.discSetId &&
              i.plan.snapshotId == plan.snapshotId &&
              i.plan.encode() == plan.encode(),
        )
        .firstOrNull;
    if (before == null) return false;
    bool allowed;
    try {
      allowed = await request((token) => api.access(token, plan));
    } on Object {
      final after = (await installed())
          .where(
            (i) =>
                i.plan.discSetId == plan.discSetId &&
                i.plan.snapshotId == plan.snapshotId &&
                i.plan.encode() == plan.encode() &&
                i.authorityRevision == before.authorityRevision &&
                i.authorized == before.authorized,
          )
          .firstOrNull;
      return after?.authorized ?? false;
    }
    return _publishAuthorization(plan, before, allowed: allowed);
  }

  Future<bool> _publishAuthorization(
    DiscSetPlan plan,
    LocalDiscSet before, {
    required bool allowed,
  }) => database.transaction(() async {
    final a = authority;
    if (a == null || !await current()) return false;
    final updated =
        await (database.update(database.profileDiscSets)..where(
              (r) =>
                  r.localProfileId.equals(a.localProfileId) &
                  r.serverInstanceId.equals(plan.serverInstanceId) &
                  r.discSetId.equals(plan.discSetId) &
                  r.snapshotId.equals(plan.snapshotId) &
                  r.authorizationState.equals(
                    before.authorized ? 'authorized' : 'revoked',
                  ) &
                  r.authorityRevision.equals(before.authorityRevision),
            ))
            .write(
              ProfileDiscSetsCompanion(
                authorizationState: Value(allowed ? 'authorized' : 'revoked'),
                authorityRevision: Value(_nextAuthorityRevision()),
                lastCheckedAt: Value(DateTime.now()),
              ),
            );
    return allowed && updated == 1;
  });

  @override
  Future<void> uninstall(LocalDiscSet install) => mutations.run(
    install.plan.serverInstanceId,
    install.plan.discSetId,
    () async {
      final a = authority;
      if (a == null || !await current() || activeSession.hasSession) return;
      await database.transaction(() async {
        await (database.delete(database.profileDiscSets)..where(
              (r) =>
                  r.localProfileId.equals(a.localProfileId) &
                  r.serverInstanceId.equals(install.plan.serverInstanceId) &
                  r.discSetId.equals(install.plan.discSetId) &
                  r.snapshotId.equals(install.plan.snapshotId),
            ))
            .go();
      });
      final refs =
          await (database.select(database.profileDiscSets)..where(
                (r) =>
                    r.serverInstanceId.equals(install.plan.serverInstanceId) &
                    r.discSetId.equals(install.plan.discSetId) &
                    r.snapshotId.equals(install.plan.snapshotId),
              ))
              .get();
      if (refs.isNotEmpty) return;
      final root = store.root(install.plan);
      await store.assertSafe(root, allowFiles: true);
      if (await root.exists()) await root.delete(recursive: true);
      await (database.delete(database.discSetInstalls)..where(
            (r) =>
                r.serverInstanceId.equals(install.plan.serverInstanceId) &
                r.discSetId.equals(install.plan.discSetId) &
                r.snapshotId.equals(install.plan.snapshotId),
          ))
          .go();
    },
  );
  @override
  void close() {
    _closed = true;
    api.close();
  }
}
