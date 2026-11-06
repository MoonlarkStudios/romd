import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';

import 'disc_set_library.dart';

/// Playback capability exposed to presentation and the composed play graph.
/// Runtime provisioning and concrete disc-set storage stay behind this port.
abstract interface class DiscSetPlayer {
  bool get runtimeQualified;
  bool supportsSystem(String systemKey);
  Stream<PlayProgress> play(
    LocalDiscSet install, {
    ReviewedLaunchSnapshot? controllerSnapshot,
  });
  void close();
}
