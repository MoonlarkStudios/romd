import 'dart:convert';

import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/domain/rom_delivery_format.dart';
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
    deliveryFormat: RomDeliveryFormat.original,
    originalIdentity: null,

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
        provenance: ContentFileProvenance.original,

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

  test(
    'persisted item missing provenance is refused without a compatibility default',
    () async {
      await repo.upsert(sample());
      final row = (await db.select(db.localInstalls).get()).single;
      final items = (jsonDecode(row.manifestSnapshot) as List<Object?>)
          .cast<Map<String, Object?>>();
      items.single.remove('provenance');
      await db.customStatement(
        'UPDATE local_installs SET manifest_snapshot = ? WHERE server_instance_id = ? AND release_id = ?',
        [jsonEncode(items), server.value, release1],
      );
      await expectLater(
        repo.findByReleaseId(release1),
        throwsA(isA<LocalInstallProjectionException>()),
      );
      expect(
        (await db.select(db.localInstalls).get()).single.manifestSnapshot,
        jsonEncode(items),
      );
    },
  );

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
          deliveryFormat: RomDeliveryFormat.original,
          originalIdentity: null,

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
              provenance: ContentFileProvenance.original,

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
          deliveryFormat: RomDeliveryFormat.original,
          originalIdentity: null,

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
            InstalledItem(
              provenance: ContentFileProvenance.original,
              relativePath: 'mana.sfc',
              sizeBytes: 8,
              sha256: shaC,
            ),
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

  group('delivery format persistence', () {
    LocalInstall chd({
      RomDeliveryFormat format = RomDeliveryFormat.chd,
      OriginalReleaseIdentity? identity,
      bool withIdentity = true,
      List<InstalledItem>? items,
      int sizeBytes = 2048,
      String primarySha256 = shaC,
    }) {
      final base = sample();
      return LocalInstall(
        serverInstanceId: base.serverInstanceId,
        releaseId: base.releaseId,
        titleId: base.titleId,
        titleName: base.titleName,
        platformId: 'ps2',
        platformName: 'PlayStation 2',
        platformShortName: 'ps2',
        coverUrl: null,
        releaseName: base.releaseName,
        releaseRevision: null,
        contentRoot: store
            .contentRoot(
              platformShortName: 'ps2',
              titleId: title1,
              releaseId: release1,
            )
            .path,
        launchRelativePath: 'disc/game.chd',
        sizeBytes: sizeBytes,
        primarySha256: primarySha256,
        manifestFingerprint: 'fingerprint-chd',
        state: InstallState.installed,
        installMode: 'permanent',
        items:
            items ??
            const <InstalledItem>[
              InstalledItem(
                provenance: ContentFileProvenance.original,

                relativePath: 'disc/game.chd',
                sizeBytes: 2048,
                sha256: shaC,
              ),
            ],
        installedAt: now,
        lastPlayedAt: null,
        deliveryFormat: format,
        originalIdentity: withIdentity
            ? identity ??
                  OriginalReleaseIdentity(
                    items: const <InstalledItem>[
                      InstalledItem(
                        provenance: ContentFileProvenance.original,

                        relativePath: 'disc/game.iso',
                        sizeBytes: 8192,
                        sha256: shaB,
                      ),
                    ],
                    launchRelativePath: 'disc/game.iso',
                  )
            : null,
      );
    }

    test('a CHD install round-trips physical and Original identity', () async {
      await repo.upsert(chd());

      final row = (await db.select(db.localInstalls).get()).single;
      expect(row.deliveryFormat, 'chd');
      expect(row.originalLaunchRelativePath, 'disc/game.iso');
      expect(
        row.originalManifestSnapshot,
        '[{"relativePath":"disc/game.iso","sizeBytes":8192,"sha256":"$shaB","provenance":"Original"}]',
      );
      expect(
        row.manifestSnapshot,
        '[{"relativePath":"disc/game.chd","sizeBytes":2048,"sha256":"$shaC","provenance":"Original"}]',
      );

      final found = (await repo.findByReleaseId(release1))!;
      expect(found.deliveryFormat, RomDeliveryFormat.chd);
      expect(found.launchRelativePath, 'disc/game.chd');
      expect(found.items.single.sha256, shaC);
      expect(found.originalLaunchRelativePath, 'disc/game.iso');
      expect(found.originalItems.single.relativePath, 'disc/game.iso');
      expect(found.originalItems.single.sizeBytes, 8192);
      expect(found.originalItems.single.sha256, shaB);
    });

    test('an Original install persists no separate identity', () async {
      await repo.upsert(sample());

      final row = (await db.select(db.localInstalls).get()).single;
      expect(row.deliveryFormat, 'original');
      expect(row.originalManifestSnapshot, isNull);
      expect(row.originalLaunchRelativePath, isNull);
      final found = (await repo.findByReleaseId(release1))!;
      expect(found.deliveryFormat, RomDeliveryFormat.original);
      expect(found.originalIdentity, isNull);
      expect(found.originalItems, same(found.items));
      expect(found.originalLaunchRelativePath, found.launchRelativePath);
    });

    final invalidWrites = <String, LocalInstall Function()>{
      'CHD without Original identity': () => chd(withIdentity: false),
      'Original with an Original identity': () =>
          chd(format: RomDeliveryFormat.original),
      'RVZ install': () => chd(format: RomDeliveryFormat.rvz),
      'CHD with two physical files': () => chd(
        items: const <InstalledItem>[
          InstalledItem(
            provenance: ContentFileProvenance.original,

            relativePath: 'disc/game.chd',
            sizeBytes: 1024,
            sha256: shaC,
          ),
          InstalledItem(
            provenance: ContentFileProvenance.original,

            relativePath: 'disc/extra.bin',
            sizeBytes: 1024,
            sha256: shaA,
          ),
        ],
      ),
      'CHD size differs from its file': () => chd(sizeBytes: 4096),
      'CHD primary digest differs from its file': () =>
          chd(primarySha256: shaA),
      'Original identity launch outside its items': () => chd(
        identity: OriginalReleaseIdentity(
          items: const <InstalledItem>[
            InstalledItem(
              provenance: ContentFileProvenance.original,

              relativePath: 'disc/game.iso',
              sizeBytes: 8192,
              sha256: shaB,
            ),
          ],
          launchRelativePath: 'disc/other.iso',
        ),
      ),
      'Original identity without a digest': () => chd(
        identity: OriginalReleaseIdentity(
          items: const <InstalledItem>[
            InstalledItem(
              provenance: ContentFileProvenance.original,

              relativePath: 'disc/game.iso',
              sizeBytes: 8192,
              sha256: null,
            ),
          ],
          launchRelativePath: 'disc/game.iso',
        ),
      ),
    };
    for (final entry in invalidWrites.entries) {
      test('refuses to write ${entry.key}', () async {
        await expectLater(
          repo.upsert(entry.value()),
          throwsA(isA<LocalInstallProjectionException>()),
        );
        expect(await db.select(db.localInstalls).get(), isEmpty);
      });
    }

    final invalidRows = <String, List<Object?>>{
      'unknown format': <Object?>['iso', null, null],
      'RVZ format': <Object?>['rvz', null, null],
      'CHD missing its Original snapshot': <Object?>[
        'chd',
        null,
        'disc/game.iso',
      ],
      'CHD missing its Original launch path': <Object?>[
        'chd',
        '[{"relativePath":"disc/game.iso","sizeBytes":8192,"sha256":"$shaB","provenance":"Original"}]',
        null,
      ],
      'CHD with a malformed Original snapshot': <Object?>[
        'chd',
        '[{"relativePath":"disc/game.iso","sizeBytes":8192}]',
        'disc/game.iso',
      ],
      'Original carrying an Original snapshot': <Object?>[
        'original',
        '[{"relativePath":"disc/game.iso","sizeBytes":8192,"sha256":"$shaB","provenance":"Original"}]',
        'disc/game.iso',
      ],
    };
    for (final entry in invalidRows.entries) {
      test('a stored row with ${entry.key} fails the projection', () async {
        await repo.upsert(chd());
        await db.customStatement(
          'UPDATE local_installs SET delivery_format = ?, '
          'original_manifest_snapshot = ?, original_launch_relative_path = ? '
          'WHERE server_instance_id = ? AND release_id = ?',
          <Object?>[...entry.value, server.value, release1],
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
    }
  });
}
