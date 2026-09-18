import 'dart:io';

import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:path/path.dart' as path;
import 'package:path_provider/path_provider.dart';

import '../../domain/local_profile.dart';

part 'app_database.g.dart';

@DriftDatabase(
  tables: <Type>[
    ServerConnections,
    LocalProfiles,
    PendingServerLocators,
    RomdAccountLinks,
    ProfileLocalGames,
    ProfilePlayHistories,
    PlayActivitySyncPreferences,
    LocalPlaySessions,
    PlayActivityOutbox,
    LegacyLocalInstalls,
    LocalInstalls,
    RuntimeOverrideRules,
    ControllerBindingRules,
    ControllerMappingProfiles,
    ControllerProfileBindingRules,
    ControllerPreferencesRows,
    ControllerHardwareMappings,
  ],
)
final class AppDatabase extends _$AppDatabase {
  AppDatabase(QueryExecutor executor) : super(executor);

  factory AppDatabase.open() => AppDatabase(_openConnection());

  @override
  int get schemaVersion => 14;

  @override
  MigrationStrategy get migration => MigrationStrategy(
    beforeOpen: (_) => customStatement('PRAGMA foreign_keys = ON'),
    onCreate: (Migrator m) => m.createAll(),
    onUpgrade: (Migrator m, int from, int to) async {
      if (from < 2) {
        // This must remain the historical pre-v4 shape. Creating the current
        // v12 table here would make the later v4 column additions target the
        // wrong physical contract on a direct old-version upgrade.
        await m.database.customStatement('''
CREATE TABLE local_installs (
  release_id TEXT NOT NULL PRIMARY KEY,
  title_id TEXT NOT NULL,
  platform_short_name TEXT NOT NULL,
  content_root TEXT NOT NULL,
  launch_relative_path TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  primary_sha256 TEXT NULL,
  manifest_fingerprint TEXT NOT NULL,
  state TEXT NOT NULL,
  install_mode TEXT NOT NULL DEFAULT 'permanent',
  manifest_snapshot TEXT NOT NULL,
  installed_at INTEGER NOT NULL,
  last_played_at INTEGER NULL
)
''');
      }
      if (from < 3) {
        await m.addColumn(localProfiles, localProfiles.romdServerOrigin);
      }
      if (from < 4) {
        await m.addColumn(localInstalls, localInstalls.titleName);
        await m.addColumn(localInstalls, localInstalls.platformId);
        await m.addColumn(localInstalls, localInstalls.platformName);
        await m.addColumn(localInstalls, localInstalls.coverUrl);
        await m.addColumn(localInstalls, localInstalls.releaseName);
        await m.addColumn(localInstalls, localInstalls.releaseRevision);
      }
      if (from < 5) {
        await m.createTable(runtimeOverrideRules);
      }
      if (from < 6) {
        await m.createTable(controllerBindingRules);
      }
      if (from < 7) {
        await m.createTable(controllerPreferencesRows);
      }
      if (from < 8) {
        // Player seating became session state (console model — order is
        // renegotiated per session, never persisted). Also repairs dev DBs
        // stamped v7 by interim slice-4 builds that predate the table.
        await m.database.customStatement(
          'DROP TABLE IF EXISTS controller_slot_claims',
        );
      }
      if (from < 9) {
        await m.createTable(controllerMappingProfiles);
        await m.createTable(controllerProfileBindingRules);
      }
      if (from < 10) {
        await m.createTable(controllerHardwareMappings);
      }
      if (from < 11) {
        // Instance identity is deliberately not inferred from a version-10
        // locator. Create the referenced table first, add only nullable/default
        // selection columns, then materialize valid origins as pending rows.
        await m.createTable(serverConnections);
        await m.addColumn(
          localProfiles,
          localProfiles.selectedServerInstanceId,
        );
        await m.addColumn(
          localProfiles,
          localProfiles.serverSelectionGeneration,
        );
        await m.createTable(pendingServerLocators);
        await m.addColumn(romdAccountLinks, romdAccountLinks.serverInstanceId);
        await m.createTable(profileLocalGames);

        final legacyProfiles = await m.database
            .customSelect('SELECT id, romd_server_origin FROM local_profiles')
            .get();
        for (final profile in legacyProfiles) {
          final profileId = profile.read<String>('id');
          final legacyOrigin = profile.read<String>('romd_server_origin');
          final normalizedOrigin = RomdServerOrigins.tryParse(
            legacyOrigin,
          )?.toString();
          if (normalizedOrigin == null) {
            continue;
          }
          await m.database.customStatement(
            '''
INSERT INTO pending_server_locators (
  local_profile_id,
  normalized_origin,
  created_at,
  last_attempt_at
)
SELECT id, ?, created_at, NULL
FROM local_profiles
WHERE id = ?
''',
            <Object?>[normalizedOrigin, profileId],
          );
        }
      }
      if (from < 12) {
        // A pre-cutover install has no trustworthy server identity. Preserve
        // the physical table exactly (including every value and timestamp),
        // quarantine it under the legacy name, then create a fresh active
        // instance-scoped table. Nothing is copied into active installs and no
        // profile grant is invented.
        await m.database.customStatement(
          'ALTER TABLE local_installs RENAME TO legacy_local_installs',
        );
        await m.createTable(localInstalls);
      }
      if (from < 13) {
        // Device-wide install timestamps are not attributable to a person.
        // Profile history therefore starts empty on every upgrade.
        await m.createTable(profilePlayHistories);
      }
      if (from < 14) {
        if (from == 13) {
          await m.database.customStatement(
            'ALTER TABLE profile_play_histories '
            'RENAME COLUMN last_completed_at TO last_played_at',
          );
        }
        await m.createTable(playActivitySyncPreferences);
        await m.createTable(localPlaySessions);
        await m.createTable(playActivityOutbox);
      }
    },
  );
}

