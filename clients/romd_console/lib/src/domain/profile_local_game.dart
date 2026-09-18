import 'romd_public_id.dart';
import 'romd_server_instance_id.dart';

enum ProfileGameAuthorization { authorized, revoked }

final class ProfileLocalGameKey {
  const ProfileLocalGameKey({
    required this.localProfileId,
    required this.serverInstanceId,
    required this.releaseId,
  });

  final String localProfileId;
  final RomdServerInstanceId serverInstanceId;
  final RomdPublicId releaseId;
}

final class ProfileLocalGame {
  const ProfileLocalGame({
    required this.key,
    required this.titleId,
    required this.authorization,
    required this.acquiredAt,
    required this.lastCheckedAt,
  });

  final ProfileLocalGameKey key;
  final RomdPublicId titleId;
  final ProfileGameAuthorization authorization;
  final DateTime acquiredAt;
  final DateTime lastCheckedAt;
}

sealed class ProfileLocalGameReadResult {
  const ProfileLocalGameReadResult();
}

final class ProfileLocalGameFound extends ProfileLocalGameReadResult {
  const ProfileLocalGameFound(this.game);

  final ProfileLocalGame game;
}

final class ProfileLocalGameNotFound extends ProfileLocalGameReadResult {
  const ProfileLocalGameNotFound();
}

final class ProfileLocalGameProjectionInconsistent
    extends ProfileLocalGameReadResult {
  const ProfileLocalGameProjectionInconsistent();
}

sealed class ProfileLocalGameListResult {
  const ProfileLocalGameListResult();
}

final class ProfileLocalGameListFound extends ProfileLocalGameListResult {
  const ProfileLocalGameListFound(this.games);

  final List<ProfileLocalGame> games;
}

final class ProfileLocalGameListProfileNotFound
    extends ProfileLocalGameListResult {
  const ProfileLocalGameListProfileNotFound();
}

final class ProfileLocalGameListProjectionInconsistent
    extends ProfileLocalGameListResult {
  const ProfileLocalGameListProjectionInconsistent();
}

enum ProfileLocalGameUpdateResult {
  updated,
  notFound,
  identityMismatch,
  stale,
  projectionInconsistent,
}
