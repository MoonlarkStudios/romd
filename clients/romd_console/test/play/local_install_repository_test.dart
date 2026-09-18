import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/content_file_store.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';

void main() {
  late AppDatabase db;
  late DriftLocalInstallRepository repo;
  late ContentFileStore store;
  final now = DateTime.utc(2026, 6, 21, 9);
  final server = RomdServerInstanceId.tryParse(
    '11111111-1111-4111-8111-111111111111',
  )!;
  final release1 = RomdPublicId.encode(101);
  final release2 = RomdPublicId.encode(102);
  final release3 = RomdPublicId.encode(103);
  final title1 = RomdPublicId.encode(201);
  final title2 = RomdPublicId.encode(202);
  final title3 = RomdPublicId.encode(203);
  const platform = 'snes';
  const shaA =
      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const shaB =
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  const shaC =
      'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc';

  LocalInstall sample({
    InstallState state = InstallState.installed,
    String serverInstanceId = '11111111-1111-4111-8111-111111111111',
    ContentFileStore? fileStore,
  }) => LocalInstall(
    serverInstanceId: serverInstanceId,
    releaseId: release1,
    titleId: title1,
    titleName: 'Chrono Trigger',
    platformId: platform,
    platformName: 'SNES',
    platformShortName: 'snes',
    coverUrl: Uri.parse('https://media.test/chrono.jpg'),
    releaseName: 'USA',
    releaseRevision: 'Rev A',
    contentRoot: (fileStore ?? store)
        .contentRoot(
          platformShortName: 'snes',
          titleId: title1,
          releaseId: release1,
        )
        .path,
    launchRelativePath: 'chrono/chrono-trigger.sfc',
    sizeBytes: 4194304,
    primarySha256: shaA,
    manifestFingerprint: 'fingerprint-1',
    state: state,
    installMode: 'permanent',
    items: const <InstalledItem>[
      InstalledItem(
        relativePath: 'chrono/chrono-trigger.sfc',
        sizeBytes: 4194304,
        sha256: shaA,
      ),
    ],
    installedAt: now,
    lastPlayedAt: null,
  );

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await db
        .into(db.serverConnections)
        .insert(
          ServerConnectionsCompanion.insert(
            instanceId: server.value,
            lastKnownOrigin: 'https://library.example',
            firstSeenAt: now,
            lastSeenAt: now,
          ),
        );
    store = ContentFileStore(
      baseDir: Directory('/data'),
      serverInstanceId: server.value,
    );
    repo = DriftLocalInstallRepository(
      database: db,
      serverInstanceId: server,
      fileStore: store,
    );
  });

  tearDown(() async {
    await db.close();
  });

  test('upsert then find round-trips all fields and items', () async {
    await repo.upsert(sample());

    final found = await repo.findByReleaseId(release1);
    expect(found, isNotNull);
    expect(found!.titleId, title1);
    expect(found.titleName, 'Chrono Trigger');
    expect(found.platformId, platform);
    expect(found.platformName, 'SNES');
    expect(found.platformShortName, 'snes');
    expect(found.coverUrl, Uri.parse('https://media.test/chrono.jpg'));
    expect(found.releaseName, 'USA');
    expect(found.releaseRevision, 'Rev A');
    expect(found.launchRelativePath, 'chrono/chrono-trigger.sfc');
    expect(found.state, InstallState.installed);
    expect(found.manifestFingerprint, 'fingerprint-1');
    expect(found.items.single.relativePath, 'chrono/chrono-trigger.sfc');
    expect(found.items.single.sizeBytes, 4194304);
    expect(found.items.single.sha256, shaA);
    expect(found.lastPlayedAt, isNull);
  });

  test('upsert replaces an existing row by releaseId', () async {
    await repo.upsert(sample());
    await repo.upsert(sample(state: InstallState.corrupt));

    final found = await repo.findByReleaseId(release1);
    expect(found!.state, InstallState.corrupt);
  });

  test('findByReleaseId returns null when absent', () async {
    expect(await repo.findByReleaseId('missing'), isNull);
  });

  test('updateState mutates only the state', () async {
    await repo.upsert(sample());
    await repo.updateState(release1, InstallState.corrupt);

    final found = await repo.findByReleaseId(release1);
    expect(found!.state, InstallState.corrupt);
    expect(found.titleId, title1);
  });

  test(
    'listInstalled excludes corrupt installs and sorts newest install first',
    () async {
      await repo.upsert(sample());
      await repo.upsert(
        LocalInstall(
          serverInstanceId: '11111111-1111-4111-8111-111111111111',
          releaseId: release2,
          titleId: title2,
          titleName: 'EarthBound',
          platformId: platform,
          platformName: 'SNES',
          platformShortName: 'snes',
          coverUrl: null,
          releaseName: 'USA',
          releaseRevision: null,
          contentRoot: store
              .contentRoot(
                platformShortName: 'snes',
                titleId: title2,
                releaseId: release2,
              )
              .path,
          launchRelativePath: 'earthbound.sfc',
          sizeBytes: 8,
          primarySha256: null,
          manifestFingerprint: 'fingerprint-2',
          state: InstallState.installed,
          installMode: 'permanent',
          items: const <InstalledItem>[
            InstalledItem(
              relativePath: 'earthbound.sfc',
              sizeBytes: 8,
              sha256: shaB,
            ),
          ],
          installedAt: now.add(const Duration(hours: 1)),
          lastPlayedAt: null,
        ),
      );
      await repo.upsert(
        LocalInstall(
          serverInstanceId: '11111111-1111-4111-8111-111111111111',
          releaseId: release3,
          titleId: title3,
          titleName: 'Secret of Mana',
          platformId: platform,
          platformName: 'SNES',
          platformShortName: 'snes',
          coverUrl: null,
          releaseName: 'USA',
          releaseRevision: null,
          contentRoot: store
              .contentRoot(
                platformShortName: 'snes',
                titleId: title3,
                releaseId: release3,
              )
              .path,
          launchRelativePath: 'mana.sfc',
          sizeBytes: 8,
          primarySha256: null,
          manifestFingerprint: 'fingerprint-3',
          state: InstallState.installed,
          installMode: 'permanent',
          items: const <InstalledItem>[
            InstalledItem(relativePath: 'mana.sfc', sizeBytes: 8, sha256: shaC),
          ],
          installedAt: now.subtract(const Duration(hours: 1)),
          lastPlayedAt: null,
        ),
      );
      await repo.upsert(sample(state: InstallState.corrupt));

      final installed = await repo.listInstalled();

      expect(installed.map((i) => i.releaseId), <String>[release2, release3]);
    },
  );

  test('deleteByReleaseId removes only the selected release', () async {
    await repo.upsert(sample());
    await repo.deleteByReleaseId(release1);

    expect(await repo.findByReleaseId(release1), isNull);
  });

  test('same release id is isolated across server instances', () async {
    final serverB = RomdServerInstanceId.tryParse(
      '22222222-2222-4222-8222-222222222222',
    )!;
    await db
        .into(db.serverConnections)
        .insert(
          ServerConnectionsCompanion.insert(
            instanceId: serverB.value,
            lastKnownOrigin: 'https://other.example',
            firstSeenAt: now,
            lastSeenAt: now,
          ),
        );
    final storeB = ContentFileStore(
      baseDir: Directory('/data'),
      serverInstanceId: serverB.value,
    );
    final repoB = DriftLocalInstallRepository(
      database: db,
      serverInstanceId: serverB,
      fileStore: storeB,
    );
    await repo.upsert(sample());
    await repoB.upsert(
      sample(serverInstanceId: serverB.value, fileStore: storeB),
    );

    expect((await repo.listInstalled()).single.serverInstanceId, server.value);
    expect(
      (await repoB.listInstalled()).single.serverInstanceId,
      serverB.value,
    );
    expect(await db.select(db.localInstalls).get(), hasLength(2));
  });

  test(
    'neutral repository watches empty without reading active rows',
    () async {
      await repo.upsert(sample());
      final neutral = DriftLocalInstallRepository(
        database: db,
        serverInstanceId: null,
        fileStore: store,
      );

      expect(await neutral.watchInstalled().first, isEmpty);
      expect(await neutral.watchInstalledReleaseIds().first, isEmpty);
      expect(await neutral.findByReleaseId(release1), isNull);
    },
  );

  test('malformed persisted snapshot fails the whole projection', () async {
    await repo.upsert(sample());
    await db.customStatement(
      'UPDATE local_installs SET manifest_snapshot = ? WHERE server_instance_id = ? AND release_id = ?',
      <Object?>[
        '[{"relativePath":"game.sfc","sizeBytes":5}]',
        server.value,
        release1,
      ],
    );

    await expectLater(
      repo.findByReleaseId(release1),
      throwsA(isA<LocalInstallProjectionException>()),
    );
    await expectLater(
      repo.listInstalled(),
      throwsA(isA<LocalInstallProjectionException>()),
    );
  });

  test('noncanonical root fails find, list, and watch projections', () async {
    await repo.upsert(sample());
    await db.customStatement(
      'UPDATE local_installs SET content_root = ? WHERE server_instance_id = ? AND release_id = ?',
      <Object?>['/data/content/other-server/game', server.value, release1],
    );

    await expectLater(
      repo.findByReleaseId(release1),
      throwsA(isA<LocalInstallProjectionException>()),
    );
    await expectLater(
      repo.listInstalled(),
      throwsA(isA<LocalInstallProjectionException>()),
    );
    await expectLater(
      repo.watchInstalled(),
      emitsError(isA<LocalInstallProjectionException>()),
    );
    await expectLater(
      repo.watchInstalledReleaseIds(),
      emitsError(isA<LocalInstallProjectionException>()),
    );
  });
}
