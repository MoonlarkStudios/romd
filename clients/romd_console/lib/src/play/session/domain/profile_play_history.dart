import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';

import 'play_target.dart';

final class ProfilePlayHistory {
  const ProfilePlayHistory({
    required this.localProfileId,
    required this.serverInstanceId,
    required this.titleId,
    required this.lastReleaseId,
    required this.lastPlayedAt,
    required this.playCount,
  });

  final String localProfileId;
  final RomdServerInstanceId serverInstanceId;
  final RomdPublicId titleId;
  final RomdPublicId lastReleaseId;
  final DateTime lastPlayedAt;
  final int playCount;
}

sealed class ProfilePlayHistoryReadResult {
  const ProfilePlayHistoryReadResult();
}

final class ProfilePlayHistoryFound extends ProfilePlayHistoryReadResult {
  const ProfilePlayHistoryFound(this.history);

  final ProfilePlayHistory history;
}

final class ProfilePlayHistoryNotFound extends ProfilePlayHistoryReadResult {
  const ProfilePlayHistoryNotFound();
}

final class ProfilePlayHistoryInconsistent
    extends ProfilePlayHistoryReadResult {
  const ProfilePlayHistoryInconsistent();
}

enum ProfilePlayHistoryRecordResult { recorded, ownerMissing, inconsistent }

final class ProfileRecentGame {
  const ProfileRecentGame({
    required this.install,
    required this.lastPlayedAt,
    required this.playCount,
    required this.eligibleReleaseCount,
  });

  final LocalInstall install;
  final DateTime lastPlayedAt;
  final int playCount;
  final int eligibleReleaseCount;
}

sealed class ProfileRecentGamesResult {
  const ProfileRecentGamesResult();
}

final class ProfileRecentGamesReady extends ProfileRecentGamesResult {
  const ProfileRecentGamesReady(this.games);

  final List<ProfileRecentGame> games;
}

final class ProfileRecentGamesInconsistent extends ProfileRecentGamesResult {
  const ProfileRecentGamesInconsistent();
}

abstract interface class ProfilePlayHistoryRepository {
  Future<ProfilePlayHistoryRecordResult> recordEnded({
    required ResolvedPlayTarget target,
    required DateTime endedAt,
  });

  Future<ProfilePlayHistoryReadResult> find({
    required String localProfileId,
    required RomdServerInstanceId serverInstanceId,
    required RomdPublicId titleId,
  });

  /// One coherent reactive projection of history intersected with current
  /// profile authorization and exact installed releases.
  Stream<ProfileRecentGamesResult> watchRecent();
}
