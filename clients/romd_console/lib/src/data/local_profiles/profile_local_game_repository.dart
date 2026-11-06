import 'package:drift/drift.dart';

import '../../domain/profile_local_game.dart';
import '../../domain/romd_public_id.dart';
import '../../domain/romd_server_instance_id.dart';
import 'app_database.dart';

abstract interface class ProfileLocalGameRepository {
  Future<ProfileLocalGameReadResult> find(ProfileLocalGameKey key);

  Future<ProfileLocalGameListResult> listForProfileServer({
    required String localProfileId,
    required RomdServerInstanceId serverInstanceId,
  });

  Future<ProfileLocalGameUpdateResult> updateAuthorization({
    required ProfileLocalGameKey key,
    required RomdPublicId expectedTitleId,
    required ProfileGameAuthorization authorization,
    required DateTime lastCheckedAt,
  });

  Future<int> countReferences({
    required RomdServerInstanceId serverInstanceId,
    required RomdPublicId releaseId,
  });

  Future<bool> delete(ProfileLocalGameKey key);
}

final class DriftProfileLocalGameRepository
    implements ProfileLocalGameRepository {
  DriftProfileLocalGameRepository({required AppDatabase database})
    : _database = database;

  final AppDatabase _database;

  @override
  Future<ProfileLocalGameReadResult> find(ProfileLocalGameKey key) async {
    final rows =
        await (_database.select(_database.profileLocalGames)..where(
              (row) =>
                  row.localProfileId.equals(key.localProfileId) &
                  row.serverInstanceId.equals(key.serverInstanceId.value) &
                  row.releaseId.equals(key.releaseId.value),
            ))
            .get();
    if (rows.isEmpty) {
      return const ProfileLocalGameNotFound();
    }
    if (rows.length != 1) {
      return const ProfileLocalGameProjectionInconsistent();
    }
    final game = _parse(rows.single);
    return game == null
        ? const ProfileLocalGameProjectionInconsistent()
        : ProfileLocalGameFound(game);
  }

  @override
  Future<ProfileLocalGameListResult> listForProfileServer({
    required String localProfileId,
    required RomdServerInstanceId serverInstanceId,
  }) => _database.transaction(
    () => _listForProfileServer(
      localProfileId: localProfileId,
      serverInstanceId: serverInstanceId,
    ),
  );

  Future<ProfileLocalGameListResult> _listForProfileServer({
    required String localProfileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    final profiles = await (_database.select(
      _database.localProfiles,
    )..where((row) => row.id.equals(localProfileId))).get();
    if (profiles.isEmpty) {
      return const ProfileLocalGameListProfileNotFound();
    }
    if (profiles.length != 1) {
      return const ProfileLocalGameListProjectionInconsistent();
    }
    final connections = await (_database.select(
      _database.serverConnections,
    )..where((row) => row.instanceId.equals(serverInstanceId.value))).get();
    if (connections.length != 1) {
      return const ProfileLocalGameListProjectionInconsistent();
    }

    final rows =
        await (_database.select(_database.profileLocalGames)
              ..where(
                (row) =>
                    row.localProfileId.equals(localProfileId) &
                    row.serverInstanceId.equals(serverInstanceId.value),
              )
              ..orderBy(<OrderingTerm Function(ProfileLocalGames)>[
                (row) => OrderingTerm.asc(row.acquiredAt),
                (row) => OrderingTerm.asc(row.releaseId),
              ]))
            .get();
    final games = <ProfileLocalGame>[];
    for (final row in rows) {
      final game = _parse(row);
      if (game == null) {
        return const ProfileLocalGameListProjectionInconsistent();
      }
      games.add(game);
    }
    return ProfileLocalGameListFound(List.unmodifiable(games));
  }

  @override
  Future<ProfileLocalGameUpdateResult> updateAuthorization({
    required ProfileLocalGameKey key,
    required RomdPublicId expectedTitleId,
    required ProfileGameAuthorization authorization,
    required DateTime lastCheckedAt,
  }) async {
    return _database.transaction(() async {
      final read = await find(key);
      final ProfileLocalGame? existing;
      switch (read) {
        case ProfileLocalGameFound(:final game):
          existing = game;
        case ProfileLocalGameNotFound():
          existing = null;
        case ProfileLocalGameProjectionInconsistent():
          return ProfileLocalGameUpdateResult.projectionInconsistent;
      }
      if (existing == null) {
        return ProfileLocalGameUpdateResult.notFound;
      }
      if (existing.titleId != expectedTitleId ||
          lastCheckedAt.isBefore(existing.acquiredAt)) {
        return ProfileLocalGameUpdateResult.identityMismatch;
      }
      if (lastCheckedAt.isBefore(existing.lastCheckedAt) ||
          (lastCheckedAt.isAtSameMomentAs(existing.lastCheckedAt) &&
              authorization != existing.authorization)) {
        return ProfileLocalGameUpdateResult.stale;
      }

      final changed =
          await (_database.update(_database.profileLocalGames)..where(
                (row) =>
                    row.localProfileId.equals(key.localProfileId) &
                    row.serverInstanceId.equals(key.serverInstanceId.value) &
                    row.releaseId.equals(key.releaseId.value) &
                    row.titleId.equals(expectedTitleId.value),
              ))
              .write(
                ProfileLocalGamesCompanion(
                  authorizationState: Value(
                    _authorizationToStorage(authorization),
                  ),
                  lastCheckedAt: Value(lastCheckedAt),
                ),
              );
      return changed == 1
          ? ProfileLocalGameUpdateResult.updated
          : ProfileLocalGameUpdateResult.identityMismatch;
    });
  }

  @override
  Future<int> countReferences({
    required RomdServerInstanceId serverInstanceId,
    required RomdPublicId releaseId,
  }) async {
    final count = _database.profileLocalGames.localProfileId.count();
    final query = _database.selectOnly(_database.profileLocalGames)
      ..addColumns(<Expression<Object>>[count])
      ..where(
        _database.profileLocalGames.serverInstanceId.equals(
              serverInstanceId.value,
            ) &
            _database.profileLocalGames.releaseId.equals(releaseId.value),
      );
    return (await query.getSingle()).read(count) ?? 0;
  }

  @override
  Future<bool> delete(ProfileLocalGameKey key) async {
    final deleted =
        await (_database.delete(_database.profileLocalGames)..where(
              (row) =>
                  row.localProfileId.equals(key.localProfileId) &
                  row.serverInstanceId.equals(key.serverInstanceId.value) &
                  row.releaseId.equals(key.releaseId.value),
            ))
            .go();
    return deleted == 1;
  }

  static ProfileLocalGame? _parse(ProfileLocalGameRow row) {
    final serverInstanceId = RomdServerInstanceId.tryParse(
      row.serverInstanceId,
    );
    final releaseId = RomdPublicId.tryParse(row.releaseId);
    final titleId = RomdPublicId.tryParse(row.titleId);
    final authorization = switch (row.authorizationState) {
      'authorized' => ProfileGameAuthorization.authorized,
      'revoked' => ProfileGameAuthorization.revoked,
      _ => null,
    };
    if (row.localProfileId.isEmpty ||
        row.localProfileId.trim() != row.localProfileId ||
        serverInstanceId == null ||
        releaseId == null ||
        titleId == null ||
        authorization == null ||
        row.lastCheckedAt.isBefore(row.acquiredAt)) {
      return null;
    }
    return ProfileLocalGame(
      key: ProfileLocalGameKey(
        localProfileId: row.localProfileId,
        serverInstanceId: serverInstanceId,
        releaseId: releaseId,
      ),
      titleId: titleId,
      authorization: authorization,
      acquiredAt: row.acquiredAt,
      lastCheckedAt: row.lastCheckedAt,
    );
  }

  static String _authorizationToStorage(ProfileGameAuthorization state) =>
      switch (state) {
        ProfileGameAuthorization.authorized => 'authorized',
        ProfileGameAuthorization.revoked => 'revoked',
      };
}
