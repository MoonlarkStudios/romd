import 'package:romd_console/src/play/content/domain/profile_local_library.dart';

final class FakeProfileLocalLibraryRepository
    implements ProfileLocalLibraryRepository {
  FakeProfileLocalLibraryRepository({
    this.library = const ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[]),
    this.releases = const ProfileLocalReleasesReady(<ProfileLocalRelease>[]),
    this.watchLibraryOverride,
  });

  ProfileLocalLibraryResult library;
  ProfileLocalReleasesResult releases;
  final Stream<ProfileLocalLibraryResult> Function(
    ProfileLocalLibrarySort sort,
  )?
  watchLibraryOverride;
  final List<ProfileLocalLibrarySort> watchedSorts =
      <ProfileLocalLibrarySort>[];

  @override
  Future<ProfileLocalReleasesResult> readReleases() async => releases;

  @override
  Stream<ProfileLocalLibraryResult> watchLibrary({
    required ProfileLocalLibrarySort sort,
  }) {
    watchedSorts.add(sort);
    return watchLibraryOverride?.call(sort) ??
        Stream<ProfileLocalLibraryResult>.value(library);
  }

  @override
  Stream<ProfileLocalReleasesResult> watchReleases() =>
      Stream<ProfileLocalReleasesResult>.value(releases);
}
