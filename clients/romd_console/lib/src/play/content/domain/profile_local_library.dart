import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';

enum ProfileLocalLibrarySort { recentPlay, recentInstall, title, system }

enum ProfileLocalLibraryState {
  ready,
  accessRequired,
  notReady,
  repairRequired,
}

final class ProfileLocalLibraryTitle {
  const ProfileLocalLibraryTitle({
    required this.install,
    required this.authorization,
    required this.state,
    required this.releaseCount,
    required this.acquiredAt,
    required this.lastPlayedAt,
    required this.playCount,
  });

  final LocalInstall install;
  final ProfileGameAuthorization authorization;
  final ProfileLocalLibraryState state;
  final int releaseCount;
  final DateTime acquiredAt;
  final DateTime? lastPlayedAt;
  final int playCount;

  bool get launchable => state == ProfileLocalLibraryState.ready;
}

sealed class ProfileLocalLibraryResult {
  const ProfileLocalLibraryResult();
}

final class ProfileLocalLibraryReady extends ProfileLocalLibraryResult {
  const ProfileLocalLibraryReady(this.titles);

  final List<ProfileLocalLibraryTitle> titles;
}

final class ProfileLocalLibraryNoServer extends ProfileLocalLibraryResult {
  const ProfileLocalLibraryNoServer();
}

final class ProfileLocalLibraryUnavailable extends ProfileLocalLibraryResult {
  const ProfileLocalLibraryUnavailable();
}

final class ProfileLocalRelease {
  const ProfileLocalRelease({required this.reference, required this.install});

  final ProfileLocalGame reference;
  final LocalInstall install;
}

sealed class ProfileLocalReleasesResult {
  const ProfileLocalReleasesResult();
}

final class ProfileLocalReleasesReady extends ProfileLocalReleasesResult {
  const ProfileLocalReleasesReady(this.releases);

  final List<ProfileLocalRelease> releases;
}

final class ProfileLocalReleasesNoServer extends ProfileLocalReleasesResult {
  const ProfileLocalReleasesNoServer();
}

final class ProfileLocalReleasesUnavailable extends ProfileLocalReleasesResult {
  const ProfileLocalReleasesUnavailable();
}

abstract interface class ProfileLocalLibraryRepository {
  Stream<ProfileLocalLibraryResult> watchLibrary({
    required ProfileLocalLibrarySort sort,
  });

  Stream<ProfileLocalReleasesResult> watchReleases();

  Future<ProfileLocalReleasesResult> readReleases();
}
