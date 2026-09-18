import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/local_install_repository.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/launch_provider.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/profile_play_history.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/window_controller.dart';

final class RecordingLaunch {
  RecordingLaunch(this.result, {this.gate, this.termination}) : _starts = true;

  RecordingLaunch.notStarted(this.result)
    : gate = null,
      termination = null,
      _starts = false;

  final LaunchResult result;
  final Future<void>? gate;
  final LaunchTermination? termination;
  final bool _starts;
  final List<EmulatorLaunchPlan> plans = <EmulatorLaunchPlan>[];
  final List<RecordingLaunchSession> sessions = <RecordingLaunchSession>[];

  bool get launched => plans.isNotEmpty;
  int get launches => plans.length;

  Future<LaunchStartResult> call(EmulatorLaunchPlan plan) async {
    plans.add(plan);
    if (!_starts) {
      return LaunchNotStarted(result);
    }
    final session = RecordingLaunchSession(
      result,
      gate: gate,
      termination: termination,
    );
    sessions.add(session);
    return LaunchStarted(session);
  }
}

final class RecordingLaunchSession implements LaunchSession {
  RecordingLaunchSession(this.result, {this.gate, this.termination});

  final LaunchResult result;
  final Future<void>? gate;
  int completedReads = 0;
  int completionStarts = 0;

  late final Future<LaunchResult> _completed = _complete();

  @override
  Future<LaunchResult> get completed {
    completedReads += 1;
    return _completed;
  }

  @override
  final LaunchTermination? termination;

  @override
  LaunchForegroundControl? get foreground => null;

  Future<LaunchResult> _complete() async {
    completionStarts += 1;
    final launchGate = gate;
    if (launchGate != null) {
      await launchGate;
    }
    return result;
  }
}

final class ScriptedLaunchProvider implements LaunchProvider {
  ScriptedLaunchProvider({
    required this.dependencyResolver,
    required this.recorder,
    this.adapterId = BuiltinRuntimeProfiles.retroArchAdapterId,
  });

  final RuntimeAdapterId adapterId;
  final RuntimeDependencyResolver dependencyResolver;
  final RecordingLaunch recorder;
  ReviewedLaunchSnapshot? lastControllerSnapshot;

  @override
  bool supports(RuntimeProfile profile) => profile.adapterId == adapterId;

  @override
  Stream<DependencyProgress> prepare(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) => dependencyResolver.resolve(profile, target);

  @override
  Future<LaunchStartResult> launch({
    required RuntimeProfile profile,
    required ResolvedPlayTarget target,
    required List<ResolvedRuntimeDependency> dependencies,
    ReviewedLaunchSnapshot? controllerSnapshot,
  }) {
    lastControllerSnapshot = controllerSnapshot;
    return recorder(
      EmulatorLaunchPlan(
        target: target,
        profile: profile,
        dependencies: dependencies,
      ),
    );
  }
}

final class RecordingLocalInstallRepository implements LocalInstallRepository {
  @override
  Future<void> deleteByReleaseId(String releaseId) async {}

  @override
  Future<LocalInstall?> findByReleaseId(String releaseId) async => null;

  @override
  Future<List<LocalInstall>> listInstalled() async => const <LocalInstall>[];

  @override
  Future<List<LocalInstall>> listInstalledByTitleId(String titleId) async =>
      const <LocalInstall>[];

  @override
  Future<void> updateState(String releaseId, InstallState state) async {}

  @override
  Future<void> upsert(LocalInstall install) async {}

  @override
  Stream<List<LocalInstall>> watchInstalled() =>
      Stream<List<LocalInstall>>.value(const <LocalInstall>[]);

  @override
  Stream<List<LocalInstall>> watchInstalledByTitleId(String titleId) =>
      Stream<List<LocalInstall>>.value(const <LocalInstall>[]);

  @override
  Stream<Set<String>> watchInstalledReleaseIds() =>
      Stream<Set<String>>.value(const <String>{});
}

final class RecordingProfilePlayHistoryRepository
    implements ProfilePlayHistoryRepository {
  final List<({ResolvedPlayTarget target, DateTime endedAt})> ended =
      <({ResolvedPlayTarget target, DateTime endedAt})>[];

  @override
  Future<ProfilePlayHistoryReadResult> find({
    required String localProfileId,
    required RomdServerInstanceId serverInstanceId,
    required RomdPublicId titleId,
  }) async => const ProfilePlayHistoryNotFound();

  @override
  Future<ProfilePlayHistoryRecordResult> recordEnded({
    required ResolvedPlayTarget target,
    required DateTime endedAt,
  }) async {
    ended.add((target: target, endedAt: endedAt));
    return ProfilePlayHistoryRecordResult.recorded;
  }

  @override
  Stream<ProfileRecentGamesResult> watchRecent() =>
      Stream<ProfileRecentGamesResult>.value(
        const ProfileRecentGamesReady(<ProfileRecentGame>[]),
      );
}

final class RecordingWindowController implements WindowController {
  int reclaimCount = 0;

  @override
  Future<void> reclaimForeground() async {
    reclaimCount += 1;
  }
}
