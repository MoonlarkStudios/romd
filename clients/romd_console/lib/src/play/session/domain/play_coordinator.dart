import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

import 'launch_service.dart';
import 'runtime_dependency_resolver.dart';

/// Progress of one play attempt. [PlayCompleted] is terminal and always last.
/// [PlayPreparingRuntime] carries only non-terminal [DependencyActivity] —
/// terminal dependency outcomes surface as [PlayCompleted], so the UI has one
/// terminal handler.
sealed class PlayProgress {
  const PlayProgress();
}

final class PlayPreparingRuntime extends PlayProgress {
  const PlayPreparingRuntime(this.activity);
  final DependencyActivity activity;
}

final class PlayLaunching extends PlayProgress {
  const PlayLaunching();
}

final class PlayRuntimePreferenceSaved extends PlayProgress {
  const PlayRuntimePreferenceSaved({
    required this.platformLabel,
    required this.runtimeDisplayName,
  });

  final String platformLabel;
  final String runtimeDisplayName;
}

final class PlayCompleted extends PlayProgress {
  const PlayCompleted(this.result);
  final LaunchResult result;
}

/// Owns resolve → provision → launch for an installed target. The single
/// authority for "can this device play this?" — the UI only renders phases.
abstract interface class PlayCoordinator {
  Stream<PlayProgress> play(
    PlayRequest request, {
    ReviewedLaunchSnapshot? controllerSnapshot,
  });

  void close();
}
