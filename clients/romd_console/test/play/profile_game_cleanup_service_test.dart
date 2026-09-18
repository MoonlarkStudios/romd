import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' hide isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/content_file_store.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/data/install_mutation_serializer.dart';
import 'package:romd_console/src/play/content/data/profile_game_cleanup_service.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';

const _instanceA = '11111111-1111-4111-8111-111111111111';
const _instanceB = '22222222-2222-4222-8222-222222222222';
const _sha = '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824';

final class _TrackingRefreshTokenStore implements RefreshTokenStore {
  final Map<String, String> instanceTokens = <String, String>{};
  final Map<String, String> legacyTokens = <String, String>{};
  final List<String> deletes = <String>[];
  String? failInstancePartitionOnce;

  String instancePartition(
    String profileId,
    RomdServerInstanceId serverInstanceId,
  ) => '$profileId:${serverInstanceId.value}';

  String legacyPartition(String profileId, Uri origin) => '$profileId:$origin';

  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async => instanceTokens[instancePartition(profileId, serverInstanceId)];

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) async {
    instanceTokens[instancePartition(profileId, serverInstanceId)] =
        refreshToken;
  }

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    final partition = instancePartition(profileId, serverInstanceId);
    deletes.add('instance:$partition');
    if (failInstancePartitionOnce == partition) {
      failInstancePartitionOnce = null;
      throw StateError('injected credential delete failure');
    }
    instanceTokens.remove(partition);
  }

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) async {
    final partition = legacyPartition(profileId, serverOrigin);
    deletes.add('legacy:$partition');
    legacyTokens.remove(partition);
  }
}

final class _StalesAfterDeleteLease implements InstallOperationLease {
  int _checks = 0;

  @override
  bool get isCurrent => ++_checks <= 2;
}

