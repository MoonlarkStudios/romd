import 'dart:math';

import 'package:drift/drift.dart';

import '../../domain/local_profile.dart';
import '../../domain/romd_server_instance_id.dart';
import 'app_database.dart';

abstract interface class LocalProfileRepository {
  Future<List<LocalProfile>> listProfiles();

  Stream<List<LocalProfile>> watchProfiles();

  Future<LocalProfile> createProfile(CreateLocalProfileRequest request);

  Future<LocalProfile> linkRomdAccount({
    required String localProfileId,
    required RomdAccountLink accountLink,
  });

  Future<LocalProfile> unlinkRomdAccount({required String localProfileId});

  /// Attaches, changes, or (when [serverOrigin] is `null`) detaches this
  /// profile's ROMD server. Any linked account is cleared, since credentials
  /// are scoped to a specific server.
  Future<LocalProfile> updateRomdServerOrigin({
    required String localProfileId,
    required Uri? serverOrigin,
  });

  Future<void> markLastUsed(String profileId);
}

abstract interface class ServerBoundLocalProfileRepository {
  Future<LocalProfile> linkRomdAccountToSelectedServer({
    required String localProfileId,
    required int expectedGeneration,
    required RomdServerInstanceId serverInstanceId,
    required RomdAccountLink accountLink,
  });
}

