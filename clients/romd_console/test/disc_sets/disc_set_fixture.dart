import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/disc_sets/disc_set_api.dart';
import 'package:romd_console/src/play/disc_sets/disc_set_service.dart';
import 'package:romd_console/src/play/disc_sets/disc_set_store.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_models.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';

const server = '11111111-1111-4111-8111-111111111111';
const revision = '22222222-2222-4222-8222-222222222222';
final origin = Uri.parse('http://localhost:11338');
final setId = RomdPublicId.encode(41), titleId = RomdPublicId.encode(42);
final releases = [RomdPublicId.encode(43), RomdPublicId.encode(44)];
String digest(String text) => sha256.convert(utf8.encode(text)).toString();
Map<String, Object?> fixture({
  bool partial = false,
  String snapshotSeed = 'one',
  bool grants = true,
}) {
  final paths = [
    for (var i = 0; i < (partial ? 1 : 2); i++) 'disc-0${i + 1}/game.cue',
  ];
  final playlist = '${paths.join('\n')}\n';
  return {
    'contractVersion': 1,
    'serverInstanceId': server,
    'discSetId': setId,
    'revision': revision,
    'deliverySnapshotId': digest(snapshotSeed),
    'titleId': titleId,
    'systemKey': 'psx',
    'name': 'Edition (USA)',
    'observationId': digest('observation'),
    'expectedDiscCount': null,
    'hasAllExpectedDiscs': false,
    'selection': {
      'kind': partial ? 'partial' : 'all-known',
      'completeness': partial ? 'partial' : 'unknown-total',
      'selectedReleaseIds': partial ? [releases.first] : releases,
      'omittedDiscs': partial
          ? [
              {'releaseId': releases.last, 'ordinal': 2},
            ]
          : <Object?>[],
      'missingExpectedOrdinals': <int>[],
    },
    'discs': [
      for (var i = 0; i < paths.length; i++)
        {
          'releaseId': releases[i],
          'ordinal': i + 1,
          'name': 'Disc ${i + 1}',
          'entryPoint': paths[i],
          'layoutId': null,
          'layoutRevision': null,
          'files': [
            for (final name in ['game.cue', 'track.bin'])
              {
                'provenance': 'Original',

                'relativePath': 'disc-0${i + 1}/$name',
                'sizeBytes': payload(name).length.toString(),
                'sha256': digest(payload(name)),
                if (grants)
                  'contentGrant': {
                    'downloadUrl': '/delivery/content/token.signature',
                    'expiresAt': '2026-10-16T00:00:00Z',
                  },
              },
          ],
        },
    ],
    'playlist': {
      'artifactId': 'm3u-sha256:${digest(playlist)}',
      'relativePath': 'playlist.m3u',
      'sha256': digest(playlist),
      'sizeBytes': utf8.encode(playlist).length.toString(),
      'encoding': 'utf-8',
      'text': playlist,
    },
  };
}

String payload(String name) => name.endsWith('.cue')
    ? 'FILE "track.bin" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n'
    : 'original track bytes';
DiscSetPlan plan({bool partial = false, String snapshotSeed = 'one'}) =>
    DiscSetPlan.parse(
      fixture(partial: partial, snapshotSeed: snapshotSeed),
      origin: origin,
    );
Map<String, Object?> statusFixture({bool unavailable = false}) => {
  'contractVersion': 1,
  'serverInstanceId': server,
  'discSetId': setId,
  'revision': revision,
  'titleId': titleId,
  'systemKey': 'psx',
  'name': 'Edition (USA)',
  'observationId': digest('observation'),
  'expectedDiscCount': null,
  'missingExpectedOrdinals': <int>[],
  'discs': [
    for (var i = 0; i < 2; i++)
      {
        'releaseId': releases[i],
        'ordinal': i + 1,
        'name': 'Disc ${i + 1}',
        'layoutId': null,
        'layoutRevision': null,
        'readiness': unavailable && i == 1 ? 'descriptor-required' : 'ready',
      },
  ],
};

final class Lease implements InstallOperationLease {
  bool valid = true;
  @override
  bool get isCurrent => valid;
}

