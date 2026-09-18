import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';

final class _FakeLaunchSession implements LaunchSession {
  const _FakeLaunchSession({this.termination, this.equalityKey});

  final String? equalityKey;

  @override
  final LaunchTermination? termination;

  @override
  Future<LaunchResult> get completed async => const LaunchExited(0);

  @override
  LaunchForegroundControl? get foreground => null;

  @override
  bool operator ==(Object other) =>
      other is _FakeLaunchSession &&
      equalityKey != null &&
      equalityKey == other.equalityKey;

  @override
  int get hashCode => equalityKey.hashCode;
}

final class _RecordingTermination implements LaunchTermination {
  int calls = 0;

  @override
  Future<bool> terminate() {
    calls += 1;
    return Future<bool>.value(true);
  }
}

void main() {
  test('tryHold and replaceIfCurrent preserve one atomic owner', () {
    final activeSession = ActiveLaunchSession();
    const reservation = _FakeLaunchSession(equalityKey: 'reservation');
    const competing = _FakeLaunchSession(equalityKey: 'competing');
    const replacement = _FakeLaunchSession(equalityKey: 'replacement');

    expect(activeSession.tryHold(reservation), isTrue);
    expect(activeSession.tryHold(competing), isFalse);
    expect(activeSession.current, same(reservation));
    expect(activeSession.replaceIfCurrent(competing, replacement), isFalse);
    expect(activeSession.replaceIfCurrent(reservation, replacement), isTrue);
    expect(activeSession.current, same(replacement));
  });

  test('terminate reaches the live session and reports delivery', () async {
    final activeSession = ActiveLaunchSession();
    final termination = _RecordingTermination();
    activeSession.hold(_FakeLaunchSession(termination: termination));

    final result = await activeSession.terminate();

    expect(result, isA<ActiveLaunchTerminationDelivered>());
    expect((result as ActiveLaunchTerminationDelivered).delivered, isTrue);
    expect(termination.calls, 1);
  });

  test(
    'clearIfCurrent keeps a replacement session when an older session exits',
    () {
      final activeSession = ActiveLaunchSession();
      final equalityKey = String.fromCharCodes(<int>[115, 97, 109, 101]);
      final first = _FakeLaunchSession(equalityKey: equalityKey);
      final second = _FakeLaunchSession(equalityKey: equalityKey);
      expect(first == second, isTrue);
      expect(identical(first, second), isFalse);

      activeSession.hold(first);
      activeSession.hold(second);

      expect(activeSession.clearIfCurrent(first), isFalse);
      expect(activeSession.current, same(second));
      expect(activeSession.clearIfCurrent(second), isTrue);
      expect(activeSession.current, isNull);
    },
  );

  test(
    'terminate distinguishes no session unsupported and delivered',
    () async {
      final activeSession = ActiveLaunchSession();

      expect(
        await activeSession.terminate(),
        isA<ActiveLaunchTerminationNoSession>(),
      );

      activeSession.hold(const _FakeLaunchSession());
      expect(
        await activeSession.terminate(),
        isA<ActiveLaunchTerminationUnsupported>(),
      );

      final termination = _RecordingTermination();
      activeSession.hold(_FakeLaunchSession(termination: termination));
      final delivered = await activeSession.terminate();

      expect(delivered, isA<ActiveLaunchTerminationDelivered>());
      expect((delivered as ActiveLaunchTerminationDelivered).delivered, isTrue);
      expect(termination.calls, 1);
    },
  );
}
