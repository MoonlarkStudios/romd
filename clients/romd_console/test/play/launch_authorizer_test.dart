import 'dart:io';

import 'package:drift/drift.dart' hide isNotNull, isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/data/release_access_api_client.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/content_file_store.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/session/data/launch_authorization_store.dart';
import 'package:romd_console/src/play/session/data/online_first_launch_authorizer.dart';
import 'package:romd_console/src/play/session/domain/launch_authorization.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

final _releaseId = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
final _titleId = RomdPublicId.tryParse(RomdPublicId.encode(201))!;

final class _RetryingSession implements AuthenticatedRequestExecutor {
  const _RetryingSession(this.token, this.refresh, this.authority);

  final String token;
  final Future<String?> Function(InstallAuthorityContext) refresh;
  final InstallAuthorityContext authority;

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
    final first = await request(token);
    if (!(isInvalidTokenResult?.call(first) ?? false)) return first;
    final next = await refresh(authority);
    if (next == null) {
      throw const SessionUnavailableException(
        SessionRefreshFailureKind.transient,
      );
    }
    return request(next);
  }
}

final _instanceId = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;

final class _Access implements ReleaseAccessApiClient {
  final List<ReleaseAccessResult> results = <ReleaseAccessResult>[];
  Future<void> Function()? beforeReturn;
  int calls = 0;
  final List<String> tokens = <String>[];
  bool closed = false;

  @override
  Future<ReleaseAccessResult> getReleaseAccess({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,
  }) async {
    calls++;
    tokens.add(accessToken);
    await beforeReturn?.call();
    return results.removeAt(0);
  }

  @override
  void close() => closed = true;
}