void main() {
  late Directory base;
  late AppDatabase database;
  late InstallMutationSerializer serializer;
  late ProfileGameCleanupService cleanup;
  late _TrackingRefreshTokenStore tokenStore;

  final now = DateTime.utc(2026, 7, 18, 12);
  final serverA = RomdServerInstanceId.tryParse(_instanceA)!;
  final serverB = RomdServerInstanceId.tryParse(_instanceB)!;
  final release = RomdPublicId.encode(101);
  final otherRelease = RomdPublicId.encode(102);
  final title = RomdPublicId.encode(201);
  final otherTitle = RomdPublicId.encode(202);
  const platform = 'snes';

  ContentFileStore store(RomdServerInstanceId instance) =>
      ContentFileStore(baseDir: base, serverInstanceId: instance.value);

  ProfileServerConnection connection(RomdServerInstanceId instance) =>
      ProfileServerConnection(
        instanceId: instance,
        origin: Uri.parse(
          instance == serverA
              ? 'https://library.example'
              : 'https://other.example',
        ),
        firstSeenAt: now,
        lastSeenAt: now,
      );

  InstallAuthorityContext authority({
    String profileId = 'profile-1',
    RomdServerInstanceId? instance,
    int generation = 1,
  }) => InstallAuthorityContext(
    localProfileId: profileId,
    connection: connection(instance ?? serverA),
    generation: generation,
  );

  Future<void> seedProfile(
    String id, {
    RomdServerInstanceId? selected,
    int generation = 1,
  }) => database
      .into(database.localProfiles)
      .insert(
        LocalProfilesCompanion.insert(
          id: id,
          displayName: id,
          avatarKey: 'default',
          accentColor: 1,
          entryMode: 'open',
          createdAt: now,
          updatedAt: now,
          selectedServerInstanceId: Value((selected ?? serverA).value),
          serverSelectionGeneration: Value(generation),
        ),
      );

  Future<LocalInstall> seedInstall({
    RomdServerInstanceId? instance,
    String? releaseId,
    String? titleId,
    bool hiddenSiblings = false,
  }) async {
    final selectedInstance = instance ?? serverA;
    final selectedRelease = releaseId ?? release;
    final selectedTitle = titleId ?? title;
    final fileStore = store(selectedInstance);
    final root = fileStore.contentRoot(
      platformShortName: 'snes',
      titleId: selectedTitle,
      releaseId: selectedRelease,
    );
    await root.create(recursive: true);
    await File('${root.path}/game.sfc').writeAsString('hello');
    if (hiddenSiblings) {
      for (final sibling in <Directory>[
        fileStore.stagingRoot(
          platformShortName: 'snes',
          titleId: selectedTitle,
          releaseId: selectedRelease,
        ),
        fileStore.backupRoot(
          platformShortName: 'snes',
          titleId: selectedTitle,
          releaseId: selectedRelease,
        ),
        fileStore.preflightRoot(
          platformShortName: 'snes',
          titleId: selectedTitle,
          releaseId: selectedRelease,
        ),
      ]) {
        await sibling.create(recursive: true);
        await File('${sibling.path}/partial').writeAsString('partial');
      }
    }
    const items = <InstalledItem>[
      InstalledItem(relativePath: 'game.sfc', sizeBytes: 5, sha256: _sha),
    ];
    final install = LocalInstall(
      serverInstanceId: selectedInstance.value,
      releaseId: selectedRelease,
      titleId: selectedTitle,
      titleName: 'Game',
      platformId: platform,
      platformName: 'SNES',
      platformShortName: 'snes',
      coverUrl: null,
      releaseName: 'USA',
      releaseRevision: null,
      contentRoot: root.path,
      launchRelativePath: 'game.sfc',
      sizeBytes: 5,
      primarySha256: _sha,
      manifestFingerprint: const ContentVerifier().manifestFingerprint(
        items.map(
          (item) => (
            relativePath: item.relativePath,
            sizeBytes: item.sizeBytes,
            sha256: item.sha256,
          ),
        ),
      ),
      state: InstallState.installed,
      installMode: 'permanent',
      items: items,
      installedAt: now,
      lastPlayedAt: null,
    );
    await DriftLocalInstallRepository(
      database: database,
      serverInstanceId: selectedInstance,
      fileStore: fileStore,
    ).upsert(install);
    return install;
  }

  Future<void> seedReference({
    String profileId = 'profile-1',
    RomdServerInstanceId? instance,
    String? releaseId,
    String? titleId,
    ProfileGameAuthorization state = ProfileGameAuthorization.authorized,
  }) => database
      .into(database.profileLocalGames)
      .insert(
        ProfileLocalGamesCompanion.insert(
          localProfileId: profileId,
          serverInstanceId: (instance ?? serverA).value,
          releaseId: releaseId ?? release,
          titleId: titleId ?? title,
          authorizationState: state.name,
          acquiredAt: now,
          lastCheckedAt: now,
        ),
      );

  Future<void> seedLegacyDuplicate(LocalInstall active) async {
    final fileStore = store(serverA);
    final root = fileStore.legacyContentRoot(
      platformShortName: active.platformShortName,
      titleId: active.titleId,
      releaseId: active.releaseId,
    );
    await root.create(recursive: true);
    await File('${root.path}/game.sfc').writeAsString('hello');
    await database
        .into(database.legacyLocalInstalls)
        .insert(
          LegacyLocalInstallsCompanion.insert(
            releaseId: active.releaseId,
            titleId: active.titleId,
            titleName: const Value('Game'),
            platformId: Value(active.platformId),
            platformName: const Value('SNES'),
            platformShortName: active.platformShortName,
            contentRoot: root.path,
            launchRelativePath: active.launchRelativePath,
            sizeBytes: active.sizeBytes,
            primarySha256: Value(active.primarySha256),
            manifestFingerprint: active.manifestFingerprint,
            state: InstallState.installed.name,
            installMode: const Value('permanent'),
            manifestSnapshot: jsonEncode(
              active.items
                  .map(
                    (item) => <String, Object?>{
                      'relativePath': item.relativePath,
                      'sizeBytes': item.sizeBytes,
                      'sha256': item.sha256,
                    },
                  )
                  .toList(),
            ),
            installedAt: now,
          ),
        );
  }

  Future<List<LocalInstallRow>> activeRows() =>
      database.select(database.localInstalls).get();

  setUp(() async {
    base = await Directory.systemTemp.createTemp('romd-cleanup-');
    database = AppDatabase(NativeDatabase.memory());
    for (final instance in <RomdServerInstanceId>[serverA, serverB]) {
      final serverConnection = connection(instance);
      await database
          .into(database.serverConnections)
          .insert(
            ServerConnectionsCompanion.insert(
              instanceId: instance.value,
              lastKnownOrigin: serverConnection.origin.toString(),
              firstSeenAt: now,
              lastSeenAt: now,
            ),
          );
    }
    await seedProfile('profile-1');
    await seedProfile('profile-2');
    serializer = InstallMutationSerializer();
    tokenStore = _TrackingRefreshTokenStore();
    cleanup = ProfileGameCleanupService(
      database: database,
      baseDir: base,
      serializer: serializer,
      refreshTokenStore: SerializedRefreshTokenStore(tokenStore),
    );
  });

  tearDown(() async {
    await database.close();
    if (await base.exists()) await base.delete(recursive: true);
  });

  test(
    'authorized and revoked references both protect shared content',
    () async {
      final install = await seedInstall();
      await seedReference();
      await seedReference(
        profileId: 'profile-2',
        state: ProfileGameAuthorization.revoked,
      );

      await cleanup.removeProfileGame(
        authority: authority(),
        releaseId: release,
      );

      final refs = await database.select(database.profileLocalGames).get();
      expect(refs, hasLength(1));
      expect(refs.single.localProfileId, 'profile-2');
      expect(refs.single.authorizationState, 'revoked');
      expect(await activeRows(), hasLength(1));
      expect(await Directory(install.contentRoot).exists(), isTrue);
    },
  );

  test('last reference removes exact content but preserves durable data', () async {
    final install = await seedInstall(hiddenSiblings: true);
    await seedReference();
    final fileStore = store(serverA);
    final save = File(
      '${fileStore.saveRoot(localProfileId: 'profile-1', platformShortName: 'snes', titleId: title).path}/save.srm',
    );
    final state = File(
      '${fileStore.stateRoot(localProfileId: 'profile-1', platformShortName: 'snes', titleId: title).path}/state.bin',
    );
    final config = File(
      '${fileStore.configRoot(platformShortName: 'snes', titleId: title).path}/config.ini',
    );
    for (final durable in <File>[save, state, config]) {
      await durable.parent.create(recursive: true);
      await durable.writeAsString('keep');
    }

    await cleanup.removeProfileGame(authority: authority(), releaseId: release);

    expect(await activeRows(), isEmpty);
    expect(await Directory(install.contentRoot).exists(), isFalse);
    expect(
      await File('${install.contentRoot}.staging/partial').exists(),
      isFalse,
    );
    expect(
      await File('${install.contentRoot}.backup/partial').exists(),
      isFalse,
    );
    expect(
      await File('${install.contentRoot}.preflight/partial').exists(),
      isFalse,
    );
    for (final durable in <File>[save, state, config]) {
      expect(await durable.readAsString(), 'keep');
    }
  });

  test(
    'operation invalidation after delete rolls back reference removal',
    () async {
      final install = await seedInstall();
      await seedReference();

      await cleanup.removeProfileGame(
        authority: authority(),
        releaseId: release,
        operation: _StalesAfterDeleteLease(),
      );

      expect(
        await database.select(database.profileLocalGames).get(),
        hasLength(1),
      );
      expect(await activeRows(), hasLength(1));
      expect(await Directory(install.contentRoot).exists(), isTrue);
    },
  );

  test('same release on another server does not protect this server', () async {
    final installA = await seedInstall();
    final installB = await seedInstall(instance: serverB);
    await seedReference();
    await seedReference(profileId: 'profile-2', instance: serverB);

    await cleanup.removeProfileGame(authority: authority(), releaseId: release);

    final rows = await activeRows();
    expect(rows.single.serverInstanceId, serverB.value);
    expect(await Directory(installA.contentRoot).exists(), isFalse);
    expect(await Directory(installB.contentRoot).exists(), isTrue);
  });

  test('neutral or stale authority cannot remove a reference', () async {
    await seedInstall();
    await seedReference();

    await cleanup.removeProfileGame(authority: null, releaseId: release);
    await cleanup.removeProfileGame(
      authority: authority(generation: 2),
      releaseId: release,
    );

    expect(
      await database.select(database.profileLocalGames).get(),
      hasLength(1),
    );
    expect(await activeRows(), hasLength(1));
  });

  test('failed deletion retains an orphan row for a later retry', () async {
    final install = await seedInstall();
    await seedReference();
    var failFinalOnce = true;
    final failing = ProfileGameCleanupService(
      database: database,
      baseDir: base,
      serializer: serializer,
      refreshTokenStore: SerializedRefreshTokenStore(tokenStore),
      deleteEntity: (entity) async {
        if (entity.path == install.contentRoot && failFinalOnce) {
          failFinalOnce = false;
          return false;
        }
        if (await entity.exists()) await entity.delete(recursive: true);
        return true;
      },
    );

    await failing.removeProfileGame(authority: authority(), releaseId: release);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
    expect(await activeRows(), hasLength(1));
    expect(await Directory(install.contentRoot).exists(), isTrue);

    await cleanup.cleanupOrphans();
    expect(await activeRows(), isEmpty);
    expect(await Directory(install.contentRoot).exists(), isFalse);
  });

  test(
    'profile deletion is credential-complete, fail-closed, and isolated',
    () async {
      final orphaned = await seedInstall();
      final shared = await seedInstall(
        releaseId: otherRelease,
        titleId: otherTitle,
      );
      await seedReference();
      await seedReference(
        releaseId: otherRelease,
        titleId: otherTitle,
        state: ProfileGameAuthorization.revoked,
      );
      await seedReference(
        profileId: 'profile-2',
        releaseId: otherRelease,
        titleId: otherTitle,
      );
      const pendingOrigin = 'https://pending.example';
      await database
          .into(database.pendingServerLocators)
          .insert(
            PendingServerLocatorsCompanion.insert(
              localProfileId: 'profile-1',
              normalizedOrigin: pendingOrigin,
              createdAt: now,
            ),
          );
      await database
          .into(database.romdAccountLinks)
          .insert(
            RomdAccountLinksCompanion.insert(
              localProfileId: 'profile-1',
              romdUserId: RomdPublicId.encode(901),
              username: 'parent',
              email: 'parent@example.com',
              linkedAt: now,
              serverInstanceId: Value(serverA.value),
            ),
          );
      for (final profileId in <String>['profile-1', 'profile-2']) {
        await database
            .into(database.controllerMappingProfiles)
            .insert(
              ControllerMappingProfilesCompanion.insert(
                localProfileId: profileId,
                sdlGuid: 'guid-$profileId',
                displayName: 'Pad $profileId',
                createdAt: now,
                updatedAt: now,
              ),
            );
        await database
            .into(database.controllerProfileBindingRules)
            .insert(
              ControllerProfileBindingRulesCompanion.insert(
                localProfileId: profileId,
                sdlGuid: 'guid-$profileId',
                scope: 'global',
                scopeValue: '',
                action: 'menu',
                button: const Value('start'),
                updatedAt: now,
              ),
            );
      }
      await database
          .into(database.controllerBindingRules)
          .insert(
            ControllerBindingRulesCompanion.insert(
              scope: 'global',
              scopeValue: '',
              action: 'menu',
              button: const Value('start'),
              updatedAt: now,
            ),
          );
      await database
          .into(database.runtimeOverrideRules)
          .insert(
            RuntimeOverrideRulesCompanion.insert(
              scope: 'platform',
              scopeValue: 'snes',
              profileId: 'retroarch-snes',
              updatedAt: now,
            ),
          );

      for (final entry in <(String, RomdServerInstanceId, String)>[
        ('profile-1', serverA, 'token-a'),
        ('profile-1', serverB, 'token-b'),
        ('profile-2', serverA, 'other-token'),
      ]) {
        tokenStore.instanceTokens[tokenStore.instancePartition(
              entry.$1,
              entry.$2,
            )] =
            entry.$3;
      }
      tokenStore.legacyTokens[tokenStore.legacyPartition(
            'profile-1',
            Uri.parse(pendingOrigin),
          )] =
          'legacy-token';
      tokenStore.failInstancePartitionOnce = tokenStore.instancePartition(
        'profile-1',
        serverB,
      );

      expect(await cleanup.deleteProfile('profile-1'), isFalse);
      expect(
        await (database.select(
          database.localProfiles,
        )..where((row) => row.id.equals('profile-1'))).get(),
        hasLength(1),
      );
      expect(
        await (database.select(
          database.romdAccountLinks,
        )..where((row) => row.localProfileId.equals('profile-1'))).get(),
        hasLength(1),
      );
      expect(
        await (database.select(
          database.profileLocalGames,
        )..where((row) => row.localProfileId.equals('profile-1'))).get(),
        hasLength(2),
      );

      expect(await cleanup.deleteProfile('profile-1'), isTrue);

      expect(
        await (database.select(
          database.localProfiles,
        )..where((row) => row.id.equals('profile-1'))).get(),
        isEmpty,
      );
      expect(await Directory(orphaned.contentRoot).exists(), isFalse);
      expect(await Directory(shared.contentRoot).exists(), isTrue);
      expect((await activeRows()).single.releaseId, otherRelease);
      expect(
        await (database.select(
          database.romdAccountLinks,
        )..where((row) => row.localProfileId.equals('profile-1'))).get(),
        isEmpty,
      );
      expect(
        await (database.select(
          database.pendingServerLocators,
        )..where((row) => row.localProfileId.equals('profile-1'))).get(),
        isEmpty,
      );
      expect(
        await (database.select(
          database.controllerMappingProfiles,
        )..where((row) => row.localProfileId.equals('profile-1'))).get(),
        isEmpty,
      );
      expect(
        await (database.select(
          database.controllerProfileBindingRules,
        )..where((row) => row.localProfileId.equals('profile-1'))).get(),
        isEmpty,
      );
      expect(
        await (database.select(
          database.profileLocalGames,
        )..where((row) => row.localProfileId.equals('profile-1'))).get(),
        isEmpty,
      );
      expect(
        await (database.select(
          database.controllerMappingProfiles,
        )..where((row) => row.localProfileId.equals('profile-2'))).get(),
        hasLength(1),
      );
      expect(
        await (database.select(
          database.controllerProfileBindingRules,
        )..where((row) => row.localProfileId.equals('profile-2'))).get(),
        hasLength(1),
      );
      expect(
        await database.select(database.controllerBindingRules).get(),
        hasLength(1),
      );
      expect(
        await database.select(database.runtimeOverrideRules).get(),
        hasLength(1),
      );
      expect(
        tokenStore.instanceTokens.keys.where(
          (partition) => partition.startsWith('profile-1:'),
        ),
        isEmpty,
      );
      expect(
        tokenStore.legacyTokens.keys.where(
          (partition) => partition.startsWith('profile-1:'),
        ),
        isEmpty,
      );
      expect(
        tokenStore.instanceTokens[tokenStore.instancePartition(
          'profile-2',
          serverA,
        )],
        'other-token',
      );
      expect(
        tokenStore.deletes,
        containsAll(<String>[
          'instance:${tokenStore.instancePartition('profile-1', serverA)}',
          'instance:${tokenStore.instancePartition('profile-1', serverB)}',
          'legacy:${tokenStore.legacyPartition('profile-1', Uri.parse(pendingOrigin))}',
        ]),
      );
    },
  );

  test('startup cleanup removes orphan rows and canonical scratch', () async {
    final install = await seedInstall();
    final fileStore = store(serverA);
    final scratch = fileStore.stagingRoot(
      platformShortName: 'snes',
      titleId: otherTitle,
      releaseId: otherRelease,
    );
    await scratch.create(recursive: true);
    await File('${scratch.path}/partial').writeAsString('partial');

    await cleanup.cleanupOrphans();

    expect(await activeRows(), isEmpty);
    expect(await Directory(install.contentRoot).exists(), isFalse);
    expect(await scratch.exists(), isFalse);
  });

  test('noncanonical active path is retained and never deleted', () async {
    await seedInstall();
    final outside = Directory('${base.path}/outside')
      ..createSync(recursive: true);
    final marker = File('${outside.path}/keep')..writeAsStringSync('keep');
    await database.customStatement(
      'UPDATE local_installs SET content_root = ? WHERE server_instance_id = ? AND release_id = ?',
      <Object?>[outside.path, serverA.value, release],
    );

    await cleanup.cleanupOrphans();

    expect(await activeRows(), hasLength(1));
    expect(await marker.readAsString(), 'keep');
  });

  test('instance-root and artifact symlinks are rejected', () async {
    for (final atInstanceRoot in <bool>[true, false]) {
      await database.delete(database.localInstalls).go();
      final instanceRoot = store(serverA).serverContentRoot;
      if (await instanceRoot.exists())
        await instanceRoot.delete(recursive: true);
      final outside = Directory(
        '${base.path}/outside-${atInstanceRoot ? 'instance' : 'artifact'}',
      )..createSync(recursive: true);
      final marker = File('${outside.path}/keep')..writeAsStringSync('keep');
      final canonical = store(serverA).contentRoot(
        platformShortName: 'snes',
        titleId: title,
        releaseId: release,
      );
      if (atInstanceRoot) {
        await instanceRoot.parent.create(recursive: true);
        await Link(instanceRoot.path).create(outside.path);
      } else {
        await canonical.parent.create(recursive: true);
        await Link(canonical.path).create(outside.path);
      }
      await database
          .into(database.localInstalls)
          .insert(
            LocalInstallsCompanion.insert(
              serverInstanceId: serverA.value,
              releaseId: release,
              titleId: title,
              titleName: const Value('Game'),
              platformId: platform,
              platformName: const Value('SNES'),
              platformShortName: 'snes',
              contentRoot: canonical.path,
              launchRelativePath: 'game.sfc',
              sizeBytes: 5,
              primarySha256: const Value(_sha),
              manifestFingerprint: 'proof',
              state: InstallState.installed.name,
              installMode: const Value('permanent'),
              manifestSnapshot: jsonEncode(<Object?>[
                <String, Object?>{
                  'relativePath': 'game.sfc',
                  'sizeBytes': 5,
                  'sha256': _sha,
                },
              ]),
              installedAt: now,
            ),
          );

      await cleanup.cleanupOrphans();

      expect(await activeRows(), hasLength(1));
      expect(await marker.readAsString(), 'keep');
      await database.delete(database.localInstalls).go();
      await Link(atInstanceRoot ? instanceRoot.path : canonical.path).delete();
    }
  });

  test(
    'legacy duplicate cleanup requires exact snapshot and full hash proof',
    () async {
      final active = await seedInstall();
      await seedReference();
      await seedLegacyDuplicate(active);
      final legacyRoot = store(serverA).legacyContentRoot(
        platformShortName: 'snes',
        titleId: title,
        releaseId: release,
      );

      await cleanup.cleanupOrphans();
      expect(
        await database.select(database.legacyLocalInstalls).get(),
        isEmpty,
      );
      expect(await legacyRoot.exists(), isFalse);

      await seedLegacyDuplicate(active);
      await File('${legacyRoot.path}/game.sfc').writeAsString('wrong');
      await cleanup.cleanupOrphans();
      expect(
        await database.select(database.legacyLocalInstalls).get(),
        hasLength(1),
      );
      expect(await legacyRoot.exists(), isTrue);

      await File('${legacyRoot.path}/game.sfc').writeAsString('hello');
      await database.customStatement(
        'UPDATE legacy_local_installs SET manifest_fingerprint = ? WHERE release_id = ?',
        <Object?>['mismatched-proof', release],
      );
      await cleanup.cleanupOrphans();
      expect(
        await database.select(database.legacyLocalInstalls).get(),
        hasLength(1),
      );
      expect(await legacyRoot.exists(), isTrue);
    },
  );

  test(
    'legacy cleanup restart removes only an exact row after content is gone',
    () async {
      final active = await seedInstall();
      await seedReference();
      await seedLegacyDuplicate(active);
      final legacyRoot = store(serverA).legacyContentRoot(
        platformShortName: 'snes',
        titleId: title,
        releaseId: release,
      );
      await legacyRoot.delete(recursive: true);

      await cleanup.cleanupOrphans();
      expect(
        await database.select(database.legacyLocalInstalls).get(),
        isEmpty,
      );

      await seedLegacyDuplicate(active);
      await legacyRoot.delete(recursive: true);
      final outside = Directory('${base.path}/legacy-symlink-target')
        ..createSync(recursive: true);
      final marker = File('${outside.path}/keep')..writeAsStringSync('keep');
      await legacyRoot.parent.create(recursive: true);
      await Link(legacyRoot.path).create(outside.path);

      await cleanup.cleanupOrphans();
      expect(
        await database.select(database.legacyLocalInstalls).get(),
        hasLength(1),
      );
      expect(await marker.readAsString(), 'keep');
    },
  );

  test(
    'serializer is FIFO and a released failed operation cannot poison it',
    () async {
      final key = InstallMutationKey(
        serverInstanceId: serverA,
        releaseId: RomdPublicId.tryParse(release)!,
      );
      final first = await serializer.acquire(key);
      var secondEntered = false;
      final secondFuture = serializer.acquire(key).then((lease) {
        secondEntered = true;
        return lease;
      });
      await Future<void>.delayed(Duration.zero);
      expect(secondEntered, isFalse);

      first.release();
      final second = await secondFuture;
      expect(secondEntered, isTrue);
      second.release();

      final afterFailure = await serializer.acquire(key);
      afterFailure.release();
    },
  );

  test('removal waits for an in-flight mutation on the exact key', () async {
    await seedInstall();
    await seedReference();
    final lease = await serializer.acquire(
      InstallMutationKey(
        serverInstanceId: serverA,
        releaseId: RomdPublicId.tryParse(release)!,
      ),
    );
    var completed = false;
    final removal = cleanup
        .removeProfileGame(authority: authority(), releaseId: release)
        .whenComplete(() => completed = true);
    await Future<void>.delayed(Duration.zero);
    expect(completed, isFalse);
    expect(
      await database.select(database.profileLocalGames).get(),
      hasLength(1),
    );

    lease.release();
    await removal;
    expect(completed, isTrue);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
  });
}