final class FakeDiscApi implements DiscSetApi {
  DiscSetPlan returned = plan();
  bool allowed = true;
  Object? error;
  int selects = 0, accesses = 0;
  bool unavailableDisc = false;
  String observed = digest('observation');
  Map<String, Object?>? lastSelection;
  Future<void> Function()? beforeAccess;
  @override
  Future<List<DiscSetStatus>> list(String token, String titleId) async => [
    DiscSetStatus({
      ...statusFixture(unavailable: unavailableDisc),
      'observationId': observed,
    }),
  ];
  @override
  Future<DiscSetStatus> status(String token, String setId) async =>
      DiscSetStatus({
        ...statusFixture(unavailable: unavailableDisc),
        'observationId': observed,
      });
  @override
  Future<DiscSetPlan> select(
    String token,
    String set,
    Map<String, Object?> selection,
  ) async {
    selects++;
    lastSelection = selection;
    if (error case final e?) throw e;
    return returned;
  }

  @override
  Future<bool> access(String token, DiscSetPlan p) async {
    accesses++;
    await beforeAccess?.call();
    if (error case final e?) throw e;
    return allowed;
  }

  @override
  void close() {}
}

final class FakeSession implements AuthenticatedRequestExecutor {
  @override
  ConsumerLoginSession? get session => null;
  @override
  int get credentialGeneration => 1;
  @override
  Future<T> execute<T>({
    required Future<T> Function(String accessToken) request,
    AuthenticatedReplayPolicy replayPolicy = AuthenticatedReplayPolicy.never,
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  }) => request('test-token');
}

final class FakeDownloads implements DownloadClient {
  int count = 0;
  bool corrupt = false, expireOnce = false;
  int? failAt;
  Future<void> Function()? afterFile;
  @override
  Stream<int> download({
    required Uri url,
    required File destination,
    int? maximumBytes,
  }) async* {
    count++;
    if (expireOnce) {
      expireOnce = false;
      throw const DownloadGrantExpired();
    }
    if (failAt == count) throw const DownloadNetworkError();
    await destination.parent.create(recursive: true);
    final text = corrupt ? 'corrupt' : payload(destination.path);
    await destination.writeAsString(text, flush: true);
    yield text.length;
    await afterFile?.call();
  }
}

final class Harness {
  Harness._(
    this.db,
    this.base,
    this.api,
    this.downloads,
    this.service,
    this.lease,
  );
  final AppDatabase db;
  final Directory base;
  final FakeDiscApi api;
  final FakeDownloads downloads;
  final DiscSetService service;
  final Lease lease;
  static Future<Harness> create({
    Future<void> Function(String)? checkpoint,
  }) async {
    final db = AppDatabase(NativeDatabase.memory());
    final base = await Directory.systemTemp.createTemp('romd-group-');
    await db
        .into(db.serverConnections)
        .insert(
          ServerConnectionsCompanion.insert(
            instanceId: server,
            lastKnownOrigin: origin.toString(),
            firstSeenAt: DateTime(2026),
            lastSeenAt: DateTime(2026),
          ),
        );
    await db
        .into(db.localProfiles)
        .insert(
          LocalProfilesCompanion.insert(
            id: 'profile-a',
            displayName: 'Player',
            avatarKey: 'default',
            accentColor: 1,
            entryMode: 'open',
            createdAt: DateTime(2026),
            updatedAt: DateTime(2026),
            selectedServerInstanceId: const Value(server),
          ),
        );
    final api = FakeDiscApi(), downloads = FakeDownloads();
    final service = DiscSetService(
      database: db,
      api: api,
      authority: InstallAuthorityContext(
        localProfileId: 'profile-a',
        connection: ProfileServerConnection(
          instanceId: RomdServerInstanceId.tryParse(server)!,
          origin: origin,
          firstSeenAt: DateTime(2026),
          lastSeenAt: DateTime(2026),
        ),
        generation: 0,
      ),
      authenticated: FakeSession(),
      downloads: downloads,
      store: DiscSetFileStore(base, server),
      mutations: DiscSetMutations(),
      activeSession: ActiveLaunchSession(),
      checkpoint: checkpoint,
    );
    return Harness._(db, base, api, downloads, service, Lease());
  }

  Future<void> install([DiscSetPlan? p]) =>
      service.install(p ?? api.returned, titleName: 'Game', operation: lease);
  Future<void> close() async {
    service.close();
    await db.close();
    if (await base.exists()) await base.delete(recursive: true);
  }
}
