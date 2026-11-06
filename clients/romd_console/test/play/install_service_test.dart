import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart' as crypto;
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
import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/rom_delivery_format.dart';
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
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/emulator/data/registry/drift_runtime_override_rule_repository.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';

import '../support/release_manifest_wire.dart';

const _instance = '11111111-1111-4111-8111-111111111111';
const _helloSha =
    '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824';

/// SHA-256 of the 4-byte CHD fixture payloads `chd!` and `chd2`.
const _chdSha =
    '14eeccfc133a4e35cf55f2d5126485793fe264fb015aa2661e279b7feac33685';
const _chd2Sha =
    '95fc257bf8373650b215a562fc23ad6bd56254ed0fa0d5653caed550da683e48';
const _xSha =
    '2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881';

/// A hypothetical second PS2 runtime that cannot open delivered CHD.
const _otherPs2 = RuntimeProfile(
  id: RuntimeProfileId('other:ps2:standalone'),
  adapterId: RuntimeAdapterId('other'),
  displayName: 'Other PS2',
  supportedPlatforms: <String>{'ps2'},
  requirements: <RuntimeDependencyRequirement>[],
);

/// The launch path's resolver over shipped profiles plus [_otherPs2], counting
/// how often install consults it.
final class _CountingResolver implements RuntimeResolver {
  _CountingResolver(this._inner);

  final RuntimeResolver _inner;
  final identities = <PlayIdentity>[];

  @override
  Future<RuntimeCandidates> resolve(PlayIdentity identity) {
    identities.add(identity);
    return _inner.resolve(identity);
  }
}

final class _ThrowingResolver implements RuntimeResolver {
  @override
  Future<RuntimeCandidates> resolve(PlayIdentity identity) =>
      Future<RuntimeCandidates>.error(StateError('rules unreadable'));
}

/// Serves one raw consumer-host manifest body to the real HTTP parser.
final class _WireManifestServer {
  _WireManifestServer._(this._server, this.client);

  final HttpServer _server;
  final HttpReleaseManifestApiClient client;
  String body = '';
  int calls = 0;

  static Future<_WireManifestServer> start() async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    late final _WireManifestServer wire;
    server.listen((request) async {
      wire.calls++;
      await request.drain<void>();
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(wire.body);
      await request.response.close();
    });
    wire = _WireManifestServer._(
      server,
      HttpReleaseManifestApiClient(
        consumerApiOrigin: Uri.parse(
          'http://${server.address.host}:${server.port}',
        ),
      ),
    );
    return wire;
  }

  Future<void> close() async {
    client.close();
    await _server.close(force: true);
  }
}

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
  bool expireAlways = false;
  final urls = <Uri>[];

  /// Bytes served for a grant; Original fixtures download `hello`.
  String Function(Uri url) contentFor = (_) => 'hello';
  Future<void> Function()? onExpire;

  @override
  Stream<int> download({
    required Uri url,
    required File destination,
    int? maximumBytes,
  }) async* {
    calls++;
    urls.add(url);
    if (expireAlways || (expireOnce && calls == 1)) {
      await onExpire?.call();
      throw const DownloadGrantExpired();
    }
    if (fail) throw const DownloadNetworkError();
    final contents = contentFor(url);
    await destination.parent.create(recursive: true);
    await destination.writeAsString(contents);
    yield contents.length;
  }
}

final class _ControlledDownload implements DownloadClient {
  _ControlledDownload(this.progress);
  final Stream<int> progress;

  @override
  Stream<int> download({
    required Uri url,
    required File destination,
    int? maximumBytes,
  }) =>
      progress;
}

final class _GatedPathProbe implements TargetFilesystemPathProbe {
  _GatedPathProbe(this.entered, this.release);
  final Completer<void> entered;
  final Future<void> release;

