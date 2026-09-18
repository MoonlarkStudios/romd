import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' hide isNotNull, isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/data/release_access_api_client.dart';
import 'package:romd_console/src/data/release_manifest_api_client.dart';
import 'package:romd_console/src/domain/console_artwork.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/domain/server_bound_release_manifest.dart';
import 'package:romd_console/src/play/content/data/content_file_store.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/data/drift_profile_local_library_repository.dart';
import 'package:romd_console/src/play/content/data/install_mutation_serializer.dart';
import 'package:romd_console/src/play/content/data/profile_game_cleanup_service.dart';
import 'package:romd_console/src/play/content/data/romd_install_service.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

const _instance = '11111111-1111-4111-8111-111111111111';
const _helloSha =
    '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824';

final _serverId = RomdServerInstanceId.tryParse(_instance)!;
final _releaseId = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
final _titleId = RomdPublicId.tryParse(RomdPublicId.encode(201))!;

final class _Access implements ReleaseAccessApiClient {
  _Access(this.result);
  ReleaseAccessResult result;
  int calls = 0;
  final tokens = <String>[];

  @override
  Future<ReleaseAccessResult> getReleaseAccess({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,
  }) async {
    calls++;
    tokens.add(accessToken);
    return result;
  }

  @override
  void close() {}
}

final class _MutableInstallOperationLease implements InstallOperationLease {
  bool current = true;

  @override
  bool get isCurrent => current;
}

const _operation = _AlwaysCurrentInstallOperationLease();

final class _AlwaysCurrentInstallOperationLease
    implements InstallOperationLease {
  const _AlwaysCurrentInstallOperationLease();

  @override
  bool get isCurrent => true;
}

final class _Manifest implements ReleaseManifestApiClient {
  _Manifest(this.result);
  ReleaseManifestResult result;
  int calls = 0;

  @override
  Future<ReleaseManifestResult> getReleaseManifest({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,

    required String expectedSystemKey,
  }) async {
    calls++;
    return result;
  }

  @override
  void close() {}
}

final class _Download implements DownloadClient {
  int calls = 0;
  bool fail = false;
  bool expireOnce = false;

  @override
  Stream<int> download({required Uri url, required File destination}) async* {
    calls++;
    if (expireOnce && calls == 1) throw const DownloadGrantExpired();
    if (fail) throw const DownloadNetworkError();
    await destination.parent.create(recursive: true);
    await destination.writeAsString('hello');
    yield 5;
  }
}

final class _RejectingPathProbe implements TargetFilesystemPathProbe {
  @override
  Future<bool> pathsAreDistinct({
    required Directory scratch,
    required Iterable<String> relativePaths,
    required File Function(Directory root, String relativePath) resolveWithin,
  }) async => false;
}

final class _NoopRefreshTokenStore implements RefreshTokenStore {
  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async => null;

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) async {}

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {}

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) async {}
}

final class _RetryingAuthenticatedSession
    implements AuthenticatedRequestExecutor {
  _RetryingAuthenticatedSession({
    required this.initialToken,
    this.refreshedToken,
    this.onRefresh,
  });

  final String initialToken;
  final String? refreshedToken;
  final Future<void> Function()? onRefresh;

  @override
  int get credentialGeneration => 1;

  @override
  ConsumerLoginSession? get session => null;

  @override
  Future<T> execute<T>({
    required Future<T> Function(String accessToken) request,
    AuthenticatedReplayPolicy replayPolicy =
        AuthenticatedReplayPolicy.onceAfterInvalidToken,
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  }) async {
    final first = await request(initialToken);
    if (!(isInvalidTokenResult?.call(first) ?? false)) return first;
    await onRefresh?.call();
    final refreshed = refreshedToken;
    if (refreshed == null) {
      throw const SessionUnavailableException(
        SessionRefreshFailureKind.transient,
      );
    }
    return request(refreshed);
  }
}