@DataClassName('ServerConnectionRow')
class ServerConnections extends Table {
  TextColumn get instanceId => text()();

  TextColumn get lastKnownOrigin => text()();

  DateTimeColumn get firstSeenAt => dateTime()();

  DateTimeColumn get lastSeenAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{instanceId};
}

@DataClassName('LocalProfileRow')
class LocalProfiles extends Table {
  TextColumn get id => text()();

  TextColumn get displayName => text().withLength(min: 1, max: 64)();

  TextColumn get avatarKey => text()();

  IntColumn get accentColor => integer()();

  TextColumn get romdServerOrigin =>
      text().withDefault(const Constant(RomdServerOrigins.defaultValue))();

  TextColumn get entryMode => text()();

  DateTimeColumn get createdAt => dateTime()();

  DateTimeColumn get updatedAt => dateTime()();

  DateTimeColumn get lastUsedAt => dateTime().nullable()();

  TextColumn get selectedServerInstanceId =>
      text().nullable().references(ServerConnections, #instanceId)();

  IntColumn get serverSelectionGeneration => integer()
      .withDefault(const Constant(0))
      .check(serverSelectionGeneration.isBiggerOrEqualValue(0))();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{id};
}

@DataClassName('PendingServerLocatorRow')
class PendingServerLocators extends Table {
  TextColumn get localProfileId =>
      text().references(LocalProfiles, #id, onDelete: KeyAction.cascade)();

  TextColumn get normalizedOrigin => text()();

  DateTimeColumn get createdAt => dateTime()();

  DateTimeColumn get lastAttemptAt => dateTime().nullable()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{localProfileId};
}

@DataClassName('RomdAccountLinkRow')
class RomdAccountLinks extends Table {
  TextColumn get localProfileId => text().references(LocalProfiles, #id)();

  TextColumn get romdUserId => text()();

  TextColumn get username => text()();

  TextColumn get email => text()();

  DateTimeColumn get linkedAt => dateTime()();

  DateTimeColumn get lastLoginAt => dateTime().nullable()();

  TextColumn get serverInstanceId =>
      text().nullable().references(ServerConnections, #instanceId)();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{localProfileId};
}

@TableIndex(
  name: 'profile_local_games_server_release',
  columns: <Symbol>{#serverInstanceId, #releaseId},
)
@DataClassName('ProfileLocalGameRow')
class ProfileLocalGames extends Table {
  TextColumn get localProfileId =>
      text().references(LocalProfiles, #id, onDelete: KeyAction.cascade)();

  TextColumn get serverInstanceId =>
      text().references(ServerConnections, #instanceId)();

  TextColumn get releaseId => text()();

  TextColumn get titleId => text()();

  TextColumn get authorizationState => text().check(
    authorizationState.isIn(const <String>['authorized', 'revoked']),
  )();

  DateTimeColumn get acquiredAt => dateTime()();

  DateTimeColumn get lastCheckedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{
    localProfileId,
    serverInstanceId,
    releaseId,
  };
}

@DataClassName('ProfilePlayHistoryRow')
class ProfilePlayHistories extends Table {
  TextColumn get localProfileId =>
      text().references(LocalProfiles, #id, onDelete: KeyAction.cascade)();

  TextColumn get serverInstanceId =>
      text().references(ServerConnections, #instanceId)();

  TextColumn get titleId => text()();

  TextColumn get lastReleaseId => text()();

  DateTimeColumn get lastPlayedAt => dateTime()();

  IntColumn get playCount =>
      integer().check(playCount.isBiggerOrEqualValue(1))();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{
    localProfileId,
    serverInstanceId,
    titleId,
  };
}

@DataClassName('PlayActivitySyncPreferenceRow')
class PlayActivitySyncPreferences extends Table {
  TextColumn get localProfileId =>
      text().references(LocalProfiles, #id, onDelete: KeyAction.cascade)();

  TextColumn get serverInstanceId =>
      text().references(ServerConnections, #instanceId)();

  BoolColumn get enabled => boolean().withDefault(const Constant(false))();

  DateTimeColumn get updatedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{
    localProfileId,
    serverInstanceId,
  };
}

@TableIndex(
  name: 'local_play_sessions_profile_server_started',
  columns: <Symbol>{#localProfileId, #serverInstanceId, #startedAt},
)
@DataClassName('LocalPlaySessionRow')
class LocalPlaySessions extends Table {
  TextColumn get sessionId => text()();

  TextColumn get localProfileId =>
      text().references(LocalProfiles, #id, onDelete: KeyAction.cascade)();

  TextColumn get serverInstanceId =>
      text().references(ServerConnections, #instanceId)();

  TextColumn get clientId => text().withLength(min: 1, max: 200)();

  TextColumn get titleId => text()();

  TextColumn get releaseId => text()();

  DateTimeColumn get startedAt => dateTime()();

  DateTimeColumn get endedAt => dateTime().nullable()();

  IntColumn get activeDurationSeconds => integer().nullable().check(
    activeDurationSeconds.isNull() |
        (activeDurationSeconds.isBiggerOrEqualValue(0) &
            activeDurationSeconds.isSmallerOrEqualValue(31622400)),
  )();

  BoolColumn get syncEligible => boolean().withDefault(const Constant(false))();

  DateTimeColumn get createdAt => dateTime()();

  DateTimeColumn get updatedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{sessionId};
}

@DataClassName('PlayActivityOutboxRow')
class PlayActivityOutbox extends Table {
  TextColumn get sessionId => text().references(
    LocalPlaySessions,
    #sessionId,
    onDelete: KeyAction.cascade,
  )();

  TextColumn get localProfileId => text()();

  TextColumn get serverInstanceId => text()();

  DateTimeColumn get queuedAt => dateTime()();

  IntColumn get attemptCount => integer()
      .withDefault(const Constant(0))
      .check(attemptCount.isBiggerOrEqualValue(0))();

  DateTimeColumn get nextAttemptAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{sessionId};
}

/// Exact hidden copy of the latest release-only install contract (v10-v11).
/// These rows lack trustworthy server identity and are never projected or
/// played. A future explicit online adoption operation may copy and verify
/// their files without mutating this evidence before commit.
@DataClassName('LegacyLocalInstallRow')
class LegacyLocalInstalls extends Table {
  TextColumn get releaseId => text()();

  TextColumn get titleId => text()();

  TextColumn get titleName => text().withDefault(const Constant(''))();

  TextColumn get platformId => text().nullable()();

  TextColumn get platformName => text().nullable()();

  TextColumn get platformShortName => text()();

  TextColumn get coverUrl => text().nullable()();

  TextColumn get releaseName => text().withDefault(const Constant(''))();

  TextColumn get releaseRevision => text().nullable()();

  TextColumn get contentRoot => text()();

  TextColumn get launchRelativePath => text()();

  IntColumn get sizeBytes => integer()();

  TextColumn get primarySha256 => text().nullable()();

  TextColumn get manifestFingerprint => text()();

  TextColumn get state => text()();

  TextColumn get installMode =>
      text().withDefault(const Constant('permanent'))();

  TextColumn get manifestSnapshot => text()();

  DateTimeColumn get installedAt => dateTime()();

  DateTimeColumn get lastPlayedAt => dateTime().nullable()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{releaseId};
}

/// Device-local record of an installed/cached release. Content is evictable;
/// [manifestSnapshot] (the grant-stripped item set) is the source of truth for
/// per-file repair, so it is required.
@DataClassName('LocalInstallRow')
class LocalInstalls extends Table {
  TextColumn get serverInstanceId =>
      text().references(ServerConnections, #instanceId)();

  TextColumn get releaseId => text()();

  TextColumn get titleId => text()();

  TextColumn get titleName => text().withDefault(const Constant(''))();

  TextColumn get platformId => text()();

  TextColumn get platformName => text().nullable()();

  TextColumn get platformShortName => text()();

  TextColumn get coverUrl => text().nullable()();

  TextColumn get releaseName => text().withDefault(const Constant(''))();

  TextColumn get releaseRevision => text().nullable()();

  TextColumn get contentRoot => text()();

  TextColumn get launchRelativePath => text()();

  IntColumn get sizeBytes => integer()();

  TextColumn get primarySha256 => text().nullable()();

  TextColumn get manifestFingerprint => text()();

  TextColumn get state => text()();

  TextColumn get installMode =>
      text().withDefault(const Constant('permanent'))();

  TextColumn get manifestSnapshot => text()();

  DateTimeColumn get installedAt => dateTime()();

  DateTimeColumn get lastPlayedAt => dateTime().nullable()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{
    serverInstanceId,
    releaseId,
  };
}

/// Device-wide runtime preference: content matching (scope, scopeValue)
/// prefers the given runtime profile. One rule per (scope, scopeValue) —
/// writes upsert, there is no delete in this slice.
@DataClassName('RuntimeOverrideRuleRow')
class RuntimeOverrideRules extends Table {
  TextColumn get scope => text()();

  TextColumn get scopeValue => text()();

  TextColumn get profileId => text()();

  DateTimeColumn get updatedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{scope, scopeValue};
}

/// Device-wide controller rebind: within (scope, scopeValue) the action is
/// chorded via the stored button; a NULL button is an explicit unbind (the
/// chord is disabled at that scope). Enum values are stored by name; rows
/// whose names no longer parse are skipped at read time, never deleted. One
/// rule per (scope, scopeValue, action) — writes upsert, there is no delete
/// in this slice.
@DataClassName('ControllerBindingRuleRow')
class ControllerBindingRules extends Table {
  TextColumn get scope => text()();

  TextColumn get scopeValue => text()();

  TextColumn get action => text()();

  TextColumn get button => text().nullable()();

  DateTimeColumn get updatedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{
    scope,
    scopeValue,
    action,
  };
}

/// Per-local-profile controller mapping metadata keyed by SDL GUID. This is
/// layout/mapping state, not player seating; serials intentionally do not
/// participate so the mapping follows the controller layout.
@DataClassName('ControllerMappingProfileRow')
class ControllerMappingProfiles extends Table {
  TextColumn get localProfileId => text().references(LocalProfiles, #id)();

  TextColumn get sdlGuid => text()();

  TextColumn get displayName => text()();

  TextColumn get templateId => text().nullable()();

  DateTimeColumn get createdAt => dateTime()();

  DateTimeColumn get updatedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{
    localProfileId,
    sdlGuid,
  };
}

/// Profile/GUID-scoped controller rebind. It mirrors the legacy rule semantics
/// but adds the local profile id and SDL GUID to the key. A NULL button remains
/// an explicit unbind, and stale enum names are skipped at read time.
@DataClassName('ControllerProfileBindingRuleRow')
class ControllerProfileBindingRules extends Table {
  TextColumn get localProfileId => text().references(LocalProfiles, #id)();

  TextColumn get sdlGuid => text()();

  TextColumn get scope => text()();

  TextColumn get scopeValue => text()();

  TextColumn get action => text()();

  TextColumn get button => text().nullable()();

  DateTimeColumn get updatedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{
    localProfileId,
    sdlGuid,
    scope,
    scopeValue,
    action,
  };
}

/// Device-wide controller preferences as a single row (id = 1): the chosen
/// device template. Upsert-only.
@DataClassName('ControllerPreferencesRow')
class ControllerPreferencesRows extends Table {
  IntColumn get id => integer()();

  TextColumn get templateId => text().nullable()();

  DateTimeColumn get updatedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{id};
}

/// Device-global physical-input setup for one SDL platform/controller mode.
/// Existing profile/GUID metadata and shortcut rules remain separate concerns.
@DataClassName('ControllerHardwareMappingRow')
class ControllerHardwareMappings extends Table {
  TextColumn get sdlPlatform => text()();

  TextColumn get sdlGuid => text()();

  TextColumn get displayName => text()();

  IntColumn get mappingFormatVersion => integer()();

  TextColumn get mappingData => text()();

  DateTimeColumn get createdAt => dateTime()();

  DateTimeColumn get updatedAt => dateTime()();

  @override
  Set<Column<Object>> get primaryKey => <Column<Object>>{sdlPlatform, sdlGuid};
}

LazyDatabase _openConnection() => LazyDatabase(() async {
  final directory = await getApplicationSupportDirectory();
  final databaseFile = File(path.join(directory.path, 'romd_console.db'));
  await databaseFile.parent.create(recursive: true);

  return NativeDatabase.createInBackground(databaseFile);
});
