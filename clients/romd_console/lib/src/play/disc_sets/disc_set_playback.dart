import 'dart:async';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/launch_provider.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/window_controller.dart';

import 'disc_set_service.dart';
import 'domain/disc_set_library.dart';
import 'domain/disc_set_player.dart';

/// Original-group launch stays inside the existing provider/adapter seam.
/// Exact group authorization precedes file resolution, provisioning, generated
/// config and process creation. Release history/sync is not fabricated.
final class DiscSetPlayback implements DiscSetPlayer {
  DiscSetPlayback({
    required this.service,
    required this.providers,
    this.windowController,
    this.profiles = BuiltinRuntimeProfiles.all,
    this.runtimeQualified = false,
  });
  final DiscSetService service;
  final List<LaunchProvider> providers;
  final List<RuntimeProfile> profiles;

  /// False in the shipped composition until real-media/controller qualification.
  /// Tests can exercise orchestration without claiming playable media.
  final bool runtimeQualified;
  final WindowController? windowController;
  bool _closed = false;
  bool supportsSystem(String systemKey) =>
      profiles
          .where((p) => p.supportsPlatform(systemKey))
          .firstOrNull
          ?.supportsOriginalDiscSets ??
      false;
  Stream<PlayProgress> play(
    LocalDiscSet install, {
    ReviewedLaunchSnapshot? controllerSnapshot,
  }) async* {
    final profile = profiles
        .where((p) => p.supportsPlatform(install.plan.systemKey))
        .firstOrNull;
    final provider = profile == null
        ? null
        : providers.where((p) => p.supports(profile)).firstOrNull;
    final reservation = _DiscSetReservation();
    final gate = await service.withAuthorizedSnapshot(install, () {
      if (_closed) return 0;
      if (profile == null ||
          !profile.supportsOriginalDiscSets ||
          provider == null)
        return 2;
      if (!runtimeQualified) return 1;
      if (!service.activeSession.tryHold(reservation)) return 3;
      return 4;
    });
    if (gate == null || gate == 0) {
      yield const PlayCompleted(LaunchAccessNotGranted());
      return;
    }
    if (gate == 1) {
      yield const PlayCompleted(
        LaunchFailed(
          'Original disc-set playback is awaiting runtime qualification.',
        ),
      );
      return;
    }
    if (gate == 2 || profile == null || provider == null) {
      yield const PlayCompleted(
        LaunchFailed(
          'This runtime does not support original disc-set playlists.',
        ),
      );
      return;
    }
    if (gate == 3) {
      yield const PlayCompleted(
        LaunchFailed('Another emulator is already open.'),
      );
      return;
    }
    LaunchSession held = reservation;
    try {
      final plan = install.plan;
      final a = service.authority;
      if (_closed ||
          a == null ||
          !await service.current() ||
          !await service.intact(plan)) {
        yield const PlayCompleted(
          LaunchFailed('This disc-set install needs repair.'),
        );
        return;
      }
      final save = service.store.durable(plan, a.localProfileId, 'saves');
      final state = Directory(
        p.join(
          service.store.durable(plan, a.localProfileId, 'states').path,
          profile.id.value.replaceAll(':', '_'),
          plan.snapshotId,
        ),
      );
      final config = service.store.durable(plan, a.localProfileId, 'config');
      for (final dir in [save, state, config]) {
        await service.store.assertSafe(dir);
      }
      final target = ResolvedPlayTarget(
        releaseId: null,
        discSetIdentity: plan.identity,
        titleId: plan.titleId,
        platformShortName: plan.systemKey,
        displayName: install.titleName,
        contentRoot: install.contentRoot,
        launchAbsolutePath: service.store
            .file(service.store.root(plan), plan.playlistPath)
            .path,
        saveRoot: save.path,
        stateRoot: state.path,
        configRoot: config.path,
        localProfileId: a.localProfileId,
        serverInstanceId: plan.serverInstanceId,
      );
      var dependencies = const <ResolvedRuntimeDependency>[];
      await for (final progress in provider.prepare(profile, target)) {
        if (_closed || !await service.current()) return;
        switch (progress) {
          case DependencyActivity():
            yield PlayPreparingRuntime(progress);
          case DependenciesReady(dependencies: final ready):
            dependencies = ready;
          case DependencyExecutableMissing(
            :final diagnostics,
            :final executableName,
          ):
            yield PlayCompleted(
              LaunchRuntimeMissing(diagnostics, runtimeName: executableName),
            );
            return;
          case DependencyResolutionFailed(:final message):
            yield PlayCompleted(LaunchFailed(message));
            return;
        }
      }
      if (_closed || !await service.current()) return;
      yield const PlayLaunching();
      final started = await service.withAuthorizedSnapshot<LaunchStartResult>(
        install,
        () async {
          if (_closed || !await service.current())
            return const LaunchNotStarted(LaunchAccessNotGranted());
          return provider.launch(
            profile: profile,
            target: target,
            dependencies: dependencies,
            controllerSnapshot: controllerSnapshot,
          );
        },
      );
      if (started == null) {
        yield const PlayCompleted(LaunchAccessNotGranted());
        return;
      }
      switch (started) {
        case LaunchNotStarted(:final result):
          yield PlayCompleted(result);
        case LaunchStarted(:final session):
          if (_closed || !await service.current()) {
            await session.termination?.terminate();
            await session.completed;
            yield const PlayCompleted(LaunchAccessNotGranted());
            return;
          }
          if (!service.activeSession.replaceIfCurrent(reservation, session)) {
            await session.termination?.terminate();
            await session.completed;
            yield const PlayCompleted(
              LaunchFailed('Another emulator is already open.'),
            );
            return;
          }
          held = session;
          final result = await session.completed;
          if (result is LaunchExited)
            await windowController?.reclaimForeground();
          yield PlayCompleted(result);
      }
    } on Object {
      yield const PlayCompleted(
        LaunchFailed('The original disc-set runtime could not start safely.'),
      );
    } finally {
      service.activeSession.clearIfCurrent(held);
    }
  }

  void close() {
    _closed = true;
  }
}

final class _DiscSetReservation implements LaunchSession {
  @override
  Future<LaunchResult> get completed => Completer<LaunchResult>().future;
  @override
  LaunchForegroundControl? get foreground => null;
  @override
  LaunchTermination? get termination => null;
}