void main() {
  late AppDatabase database;
  late Directory base;
  late ContentFileStore files;
  late DriftLocalInstallRepository installs;
  late DriftLaunchAuthorizationStore store;
  late _Access access;
  late InstallAuthorityContext authority;
  final checkedAt = DateTime.utc(2026, 7, 18, 10);

  PlayRequest request() => PlayRequest(
    releaseId: _releaseId.value,
    titleId: _titleId.value,
    displayName: 'Chrono Trigger',
  );

  Future<void> seed({
    ProfileGameAuthorization? authorization,
    String? contentRoot,
    String profileId = 'profile-1',
    int generation = 1,
  }) async {
    await database
        .into(database.serverConnections)
        .insert(
          ServerConnectionsCompanion.insert(
            instanceId: _instanceId.value,
            lastKnownOrigin: 'https://library.example',
            firstSeenAt: checkedAt,
            lastSeenAt: checkedAt,
          ),
        );
    await database
        .into(database.localProfiles)
        .insert(
          LocalProfilesCompanion.insert(
            id: profileId,
            displayName: 'Player',
            avatarKey: 'default',
            accentColor: 1,
            entryMode: 'open',
            createdAt: checkedAt,
            updatedAt: checkedAt,
            selectedServerInstanceId: Value(_instanceId.value),
            serverSelectionGeneration: Value(generation),
          ),
        );
    const manifestItems = <FingerprintItem>[
      (
        relativePath: 'chrono.sfc',
        sizeBytes: 5,
        sha256:
            '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824',
      ),
    ];
    final canonical = files.contentRoot(
      platformShortName: 'snes',
      titleId: _titleId.value,
      releaseId: _releaseId.value,
    );
    await database
        .into(database.localInstalls)
        .insert(
          LocalInstallsCompanion.insert(
            serverInstanceId: _instanceId.value,
            releaseId: _releaseId.value,
            titleId: _titleId.value,
            titleName: const Value('Chrono Trigger'),
            platformId: 'snes',
            platformName: const Value('SNES'),
            platformShortName: 'snes',
            contentRoot: contentRoot ?? canonical.path,
            launchRelativePath: 'chrono.sfc',
            sizeBytes: 5,
            manifestFingerprint: const ContentVerifier().manifestFingerprint(
              manifestItems,
            ),
            state: InstallState.installed.name,
            installMode: const Value('permanent'),
            manifestSnapshot:
                '[{"relativePath":"chrono.sfc","sizeBytes":5,"sha256":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"}]',
            installedAt: checkedAt,
          ),
        );
    if (authorization != null) {
      await database
          .into(database.profileLocalGames)
          .insert(
            ProfileLocalGamesCompanion.insert(
              localProfileId: profileId,
              serverInstanceId: _instanceId.value,
              releaseId: _releaseId.value,
              titleId: _titleId.value,
              authorizationState: authorization.name,
              acquiredAt: checkedAt,
              lastCheckedAt: checkedAt,
            ),
          );
    }
  }

  OnlineFirstLaunchAuthorizer authorizer({
    String? token,
    Future<String?> Function(InstallAuthorityContext)? refresh,
    DateTime Function()? now,
  }) => OnlineFirstLaunchAuthorizer(
    authority: authority,
    store: store,
    accessApiClient: access,
    authenticatedSession: refresh == null || token == null
        ? null
        : _RetryingSession(token, refresh, authority),
    accessTokenProvider: () => token,
    now: now,
  );

  setUp(() async {
    database = AppDatabase(NativeDatabase.memory());
    base = await Directory.systemTemp.createTemp('romd-launch-auth-');
    files = ContentFileStore(
      baseDir: base,
      serverInstanceId: _instanceId.value,
    );
    installs = DriftLocalInstallRepository(
      database: database,
      serverInstanceId: _instanceId,
      fileStore: files,
    );
    store = DriftLaunchAuthorizationStore(
      database: database,
      installs: installs,
    );
    access = _Access();
    final connection = ProfileServerConnection(
      instanceId: _instanceId,
      origin: Uri.parse('https://library.example'),
      firstSeenAt: checkedAt,
      lastSeenAt: checkedAt,
    );
    authority = InstallAuthorityContext(
      localProfileId: 'profile-1',
      connection: connection,
      generation: 1,
    );
  });

  tearDown(() async {
    await database.close();
    await base.delete(recursive: true);
  });

  test('no local game row blocks without contacting ROMD', () async {
    await seed();
    access.results.add(
      ReleaseAccessSuccess(
        ReleaseAccessAllowed(
          serverInstanceId: _instanceId,
          releaseId: _releaseId,
          titleId: _titleId,
        ),
      ),
    );

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<AuthorizationNotGranted>(),
    );
    expect(access.calls, 0);
  });

  test('missing active install blocks safely before contacting ROMD', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    await database.delete(database.localInstalls).go();

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<LaunchAuthorizationUnavailable>(),
    );
    expect(access.calls, 0);
  });

  test('offline cached allow permits and cached revoke blocks', () async {
    for (final state in ProfileGameAuthorization.values) {
      await seed(authorization: state);
      final result = await authorizer().authorize(request());
      expect(
        result,
        state == ProfileGameAuthorization.authorized
            ? isA<LaunchAuthorized>()
            : isA<AuthorizationRevoked>(),
      );
      await database.delete(database.profileLocalGames).go();
      await database.delete(database.localInstalls).go();
      await database.delete(database.localProfiles).go();
      await database.delete(database.serverConnections).go();
    }
    expect(access.calls, 0);
  });

  test(
    'explicit allow restores the row with a durable CAS timestamp',
    () async {
      await seed(authorization: ProfileGameAuthorization.revoked);
      access.results.add(
        ReleaseAccessSuccess(
          ReleaseAccessAllowed(
            serverInstanceId: _instanceId,
            releaseId: _releaseId,
            titleId: _titleId,
          ),
        ),
      );

      expect(
        await authorizer(
          token: 'token',
          now: () => checkedAt,
        ).authorize(request()),
        isA<LaunchAuthorized>(),
      );
      final row =
          (await database.select(database.profileLocalGames).get()).single;
      expect(row.authorizationState, 'authorized');
      expect(row.lastCheckedAt.isAfter(row.acquiredAt), isTrue);
      expect(
        row.lastCheckedAt.difference(row.acquiredAt),
        const Duration(seconds: 1),
      );
    },
  );

  test('same-second revoke persists a strictly newer CAS timestamp', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.results.add(
      ReleaseAccessSuccess(
        ReleaseAccessRevoked(
          serverInstanceId: _instanceId,
          releaseId: _releaseId,
        ),
      ),
    );

    expect(
      await authorizer(
        token: 'token',
        now: () => checkedAt.add(const Duration(milliseconds: 999)),
      ).authorize(request()),
      isA<AuthorizationRevoked>(),
    );
    final row =
        (await database.select(database.profileLocalGames).get()).single;
    expect(row.authorizationState, 'revoked');
    expect(row.lastCheckedAt.difference(checkedAt), const Duration(seconds: 1));
  });

  test('an old snapshot cannot overwrite a same-second CAS', () async {
    await seed(authorization: ProfileGameAuthorization.revoked);
    final initial =
        (await store.read(
                  authority: authority,
                  releaseId: _releaseId,
                  titleId: _titleId,
                )
                as LaunchAuthorizationFound)
            .snapshot;

    expect(
      await store.compareAndSet(
        authority: authority,
        releaseId: _releaseId,
        titleId: _titleId,
        expected: initial,
        authorization: ProfileGameAuthorization.authorized,
        checkedAt: checkedAt.add(const Duration(milliseconds: 500)),
      ),
      LaunchAuthorizationWriteResult.updated,
    );
    expect(
      await store.compareAndSet(
        authority: authority,
        releaseId: _releaseId,
        titleId: _titleId,
        expected: initial,
        authorization: ProfileGameAuthorization.revoked,
        checkedAt: checkedAt.add(const Duration(milliseconds: 900)),
      ),
      LaunchAuthorizationWriteResult.stale,
    );

    final row =
        (await database.select(database.profileLocalGames).get()).single;
    expect(row.authorizationState, 'authorized');
    expect(row.lastCheckedAt.difference(checkedAt), const Duration(seconds: 1));
  });

  test('all non-authoritative failures preserve the cached decision', () async {
    for (final failure in ReleaseAccessFailureKind.values) {
      if (failure == ReleaseAccessFailureKind.unauthorized) continue;
      await seed(authorization: ProfileGameAuthorization.authorized);
      access.results.add(ReleaseAccessFailure(failure));
      expect(
        await authorizer(token: 'token').authorize(request()),
        isA<LaunchAuthorized>(),
        reason: failure.name,
      );
      expect(
        (await database.select(database.profileLocalGames).get())
            .single
            .authorizationState,
        'authorized',
      );
      await database.delete(database.profileLocalGames).go();
      await database.delete(database.localInstalls).go();
      await database.delete(database.localProfiles).go();
      await database.delete(database.serverConnections).go();
    }
  });

  test('transport fallback rejects a generation change', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.beforeReturn = () async {
      await database
          .update(database.localProfiles)
          .write(
            const LocalProfilesCompanion(serverSelectionGeneration: Value(2)),
          );
    };
    access.results.add(
      const ReleaseAccessFailure(ReleaseAccessFailureKind.transport),
    );

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<AuthorizationNotGranted>(),
    );
  });

  test('transport fallback rejects a selected-server change', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.beforeReturn = () async {
      await database
          .update(database.localProfiles)
          .write(
            const LocalProfilesCompanion(selectedServerInstanceId: Value(null)),
          );
    };
    access.results.add(
      const ReleaseAccessFailure(ReleaseAccessFailureKind.transport),
    );

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<AuthorizationNotGranted>(),
    );
  });

  test('transport fallback rejects an origin change', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.beforeReturn = () async {
      await database
          .update(database.serverConnections)
          .write(
            const ServerConnectionsCompanion(
              lastKnownOrigin: Value('https://replacement.example'),
            ),
          );
    };
    access.results.add(
      const ReleaseAccessFailure(ReleaseAccessFailureKind.transport),
    );

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<AuthorizationNotGranted>(),
    );
  });

  test('transport fallback rejects an install replacement', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    const replacementItems = <FingerprintItem>[
      (
        relativePath: 'chrono-replaced.sfc',
        sizeBytes: 5,
        sha256:
            '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824',
      ),
    ];
    access.beforeReturn = () async {
      await database
          .update(database.localInstalls)
          .write(
            LocalInstallsCompanion(
              launchRelativePath: const Value('chrono-replaced.sfc'),
              manifestFingerprint: Value(
                const ContentVerifier().manifestFingerprint(replacementItems),
              ),
              manifestSnapshot: const Value(
                '[{"relativePath":"chrono-replaced.sfc","sizeBytes":5,"sha256":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"}]',
              ),
            ),
          );
    };
    access.results.add(
      const ReleaseAccessFailure(ReleaseAccessFailureKind.transport),
    );

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<LaunchAuthorizationUnavailable>(),
    );
  });

  test('transport fallback rejects install removal', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.beforeReturn = () async {
      await database.delete(database.localInstalls).go();
    };
    access.results.add(
      const ReleaseAccessFailure(ReleaseAccessFailureKind.transport),
    );

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<LaunchAuthorizationUnavailable>(),
    );
  });

  test('an older failed request cannot reuse a newer revoke', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.beforeReturn = () async {
      await database
          .update(database.profileLocalGames)
          .write(
            ProfileLocalGamesCompanion(
              authorizationState: const Value('revoked'),
              lastCheckedAt: Value(checkedAt.add(const Duration(seconds: 10))),
            ),
          );
    };
    access.results.add(
      const ReleaseAccessFailure(ReleaseAccessFailureKind.transport),
    );

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<LaunchAuthorizationUnavailable>(),
    );
    expect(
      (await database.select(database.profileLocalGames).get())
          .single
          .authorizationState,
      'revoked',
    );
  });

  test('refresh failure rejects an authority change', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.results.add(
      const ReleaseAccessFailure(
        ReleaseAccessFailureKind.unauthorized,
        bearerError: 'invalid_token',
      ),
    );

    expect(
      await authorizer(
        token: 'old',
        refresh: (_) async {
          await database
              .update(database.localProfiles)
              .write(
                const LocalProfilesCompanion(
                  serverSelectionGeneration: Value(2),
                ),
              );
          return null;
        },
      ).authorize(request()),
      isA<AuthorizationNotGranted>(),
    );
  });

  test('second 401 fallback rejects an authority change', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.beforeReturn = () async {
      if (access.calls != 2) return;
      await database
          .update(database.serverConnections)
          .write(
            const ServerConnectionsCompanion(
              lastKnownOrigin: Value('https://replacement.example'),
            ),
          );
    };
    access.results.addAll(const <ReleaseAccessResult>[
      ReleaseAccessFailure(
        ReleaseAccessFailureKind.unauthorized,
        bearerError: 'invalid_token',
      ),
      ReleaseAccessFailure(
        ReleaseAccessFailureKind.unauthorized,
        bearerError: 'invalid_token',
      ),
    ]);

    expect(
      await authorizer(
        token: 'old',
        refresh: (_) async => 'new',
      ).authorize(request()),
      isA<AuthorizationNotGranted>(),
    );
    expect(access.tokens, <String>['old', 'new']);
  });

  test('401 refreshes once; failure and second 401 preserve cache', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.results.addAll(<ReleaseAccessResult>[
      const ReleaseAccessFailure(
        ReleaseAccessFailureKind.unauthorized,
        bearerError: 'invalid_token',
      ),
      ReleaseAccessSuccess(
        ReleaseAccessAllowed(
          serverInstanceId: _instanceId,
          releaseId: _releaseId,
          titleId: _titleId,
        ),
      ),
    ]);
    expect(
      await authorizer(
        token: 'old',
        refresh: (_) async => 'new',
      ).authorize(request()),
      isA<LaunchAuthorized>(),
    );
    expect(access.tokens, <String>['old', 'new']);

    await database
        .update(database.profileLocalGames)
        .write(
          ProfileLocalGamesCompanion(
            lastCheckedAt: Value(checkedAt.add(const Duration(seconds: 2))),
          ),
        );
    access
      ..tokens.clear()
      ..results.addAll(const <ReleaseAccessResult>[
        ReleaseAccessFailure(
          ReleaseAccessFailureKind.unauthorized,
          bearerError: 'invalid_token',
        ),
        ReleaseAccessFailure(
          ReleaseAccessFailureKind.unauthorized,
          bearerError: 'invalid_token',
        ),
      ]);
    expect(
      await authorizer(
        token: 'old-2',
        refresh: (_) async => 'new-2',
      ).authorize(request()),
      isA<LaunchAuthorized>(),
    );
    expect(access.tokens, <String>['old-2', 'new-2']);
  });

  test('allow loses a CAS race while revoke still blocks', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    access.beforeReturn = () async {
      await database
          .update(database.profileLocalGames)
          .write(
            ProfileLocalGamesCompanion(
              lastCheckedAt: Value(checkedAt.add(const Duration(seconds: 10))),
            ),
          );
      access.beforeReturn = null;
    };
    access.results.add(
      ReleaseAccessSuccess(
        ReleaseAccessAllowed(
          serverInstanceId: _instanceId,
          releaseId: _releaseId,
          titleId: _titleId,
        ),
      ),
    );
    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<LaunchAuthorizationUnavailable>(),
    );

    access.beforeReturn = () async {
      await database
          .update(database.profileLocalGames)
          .write(
            ProfileLocalGamesCompanion(
              lastCheckedAt: Value(checkedAt.add(const Duration(seconds: 20))),
            ),
          );
      access.beforeReturn = null;
    };
    access.results.add(
      ReleaseAccessSuccess(
        ReleaseAccessRevoked(
          serverInstanceId: _instanceId,
          releaseId: _releaseId,
        ),
      ),
    );
    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<AuthorizationRevoked>(),
    );
  });

  test('wrong authority and corrupt install block before access', () async {
    await seed(
      authorization: ProfileGameAuthorization.authorized,
      contentRoot: '${base.path}/outside',
    );
    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<LaunchAuthorizationUnavailable>(),
    );
    expect(access.calls, 0);

    authority = InstallAuthorityContext(
      localProfileId: 'profile-1',
      connection: authority.connection,
      generation: 2,
    );
    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<AuthorizationNotGranted>(),
    );
    expect(access.calls, 0);
  });

  test('manifest fingerprint drift blocks before access', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    await database
        .update(database.localInstalls)
        .write(
          const LocalInstallsCompanion(
            manifestFingerprint: Value('tampered-fingerprint'),
          ),
        );

    expect(
      await authorizer(token: 'token').authorize(request()),
      isA<LaunchAuthorizationUnavailable>(),
    );
    expect(access.calls, 0);
  });

  test('close aborts future authorization work', () async {
    await seed(authorization: ProfileGameAuthorization.authorized);
    final subject = authorizer(token: 'token');
    subject.close();
    expect(access.closed, isTrue);
    expect(await subject.authorize(request()), isA<AuthorizationNotGranted>());
    expect(access.calls, 0);
  });
}
