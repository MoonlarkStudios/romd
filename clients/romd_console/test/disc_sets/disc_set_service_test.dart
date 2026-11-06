import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' hide isNull, isNotNull;
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/content/data/content_file_store.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_failure.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_models.dart';

import 'disc_set_fixture.dart';

void main() {
  late Harness h;
  setUp(() async {
    h = await Harness.create();
  });
  tearDown(() async => h.close());
  test(
    'installs exact original companions and server M3U under separate identity',
    () async {
      await h.install();
      final installs = await h.service.installed();
      expect(installs.length, 1);
      final p = installs.single.plan;
      expect(await h.service.intact(p), isTrue);
      expect(
        await h.service.store
            .file(h.service.store.root(p), 'playlist.m3u')
            .readAsString(),
        p.playlistText,
      );
      expect(h.downloads.count, 4);
      expect(await h.db.select(h.db.localInstalls).get(), isEmpty);
      expect(await h.db.select(h.db.profileLocalGames).get(), isEmpty);
      expect(
        await ContentFileStore(
          baseDir: h.base,
          serverInstanceId: server,
        ).scanCanonicalArtifacts(),
        isEmpty,
      );
      expect(
        (await h.db.select(h.db.discSetInstalls).get()).single.planJson,
        isNot(contains('contentGrant')),
      );
      expect(await h.db.select(h.db.discSetInstallJournals).get(), isEmpty);
    },
  );
  test(
    'unknown total does not claim complete; one-disc partial is a group',
    () async {
      h.api.returned = plan(partial: true);
      await h.install();
      final local = (await h.service.installed()).single;
      expect(local.plan.selectedReleaseIds, [releases.first]);
      expect(local.plan.json['hasAllExpectedDiscs'], isFalse);
      expect(local.plan.allowPartial, isTrue);
      expect(await h.db.select(h.db.localInstalls).get(), isEmpty);
    },
  );
  test(
    'hash mismatch never commits profile authority or final directory',
    () async {
      h.downloads.corrupt = true;
      await expectLater(h.install(), throwsFormatException);
      expect(await h.service.installed(), isEmpty);
      expect(await h.service.store.root(h.api.returned).exists(), isFalse);
      expect(h.api.accesses, 0);
      expect(
        (await h.db.select(h.db.discSetInstallJournals).get()).single.planJson,
        isNot(contains('token.signature')),
      );
    },
  );
  test(
    'resumes process interruption with verified exact files and fresh grants',
    () async {
      h.downloads.failAt = 2;
      await expectLater(h.install(), throwsA(isA<DownloadNetworkError>()));
      final journal = await h.service.pending(setId);
      expect(journal, isNotNull);
      expect(await h.service.installed(), isEmpty);
      h.downloads.failAt = null;
      await h.install(journal);
      expect(
        h.downloads.count,
        5,
      ); // one verified CUE reused, failed BIN retried.
      expect(await h.service.intact(h.api.returned), isTrue);
      expect(h.api.selects, 2);
    },
  );
  test(
    'changed snapshot during renewal cannot mix downloads or replace current install',
    () async {
      await h.install();
      final original = (await h.service.installed()).single;
      h.api.returned = plan(snapshotSeed: 'changed');
      await expectLater(
        h.install(original.plan),
        throwsA(isA<DiscSetException>()),
      );
      expect(
        (await h.service.installed()).single.plan.snapshotId,
        original.plan.snapshotId,
      );
      expect(h.downloads.count, 4);
    },
  );
  test('expired capability renews only the same snapshot', () async {
    h.downloads.expireOnce = true;
    await h.install();
    expect(h.api.selects, 2);
    expect(await h.service.intact(h.api.returned), isTrue);
  });
  test(
    'cancellation leaves grant-free journal and preserves prior valid snapshot',
    () async {
      await h.install();
      final original = (await h.service.installed()).single;
      h.api.returned = plan(snapshotSeed: 'update');
      h.downloads.afterFile = () async {
        h.lease.valid = false;
      };
      await expectLater(h.install(), throwsA(isA<DiscSetException>()));
      expect(
        (await h.service.installed()).single.plan.snapshotId,
        original.plan.snapshotId,
      );
      expect(await h.service.intact(original.plan), isTrue);
      expect(await h.service.pending(setId), isNotNull);
    },
  );
  test(
    'successful update is explicit and keeps edition saves and prior snapshot',
    () async {
      await h.install();
      final original = (await h.service.installed()).single;
      final save = h.service.store.durable(original.plan, 'profile-a', 'saves');
      await save.create(recursive: true);
      await File('${save.path}/playlist.srm').writeAsString('save');
      h.api.returned = plan(snapshotSeed: 'update');
      await h.install();
      final updated = (await h.service.installed()).single;
      expect(updated.plan.snapshotId, isNot(original.plan.snapshotId));
      expect(
        h.service.store.durable(updated.plan, 'profile-a', 'saves').path,
        save.path,
      );
      expect(await File('${save.path}/playlist.srm').readAsString(), 'save');
      expect(await h.service.intact(original.plan), isTrue);
    },
  );
  test(
    'repairs a corrupted existing snapshot after exact online reauthorization',
    () async {
      await h.install();
      final local = (await h.service.installed()).single;
      await h.service.store
          .file(h.service.store.root(local.plan), 'disc-01/track.bin')
          .writeAsString('corrupt');
      expect(await h.service.intact(local.plan), isFalse);
      await h.install();
      expect(await h.service.intact(local.plan), isTrue);
      expect(
        await Directory(
          '${h.service.store.root(local.plan).path}.backup',
        ).exists(),
        isFalse,
      );
    },
  );
  test('denied installation creates no profile reference', () async {
    h.api.allowed = false;
    await expectLater(h.install(), throwsA(isA<DiscSetException>()));
    expect(await h.service.installed(), isEmpty);
    expect(await h.service.store.root(h.api.returned).exists(), isFalse);
  });
  test(
    'repair access denial revokes the exact acquired snapshot offline',
    () async {
      await h.install();
      final local = (await h.service.installed()).single;
      h.api.allowed = false;
      await expectLater(
        h.install(local.plan),
        throwsA(isA<DiscSetException>()),
      );
      final denied = (await h.service.installed()).single;
      expect(denied.authorized, isFalse);
      expect(denied.authorityRevision, isNot(local.authorityRevision));
      expect(await h.service.intact(local.plan), isTrue);
      h.api.error = const DiscSetException(DiscSetFailureKind.unavailable);
      expect(await h.service.authorize(local), isFalse);
    },
  );
  test(
    'denied update preserves authority for a different acquired snapshot',
    () async {
      await h.install();
      final original = (await h.service.installed()).single;
      h.api.returned = plan(snapshotSeed: 'denied update');
      h.api.allowed = false;
      await expectLater(h.install(), throwsA(isA<DiscSetException>()));
      final retained = (await h.service.installed()).single;
      expect(retained.plan.snapshotId, original.plan.snapshotId);
      expect(retained.authorized, isTrue);
      expect(retained.authorityRevision, original.authorityRevision);
      expect(await h.service.intact(original.plan), isTrue);
    },
  );
  test(
    'repair denial cannot overwrite a newer authority publication',
    () async {
      await h.install();
      final original = (await h.service.installed()).single;
      h.api.allowed = false;
      h.api.beforeAccess = () async {
        await (h.db.update(h.db.profileDiscSets)..where(
              (r) =>
                  r.localProfileId.equals('profile-a') &
                  r.serverInstanceId.equals(server) &
                  r.discSetId.equals(setId) &
                  r.snapshotId.equals(original.plan.snapshotId),
            ))
            .write(
              const ProfileDiscSetsCompanion(
                authorizationState: Value('authorized'),
                authorityRevision: Value('newer-authority'),
              ),
            );
      };
      await expectLater(
        h.install(original.plan),
        throwsA(isA<DiscSetException>()),
      );
      final retained = (await h.service.installed()).single;
      expect(retained.authorized, isTrue);
      expect(retained.authorityRevision, 'newer-authority');
    },
  );
  test('exact revoke is persisted and unavailable never upgrades it', () async {
    await h.install();
    final local = (await h.service.installed()).single;
    h.api.allowed = false;
    expect(await h.service.authorize(local), isFalse);
    expect((await h.service.installed()).single.authorized, isFalse);
    h.api.error = const DiscSetException(DiscSetFailureKind.unavailable);
    expect(await h.service.authorize(local), isFalse);
  });
  test(
    'unavailable permits only exact acquired cached authority and changes no check time',
    () async {
      await h.install();
      final local = (await h.service.installed()).single;
      h.api.error = const DiscSetException(DiscSetFailureKind.stale);
      expect(await h.service.authorize(local), isTrue);
      expect((await h.service.installed()).single.checkedAt, local.checkedAt);
    },
  );
  test(
    'policy response cannot authorize a profile/server generation switched during the request',
    () async {
      await h.install();
      final local = (await h.service.installed()).single;
      h.api.beforeAccess = () async {
        await (h.db.update(
          h.db.localProfiles,
        )..where((r) => r.id.equals('profile-a'))).write(
          const LocalProfilesCompanion(serverSelectionGeneration: Value(1)),
        );
      };
      expect(await h.service.authorize(local), isFalse);
      expect(await h.service.installed(), isEmpty);
    },
  );
  test('stale access response cannot overwrite newer cached revoke', () async {
    await h.install();
    final local = (await h.service.installed()).single;
    h.api.beforeAccess = () async {
      await (h.db.update(
        h.db.profileDiscSets,
      )..where((r) => r.discSetId.equals(setId))).write(
        ProfileDiscSetsCompanion(
          authorizationState: const Value('revoked'),
          authorityRevision: const Value('newer-revoke'),
          lastCheckedAt: Value(DateTime(2030)),
        ),
      );
    };
    expect(await h.service.authorize(local), isFalse);
    expect((await h.service.installed()).single.authorized, isFalse);
  });
  test(
    'uninstall removes only this profile reference while another owns exact snapshot',
    () async {
      await h.install();
      final local = (await h.service.installed()).single;
      await h.db
          .into(h.db.localProfiles)
          .insert(
            LocalProfilesCompanion.insert(
              id: 'profile-b',
              displayName: 'Guest',
              avatarKey: 'default',
              accentColor: 1,
              entryMode: 'open',
              createdAt: DateTime(2026),
              updatedAt: DateTime(2026),
            ),
          );
      await h.db
          .into(h.db.profileDiscSets)
          .insert(
            ProfileDiscSetsCompanion.insert(
              localProfileId: 'profile-b',
              serverInstanceId: server,
              discSetId: setId,
              snapshotId: local.plan.snapshotId,
              authorizationState: 'authorized',
              authorityRevision: 'profile-b-acquisition',
              lastCheckedAt: DateTime(2026),
            ),
          );
      await h.service.uninstall(local);
      expect(await h.service.installed(), isEmpty);
      expect(await h.service.store.root(local.plan).exists(), isTrue);
    },
  );
  test(
    'uninstall keeps durable saves and never touches individual release content',
    () async {
      await h.install();
      final local = (await h.service.installed()).single;
      final save = h.service.store.durable(local.plan, 'profile-a', 'saves');
      await save.create(recursive: true);
      await File('${save.path}/playlist.srm').writeAsString('save');
      final release =
          ContentFileStore(
            baseDir: h.base,
            serverInstanceId: server,
          ).contentRoot(
            platformShortName: 'psx',
            titleId: titleId,
            releaseId: releases.first,
          );
      await release.create(recursive: true);
      await File('${release.path}/original.bin').writeAsString('original');
      await h.service.uninstall(local);
      expect(await h.service.store.root(local.plan).exists(), isFalse);
      expect(await save.exists(), isTrue);
      expect(
        await File('${release.path}/original.bin').readAsString(),
        'original',
      );
    },
  );
  test('symlink staging path is rejected before any download', () async {
    final outside = await Directory.systemTemp.createTemp('outside-group-');
    addTearDown(() => outside.delete(recursive: true));
    final staging = h.service.store.staging(h.api.returned);
    await staging.parent.create(recursive: true);
    await Link(staging.path).create(outside.path);
    await expectLater(h.install(), throwsA(isA<FileSystemException>()));
    expect(h.downloads.count, 0);
    expect(await outside.list().toList(), isEmpty);
  });
  test(
    'journal metadata contains exact hashes and membership, never credentials',
    () async {
      h.downloads.failAt = 1;
      await expectLater(h.install(), throwsA(isA<DownloadNetworkError>()));
      final row = (await h.db.select(h.db.discSetInstallJournals).get()).single;
      final p = DiscSetPlan.parse(
        jsonDecode(row.planJson),
        origin: origin,
        persisted: true,
      );
      expect(p.selectedReleaseIds, releases);
      expect(row.planJson, isNot(contains('test-token')));
      expect(row.planJson, isNot(contains('/delivery/')));
    },
  );
  test(
    'newer deny queued during installation publication wins over earlier allow',
    () async {
      await h.close();
      Future<bool>? denied;
      bool overlap = false;
      bool denialCompleted = false;
      h = await Harness.create(
        checkpoint: (phase) async {
          if (phase == 'published' && overlap) {
            h.api.allowed = false;
            final current = (await h.service.installed()).single;
            denied = h.service.authorize(current).then((value) {
              denialCompleted = true;
              return value;
            });
            await Future<void>.delayed(Duration.zero);
            expect(
              denialCompleted,
              isFalse,
              reason: 'Authorization must wait for the same publication queue.',
            );
          }
        },
      );
      await h.install();
      overlap = true;
      await h.install();
      expect(await denied, isFalse);
      expect((await h.service.installed()).single.authorized, isFalse);
      h.api.error = const DiscSetException(DiscSetFailureKind.unavailable);
      expect(
        await h.service.authorize((await h.service.installed()).single),
        isFalse,
      );
    },
  );
  test(
    'omitted-member readiness can change observation without invalidating selected resume snapshot',
    () async {
      h.api.returned = plan(partial: true);
      h.downloads.failAt = 1;
      await expectLater(h.install(), throwsA(isA<DownloadNetworkError>()));
      final pending = (await h.service.pending(setId))!;
      final newObservation = digest('new readiness');
      h.api.observed = newObservation;
      final fresh = fixture(partial: true)..['observationId'] = newObservation;
      h.api.returned = DiscSetPlan.parse(fresh, origin: origin);
      h.downloads.failAt = null;
      await h.install(pending);
      final installed = (await h.service.installed()).single;
      expect(installed.plan.snapshotId, pending.snapshotId);
      expect(installed.plan.observationId, newObservation);
      expect(h.api.lastSelection!['observationId'], newObservation);
    },
  );
  test(
    'published bytes without SQL commit require fresh authorization before recovery adoption',
    () async {
      await h.close();
      bool fail = true;
      h = await Harness.create(
        checkpoint: (phase) async {
          if (phase == 'published' && fail)
            throw const FileSystemException('simulated commit interruption');
        },
      );
      await expectLater(h.install(), throwsA(isA<FileSystemException>()));
      expect(await h.service.installed(), isEmpty);
      expect(await h.service.intact(h.api.returned), isTrue);
      expect(await h.service.pending(setId), isNotNull);
      h.api.allowed = false;
      fail = false;
      await expectLater(h.install(), throwsA(isA<DiscSetException>()));
      expect(await h.service.installed(), isEmpty);
      h.api.allowed = true;
      await h.install();
      expect((await h.service.installed()).single.authorized, isTrue);
    },
  );
  test(
    'same-second policy version change blocks an old allow even if other fields match',
    () async {
      await h.install();
      final local = (await h.service.installed()).single;
      h.api.beforeAccess = () async {
        await (h.db.update(
          h.db.profileDiscSets,
        )..where((r) => r.discSetId.equals(setId))).write(
          const ProfileDiscSetsCompanion(
            authorityRevision: Value('changed-same-second'),
          ),
        );
      };
      expect(await h.service.authorize(local), isFalse);
    },
  );
  test(
    'unverified extra companion cannot enter a published group snapshot',
    () async {
      final staging = h.service.store.staging(h.api.returned);
      await staging.create(recursive: true);
      await File('${staging.path}/unexpected.sbi').writeAsString('unverified');
      await expectLater(h.install(), throwsFormatException);
      expect(await h.service.installed(), isEmpty);
      expect(await h.service.store.root(h.api.returned).exists(), isFalse);
    },
  );
}
