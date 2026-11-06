import 'package:romd_console/src/play/content/domain/install_service.dart';

import 'disc_set_models.dart';

final class LocalDiscSet {
  const LocalDiscSet(
    this.plan,
    this.titleName,
    this.contentRoot,
    this.authorized,
    this.checkedAt,
    this.authorityRevision,
  );
  final DiscSetPlan plan;
  final String titleName, contentRoot;
  final bool authorized;
  final DateTime checkedAt;
  final String authorityRevision;
}

/// Snapshot-specific immutable installs plus exact profile references. A crash
/// between filesystem publication and SQL commit leaves only an unreferenced,
/// re-verifiable directory and the durable grant-free journal.
abstract interface class DiscSetLibrary {
  Future<bool> current();
  Future<List<DiscSetStatus>> list(String titleId);
  Future<DiscSetPlan> select(DiscSetStatus status, {required bool partial});
  Future<List<LocalDiscSet>> installed({String? titleId});
  Future<DiscSetPlan?> pending(String set);
  Future<void> install(
    DiscSetPlan plan, {
    required String titleName,
    required InstallOperationLease operation,
    void Function(int, int)? progress,
  });
  Future<void> uninstall(LocalDiscSet install);
  void close();
}