  @override
  Future<bool> pathsAreDistinct({
    required Directory scratch,
    required Iterable<String> relativePaths,
    required File Function(Directory root, String relativePath) resolveWithin,
  }) async {
    entered.complete();
    await release;
    return true;
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
  Future<bool> replace({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String expected,
    required String refreshToken,
  }) async => false;

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

  ServerBoundReleaseManifest manifest({
    String relativePath = 'chrono.sfc',
    Uri? grantUrl,
  }) => ServerBoundReleaseManifest(
    isDeliveryComplete: true,

    serverInstanceId: _serverId,
    releaseId: _releaseId,
    titleId: _titleId,
    systemKey: 'snes',
    name: 'Chrono Trigger',
    revision: null,
    isComplete: true,
    runtime: ServerBoundReleaseRuntime(
      contentType: 'single_rom',
      launch: ServerBoundLaunchTarget(type: 'file', relativePath: relativePath),
      packaging: 'direct_files',
      minimumInstallBytes: 5,
    ),
    items: <ServerBoundReleaseManifestItem>[
      ServerBoundReleaseManifestItem(
        provenance: ContentFileProvenance.original,
        formats: [
          ServerBoundReleaseManifestFormat(
            format: RomDeliveryFormat.original,
            state: RomDeliveryState.available,
            relativePath: relativePath,
            profileVersion: 'identity/v1',
            runtimeApplicability: const [],
            filename: (relativePath).split('/').last,
            sizeBytes: 5,
            sha256: _helloSha,
            contentGrant: ServerBoundContentGrant(
              downloadUrl:
                  grantUrl ??
                  Uri.parse('https://library.example/delivery/content/a.b'),
              expiresAt: now.add(const Duration(minutes: 5)),
            ),
          ),
        ],

        relativePath: relativePath,
        role: 'rom',
        sizeBytes: 5,
        sha256: _helloSha,
        isAvailable: true,
        contentGrant: ServerBoundContentGrant(
          downloadUrl:
              grantUrl ??
              Uri.parse('https://library.example/delivery/content/a.b'),
          expiresAt: now.add(const Duration(minutes: 5)),
        ),
      ),
    ],
  );

  ContentFileStore fileStore() =>
      ContentFileStore(baseDir: base, serverInstanceId: _instance);

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
    RuntimeResolver? runtimeResolver,
    ReleaseManifestApiClient? manifestClient,
    DownloadClient? downloadClient,
    ProfileLocalLibraryRepository? profileLocalLibrary,
    Future<void> Function(AcquisitionCheckpoint checkpoint)? checkpoint,
  }) => RomdInstallService(
    releaseAccessApiClient: access,
    releaseManifestApiClient: manifestClient ?? manifests,
    runtimeResolver: runtimeResolver,

    database: database,
    authority: neutralAuthority
        ? null
        : InstallAuthorityContext(
            localProfileId: profileId,
            connection: connection,
            generation: generation,
          ),
    authenticatedSession: authenticatedSession,
    downloadClient: downloadClient ?? downloads,
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

  const generatedCue =
      'FILE "track.bin" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00\n';
  ServerBoundReleaseManifest cdManifest(ContentFileProvenance cueSource) {
    final cueHash = crypto.sha256.convert(utf8.encode(generatedCue)).toString();
    ServerBoundContentGrant grant(String route, String token) =>
        ServerBoundContentGrant(
          downloadUrl: Uri.parse(
            'https://library.example/delivery/$route/$token.signature',
          ),
          expiresAt: now.add(const Duration(minutes: 5)),
        );
    final cueGrant = grant(
      cueSource == ContentFileProvenance.derived
          ? 'derived-descriptor'
          : 'content',
      'cue',
    );
    return ServerBoundReleaseManifest(
      serverInstanceId: _serverId,
      releaseId: _releaseId,
      titleId: _titleId,
      systemKey: 'psx',
      name: 'Synthetic CD',
      revision: null,
      isComplete: cueSource == ContentFileProvenance.original,
      isDeliveryComplete: true,
      runtime: ServerBoundReleaseRuntime(
        contentType: 'unknown',
        launch: const ServerBoundLaunchTarget(
          type: 'file',
          relativePath: 'disc.cue',
        ),
        packaging: 'direct_files',
        minimumInstallBytes: utf8.encode(generatedCue).length + 5,
      ),
      items: [
        ServerBoundReleaseManifestItem(
          relativePath: 'disc.cue',
          role: 'rom',
          provenance: cueSource,
          sizeBytes: utf8.encode(generatedCue).length,
          sha256: cueHash,
          isAvailable: true,
          contentGrant: cueGrant,
          formats: cueSource == ContentFileProvenance.derived
              ? []
              : [
                  ServerBoundReleaseManifestFormat(
                    format: RomDeliveryFormat.original,
                    state: RomDeliveryState.available,
                    relativePath: 'disc.cue',
                    profileVersion: 'identity/v1',
                    runtimeApplicability: [],
                    filename: 'disc.cue',
                    sizeBytes: utf8.encode(generatedCue).length,
                    sha256: cueHash,
                    contentGrant: cueGrant,
                  ),
                ],
        ),
        ServerBoundReleaseManifestItem(
          relativePath: 'track.bin',
          role: 'rom',
          provenance: ContentFileProvenance.original,
          sizeBytes: 5,
          sha256: _helloSha,
          isAvailable: true,
          contentGrant: grant('content', 'bin'),
          formats: [
            ServerBoundReleaseManifestFormat(
              format: RomDeliveryFormat.original,
              state: RomDeliveryState.available,
              relativePath: 'track.bin',
              profileVersion: 'identity/v1',
              runtimeApplicability: [],
              filename: 'track.bin',
              sizeBytes: 5,
              sha256: _helloSha,
              contentGrant: grant('content', 'bin'),
            ),
          ],
        ),
      ],
    );
  }

  PlayTarget cdTarget() => PlayTarget(
    releaseId: _releaseId.value,
    titleId: _titleId.value,
    displayName: 'Synthetic CD',
    localProfileId: 'profile-1',
    platformId: 'psx',
    platformName: 'PlayStation',
  );
  test(
    'generated CUE and original BIN install atomically and persist their distinct provenance',
    () async {
      manifests.result = ReleaseManifestSuccess(
        cdManifest(ContentFileProvenance.derived),
      );
      downloads.contentFor = (url) =>
          url.path.contains('cue.signature') ? generatedCue : 'hello';
      final events = await service()
          .install(cdTarget(), accessToken: 'token', operation: _operation)
          .toList();
      expect(events.last, isA<InstallCompleted>());
      final stored = (await installs.findByReleaseId(_releaseId.value))!;
      expect(stored.items.map((item) => item.provenance), [
        ContentFileProvenance.derived,
        ContentFileProvenance.original,
      ]);
      expect(
        stored.items.first.sha256,
        crypto.sha256.convert(utf8.encode(generatedCue)).toString(),
      );
      expect(stored.items.first.sizeBytes, utf8.encode(generatedCue).length);
      expect(
        await File('${stored.contentRoot}/disc.cue').readAsString(),
        generatedCue,
      );
      expect(
        await File('${stored.contentRoot}/track.bin').readAsString(),
        'hello',
      );
      final row = (await database.select(database.localInstalls).get()).single;
      expect(row.manifestSnapshot, contains('Derived'));
      expect(row.manifestSnapshot, isNot(contains('contentGrant')));
      expect(row.manifestSnapshot, isNot(contains('signature')));
      expect(
        await database.select(database.profileLocalGames).get(),
        hasLength(1),
      );
    },
  );
  test(
    'grant renewal never relabels captured generated bytes as an arriving original',
    () async {
      manifests.result = ReleaseManifestSuccess(
        cdManifest(ContentFileProvenance.derived),
      );
      downloads
        ..expireOnce = true
        ..onExpire = () async {
          manifests.result = ReleaseManifestSuccess(
            cdManifest(ContentFileProvenance.original),
          );
        };
      final events = await service()
          .install(cdTarget(), accessToken: 'token', operation: _operation)
          .toList();
      expect(
        (events.last as InstallFailed).kind,
        InstallFailureKind.contentUnavailable,
      );
      expect(downloads.calls, 1);
      expect(await database.select(database.localInstalls).get(), isEmpty);
      expect(await database.select(database.profileLocalGames).get(), isEmpty);
      expect(
        fileStore()
            .stagingRoot(
              platformShortName: 'psx',
              titleId: _titleId.value,
              releaseId: _releaseId.value,
            )
            .existsSync(),
        isFalse,
      );
    },
  );

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

  for (final outcome in <String>['recover', 'expire', 'exhaust']) {
    test(
      'HTTP 503 install $outcome preserves earlier items during retries',
      () async {
        final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
        addTearDown(() => server.close(force: true));
        final origin = Uri.parse(
          'http://${server.address.host}:${server.port}',
        );
        final calls = <String, int>{};
        final earlierItemPresent = <bool>[];

        ServerBoundReleaseManifest httpManifest(String prefix) {
          final template = manifest();
          return ServerBoundReleaseManifest(
            isDeliveryComplete: true,
            serverInstanceId: template.serverInstanceId,
            releaseId: template.releaseId,
            titleId: template.titleId,
            systemKey: template.systemKey,
            name: template.name,
            revision: null,
            isComplete: true,
            runtime: const ServerBoundReleaseRuntime(
              contentType: 'single_rom',
              launch: ServerBoundLaunchTarget(
                type: 'file',
                relativePath: 'chrono.sfc',
              ),
              packaging: 'direct_files',
              minimumInstallBytes: 10,
            ),
            items: [
              for (final path in ['chrono.sfc', 'extra.bin'])
                ServerBoundReleaseManifestItem(
                  provenance: ContentFileProvenance.original,
                  relativePath: path,
                  role: 'rom',
                  sizeBytes: 5,
                  sha256: _helloSha,
                  isAvailable: true,
                  contentGrant: ServerBoundContentGrant(
                    downloadUrl: origin.resolve(
                      '/delivery/content/$prefix-$path',
                    ),
                    expiresAt: now.add(const Duration(minutes: 5)),
                  ),
                  formats: [
                    ServerBoundReleaseManifestFormat(
                      format: RomDeliveryFormat.original,
                      state: RomDeliveryState.available,
                      relativePath: path,
                      profileVersion: 'identity/v1',
                      runtimeApplicability: const [],
                      filename: path,
                      sizeBytes: 5,
                      sha256: _helloSha,
                      contentGrant: ServerBoundContentGrant(
                        downloadUrl: origin.resolve(
                          '/delivery/content/$prefix-$path',
                        ),
                        expiresAt: now.add(const Duration(minutes: 5)),
                      ),
                    ),
                  ],
                ),
            ],
          );
        }

        manifests.result = ReleaseManifestSuccess(httpManifest('initial'));
        server.listen((request) async {
          final path = request.uri.path;
          final count = calls.update(
            path,
            (value) => value + 1,
            ifAbsent: () => 1,
          );
          if (path.endsWith('initial-extra.bin')) {
            final earlier = base
                .listSync(recursive: true)
                .whereType<File>()
                .where((file) => file.path.endsWith('/chrono.sfc'))
                .toList();
            earlierItemPresent.add(
              earlier.length == 1 &&
                  earlier.single.readAsStringSync() == 'hello',
            );
            if (count == 1 || outcome == 'exhaust') {
              request.response
                ..statusCode = HttpStatus.serviceUnavailable
                ..headers.set(HttpHeaders.retryAfterHeader, '0');
              await request.response.close();
              return;
            }
            if (outcome == 'expire') {
              manifests.result = ReleaseManifestSuccess(httpManifest('fresh'));
              request.response.statusCode = HttpStatus.notFound;
              await request.response.close();
              return;
            }
          }
          request.response.write('hello');
          await request.response.close();
        });
        final httpDownloads = HttpDownloadClient();
        addTearDown(httpDownloads.close);

        final events = await service(
          downloadClient: httpDownloads,
        ).install(target, accessToken: 'token', operation: _operation).toList();

        expect(earlierItemPresent, isNotEmpty);
        expect(earlierItemPresent, everyElement(isTrue));
        expect(calls['/delivery/content/initial-chrono.sfc'], 1);
        expect(
          calls['/delivery/content/initial-extra.bin'],
          outcome == 'exhaust' ? 4 : 2,
        );
        if (outcome == 'exhaust') {
          expect(
            (events.last as InstallFailed).kind,
            InstallFailureKind.downloadFailed,
          );
          expect(await database.select(database.localInstalls).get(), isEmpty);
          expect(
            await database.select(database.profileLocalGames).get(),
            isEmpty,
          );
          expect(base.listSync(recursive: true).whereType<File>(), isEmpty);
        } else {
          final installed = (events.last as InstallCompleted).resolved;
          expect(
            await File(installed.launchAbsolutePath).readAsString(),
            'hello',
          );
          expect(
            await File(
              '${File(installed.launchAbsolutePath).parent.path}/extra.bin',
            ).readAsString(),
            'hello',
          );
          expect(manifests.calls, outcome == 'expire' ? 2 : 1);
          if (outcome == 'expire') {
            expect(calls['/delivery/content/fresh-chrono.sfc'], 1);
            expect(calls['/delivery/content/fresh-extra.bin'], 1);
          }
        }
      },
    );
  }

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

  test(
    'cancelling install during 503 backoff cancels download and releases mutation',
    () async {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      addTearDown(() => server.close(force: true));
      var requests = 0;
      server.listen((request) async {
        requests++;
        if (requests == 1) {
          request.response
            ..statusCode = HttpStatus.serviceUnavailable
            ..headers.set(HttpHeaders.retryAfterHeader, '15');
        } else {
          // Also lets a failing regression test finish cleanup after releasing its wait.
          request.response.write('hello');
        }
        await request.response.close();
      });
      manifests.result = ReleaseManifestSuccess(
        manifest(
          grantUrl: Uri.parse(
            'http://${server.address.host}:${server.port}/delivery/content/test',
          ),
        ),
      );
      final waiting = Completer<void>();
      late Timer retryTimer;
      late void Function() releaseWait;
      final httpDownloads = HttpDownloadClient(
        createRetryTimer: (delay, callback) {
          expect(delay, const Duration(seconds: 15));
          releaseWait = callback;
          retryTimer = Timer(delay, callback);
          waiting.complete();
          return retryTimer;
        },
      );
      addTearDown(httpDownloads.close);
      final events = <InstallProgress>[];
      final subscription = service(downloadClient: httpDownloads)
          .install(target, accessToken: 'token', operation: _operation)
          .listen(events.add);
      await waiting.future.timeout(const Duration(seconds: 5));
      final staging = fileStore().stagingRoot(
        platformShortName: 'snes',
        titleId: _titleId.value,
        releaseId: _releaseId.value,
      );
      expect(staging.existsSync(), isTrue);
      var mutationAcquired = false;
      final nextMutation = mutationSerializer
          .acquire(
            InstallMutationKey(
              serverInstanceId: _serverId,
              releaseId: _releaseId,
            ),
          )
          .then((lease) {
            mutationAcquired = true;
            return lease;
          });
      expect(mutationAcquired, isFalse);
      final cancelled = subscription.cancel();
      try {
        await cancelled.timeout(const Duration(seconds: 1));
        await nextMutation.timeout(const Duration(seconds: 1));
        expect(retryTimer.isActive, isFalse);
        expect(requests, 1);
        expect(staging.existsSync(), isFalse);
        expect(events.whereType<InstallCompleted>(), isEmpty);
        expect(await database.select(database.localInstalls).get(), isEmpty);
        expect(
          await database.select(database.profileLocalGames).get(),
          isEmpty,
        );
      } finally {
        if (retryTimer.isActive) {
          retryTimer.cancel();
          releaseWait();
        }
        await cancelled.timeout(const Duration(seconds: 5));
        (await nextMutation.timeout(const Duration(seconds: 5))).release();
      }
    },
  );

  test(
    'install cancellation retains staging and mutation until download cleanup finishes',
    () async {
      final started = Completer<void>();
      final cleanupStarted = Completer<void>();
      final finishCleanup = Completer<void>();
      var downloadCleaned = false;
      final source = StreamController<int>(
        onListen: started.complete,
        onCancel: () async {
          cleanupStarted.complete();
          await finishCleanup.future;
          downloadCleaned = true;
        },
      );
      addTearDown(source.close);
      final subscription =
          service(downloadClient: _ControlledDownload(source.stream))
              .install(target, accessToken: 'token', operation: _operation)
              .listen((_) {});
      await started.future;
      final staging = fileStore().stagingRoot(
        platformShortName: 'snes',
        titleId: _titleId.value,
        releaseId: _releaseId.value,
      );
      var mutationAcquired = false;
      final nextMutation = mutationSerializer
          .acquire(
            InstallMutationKey(
              serverInstanceId: _serverId,
              releaseId: _releaseId,
            ),
          )
          .then((lease) {
            mutationAcquired = true;
            return lease;
          });
      final cancelled = subscription.cancel();
      try {
        await cleanupStarted.future;
        // Upstream cleanup remains gated while filesystem continuations can run.
        await expectLater(
          nextMutation.timeout(const Duration(seconds: 1)),
          throwsA(isA<TimeoutException>()),
        );
        expect(downloadCleaned, isFalse);
        expect(staging.existsSync(), isTrue);
        expect(mutationAcquired, isFalse);
      } finally {
        finishCleanup.complete();
        await cancelled.timeout(const Duration(seconds: 1));
        (await nextMutation.timeout(const Duration(seconds: 1))).release();
      }
      expect(downloadCleaned, isTrue);
      expect(staging.existsSync(), isFalse);
      expect(await database.select(database.localInstalls).get(), isEmpty);
      expect(await database.select(database.profileLocalGames).get(), isEmpty);
    },
  );

  test('install cancelled during preflight starts no download', () async {
    final entered = Completer<void>();
    final release = Completer<void>();
    final subscription =
        service(pathProbe: _GatedPathProbe(entered, release.future))
            .install(target, accessToken: 'token', operation: _operation)
            .listen((_) {});
    await entered.future;

    final cancelled = subscription.cancel();
    release.complete();
    await cancelled.timeout(const Duration(seconds: 1));

    expect(downloads.calls, 0);
    expect(await database.select(database.localInstalls).get(), isEmpty);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
    expect(base.listSync(recursive: true).whereType<File>(), isEmpty);
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
        'provenance': 'Original',

        'relativePath': 'chrono.sfc',
        'sizeBytes': 5,
        'sha256': _helloSha,
      },
      <String, Object?>{
        'provenance': 'Original',

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

  test(
    'oversized download cancels staging and preserves an existing install',
    () async {
      final original = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();
      final resolved = (original.last as InstallCompleted).resolved;
      final originalRow =
          (await database.select(database.localInstalls).get()).single;
      manifests.result = ReleaseManifestSuccess(
        manifest(relativePath: 'replacement.sfc'),
      );
      downloads.contentFor = (_) => 'too large';

      final events = await service()
          .install(target, accessToken: 'token', operation: _operation)
          .toList();

      expect(
        (events.last as InstallFailed).kind,
        InstallFailureKind.downloadFailed,
      );
      expect(
        (await database.select(database.localInstalls).get()).single,
        originalRow,
      );
      expect(await File(resolved.launchAbsolutePath).readAsString(), 'hello');
      expect(
        base
            .listSync(recursive: true)
            .whereType<File>()
            .any((file) => file.path.endsWith('replacement.sfc')),
        isFalse,
      );
      expect(
        (await database.select(database.profileLocalGames).get())
            .single
            .authorizationState,
        'authorized',
      );
    },
  );

  for (final checkpoint in <AcquisitionCheckpoint>[
    AcquisitionCheckpoint.beforeDeactivation,
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

  group('runtime-driven delivery format', () {
    final ps2Target = PlayTarget(
      releaseId: _releaseId.value,
      titleId: _titleId.value,
      displayName: 'Disc Game',
      localProfileId: 'profile-1',
      platformId: 'ps2',
      platformName: 'PlayStation 2',
    );
    late _CountingResolver resolver;
    late DriftRuntimeOverrideRuleRepository rules;

    ServerBoundReleaseManifest ps2Manifest({
      String chdPath = 'disc/game.chd',
      String chdToken = 'chd-v1',
      String chdSha256 = _chdSha,
      RomDeliveryState chdState = RomDeliveryState.available,
      String? chdMd5 = 'cccccccccccccccccccccccccccccccc',
    }) => ServerBoundReleaseManifest(
      isDeliveryComplete: true,

      serverInstanceId: _serverId,
      releaseId: _releaseId,
      titleId: _titleId,
      systemKey: 'ps2',
      name: 'Disc Game',
      revision: null,
      isComplete: true,
      runtime: const ServerBoundReleaseRuntime(
        contentType: 'single_rom',
        launch: ServerBoundLaunchTarget(
          type: 'file',
          relativePath: 'disc/game.iso',
        ),
        packaging: 'direct_files',
        minimumInstallBytes: 5,
      ),
      items: <ServerBoundReleaseManifestItem>[
        ServerBoundReleaseManifestItem(
          provenance: ContentFileProvenance.original,

          relativePath: 'disc/game.iso',
          role: 'rom',
          sizeBytes: 5,
          sha256: _helloSha,
          isAvailable: true,
          contentGrant: ServerBoundContentGrant(
            downloadUrl: Uri.parse(
              'https://library.example/delivery/content/iso.sig',
            ),
            expiresAt: now.add(const Duration(minutes: 5)),
          ),
          formats: <ServerBoundReleaseManifestFormat>[
            ServerBoundReleaseManifestFormat(
              format: RomDeliveryFormat.chd,
              state: chdState,
              relativePath: chdPath,
              filename: chdPath.split('/').last,
              profileVersion: 'chd-observed/v1',
              runtimeApplicability: const <String>['pcsx2'],
              sizeBytes: 4,
              sha256: chdSha256,
              md5: chdMd5,
              sha1: 'dddddddddddddddddddddddddddddddddddddddd',
              contentGrant: ServerBoundContentGrant(
                downloadUrl: Uri.parse(
                  'https://library.example/delivery/content/$chdToken.sig',
                ),
                expiresAt: now.add(const Duration(minutes: 5)),
              ),
              runtime: ServerBoundReleaseRuntime(
                contentType: 'single_rom',
                launch: ServerBoundLaunchTarget(
                  type: 'file',
                  relativePath: chdPath,
                ),
                packaging: 'direct_files',
                minimumInstallBytes: 4,
              ),
            ),
          ],
        ),
      ],
    );

    String chdOrHello(Uri url) => url.path.contains('/chd') ? 'chd!' : 'hello';

    Future<void> prefer(RuntimeProfile profile) => rules.setRule(
      scope: RuntimeOverrideScope.platform,
      scopeValue: 'ps2',
      profileId: profile.id,
    );

    Future<List<InstallProgress>> installPs2({
      ReleaseManifestApiClient? manifestClient,
    }) => service(
      runtimeResolver: resolver,
      manifestClient: manifestClient,
    ).install(ps2Target, accessToken: 'token', operation: _operation).toList();

    Future<LocalInstall> stored() async =>
        (await installs.findByReleaseId(_releaseId.value))!;

    setUp(() {
      rules = DriftRuntimeOverrideRuleRepository(database: database);
      resolver = _CountingResolver(
        LocalRuntimeResolver(
          profiles: <RuntimeProfile>[...BuiltinRuntimeProfiles.all, _otherPs2],
          rules: rules,
        ),
      );
      manifests.result = ReleaseManifestSuccess(ps2Manifest());
      downloads.contentFor = chdOrHello;
    });

    test(
      'PCSX2 default installs the CHD once with both identities kept',
      () async {
        final events = await installPs2();

        expect(events.last, isA<InstallCompleted>());
        final resolved = (events.last as InstallCompleted).resolved;
        expect(resolved.deliveryFormat, RomDeliveryFormat.chd);
        expect(resolved.launchAbsolutePath, endsWith('/disc/game.chd'));
        expect(await File(resolved.launchAbsolutePath).readAsString(), 'chd!');
        expect(
          File('${resolved.contentRoot}/disc/game.iso').existsSync(),
          isFalse,
        );
        expect(downloads.urls.map((url) => url.path), <String>[
          '/delivery/content/chd-v1.sig',
        ]);
        expect(resolver.identities, hasLength(1));
        expect(resolver.identities.single.platformShortName, 'ps2');
        expect(resolver.identities.single.titleId, _titleId.value);
        expect(resolver.identities.single.releaseId, _releaseId.value);

        final install = await stored();
        expect(install.deliveryFormat, RomDeliveryFormat.chd);
        expect(install.launchRelativePath, 'disc/game.chd');
        expect(install.sizeBytes, 4);
        expect(install.primarySha256, _chdSha);
        expect(install.items.single.sha256, _chdSha);
        expect(install.originalLaunchRelativePath, 'disc/game.iso');
        expect(install.originalItems.single.relativePath, 'disc/game.iso');
        expect(install.originalItems.single.sizeBytes, 5);
        expect(install.originalItems.single.sha256, _helloSha);
        expect(
          install.manifestFingerprint,
          const ContentVerifier().manifestFingerprint(const <FingerprintItem>[
            (
              provenance: ContentFileProvenance.original,
              relativePath: 'disc/game.chd',
              sizeBytes: 4,
              sha256: _chdSha,
            ),
          ]),
        );

        final relaunch = await resolveInstalled(
          service(runtimeResolver: resolver),
          localProfileId: 'profile-1',
        );
        expect(relaunch?.deliveryFormat, RomDeliveryFormat.chd);
        expect(relaunch?.launchAbsolutePath, resolved.launchAbsolutePath);
      },
    );

    test('a non-PCSX2 preference installs the Original', () async {
      await prefer(_otherPs2);

      final events = await installPs2();

      expect(events.last, isA<InstallCompleted>());
      final resolved = (events.last as InstallCompleted).resolved;
      expect(resolved.deliveryFormat, RomDeliveryFormat.original);
      expect(await File(resolved.launchAbsolutePath).readAsString(), 'hello');
      expect(downloads.urls.single.path, '/delivery/content/iso.sig');
      final install = await stored();
      expect(install.deliveryFormat, RomDeliveryFormat.original);
      expect(install.originalIdentity, isNull);
      expect(install.launchRelativePath, 'disc/game.iso');
      final row = (await database.select(database.localInstalls).get()).single;
      expect(row.deliveryFormat, 'original');
      expect(row.originalManifestSnapshot, isNull);
      expect(row.originalLaunchRelativePath, isNull);
    });

    test('without a composed resolver the install is Original', () async {
      final events = await service()
          .install(ps2Target, accessToken: 'token', operation: _operation)
          .toList();
      expect(
        (events.last as InstallCompleted).resolved.deliveryFormat,
        RomDeliveryFormat.original,
      );
      expect(downloads.urls.single.path, '/delivery/content/iso.sig');
    });

    test(
      'a manifest without available CHD never consults the resolver',
      () async {
        manifests.result = ReleaseManifestSuccess(manifest());
        final events = await service(
          runtimeResolver: resolver,
        ).install(target, accessToken: 'token', operation: _operation).toList();
        expect(events.last, isA<InstallCompleted>());
        expect(resolver.identities, isEmpty);

        manifests.result = ReleaseManifestSuccess(
          ps2Manifest(chdState: RomDeliveryState.preparing),
        );
        final ps2 = await installPs2();
        expect(
          (ps2.last as InstallCompleted).resolved.deliveryFormat,
          RomDeliveryFormat.original,
        );
        expect(resolver.identities, isEmpty);
      },
    );

    test('an incoherent CHD falls back to Original before download', () async {
      manifests.result = ReleaseManifestSuccess(ps2Manifest(chdMd5: null));
      final events = await installPs2();
      expect(
        (events.last as InstallCompleted).resolved.deliveryFormat,
        RomDeliveryFormat.original,
      );
      expect(downloads.urls.single.path, '/delivery/content/iso.sig');
    });

    group('raw server manifests through the HTTP parser', () {
      late _WireManifestServer wire;

      setUp(() async {
        wire = await _WireManifestServer.start();
      });

      tearDown(() => wire.close());

      Map<String, Object?> ps2Wire() =>
          jsonDecode(
                serverWireSingleDiscPs2Manifest(
                  serverInstanceId: _instance,
                  releaseId: _releaseId.value,
                  titleId: _titleId.value,
                  logicalSha256: _helloSha,
                  logicalSizeBytes: 5,
                  chdSha256: _chdSha,
                  chdSizeBytes: 4,
                ),
              )!
              as Map<String, Object?>;

      Map<String, Object?> itemOf(Map<String, Object?> value) =>
          (value['items']! as List<Object?>).first! as Map<String, Object?>;

      Map<String, Object?> chdOf(Map<String, Object?> value) =>
          (itemOf(value)['formats']! as List<Object?>).last!
              as Map<String, Object?>;

      test('the server wire shape installs the CHD for PCSX2', () async {
        wire.body = jsonEncode(ps2Wire());
        final events = await installPs2(manifestClient: wire.client);
        expect(
          (events.last as InstallCompleted).resolved.deliveryFormat,
          RomDeliveryFormat.chd,
        );
        expect(downloads.urls.single.path, '/delivery/content/chd.sig');
        expect(wire.calls, 1);
      });

      test(
        'a GameCube wire manifest with format runtimes installs the Original',
        () async {
          Map<String, Object?> runtimeFor(
            String path,
            int size,
          ) => <String, Object?>{
            'contentType': 'single_rom',
            'launch': <String, Object?>{'type': 'file', 'relativePath': path},
            'packaging': 'direct_files',
            'minimumInstallBytes': '$size',
          };
          Map<String, Object?> grant(String token) => <String, Object?>{
            'downloadUrl': '/delivery/content/$token.sig',
            'expiresAt': '2030-01-01T00:15:00+00:00',
          };
          wire.body = jsonEncode(<String, Object?>{
            'isDeliveryComplete': true,

            'serverInstanceId': _instance,
            'releaseId': _releaseId.value,
            'titleId': _titleId.value,
            'systemKey': 'gc',
            'name': 'Disc Game',
            'revision': null,
            'isComplete': true,
            'runtime': runtimeFor('disc/game.iso', 5),
            'items': <Object?>[
              <String, Object?>{
                'provenance': 'Original',

                'relativePath': 'disc/game.iso',
                'role': 'rom',
                'sizeBytes': '5',
                'sha256': _helloSha,
                'isAvailable': true,
                'contentGrant': grant('item'),
                'formats': <Object?>[
                  <String, Object?>{
                    'relativePath': 'disc/game.iso',
                    'runtime': runtimeFor('disc/game.iso', 5),
                    'contentGrant': grant('original'),
                    'format': 'original',
                    'label': 'Original',
                    'state': 'available',
                    'filename': 'game.iso',
                    'sizeBytes': '5',
                    'sha256': _helloSha,
                    'profileVersion': 'identity/v1',
                    'runtimeApplicability': <String>[],
                  },
                  <String, Object?>{
                    'relativePath': 'disc/game.rvz',
                    'runtime': runtimeFor('disc/game.rvz', 4),
                    'contentGrant': grant('rvz'),
                    'format': 'rvz',
                    'label': 'RVZ',
                    'fileExtension': '.rvz',
                    'state': 'available',
                    'filename': 'game.rvz',
                    'sizeBytes': '4',
                    'sha256': _chdSha,
                    'md5': 'c' * 32,
                    'sha1': 'd' * 40,
                    'profileVersion': 'rvz-import/v1',
                    'runtimeApplicability': <String>['dolphin'],
                  },
                ],
              },
            ],
          });
          final gameCube = PlayTarget(
            releaseId: _releaseId.value,
            titleId: _titleId.value,
            displayName: 'Disc Game',
            localProfileId: 'profile-1',
            platformId: 'gc',
            platformName: 'GameCube',
          );

          final events =
              await service(
                    runtimeResolver: resolver,
                    manifestClient: wire.client,
                  )
                  .install(
                    gameCube,
                    accessToken: 'token',
                    operation: _operation,
                  )
                  .toList();

          final resolved = (events.last as InstallCompleted).resolved;
          expect(resolved.deliveryFormat, RomDeliveryFormat.original);
          expect(resolved.launchAbsolutePath, endsWith('/disc/game.iso'));
          expect(downloads.urls.map((url) => url.path), <String>[
            '/delivery/content/item.sig',
          ]);
          expect(resolver.identities, isEmpty);
          expect(wire.calls, 1);
        },
      );

      final fallbacks = <String, void Function(Map<String, Object?> value)>{
        'multi-file PS2 with a null CHD runtime': (value) {
          final runtime = value['runtime']! as Map<String, Object?>;
          runtime
            ..['contentType'] = 'unknown'
            ..['minimumInstallBytes'] = '6';
          for (final format
              in (itemOf(value)['formats']! as List<Object?>)
                  .cast<Map<String, Object?>>()) {
            format['runtime'] = null;
          }
          (value['items']! as List<Object?>).add(<String, Object?>{
            'provenance': 'Original',

            'relativePath': 'disc/extra.bin',
            'role': 'rom',
            'sizeBytes': '1',
            'sha256': _xSha,
            'isAvailable': true,
            'contentGrant': <String, Object?>{
              'downloadUrl': '/delivery/content/extra.sig',
              'expiresAt': '2030-01-01T00:15:00+00:00',
            },
            'formats': <Object?>[
              <String, Object?>{
                'relativePath': 'disc/extra.bin',
                'runtime': null,
                'contentGrant': <String, Object?>{
                  'downloadUrl': '/delivery/content/extra-original.sig',
                  'expiresAt': '2030-01-01T00:15:00+00:00',
                },
                'format': 'original',
                'label': 'Original',
                'state': 'available',
                'filename': 'extra.bin',
                'sizeBytes': '1',
                'sha256': _xSha,
                'profileVersion': 'identity/v1',
                'runtimeApplicability': <String>[],
              },
            ],
          });
        },
        'missing CHD digests': (value) => chdOf(value)
          ..remove('md5')
          ..remove('sha1'),
        'a CHD runtime launch path mismatch': (value) =>
            ((chdOf(value)['runtime']! as Map<String, Object?>)['launch']!
                    as Map<String, Object?>)['relativePath'] =
                'disc/other.chd',
      };
      for (final entry in fallbacks.entries) {
        test('${entry.key} still installs the Original', () async {
          final value = ps2Wire();
          entry.value(value);
          wire.body = jsonEncode(value);
          downloads.contentFor = (url) =>
              url.path.contains('extra') ? 'x' : chdOrHello(url);
          final events = await installPs2(manifestClient: wire.client);
          expect(
            (events.last as InstallCompleted).resolved.deliveryFormat,
            RomDeliveryFormat.original,
          );
          expect(
            downloads.urls.map((url) => url.path),
            isNot(contains('/delivery/content/chd.sig')),
          );
          expect(downloads.urls.first.path, '/delivery/content/item.sig');
          expect((await stored()).launchRelativePath, 'disc/game.iso');
        });
      }
    });

    test(
      'a CHD hash mismatch fails without falling back to Original',
      () async {
        expect((await installPs2()).last, isA<InstallCompleted>());
        final previous = await stored();
        expect(previous.deliveryFormat, RomDeliveryFormat.chd);
        manifests.result = ReleaseManifestSuccess(
          ps2Manifest(chdToken: 'chd-v2', chdSha256: _chd2Sha),
        );
        downloads
          ..urls.clear()
          ..calls = 0
          ..contentFor = (url) => url.path.contains('/chd') ? 'bad!' : 'hello';

        final events = await installPs2();

        expect(
          (events.last as InstallFailed).kind,
          InstallFailureKind.hashMismatch,
        );
        expect(downloads.calls, 1);
        expect(downloads.urls.single.path, '/delivery/content/chd-v2.sig');
        final kept = await stored();
        expect(kept.deliveryFormat, RomDeliveryFormat.chd);
        expect(kept.primarySha256, _chdSha);
        expect(kept.manifestFingerprint, previous.manifestFingerprint);
        expect(
          await File('${kept.contentRoot}/disc/game.chd').readAsString(),
          'chd!',
        );
        expect(File('${kept.contentRoot}/disc/game.iso').existsSync(), isFalse);
        expect(
          base
              .listSync(recursive: true)
              .whereType<Directory>()
              .where((entry) => entry.path.endsWith('.staging')),
          isEmpty,
        );
      },
    );

    test(
      'an intact CHD install is reused; launch checks size like Original',
      () async {
        expect((await installPs2()).last, isA<InstallCompleted>());
        final first = await stored();

        final reused = await installPs2();
        expect(reused.last, isA<InstallCompleted>());
        expect(downloads.calls, 1);
        expect((await stored()).installedAt, first.installedAt);

        // Same-size tampering is caught by install-time verification, not by
        // the launch resolve (which, as for Original, checks existence/size).
        final chd = File('${first.contentRoot}/disc/game.chd');
        await chd.writeAsString('bad!');
        expect(
          await resolveInstalled(
            service(runtimeResolver: resolver),
            localProfileId: 'profile-1',
          ),
          isNotNull,
        );
        final repaired = await installPs2();
        expect(repaired.last, isA<InstallCompleted>());
        expect(downloads.calls, 2);
        expect(await chd.readAsString(), 'chd!');

        await chd.writeAsString('truncated');
        expect(
          await resolveInstalled(
            service(runtimeResolver: resolver),
            localProfileId: 'profile-1',
          ),
          isNull,
        );
        expect((await stored()).state, InstallState.corrupt);
      },
    );

    test(
      'after the runtime changes, reinstalling replaces CHD with Original',
      () async {
        final chd = ((await installPs2()).last as InstallCompleted).resolved;
        expect(chd.deliveryFormat, RomDeliveryFormat.chd);
        final before = await stored();

        await prefer(_otherPs2);
        final events = await installPs2();

        final iso = (events.last as InstallCompleted).resolved;
        expect(iso.deliveryFormat, RomDeliveryFormat.original);
        expect(downloads.urls.last.path, '/delivery/content/iso.sig');
        expect(iso.contentRoot, chd.contentRoot);
        expect(iso.saveRoot, chd.saveRoot);
        expect(iso.stateRoot, chd.stateRoot);
        expect(iso.configRoot, chd.configRoot);
        expect(File(chd.launchAbsolutePath).existsSync(), isFalse);
        expect(await File(iso.launchAbsolutePath).readAsString(), 'hello');
        final after = await stored();
        expect(after.deliveryFormat, RomDeliveryFormat.original);
        expect(after.originalIdentity, isNull);
        expect(after.installedAt, before.installedAt);
      },
    );

    test(
      'a verified Original on the device is kept even when PCSX2 prefers CHD',
      () async {
        await prefer(_otherPs2);
        expect((await installPs2()).last, isA<InstallCompleted>());
        final original = await stored();
        await prefer(
          BuiltinRuntimeProfiles.all.singleWhere(
            (profile) =>
                profile.adapterId == BuiltinRuntimeProfiles.pcsx2AdapterId,
          ),
        );

        final kept = await installPs2();

        final resolved = (kept.last as InstallCompleted).resolved;
        expect(resolved.deliveryFormat, RomDeliveryFormat.original);
        expect(resolved.launchAbsolutePath, endsWith('/disc/game.iso'));
        expect(downloads.calls, 1);
        expect(downloads.urls.single.path, '/delivery/content/iso.sig');
        final row = await stored();
        expect(row.deliveryFormat, RomDeliveryFormat.original);
        expect(row.installedAt, original.installedAt);

        // CHD is chosen only once no reusable Original remains.
        await File('${row.contentRoot}/disc/game.iso').writeAsString('HELLO');
        final swapped = await installPs2();
        expect(
          (swapped.last as InstallCompleted).resolved.deliveryFormat,
          RomDeliveryFormat.chd,
        );
        expect(downloads.urls.last.path, '/delivery/content/chd-v1.sig');
        expect((await stored()).deliveryFormat, RomDeliveryFormat.chd);
      },
    );

    test('a failing runtime resolver installs the Original', () async {
      final events = await service(runtimeResolver: _ThrowingResolver())
          .install(ps2Target, accessToken: 'token', operation: _operation)
          .toList();

      final resolved = (events.last as InstallCompleted).resolved;
      expect(resolved.deliveryFormat, RomDeliveryFormat.original);
      expect(downloads.urls.single.path, '/delivery/content/iso.sig');
      expect((await stored()).deliveryFormat, RomDeliveryFormat.original);
    });

    test(
      'an expired CHD grant re-plans CHD from fresh facts, resolving once',
      () async {
        downloads
          ..expireOnce = true
          ..onExpire = () async {
            manifests.result = ReleaseManifestSuccess(
              ps2Manifest(
                chdPath: 'disc/fresh.chd',
                chdToken: 'chd-v2',
                chdSha256: _chd2Sha,
              ),
            );
          }
          ..contentFor = (url) => url.path.contains('chd-v2') ? 'chd2' : 'chd!';

        final events = await installPs2();

        expect(events.last, isA<InstallCompleted>());
        expect(manifests.calls, 2);
        expect(resolver.identities, hasLength(1));
        expect(downloads.urls.map((url) => url.path), <String>[
          '/delivery/content/chd-v1.sig',
          '/delivery/content/chd-v2.sig',
        ]);
        final install = await stored();
        expect(install.launchRelativePath, 'disc/fresh.chd');
        expect(install.primarySha256, _chd2Sha);
        expect(install.originalItems.single.sha256, _helloSha);
      },
    );

    for (final change in <String>['withdrawn', 'runtime preference']) {
      test(
        'a refreshed manifest after a $change never substitutes Original',
        () async {
          downloads
            ..expireOnce = true
            ..onExpire = () async {
              if (change == 'withdrawn') {
                manifests.result = ReleaseManifestSuccess(
                  ps2Manifest(chdState: RomDeliveryState.unsupported),
                );
              } else {
                await prefer(_otherPs2);
              }
            };

          final events = await installPs2();

          if (change == 'withdrawn') {
            expect(
              (events.last as InstallFailed).kind,
              InstallFailureKind.contentUnavailable,
            );
            expect(downloads.urls.map((url) => url.path), <String>[
              '/delivery/content/chd-v1.sig',
            ]);
            expect(
              await database.select(database.localInstalls).get(),
              isEmpty,
            );
            expect(
              await database.select(database.profileLocalGames).get(),
              isEmpty,
            );
          } else {
            // The runtime was resolved once; a later preference change does
            // not re-pick the format inside the same install.
            expect(events.last, isA<InstallCompleted>());
            expect((await stored()).deliveryFormat, RomDeliveryFormat.chd);
            expect(resolver.identities, hasLength(1));
          }
        },
      );
    }

    test(
      'a twice-expired CHD grant fails without an Original download',
      () async {
        downloads.expireAlways = true;
        final events = await installPs2();
        expect(
          (events.last as InstallFailed).kind,
          InstallFailureKind.downloadFailed,
        );
        expect(
          downloads.urls.map((url) => url.path),
          everyElement('/delivery/content/chd-v1.sig'),
        );
        expect(await database.select(database.localInstalls).get(), isEmpty);
      },
    );

    test('an incomplete manifest installs nothing in any format', () async {
      final current = ps2Manifest();
      manifests.result = ReleaseManifestSuccess(
        ServerBoundReleaseManifest(
          isDeliveryComplete: false,

          serverInstanceId: current.serverInstanceId,
          releaseId: current.releaseId,
          titleId: current.titleId,
          systemKey: current.systemKey,
          name: current.name,
          revision: current.revision,
          isComplete: false,
          runtime: current.runtime,
          items: current.items,
        ),
      );
      final events = await installPs2();
      expect(
        (events.last as InstallFailed).kind,
        InstallFailureKind.contentUnavailable,
      );
      expect(downloads.calls, 0);
      expect(await database.select(database.localInstalls).get(), isEmpty);
    });

    test('a first-fetch identity mismatch installs nothing', () async {
      manifests.result = const ReleaseManifestFailure(
        ReleaseManifestFailureKind.identityMismatch,
      );

      final events = await installPs2();

      expect(
        (events.last as InstallFailed).kind,
        InstallFailureKind.manifestUnavailable,
      );
      expect(downloads.calls, 0);
      expect(resolver.identities, isEmpty);
      expect(await database.select(database.localInstalls).get(), isEmpty);
      expect(await database.select(database.profileLocalGames).get(), isEmpty);
    });

    test(
      'an identity mismatch on the CHD grant refresh never substitutes Original',
      () async {
        downloads
          ..expireOnce = true
          ..onExpire = () async {
            manifests.result = const ReleaseManifestFailure(
              ReleaseManifestFailureKind.identityMismatch,
            );
          };

        final events = await installPs2();

        expect(
          (events.last as InstallFailed).kind,
          InstallFailureKind.manifestUnavailable,
        );
        expect(manifests.calls, 2);
        expect(downloads.urls.map((url) => url.path), <String>[
          '/delivery/content/chd-v1.sig',
        ]);
        expect(await database.select(database.localInstalls).get(), isEmpty);
        expect(
          await database.select(database.profileLocalGames).get(),
          isEmpty,
        );
        expect(
          base
              .listSync(recursive: true)
              .whereType<Directory>()
              .where((entry) => entry.path.endsWith('.staging')),
          isEmpty,
        );
      },
    );
  });
}
