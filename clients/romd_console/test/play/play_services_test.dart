import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_library.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_player.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/play/session/domain/profile_play_history.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';

import '../helpers/in_memory_controller_preferences.dart';

void main() {
  test('closing the composed graph releases independently supplied disc-set ports once', () {
    final closed = <String>[];
    final services = PlayServices(
      install: _Install(),
      localLibrary: _LocalLibrary(),
      coordinator: _Coordinator(closed),
      playHistory: _History(),
      runtimeResolver: const LocalRuntimeResolver(),
      runtimeRules: _Rules(),
      activeLaunchSession: ActiveLaunchSession(),
      controllerPreferences: InMemoryControllerPreferences(),
      controllerSlotClaims: SessionControllerSlotClaims(),
      discSets: _DiscSetLibrary(closed),
      discSetPlayback: _DiscSetPlayer(closed),
      onClose: () => closed.add('graph'),
    );

    services.close();
    services.close();

    expect(closed, ['coordinator', 'player', 'library', 'graph']);
  });
}

final class _Install implements InstallService {
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _LocalLibrary implements ProfileLocalLibraryRepository {
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _History implements ProfilePlayHistoryRepository {
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Rules implements RuntimeOverrideRuleRepository {
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Coordinator implements PlayCoordinator {
  _Coordinator(this.closed);
  final List<String> closed;
  @override
  void close() => closed.add('coordinator');
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _DiscSetLibrary implements DiscSetLibrary {
  _DiscSetLibrary(this.closed);
  final List<String> closed;
  @override
  void close() => closed.add('library');
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _DiscSetPlayer implements DiscSetPlayer {
  _DiscSetPlayer(this.closed);
  final List<String> closed;
  @override
  void close() => closed.add('player');
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
