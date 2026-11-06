import 'dart:async';

import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

final class InstallMutationKey implements Comparable<InstallMutationKey> {
  const InstallMutationKey({
    required this.serverInstanceId,
    required this.releaseId,
  });

  final RomdServerInstanceId serverInstanceId;
  final RomdPublicId releaseId;

  @override
  int compareTo(InstallMutationKey other) {
    final instance = serverInstanceId.value.compareTo(
      other.serverInstanceId.value,
    );
    return instance != 0
        ? instance
        : releaseId.value.compareTo(other.releaseId.value);
  }

  @override
  bool operator ==(Object other) =>
      other is InstallMutationKey &&
      serverInstanceId == other.serverInstanceId &&
      releaseId == other.releaseId;

  @override
  int get hashCode => Object.hash(serverInstanceId, releaseId);
}

/// App-scoped FIFO serialization for one exact physical install identity.
///
/// A failing or cancelled operation cannot poison later work because queue
/// progress depends only on releasing the lease, never on the operation's
/// result future.
final class InstallMutationSerializer {
  final Map<InstallMutationKey, Future<void>> _tails =
      <InstallMutationKey, Future<void>>{};

  Future<InstallMutationLease> acquire(InstallMutationKey key) async {
    final previous = _tails[key] ?? Future<void>.value();
    final released = Completer<void>();
    final next = previous.then((_) => released.future);
    _tails[key] = next;
    await previous;
    return InstallMutationLease._(() {
      if (!released.isCompleted) released.complete();
      next.whenComplete(() {
        if (identical(_tails[key], next)) _tails.remove(key);
      });
    });
  }
}

final class InstallMutationLease {
  InstallMutationLease._(this._release);

  final void Function() _release;
  bool _released = false;

  void release() {
    if (_released) return;
    _released = true;
    _release();
  }
}
