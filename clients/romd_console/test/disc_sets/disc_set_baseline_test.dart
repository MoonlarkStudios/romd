import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/emulator/data/registry/drift_runtime_override_rule_repository.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

import 'disc_set_fixture.dart';

void main() {
  test(
    'fresh baseline reopen retains release installs, authority, runtime and controller rows',
    () async {
      final base = await Directory.systemTemp.createTemp('group-baseline-');
      addTearDown(() => base.delete(recursive: true));
      final file = File('${base.path}/profiles.sqlite');
      final old = AppDatabase(NativeDatabase(file));
      await old
          .into(old.serverConnections)
          .insert(
            ServerConnectionsCompanion.insert(
              instanceId: server,
              lastKnownOrigin: origin.toString(),
              firstSeenAt: DateTime(2026),
              lastSeenAt: DateTime(2026),
            ),
          );
      await old
          .into(old.localProfiles)
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
      await old
          .into(old.localInstalls)
          .insert(
            LocalInstallsCompanion.insert(
              deliveryFormat: 'original',
              serverInstanceId: server,
              releaseId: releases.first,
              titleId: titleId,
              platformId: 'psx',
              platformShortName: 'psx',
              contentRoot: '/existing/original',
              launchRelativePath: 'disc.iso',
              sizeBytes: 16,
              primarySha256: Value(digest('synthetic original ISO')),
              manifestFingerprint: const ContentVerifier().manifestFingerprint([
                (
                  relativePath: 'disc.iso',
                  sizeBytes: 16,
                  sha256: digest('synthetic original ISO'),
                  provenance: ContentFileProvenance.original,
                ),
              ]),
              state: 'installed',
              manifestSnapshot: jsonEncode([
                {
                  'relativePath': 'disc.iso',
                  'sizeBytes': 16,
                  'sha256': digest('synthetic original ISO'),
                  'provenance': 'Original',
                },
              ]),
              installedAt: DateTime(2026),
            ),
          );
      await old
          .into(old.profileLocalGames)
          .insert(
            ProfileLocalGamesCompanion.insert(
              localProfileId: 'profile-a',
              serverInstanceId: server,
              releaseId: releases.first,
              titleId: titleId,
              authorizationState: 'authorized',
              acquiredAt: DateTime(2026),
              lastCheckedAt: DateTime(2026),
            ),
          );
      await DriftRuntimeOverrideRuleRepository(database: old).setRule(
        scope: RuntimeOverrideScope.platform,
        scopeValue: 'psx',
        profileId: const RuntimeProfileId('retroarch:psx:swanstation'),
      );
      await old.customStatement(
        "INSERT INTO controller_preferences_rows (id, template_id, updated_at) VALUES (1,'existing-template',?)",
        [DateTime(2026).millisecondsSinceEpoch ~/ 1000],
      );
      final names = [
        'local_installs',
        'profile_local_games',
        'local_profiles',
        'runtime_override_rules',
        'controller_preferences_rows',
      ];
      final before = <String, List<Map<String, Object?>>>{};
      for (final name in names) {
        before[name] = (await old.customSelect('SELECT * FROM $name').get())
            .map((r) => r.data)
            .toList();
      }
      await old.close();
      final upgraded = AppDatabase(NativeDatabase(file));
      addTearDown(upgraded.close);
      for (final name in names) {
        expect(
          (await upgraded.customSelect('SELECT * FROM $name').get())
              .map((r) => r.data)
              .toList(),
          before[name],
          reason: name,
        );
      }
      expect(
        (await upgraded.customSelect('PRAGMA user_version').getSingle())
            .read<int>('user_version'),
        17,
      );
      expect(await upgraded.select(upgraded.discSetInstalls).get(), isEmpty);
      expect(await upgraded.select(upgraded.profileDiscSets).get(), isEmpty);
      expect(
        await upgraded.select(upgraded.discSetInstallJournals).get(),
        isEmpty,
      );
    },
  );
}
