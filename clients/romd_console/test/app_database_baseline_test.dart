import 'dart:io';
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:sqlite3/sqlite3.dart' as sqlite;

void main() {
  test(
    'fresh baseline creates scoped install and group tables without a quarantine',
    () async {
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);

      final tables = await _tableNames(db);
      final localInstallColumns = await _columnNames(db, 'local_installs');
      final localProfileColumns = await _columnNames(db, 'local_profiles');
      final accountLinkColumns = await _columnNames(db, 'romd_account_links');
      final profileGameIndex = await db
          .customSelect('PRAGMA index_info(profile_local_games_server_release)')
          .get();

      expect(db.schemaVersion, 17);
      expect(
        tables,
        containsAll(<String>{
          'server_connections',
          'disc_set_installs',
          'profile_disc_sets',
          'disc_set_install_journals',
          'pending_server_locators',
          'profile_local_games',
          'profile_play_histories',
          'play_activity_sync_preferences',
          'local_play_sessions',
          'play_activity_outbox',
          'controller_mapping_profiles',
          'controller_profile_binding_rules',
          'controller_hardware_mappings',
        }),
      );
      expect(tables, isNot(contains('legacy_local_installs')));
      expect(
        localProfileColumns,
        containsAll(<String>{
          'selected_server_instance_id',
          'server_selection_generation',
        }),
      );
      expect(accountLinkColumns, contains('server_instance_id'));
      expect(
        await _columnNames(db, 'profile_play_histories'),
        contains('last_played_at'),
      );
      expect(
        await _columnNames(db, 'profile_play_histories'),
        isNot(contains('last_completed_at')),
      );
      expect(
        profileGameIndex.map((row) => row.read<String>('name')).toList(),
        <String>['server_instance_id', 'release_id'],
      );
      expect(localInstallColumns, contains('server_instance_id'));
      expect(localInstallColumns, contains('release_id'));
      expect(
        localInstallColumns,
        containsAll(<String>{
          'delivery_format',
          'original_manifest_snapshot',
          'original_launch_relative_path',
        }),
      );
      expect(await db.select(db.localInstalls).get(), isEmpty);
    },
  );

  test('fresh baseline enforces keys, checks, and delete behavior', () async {
    final db = AppDatabase(NativeDatabase.memory());
    addTearDown(db.close);
    await db.customStatement('PRAGMA foreign_keys = ON');

    await db.customStatement('''
INSERT INTO server_connections VALUES (
  '11111111-1111-4111-8111-111111111111',
  'https://library.example',
  1783612800000,
  1783612800000
)
''');
    for (final profileId in <String>['profile-a', 'profile-b']) {
      await db.customStatement('''
INSERT INTO local_profiles (
  id, display_name, avatar_key, accent_color, romd_server_origin,
  entry_mode, created_at, updated_at, last_used_at
) VALUES (
  '$profileId', '$profileId', 'default', 1, '',
  'open', 1783612800000, 1783612800000, NULL
)
''');
      await db.customStatement('''
INSERT INTO pending_server_locators VALUES (
  '$profileId', 'https://same.example', 1783612800000, NULL
)
''');
    }

    expect(
      await _scalarInt(
        db,
        "SELECT COUNT(*) AS value FROM pending_server_locators WHERE normalized_origin = 'https://same.example'",
      ),
      2,
    );
    await expectLater(
      db.customStatement('''
UPDATE local_profiles
SET server_selection_generation = -1
WHERE id = 'profile-a'
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
UPDATE local_profiles
SET selected_server_instance_id = '22222222-2222-4222-8222-222222222222'
WHERE id = 'profile-a'
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
INSERT INTO profile_local_games VALUES (
  'profile-a',
  '22222222-2222-4222-8222-222222222222',
  'release-invalid-server',
  'title-1',
  'authorized',
  1783612800000,
  1783612800000
)
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
INSERT INTO profile_local_games VALUES (
  'profile-a',
  '11111111-1111-4111-8111-111111111111',
  'release-invalid-state',
  'title-1',
  'allowed',
  1783612800000,
  1783612800000
)
'''),
      throwsA(isA<Exception>()),
    );

    await db.customStatement('''
INSERT INTO profile_local_games VALUES (
  'profile-a',
  '11111111-1111-4111-8111-111111111111',
  'release-1',
  'title-1',
  'authorized',
  1783612800000,
  1783612800000
)
''');
    await db.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'profile-a',
  '11111111-1111-4111-8111-111111111111',
  'title-1',
  'release-1',
  1783612800000,
  1
)
''');
    await expectLater(
      db.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'profile-b',
  '11111111-1111-4111-8111-111111111111',
  'title-invalid-count',
  'release-1',
  1783612800000,
  0
)
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
INSERT INTO profile_local_games VALUES (
  'profile-a',
  '11111111-1111-4111-8111-111111111111',
  'release-1',
  'title-2',
  'revoked',
  1783612800000,
  1783612800000
)
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
DELETE FROM server_connections
WHERE instance_id = '11111111-1111-4111-8111-111111111111'
'''),
      throwsA(isA<Exception>()),
    );

    await db.customStatement(
      "DELETE FROM local_profiles WHERE id = 'profile-a'",
    );
    expect(
      await _scalarInt(
        db,
        "SELECT COUNT(*) AS value FROM pending_server_locators WHERE local_profile_id = 'profile-a'",
      ),
      0,
    );
    expect(
      await _scalarInt(
        db,
        "SELECT COUNT(*) AS value FROM profile_play_histories WHERE local_profile_id = 'profile-a'",
      ),
      0,
    );
    expect(
      await _scalarInt(
        db,
        "SELECT COUNT(*) AS value FROM profile_local_games WHERE local_profile_id = 'profile-a'",
      ),
      0,
    );
    expect(
      await _scalarInt(db, 'SELECT COUNT(*) AS value FROM server_connections'),
      1,
    );
  });

  for (final fixture in [
    (name: 'sentinel', type: 'view'),
    (name: 'sqliteXsentinel', type: 'view'),
    (name: 'sqliteXsentinel', type: 'table'),
  ]) {
    test(
      'unversioned ${fixture.type} ${fixture.name} is refused without modifying its file',
      () async {
        final directory = Directory.systemTemp.createTempSync(
          'romd-object-refusal-',
        );
        addTearDown(() => directory.deleteSync(recursive: true));
        final file = File('${directory.path}/existing.sqlite');
        final raw = sqlite.sqlite3.open(file.path);
        if (fixture.type == 'view') {
          raw.execute('CREATE VIEW ${fixture.name} AS SELECT 1 AS value');
        } else {
          raw.execute('CREATE TABLE ${fixture.name} (value INTEGER NOT NULL)');
          raw.execute('INSERT INTO ${fixture.name} (value) VALUES (1)');
        }
        raw.close();
        final before = file.readAsBytesSync();
        final db = AppDatabase(NativeDatabase(file));
        try {
          await expectLater(
            db.customSelect('SELECT * FROM ${fixture.name}').get(),
            throwsA(isA<UnsupportedError>()),
          );
        } finally {
          await db.close();
        }
        expect(file.readAsBytesSync(), before);
        final observer = sqlite.sqlite3.open(file.path);
        try {
          expect(
            observer.select('PRAGMA user_version').single['user_version'],
            0,
          );
          expect(
            observer.select(
              'SELECT type, name FROM sqlite_master WHERE name = ?',
              [fixture.name],
            ).single,
            {'type': fixture.type, 'name': fixture.name},
          );
          expect(
            observer
                .select('SELECT value FROM ${fixture.name}')
                .single['value'],
            1,
          );
        } finally {
          observer.close();
        }
      },
    );
  }

  for (final version in [0, 1, 10, 15, 16, 18]) {
    test(
      'unsupported existing database v$version is refused without modifying its file',
      () async {
        final directory = Directory.systemTemp.createTempSync(
          'romd-baseline-refusal-',
        );
        addTearDown(() => directory.deleteSync(recursive: true));
        final file = File('${directory.path}/existing.sqlite');
        final raw = sqlite.sqlite3.open(file.path);
        raw.execute('CREATE TABLE sentinel(value TEXT NOT NULL)');
        raw.execute("INSERT INTO sentinel VALUES ('retain exactly')");
        raw.execute('PRAGMA user_version = $version');
        raw.close();
        final before = file.readAsBytesSync();
        final db = AppDatabase(NativeDatabase(file));
        try {
          await expectLater(
            db.customSelect('SELECT * FROM sentinel').get(),
            throwsA(isA<UnsupportedError>()),
          );
        } finally {
          await db.close();
        }
        expect(file.readAsBytesSync(), before);
        final observer = sqlite.sqlite3.open(file.path);
        try {
          expect(
            observer.select('PRAGMA user_version').single['user_version'],
            version,
          );
          expect(
            observer.select('SELECT * FROM sentinel').single['value'],
            'retain exactly',
          );
          expect(
            observer
                .select("SELECT name FROM sqlite_master WHERE type='table'")
                .map((row) => row['name']),
            ['sentinel'],
          );
        } finally {
          observer.close();
        }
      },
    );
  }
}

Future<Set<String>> _tableNames(AppDatabase db) async {
  final rows = await db
      .customSelect("SELECT name FROM sqlite_master WHERE type = 'table'")
      .get();
  return rows.map((row) => row.read<String>('name')).toSet();
}

Future<Set<String>> _columnNames(AppDatabase db, String table) async {
  final rows = await db.customSelect('PRAGMA table_info($table)').get();
  return rows.map((row) => row.read<String>('name')).toSet();
}

Future<int> _scalarInt(AppDatabase db, String sql) async =>
    (await db.customSelect(sql).getSingle()).read<int>('value');
