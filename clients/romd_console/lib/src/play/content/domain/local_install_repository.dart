import 'install_state.dart';
import 'local_install.dart';

/// Persistence for device-local install records.
abstract interface class LocalInstallRepository {
  Future<LocalInstall?> findByReleaseId(String releaseId);

  Future<List<LocalInstall>> listInstalled();

  Future<List<LocalInstall>> listInstalledByTitleId(String titleId);

  Stream<List<LocalInstall>> watchInstalled();

  Stream<List<LocalInstall>> watchInstalledByTitleId(String titleId);

  /// Live set of release ids in [InstallState.installed]. Emits on every change
  /// to the install table, so the library can mark on-device titles reactively.
  Stream<Set<String>> watchInstalledReleaseIds();

  Future<void> upsert(LocalInstall install);

  Future<void> updateState(String releaseId, InstallState state);

  Future<void> deleteByReleaseId(String releaseId);
}