void main() {
  late Directory base;
  late AppDatabase database;
  late DriftLocalInstallRepository installs;
  late InstallMutationSerializer mutationSerializer;
  late ProfileGameCleanupService cleanupService;
  late _Access access;
  late _Manifest manifests;
  late _Download downloads;

  final now = DateTime.utc(2026, 7, 18, 12);
  final connection = ProfileServerConnection(
    instanceId: _serverId,
    origin: Uri.parse('https://library.example'),
    firstSeenAt: now,
    lastSeenAt: now,
  );
  final target = PlayTarget(
    releaseId: _releaseId.value,
    titleId: _titleId.value,
    displayName: 'Chrono Trigger',
    localProfileId: 'profile-1',
    platformId: 'snes',
    platformName: 'SNES',
  );

  ServerBoundReleaseManifest manifest({String relativePath = 'chrono.sfc'}) =>
      ServerBoundReleaseManifest(
        serverInstanceId: _serverId,
        releaseId: _releaseId,
        titleId: _titleId,
        systemKey: 'snes',
        name: 'Chrono Trigger',
        revision: null,
        isComplete: true,
        runtime: ServerBoundReleaseRuntime(
          contentType: 'single_rom',
          launch: ServerBoundLaunchTarget(
            type: 'file',
            relativePath: relativePath,
          ),
          packaging: 'direct_files',
          minimumInstallBytes: 5,
        ),
        items: <ServerBoundReleaseManifestItem>[
          ServerBoundReleaseManifestItem(
            relativePath: relativePath,
            role: 'rom',
            sizeBytes: 5,
            sha256: _helloSha,
            isAvailable: true,
            contentGrant: ServerBoundContentGrant(
              downloadUrl: Uri.parse(
                'https://library.example/delivery/content/a.b',
              ),
              expiresAt: now.add(const Duration(minutes: 5)),
            ),
          ),
        ],
      );

  ContentFileStore fileStore() =>
      ContentFileStore(baseDir: base, serverInstanceId: _instance);

  Future<Directory> seedLegacy({String contents = 'hello'}) async {
    final root = fileStore().legacyContentRoot(
      platformShortName: 'snes',
      titleId: _titleId.value,
      releaseId: _releaseId.value,
    );
    await root.create(recursive: true);
    await File('${root.path}/chrono.sfc').writeAsString(contents);
    await database
        .into(database.legacyLocalInstalls)
        .insert(
          LegacyLocalInstallsCompanion.insert(
            releaseId: _releaseId.value,
            titleId: _titleId.value,
            titleName: const Value('Chrono Trigger'),
            platformId: const Value('snes'),
            platformName: const Value('SNES'),
            platformShortName: 'snes',
            contentRoot: root.path,
            launchRelativePath: 'chrono.sfc',
            sizeBytes: contents.length,
            manifestFingerprint: const ContentVerifier().manifestFingerprint(
              const <FingerprintItem>[
                (relativePath: 'chrono.sfc', sizeBytes: 5, sha256: _helloSha),
              ],
            ),
            state: InstallState.installed.name,
            installMode: const Value('permanent'),
            manifestSnapshot: jsonEncode(<Object?>[
              <String, Object?>{
                'relativePath': 'chrono.sfc',
                'sizeBytes': contents.length,
                'sha256': _helloSha,
              },
            ]),
            installedAt: now.subtract(const Duration(days: 1)),
          ),
        );
    return root;
  }

  Future<void> seedProfile(String id, {int generation = 1}) async {
    await database
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
            selectedServerInstanceId: const Value(_instance),
            serverSelectionGeneration: Value(generation),
          ),
        );
  }

  RomdInstallService service({
    String profileId = 'profile-1',
    int generation = 1,
    bool neutralAuthority = false,
    TargetFilesystemPathProbe pathProbe = const IoTargetFilesystemPathProbe(),
    AuthenticatedRequestExecutor? authenticatedSession,
    ProfileLocalLibraryRepository? profileLocalLibrary,
    Future<void> Function(AcquisitionCheckpoint checkpoint)? checkpoint,
  }) => RomdInstallService(
    releaseAccessApiClient: access,
    releaseManifestApiClient: manifests,

    database: database,
    authority: neutralAuthority
        ? null
        : InstallAuthorityContext(
            localProfileId: profileId,
            connection: connection,
            generation: generation,
          ),
    authenticatedSession: authenticatedSession,
    downloadClient: downloads,
    verifier: const ContentVerifier(),
    fileStore: ContentFileStore(
      baseDir: base,
      serverInstanceId: _instance,
      pathProbe: pathProbe,
    ),
    installs: installs,
    profileLocalLibrary:
        profileLocalLibrary ??
        DriftProfileLocalLibraryRepository(
          database: database,
          installs: installs,
          authority: neutralAuthority
              ? null
              : InstallAuthorityContext(
                  localProfileId: profileId,
                  connection: connection,
                  generation: generation,
                ),
        ),
    mutationSerializer: mutationSerializer,
    cleanupService: cleanupService,
    now: () => now,
    checkpoint: checkpoint,
  );

  Future<ResolvedPlayTarget?> resolveInstalled(
    RomdInstallService installService, {
    required String localProfileId,
    int generation = 1,
  }) async {
    final lease = await mutationSerializer.acquire(
      InstallMutationKey(serverInstanceId: _serverId, releaseId: _releaseId),
    );
    try {
      return await installService.resolveInstalledForLaunch(
        authority: InstallAuthorityContext(
          localProfileId: localProfileId,
          connection: connection,
          generation: generation,
        ),
        releaseId: _releaseId.value,
        titleId: _titleId.value,
        displayName: 'Chrono Trigger',
      );
    } finally {
      lease.release();
    }
  }

  setUp(() async {
    base = await Directory.systemTemp.createTemp('romd-acquisition-');
    database = AppDatabase(NativeDatabase.memory());
    mutationSerializer = InstallMutationSerializer();
    cleanupService = ProfileGameCleanupService(
      database: database,
      baseDir: base,
      serializer: mutationSerializer,
      refreshTokenStore: _NoopRefreshTokenStore(),
    );
    await database
        .into(database.serverConnections)
        .insert(
          ServerConnectionsCompanion.insert(
            instanceId: _instance,
            lastKnownOrigin: connection.origin.toString(),
            firstSeenAt: now,
            lastSeenAt: now,
          ),
        );
    await seedProfile('profile-1');
    installs = DriftLocalInstallRepository(
      database: database,
      serverInstanceId: _serverId,
      fileStore: ContentFileStore(baseDir: base, serverInstanceId: _instance),
    );
    access = _Access(
      ReleaseAccessSuccess(
        ReleaseAccessAllowed(
          serverInstanceId: _serverId,
          releaseId: _releaseId,
          titleId: _titleId,
        ),
      ),
    );
    manifests = _Manifest(ReleaseManifestSuccess(manifest()));
    downloads = _Download();
  });

  tearDown(() async {
    await database.close();
    await base.delete(recursive: true);
  });

  test(
    'allowed verified download commits install and initiating grant',
    () async {
      final events = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();

      expect(events.last, isA<InstallCompleted>());
      expect(downloads.calls, 1);
      expect(await database.select(database.localInstalls).get(), hasLength(1));
      final game =
          (await database.select(database.profileLocalGames).get()).single;
      expect(game.localProfileId, 'profile-1');
      expect(game.authorizationState, ProfileGameAuthorization.authorized.name);
      expect(game.serverInstanceId, _instance);
    },
  );

  test(
    'presentation reads require this profile reference but retain revoked games',
    () async {
      final profileOne = service();
      final installed = await profileOne
          .install(target, accessToken: 'token', operation: _operation)
          .toList();
      expect(installed.last, isA<InstallCompleted>());
      expect(await profileOne.findInstall(_releaseId.value), isNotNull);
      expect(await profileOne.listInstalled(), hasLength(1));
      expect(await profileOne.watchInstalledReleaseIds().first, <String>{
        _releaseId.value,
      });

      await seedProfile('profile-2');
      final otherProfile = service(profileId: 'profile-2');
      expect(await otherProfile.findInstall(_releaseId.value), isNull);
      expect(await otherProfile.listInstalled(), isEmpty);
      expect(await otherProfile.watchInstalled().first, isEmpty);

      await database
          .update(database.profileLocalGames)
          .write(
            const ProfileLocalGamesCompanion(
              authorizationState: Value('revoked'),
            ),
          );
      expect(await profileOne.findInstall(_releaseId.value), isNotNull);
      expect(
        await profileOne.listInstalledForTitle(_titleId.value),
        hasLength(1),
      );
    },
  );

  test('explicit revoke creates no install or profile grant', () async {
    access.result = ReleaseAccessSuccess(
      ReleaseAccessRevoked(serverInstanceId: _serverId, releaseId: _releaseId),
    );
    final events = await service()
        .install(target, accessToken: 'token', operation: _operation)
        .toList();

    expect(
      (events.last as InstallFailed).kind,
      InstallFailureKind.accessNotGranted,
    );
    expect(manifests.calls, 0);
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
  });

  test('non-authoritative access failure writes nothing', () async {
    access.result = const ReleaseAccessFailure(
      ReleaseAccessFailureKind.malformedResponse,
    );
    await service()
        .install(target, accessToken: 'token', operation: _operation)
        .toList();
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
  });

  test('strict manifest identity failure writes nothing', () async {
    manifests.result = const ReleaseManifestFailure(
      ReleaseManifestFailureKind.identityMismatch,
    );
    final events = await service()
        .install(target, accessToken: 'token', operation: _operation)
        .toList();
    expect(
      (events.last as InstallFailed).kind,
      InstallFailureKind.manifestUnavailable,
    );
    expect(downloads.calls, 0);
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
  });

  test(
    'target-filesystem collision fails before staging or download',
    () async {
      final events = await service(
        pathProbe: _RejectingPathProbe(),
      ).install(target, accessToken: 'token', operation: _operation).toList();
      expect(
        (events.last as InstallFailed).kind,
        InstallFailureKind.unsupportedManifest,
      );
      expect(downloads.calls, 0);
      expect(await database.select(database.localInstalls).get(), isEmpty);
      expect(await database.select(database.profileLocalGames).get(), isEmpty);
    },
  );

  test('download failure leaves no active row, grant, or staging', () async {
    downloads.fail = true;
    final events = await service()
        .install(target, accessToken: 'token', operation: _operation)
        .toList();
    expect(
      (events.last as InstallFailed).kind,
      InstallFailureKind.downloadFailed,
    );
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
    expect(base.listSync(recursive: true).whereType<File>(), isEmpty);
  });

  test(
    'already invalid caller operation performs no authorization work',
    () async {
      final operation = _MutableInstallOperationLease()..current = false;

      final events = await service()
          .install(target, accessToken: 'token', operation: operation)
          .toList();

      expect(
        (events.single as InstallFailed).kind,
        InstallFailureKind.authorityChanged,
      );
      expect(access.calls, 0);
      expect(manifests.calls, 0);
      expect(downloads.calls, 0);
      expect(await database.select(database.localInstalls).get(), isEmpty);
      expect(await database.select(database.profileLocalGames).get(), isEmpty);
    },
  );

  test('cancelling progress cleans staging and creates no rows', () async {
    final cancelled = Completer<void>();
    late StreamSubscription<InstallProgress> subscription;
    subscription = service()
        .install(target, accessToken: 'token', operation: _operation)
        .listen((event) {
          if (event is InstallDownloading && !cancelled.isCompleted) {
            subscription.cancel().then((_) => cancelled.complete());
          }
        });
    await cancelled.future;

    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
    expect(
      base
          .listSync(recursive: true)
          .whereType<Directory>()
          .where((entry) => entry.path.endsWith('.staging')),
      isEmpty,
    );

    final retry = await service()
        .install(target, accessToken: 'token', operation: _operation)
        .toList();
    expect(retry.last, isA<InstallCompleted>());
  });

  test('profile removal waits for the full acquisition mutation', () async {
    final acquisitionPaused = Completer<void>();
    final resumeAcquisition = Completer<void>();
    final installing = service(
      checkpoint: (checkpoint) async {
        if (checkpoint == AcquisitionCheckpoint.beforeFinalCommit) {
          acquisitionPaused.complete();
          await resumeAcquisition.future;
        }
      },
    ).install(target, accessToken: 'token', operation: _operation).toList();
    await acquisitionPaused.future;

    var removalCompleted = false;
    final removal = service()
        .uninstall(target.releaseId)
        .whenComplete(() => removalCompleted = true);
    await Future<void>.delayed(Duration.zero);
    expect(removalCompleted, isFalse);

    resumeAcquisition.complete();
    expect((await installing).last, isA<InstallCompleted>());
    await removal;

    expect(await database.select(database.profileLocalGames).get(), isEmpty);
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(removalCompleted, isTrue);
  });

  test(
    'expired grant refreshes strict manifest once and retries cleanly',
    () async {
      downloads.expireOnce = true;
      final events = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();
      expect(events.last, isA<InstallCompleted>());
      expect(downloads.calls, 2);
      expect(manifests.calls, 2);
      expect(await database.select(database.localInstalls).get(), hasLength(1));
      expect(
        await database.select(database.profileLocalGames).get(),
        hasLength(1),
      );
    },
  );

  test(
    'generation change rolls back materialized content and all rows',
    () async {
      await (database.update(
        database.localProfiles,
      )..where((row) => row.id.equals('profile-1'))).write(
        const LocalProfilesCompanion(serverSelectionGeneration: Value(2)),
      );
      final events = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();
      expect(
        (events.last as InstallFailed).kind,
        InstallFailureKind.authorityChanged,
      );
      expect(await database.select(database.localInstalls).get(), isEmpty);
      expect(await database.select(database.profileLocalGames).get(), isEmpty);
      expect(base.listSync(recursive: true).whereType<File>(), isEmpty);
    },
  );

  test(
    'invalidated caller operation rolls back before authoritative commit',
    () async {
      final operation = _MutableInstallOperationLease();
      final events = await service(
        checkpoint: (checkpoint) async {
          if (checkpoint == AcquisitionCheckpoint.beforeFinalCommit) {
            operation.current = false;
          }
        },
      ).install(target, accessToken: 'token', operation: operation).toList();

      expect(
        (events.last as InstallFailed).kind,
        InstallFailureKind.authorityChanged,
      );
      expect(await database.select(database.localInstalls).get(), isEmpty);
      expect(await database.select(database.profileLocalGames).get(), isEmpty);
      expect(base.listSync(recursive: true).whereType<File>(), isEmpty);
    },
  );

  test(
    'second profile attach repeats authority and verification without download',
    () async {
      await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();
      await seedProfile('profile-2');
      final profileTwoTarget = PlayTarget(
        releaseId: target.releaseId,
        titleId: target.titleId,
        displayName: target.displayName,
        localProfileId: 'profile-2',
        platformId: target.platformId,
        platformName: target.platformName,
      );
      final events = await service(profileId: 'profile-2')
          .install(
            profileTwoTarget,
            accessToken: 'token',
            operation: _operation,
          )
          .toList();

      expect(events.last, isA<InstallCompleted>());
      expect(downloads.calls, 1);
      expect(access.calls, 2);
      expect(await database.select(database.localInstalls).get(), hasLength(1));
      expect(
        await database.select(database.profileLocalGames).get(),
        hasLength(2),
      );
    },
  );

  test('tampered cross-instance content root fails before file probing', () async {
    await service()
        .install(target, accessToken: 'token', operation: _operation)
        .toList();
    await database.customStatement(
      'UPDATE local_installs SET content_root = ? WHERE server_instance_id = ? AND release_id = ?',
      <Object?>[
        '${base.path}/content/servers/22222222-2222-4222-8222-222222222222/snes/${_titleId.value}/${_releaseId.value}',
        _instance,
        _releaseId.value,
      ],
    );

    expect(
      await resolveInstalled(service(), localProfileId: 'profile-1'),
      isNull,
    );
    expect(
      (await database.select(database.localInstalls).get()).single.state,
      'installed',
    );
  });

  test(
    'resolve verification cannot corrupt a concurrently replaced row',
    () async {
      final installed = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();
      final resolved = (installed.last as InstallCompleted).resolved;
      await File(resolved.launchAbsolutePath).delete();
      final beforeCorruptMark = Completer<void>();
      final resumeCorruptMark = Completer<void>();
      final staleResolve = resolveInstalled(
        service(
          checkpoint: (checkpoint) async {
            if (checkpoint == AcquisitionCheckpoint.beforeCorruptMark) {
              beforeCorruptMark.complete();
              await resumeCorruptMark.future;
            }
          },
        ),
        localProfileId: target.localProfileId,
      );
      await beforeCorruptMark.future;

      var replacementCompleted = false;
      final replacement = service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList()
          .whenComplete(() => replacementCompleted = true);
      await Future<void>.delayed(Duration.zero);
      expect(replacementCompleted, isFalse);

      resumeCorruptMark.complete();
      expect(await staleResolve, isNull);
      expect((await replacement).last, isA<InstallCompleted>());
      expect(
        (await database.select(database.localInstalls).get()).single.state,
        InstallState.installed.name,
      );
    },
  );

  test(
    'resolve with the wrong profile fails closed without mutation',
    () async {
      await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();

      expect(
        await resolveInstalled(service(), localProfileId: 'profile-2'),
        isNull,
      );
      expect(
        await resolveInstalled(
          service(neutralAuthority: true),
          localProfileId: target.localProfileId,
        ),
        isNull,
      );
      await (database.update(
        database.localProfiles,
      )..where((row) => row.id.equals('profile-1'))).write(
        const LocalProfilesCompanion(serverSelectionGeneration: Value(2)),
      );
      expect(
        await resolveInstalled(
          service(),
          localProfileId: target.localProfileId,
        ),
        isNull,
      );
      expect(
        (await database.select(database.localInstalls).get()).single.state,
        InstallState.installed.name,
      );
    },
  );

  test('401 refreshes once and retries with the restored token', () async {
    access.result = const ReleaseAccessFailure(
      ReleaseAccessFailureKind.unauthorized,
      bearerError: 'invalid_token',
    );
    final events = await service(
      authenticatedSession: _RetryingAuthenticatedSession(
        initialToken: 'expired-token',
        refreshedToken: 'restored-token',
        onRefresh: () async {
          access.result = ReleaseAccessSuccess(
            ReleaseAccessAllowed(
              serverInstanceId: _serverId,
              releaseId: _releaseId,
              titleId: _titleId,
            ),
          );
        },
      ),
    ).install(target, operation: _operation).toList();

    expect(events.last, isA<InstallCompleted>());
    expect(access.tokens, <String>['expired-token', 'restored-token']);
  });

  test('failed refresh and a second 401 create no state', () async {
    access.result = const ReleaseAccessFailure(
      ReleaseAccessFailureKind.unauthorized,
      bearerError: 'invalid_token',
    );
    final failedRefresh = await service(
      authenticatedSession: _RetryingAuthenticatedSession(
        initialToken: 'expired-token',
      ),
    ).install(target, operation: _operation).toList();
    expect(failedRefresh.last, isA<InstallFailed>());
    expect(access.calls, 1);

    final second401 = await service(
      authenticatedSession: _RetryingAuthenticatedSession(
        initialToken: 'expired-token',
        refreshedToken: 'still-unauthorized',
      ),
    ).install(target, operation: _operation).toList();
    expect(second401.last, isA<InstallFailed>());
    expect(access.calls, 3);
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
  });

  test('profile switch during access refresh creates no state', () async {
    access.result = const ReleaseAccessFailure(
      ReleaseAccessFailureKind.unauthorized,
      bearerError: 'invalid_token',
    );
    final events = await service(
      authenticatedSession: _RetryingAuthenticatedSession(
        initialToken: 'expired-token',
        refreshedToken: 'restored-token',
        onRefresh: () async {
          await (database.update(
            database.localProfiles,
          )..where((row) => row.id.equals('profile-1'))).write(
            const LocalProfilesCompanion(serverSelectionGeneration: Value(2)),
          );
          access.result = ReleaseAccessSuccess(
            ReleaseAccessAllowed(
              serverInstanceId: _serverId,
              releaseId: _releaseId,
              titleId: _titleId,
            ),
          );
        },
      ),
    ).install(target, operation: _operation).toList();

    expect(
      (events.last as InstallFailed).kind,
      InstallFailureKind.authorityChanged,
    );
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
  });

  final mismatches = <String, Object? Function()>{
    'title': () => RomdPublicId.encode(999),
    'platform': () => RomdPublicId.encode(998),
    'root': () => '${base.path}/content/elsewhere',
    'fingerprint': () => 'tampered-fingerprint',
    'items': () => jsonEncode(<Object?>[
      <String, Object?>{
        'relativePath': 'chrono.sfc',
        'sizeBytes': 5,
        'sha256': _helloSha,
      },
      <String, Object?>{
        'relativePath': 'unexpected.bin',
        'sizeBytes': 1,
        'sha256':
            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
      },
    ]),
  };
  for (final mismatch in mismatches.entries) {
    test('${mismatch.key} mismatch downloads and commits fresh proof', () async {
      await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();
      final column = switch (mismatch.key) {
        'title' => 'title_id',
        'platform' => 'platform_id',
        'root' => 'content_root',
        'fingerprint' => 'manifest_fingerprint',
        'items' => 'manifest_snapshot',
        _ => throw StateError('unknown mismatch'),
      };
      await database.customStatement(
        'UPDATE local_installs SET $column = ? WHERE server_instance_id = ? AND release_id = ?',
        <Object?>[mismatch.value(), _instance, _releaseId.value],
      );

      final events = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();

      expect(events.last, isA<InstallCompleted>());
      expect(downloads.calls, 2);
      final row = (await database.select(database.localInstalls).get()).single;
      expect(row.titleId, _titleId.value);
      expect(row.platformId, 'snes');
      expect(
        row.contentRoot,
        fileStore()
            .contentRoot(
              platformShortName: 'snes',
              titleId: _titleId.value,
              releaseId: _releaseId.value,
            )
            .path,
      );
      expect(row.manifestSnapshot, contains('chrono.sfc'));
      expect(row.manifestSnapshot, isNot(contains('unexpected.bin')));
    });
  }

  test(
    'verified legacy content is copied and adopted without download',
    () async {
      final legacyRoot = await seedLegacy();

      final events = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();

      expect(events.last, isA<InstallCompleted>());
      expect(downloads.calls, 0);
      expect(await database.select(database.localInstalls).get(), hasLength(1));
      expect(
        await database.select(database.profileLocalGames).get(),
        hasLength(1),
      );
      expect(
        await database.select(database.legacyLocalInstalls).get(),
        hasLength(1),
      );
      expect(await legacyRoot.exists(), isTrue);
      expect(
        await resolveInstalled(
          service(),
          localProfileId: target.localProfileId,
        ),
        isNotNull,
      );

      await cleanupService.cleanupOrphans();
      expect(
        await database.select(database.legacyLocalInstalls).get(),
        isEmpty,
      );
      expect(await legacyRoot.exists(), isFalse);
    },
  );

  test(
    'corrupt legacy content falls back without deleting legacy evidence',
    () async {
      final legacyRoot = await seedLegacy(contents: 'wrong');

      final events = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();

      expect(events.last, isA<InstallCompleted>());
      expect(downloads.calls, 1);
      expect(
        await database.select(database.legacyLocalInstalls).get(),
        hasLength(1),
      );
      expect(await legacyRoot.exists(), isTrue);
    },
  );

  test('stale legacy adoption leaves row and files untouched', () async {
    final legacyRoot = await seedLegacy();
    await (database.update(
      database.localProfiles,
    )..where((row) => row.id.equals('profile-1'))).write(
      const LocalProfilesCompanion(serverSelectionGeneration: Value(2)),
    );

    final events = await service()
        .install(target, accessToken: 'token', operation: _operation)
        .toList();

    expect(
      (events.last as InstallFailed).kind,
      InstallFailureKind.authorityChanged,
    );
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
    expect(
      await database.select(database.legacyLocalInstalls).get(),
      hasLength(1),
    );
    expect(await legacyRoot.exists(), isTrue);
  });

  test(
    'failed acquisition never retains artwork before catalog commit',
    () async {
      final artworkTarget = PlayTarget(
        releaseId: target.releaseId,
        titleId: target.titleId,
        displayName: target.displayName,
        localProfileId: target.localProfileId,
        platformId: target.platformId,
        platformName: target.platformName,
        artwork: [
          ConsoleArtwork(
            role: 'Poster',
            url: connection.origin.resolve('/artwork/asset/card/v1'),
            fit: 'Cover',
            contentVersion: 'v1',
            assetId: 'asset',
            width: 600,
            height: 900,
            originalWidth: 600,
            originalHeight: 900,
            fallbackReason: 'None',
            variants: const [],
          ),
        ],
      );
      final events =
          await service(
                checkpoint: (checkpoint) async {
                  if (checkpoint == AcquisitionCheckpoint.beforeFinalCommit)
                    throw StateError('injected');
                },
              )
              .install(
                artworkTarget,
                accessToken: 'token',
                operation: _operation,
              )
              .toList();
      expect(events.last, isA<InstallFailed>());
      expect(await database.select(database.localInstalls).get(), isEmpty);
      expect(Directory('${base.path}/artwork').existsSync(), isFalse);
    },
  );

  test('legacy final commit failure leaves row and files untouched', () async {
    final legacyRoot = await seedLegacy();

    final events = await service(
      checkpoint: (checkpoint) async {
        if (checkpoint == AcquisitionCheckpoint.beforeFinalCommit) {
          throw StateError('database unavailable');
        }
      },
    ).install(target, accessToken: 'token', operation: _operation).toList();

    expect((events.last as InstallFailed).kind, InstallFailureKind.ioError);
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
    expect(
      await database.select(database.legacyLocalInstalls).get(),
      hasLength(1),
    );
    expect(await legacyRoot.exists(), isTrue);
  });

  for (final checkpoint in <AcquisitionCheckpoint>[
    AcquisitionCheckpoint.afterDeactivation,
    AcquisitionCheckpoint.afterPromotion,
    AcquisitionCheckpoint.beforeFinalCommit,
  ]) {
    test('${checkpoint.name} failure restores prior row and files', () async {
      await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();
      manifests.result = ReleaseManifestSuccess(
        manifest(relativePath: 'replacement.sfc'),
      );

      final events = await service(
        checkpoint: (current) async {
          if (current == checkpoint) throw StateError('injected');
        },
      ).install(target, accessToken: 'token', operation: _operation).toList();

      expect((events.last as InstallFailed).kind, InstallFailureKind.ioError);
      final row = (await database.select(database.localInstalls).get()).single;
      expect(row.launchRelativePath, 'chrono.sfc');
      expect(File('${row.contentRoot}/chrono.sfc').existsSync(), isTrue);
      expect(File('${row.contentRoot}/replacement.sfc').existsSync(), isFalse);
    });
  }
}
