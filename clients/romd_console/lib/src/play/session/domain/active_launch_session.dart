import 'dart:async';

import 'package:romd_console/src/play/session/domain/launch_session.dart';

/// Provider-agnostic handle to the one launch session ROMD is currently
/// supervising.
final class ActiveLaunchSession {
  LaunchSession? _current;
  final StreamController<bool> _changes = StreamController<bool>.broadcast(
    sync: true,
  );

  LaunchSession? get current => _current;

  bool get hasSession => _current != null;
  Stream<bool> get changes => _changes.stream;

  void hold(LaunchSession session) {
    _current = session;
    _changes.add(true);
  }

  bool tryHold(LaunchSession session) {
    if (_current != null) return false;
    hold(session);
    return true;
  }

  bool replaceIfCurrent(LaunchSession current, LaunchSession replacement) {
    if (!identical(_current, current)) return false;
    _current = replacement;
    return true;
  }

  bool clearIfCurrent(LaunchSession session) {
    if (!identical(_current, session)) {
      return false;
    }
    _current = null;
    _changes.add(false);
    return true;
  }

  Future<ActiveLaunchTerminationResult> terminate() {
    final session = _current;
    if (session == null) {
      return Future<ActiveLaunchTerminationResult>.value(
        const ActiveLaunchTerminationNoSession(),
      );
    }

    final termination = session.termination;
    if (termination == null) {
      return Future<ActiveLaunchTerminationResult>.value(
        const ActiveLaunchTerminationUnsupported(),
      );
    }

    return termination.terminate().then(
      (delivered) => ActiveLaunchTerminationDelivered(delivered),
    );
  }
}

sealed class ActiveLaunchTerminationResult {
  const ActiveLaunchTerminationResult();
}

final class ActiveLaunchTerminationNoSession
    extends ActiveLaunchTerminationResult {
  const ActiveLaunchTerminationNoSession();
}

final class ActiveLaunchTerminationUnsupported
    extends ActiveLaunchTerminationResult {
  const ActiveLaunchTerminationUnsupported();
}

final class ActiveLaunchTerminationDelivered
    extends ActiveLaunchTerminationResult {
  const ActiveLaunchTerminationDelivered(this.delivered);

  final bool delivered;
}
