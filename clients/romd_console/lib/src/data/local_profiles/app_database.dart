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
    LocalInstalls,
    RuntimeOverrideRules,
    ControllerBindingRules,
    ControllerMappingProfiles,
    ControllerProfileBindingRules,
    ControllerPreferencesRows,
    ControllerHardwareMappings,
    DiscSetInstalls,
    ProfileDiscSets,
    DiscSetInstallJournals,
  ],
)
final class AppDatabase extends _$AppDatabase {
  AppDatabase(QueryExecutor executor) : super(executor);

  factory AppDatabase.open() => AppDatabase(_openConnection());

  @override
  int get schemaVersion => 17;

  @override
  MigrationStrategy get migration => MigrationStrategy(
    beforeOpen: (_) => customStatement('PRAGMA foreign_keys = ON'),
    onCreate: (Migrator m) async {
      final existing = await customSelect(
        "SELECT name FROM sqlite_master WHERE name NOT GLOB 'sqlite_*'",
      ).get();
      if (existing.isNotEmpty)
        throw UnsupportedError(
          'An empty database is required for the fresh console baseline.',
        );
      await m.createAll();
    },
    onUpgrade: (Migrator m, int from, int to) async => throw UnsupportedError(
      'Unsupported console database version $from; this deployment requires a fresh version $to database.',
    ),
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

  /// Physical format under [contentRoot]; [manifestSnapshot] lists its files.
  TextColumn get deliveryFormat => text()();

  /// Logical Original items and launch path for a non-Original install; NULL
  /// for Original installs, whose [manifestSnapshot] is that identity.
  TextColumn get originalManifestSnapshot => text().nullable()();

  TextColumn get originalLaunchRelativePath => text().nullable()();

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

/// Separate group identities and journals never masquerade as release rows.
class DiscSetInstalls extends Table {
  TextColumn get serverInstanceId =>
      text().references(ServerConnections, #instanceId)();
  TextColumn get discSetId => text()();
  TextColumn get snapshotId => text()();
  TextColumn get titleId => text()();
  TextColumn get titleName => text()();
  TextColumn get planJson => text()();
  TextColumn get contentRoot => text()();
  DateTimeColumn get installedAt => dateTime()();
  @override
  Set<Column<Object>> get primaryKey => {
    serverInstanceId,
    discSetId,
    snapshotId,
  };
}

class ProfileDiscSets extends Table {
  TextColumn get localProfileId =>
      text().references(LocalProfiles, #id, onDelete: KeyAction.cascade)();
  TextColumn get serverInstanceId =>
      text().references(ServerConnections, #instanceId)();
  TextColumn get discSetId => text()();
  TextColumn get snapshotId => text()();
  TextColumn get authorizationState =>
      text().check(authorizationState.isIn(['authorized', 'revoked']))();
  TextColumn get authorityRevision => text()();
  DateTimeColumn get lastCheckedAt => dateTime()();
  @override
  Set<Column<Object>> get primaryKey => {
    localProfileId,
    serverInstanceId,
    discSetId,
  };
}

/// Grant-free desired snapshot survives cancellation/process termination.
class DiscSetInstallJournals extends Table {
  TextColumn get localProfileId =>
      text().references(LocalProfiles, #id, onDelete: KeyAction.cascade)();
  TextColumn get serverInstanceId =>
      text().references(ServerConnections, #instanceId)();
  TextColumn get discSetId => text()();
  TextColumn get planJson => text()();
  TextColumn get titleName => text()();
  DateTimeColumn get updatedAt => dateTime()();
  @override
  Set<Column<Object>> get primaryKey => {
    localProfileId,
    serverInstanceId,
    discSetId,
  };
}