final class DriftLocalProfileRepository
    implements LocalProfileRepository, ServerBoundLocalProfileRepository {
  DriftLocalProfileRepository({
    required AppDatabase database,
    DateTime Function()? now,
    String Function()? createId,
  }) : _database = database,
       _now = now ?? DateTime.now,
       _createId = createId ?? _createDefaultId;

  final AppDatabase _database;
  final DateTime Function() _now;
  final String Function() _createId;

  // Empty text encodes "no server attached" for the non-null storage column.
  static const _noServerSentinel = '';

  @override
  Future<List<LocalProfile>> listProfiles() async {
    final rows = await _profileRows().get();

    return rows.map(_toDomain).toList(growable: false);
  }

  @override
  Stream<List<LocalProfile>> watchProfiles() => _profileRows().watch().map(
    (rows) => rows.map(_toDomain).toList(growable: false),
  );

  @override
  Future<LocalProfile> createProfile(CreateLocalProfileRequest request) async {
    final displayName = request.displayName.trim();
    if (displayName.isEmpty) {
      throw ArgumentError.value(
        request.displayName,
        'displayName',
        'Display name is required.',
      );
    }

    final timestamp = _now();
    final profileId = _createId();
    final row = LocalProfilesCompanion.insert(
      id: profileId,
      displayName: displayName,
      avatarKey: request.avatarKey,
      accentColor: request.accentColor,
      romdServerOrigin: Value(_encodeOrigin(request.romdServerOrigin)),
      entryMode: _entryModeToStorage(request.entryMode),
      createdAt: timestamp,
      updatedAt: timestamp,
    );

    await _database.transaction(() async {
      await _database.into(_database.localProfiles).insert(row);
      final requestedOrigin = request.romdServerOrigin;
      if (requestedOrigin != null) {
        final normalized = RomdServerOrigins.parse(requestedOrigin.toString());
        await _database
            .into(_database.pendingServerLocators)
            .insert(
              PendingServerLocatorsCompanion.insert(
                localProfileId: profileId,
                normalizedOrigin: normalized.toString(),
                createdAt: timestamp,
              ),
            );
      }
    });

    final created =
        await (_profileRows()
              ..where(_database.localProfiles.id.equals(profileId)))
            .getSingle();

    return _toDomain(created);
  }

  @override
  Future<LocalProfile> linkRomdAccount({
    required String localProfileId,
    required RomdAccountLink accountLink,
  }) async {
    await _database
        .into(_database.romdAccountLinks)
        .insertOnConflictUpdate(
          RomdAccountLinksCompanion.insert(
            localProfileId: localProfileId,
            romdUserId: accountLink.romdUserId,
            username: accountLink.username,
            email: accountLink.email,
            linkedAt: accountLink.linkedAt,
            lastLoginAt: Value(accountLink.lastLoginAt),
          ),
        );

    final linkedProfile =
        await (_profileRows()
              ..where(_database.localProfiles.id.equals(localProfileId)))
            .getSingle();

    return _toDomain(linkedProfile);
  }

  @override
  Future<LocalProfile> linkRomdAccountToSelectedServer({
    required String localProfileId,
    required int expectedGeneration,
    required RomdServerInstanceId serverInstanceId,
    required RomdAccountLink accountLink,
  }) => _database.transaction(() async {
    final profiles =
        await (_database.select(_database.localProfiles)..where(
              (row) =>
                  row.id.equals(localProfileId) &
                  row.serverSelectionGeneration.equals(expectedGeneration) &
                  row.selectedServerInstanceId.equals(serverInstanceId.value),
            ))
            .get();
    if (profiles.length != 1) {
      throw StateError('The selected ROMD server changed before account link.');
    }

    final connections = await (_database.select(
      _database.serverConnections,
    )..where((row) => row.instanceId.equals(serverInstanceId.value))).get();
    if (connections.length != 1) {
      throw StateError('The selected ROMD server connection is unavailable.');
    }

    await _database
        .into(_database.romdAccountLinks)
        .insertOnConflictUpdate(
          RomdAccountLinksCompanion.insert(
            localProfileId: localProfileId,
            romdUserId: accountLink.romdUserId,
            username: accountLink.username,
            email: accountLink.email,
            linkedAt: accountLink.linkedAt,
            lastLoginAt: Value(accountLink.lastLoginAt),
            serverInstanceId: Value(serverInstanceId.value),
          ),
        );

    final linkedProfile =
        await (_profileRows()
              ..where(_database.localProfiles.id.equals(localProfileId)))
            .getSingle();
    return _toDomain(linkedProfile);
  });

  @override
  Future<LocalProfile> unlinkRomdAccount({
    required String localProfileId,
  }) async {
    await (_database.delete(
      _database.romdAccountLinks,
    )..where((row) => row.localProfileId.equals(localProfileId))).go();

    final profile =
        await (_profileRows()
              ..where(_database.localProfiles.id.equals(localProfileId)))
            .getSingle();

    return _toDomain(profile);
  }

  @override
  Future<LocalProfile> updateRomdServerOrigin({
    required String localProfileId,
    required Uri? serverOrigin,
  }) async {
    final timestamp = _now();

    await _database.transaction(() async {
      await (_database.update(
        _database.localProfiles,
      )..where((row) => row.id.equals(localProfileId))).write(
        LocalProfilesCompanion(
          romdServerOrigin: Value(_encodeOrigin(serverOrigin)),
          updatedAt: Value(timestamp),
        ),
      );
      await (_database.delete(
        _database.romdAccountLinks,
      )..where((row) => row.localProfileId.equals(localProfileId))).go();
    });

    final profile =
        await (_profileRows()
              ..where(_database.localProfiles.id.equals(localProfileId)))
            .getSingle();

    return _toDomain(profile);
  }

  @override
  Future<void> markLastUsed(String profileId) async {
    final timestamp = _now();
    await (_database.update(
      _database.localProfiles,
    )..where((row) => row.id.equals(profileId))).write(
      LocalProfilesCompanion(
        lastUsedAt: Value(timestamp),
        updatedAt: Value(timestamp),
      ),
    );
  }

  JoinedSelectStatement<HasResultSet, dynamic> _profileRows() {
    final query =
        _database.select(_database.localProfiles).join(
          <Join<HasResultSet, dynamic>>[
            leftOuterJoin(
              _database.romdAccountLinks,
              _database.romdAccountLinks.localProfileId.equalsExp(
                _database.localProfiles.id,
              ),
            ),
            leftOuterJoin(
              _database.serverConnections,
              _database.serverConnections.instanceId.equalsExp(
                _database.localProfiles.selectedServerInstanceId,
              ),
            ),
          ],
        )..orderBy(<OrderingTerm>[
          OrderingTerm(expression: _database.localProfiles.lastUsedAt.isNull()),
          OrderingTerm(
            expression: _database.localProfiles.lastUsedAt,
            mode: OrderingMode.desc,
          ),
          OrderingTerm(expression: _database.localProfiles.createdAt),
        ]);

    return query;
  }

  LocalProfile _toDomain(TypedResult row) {
    final profile = row.readTable(_database.localProfiles);
    final accountLink = row.readTableOrNull(_database.romdAccountLinks);
    final selectedConnection = row.readTableOrNull(_database.serverConnections);
    final parsedSelectedOrigin = selectedConnection == null
        ? null
        : RomdServerOrigins.tryParse(selectedConnection.lastKnownOrigin);
    final selectedOrigin =
        parsedSelectedOrigin?.toString() == selectedConnection?.lastKnownOrigin
        ? parsedSelectedOrigin
        : null;
    final matchingAccountLink =
        accountLink != null &&
            selectedConnection != null &&
            selectedOrigin != null &&
            RomdServerInstanceId.tryParse(selectedConnection.instanceId) !=
                null &&
            accountLink.serverInstanceId == selectedConnection.instanceId
        ? accountLink
        : null;

    return LocalProfile(
      id: profile.id,
      displayName: profile.displayName,
      avatarKey: profile.avatarKey,
      accentColor: profile.accentColor,
      romdServerOrigin: selectedOrigin,
      entryMode: _entryModeFromStorage(profile.entryMode),
      createdAt: profile.createdAt,
      updatedAt: profile.updatedAt,
      lastUsedAt: profile.lastUsedAt,
      romdAccountLink: matchingAccountLink == null
          ? null
          : RomdAccountLink(
              romdUserId: matchingAccountLink.romdUserId,
              username: matchingAccountLink.username,
              email: matchingAccountLink.email,
              linkedAt: matchingAccountLink.linkedAt,
              lastLoginAt: matchingAccountLink.lastLoginAt,
            ),
    );
  }

  static String _encodeOrigin(Uri? origin) =>
      origin?.toString() ?? _noServerSentinel;

  static String _entryModeToStorage(LocalProfileEntryMode mode) =>
      switch (mode) {
        LocalProfileEntryMode.open => 'open',
        LocalProfileEntryMode.pin => 'pin',
      };

  static LocalProfileEntryMode _entryModeFromStorage(String value) =>
      switch (value) {
        'open' => LocalProfileEntryMode.open,
        'pin' => LocalProfileEntryMode.pin,
        _ => LocalProfileEntryMode.open,
      };

  static String _createDefaultId() {
    final random = Random.secure().nextInt(0x7fffffff).toRadixString(36);
    final timestamp = DateTime.now().microsecondsSinceEpoch.toRadixString(36);

    return 'profile_$timestamp$random';
  }
}
