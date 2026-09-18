import 'dart:async';

import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/play/content/data/install_mutation_serializer.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/installed_play_target_resolver.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/launch_authorization.dart';
import 'package:romd_console/src/play/session/domain/launch_provider.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_activity.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/profile_play_history.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';
import 'package:romd_console/src/play/session/domain/window_controller.dart';

import 'play_activity_sync_service.dart';

/// Drives one play attempt: resolve the runtime profile, satisfy its
/// requirements through the selected provider, then ask that provider to
/// launch the target with the resolved dependencies.
///
/// Cancellation: cancelling the listener during dependency resolution aborts
/// it (the `async*` body and its inner `await for` propagate the cancel to the
/// resolver). Once the provider launch is awaited, the spine records play
/// history and reclaims foreground only for a started process that exited.
final class RomdPlayCoordinator implements PlayCoordinator {
  RomdPlayCoordinator({
    required RuntimeResolver resolver,
    required List<LaunchProvider> launchProviders,
    required ProfilePlayHistoryRepository playHistory,
    required LaunchAuthorizer authorizer,
    required InstalledPlayTargetResolver installedTargetResolver,
    required InstallMutationSerializer mutationSerializer,
    required RuntimeOverrideRuleRepository runtimeRules,
    PlayActivityRepository? playActivity,
    PlayActivitySyncService? playActivitySync,
    ActiveLaunchSession? activeLaunchSession,
    WindowController? windowController,
    DateTime Function()? now,
  }) : _resolver = resolver,
       _launchProviders = List<LaunchProvider>.unmodifiable(launchProviders),
       _playHistory = playHistory,
       _authorizer = authorizer,
       _installedTargetResolver = installedTargetResolver,
       _mutationSerializer = mutationSerializer,
       _runtimeRules = runtimeRules,
       _playActivity = playActivity,
       _playActivitySync = playActivitySync,
       _activeLaunchSession = activeLaunchSession,
       _windowController = windowController,
       _now = now ?? DateTime.now;

  final RuntimeResolver _resolver;
  final List<LaunchProvider> _launchProviders;
  final ProfilePlayHistoryRepository _playHistory;
  final LaunchAuthorizer _authorizer;
  final InstalledPlayTargetResolver _installedTargetResolver;
  final InstallMutationSerializer _mutationSerializer;
  final RuntimeOverrideRuleRepository _runtimeRules;
  final PlayActivityRepository? _playActivity;
  final PlayActivitySyncService? _playActivitySync;
  final ActiveLaunchSession? _activeLaunchSession;
  final WindowController? _windowController;
  final DateTime Function() _now;
  bool _closed = false;

  @override
  Stream<PlayProgress> play(
    PlayRequest request, {
    ReviewedLaunchSnapshot? controllerSnapshot,
  }) async* {
    if (_closed) {
      yield const PlayCompleted(
        LaunchFailed('This launcher session is no longer active.'),
      );
      return;
    }
    final authority = _authorizer.authority;
    final releaseId = RomdPublicId.tryParse(request.releaseId);
    final titleId = RomdPublicId.tryParse(request.titleId);
    if (authority == null || releaseId == null || titleId == null) {
      yield const PlayCompleted(LaunchAccessNotGranted());
      return;
    }

    final lease = await _mutationSerializer.acquire(
      InstallMutationKey(
        serverInstanceId: authority.connection.instanceId,
        releaseId: releaseId,
      ),
    );
    ResolvedPlayTarget? resolvedTarget;
    try {
      if (_closed) {
        yield const PlayCompleted(
          LaunchFailed('This launcher session is no longer active.'),
        );
        return;
      }
      final authorization = await _authorizer.authorize(request);
      if (_closed) {
        yield const PlayCompleted(
          LaunchFailed('This launcher session is no longer active.'),
        );
        return;
      }
      switch (authorization) {
        case AuthorizationNotGranted():
          yield const PlayCompleted(LaunchAccessNotGranted());
          return;
        case AuthorizationRevoked():
          yield const PlayCompleted(LaunchAccessRevoked());
          return;
        case LaunchAuthorizationUnavailable():
          yield const PlayCompleted(
            LaunchFailed('Access could not be verified safely. Try again.'),
          );
          return;
        case LaunchAuthorized(
          authority: final permittedAuthority,
          releaseId: final permittedRelease,
          titleId: final permittedTitle,
        ):
          if (!authority.hasSameAuthority(permittedAuthority) ||
              permittedRelease != releaseId ||
              permittedTitle != titleId) {
            yield const PlayCompleted(
              LaunchFailed('Access could not be verified safely. Try again.'),
            );
            return;
          }
      }

      resolvedTarget = await _installedTargetResolver.resolveInstalledForLaunch(
        authority: authority,
        releaseId: releaseId.value,
        titleId: titleId.value,
        displayName: request.displayName,
      );
      if (_closed) {
        yield const PlayCompleted(
          LaunchFailed('This launcher session is no longer active.'),
        );
        return;
      }
      final target = resolvedTarget;
      if (target == null || !_targetMatches(target, request, authority)) {
        yield const PlayCompleted(
          LaunchFailed('This install needs repair. Install it again to play.'),
        );
        return;
      }

      final requestedRuntime = request.requestedRuntimeProfileId;
      if (requestedRuntime != null) {
        final available = await _resolver.resolve(
          PlayIdentity.ofTarget(target),
        );
        if (_closed) {
          yield const PlayCompleted(
            LaunchFailed('This launcher session is no longer active.'),
          );
          return;
        }
        RuntimeProfile? selectedRuntime;
        for (final candidate in available.candidates) {
          if (candidate.id == requestedRuntime) selectedRuntime = candidate;
        }
        if (selectedRuntime == null) {
          yield const PlayCompleted(
            LaunchFailed('That runtime is not available for this game.'),
          );
          return;
        }
        try {
          await _runtimeRules.setRule(
            scope: RuntimeOverrideScope.platform,
            scopeValue: target.platformShortName,
            profileId: requestedRuntime,
          );
        } on Object {
          yield const PlayCompleted(
            LaunchFailed("Couldn't save the runtime preference."),
          );
          return;
        }
        if (_closed) {
          yield const PlayCompleted(
            LaunchFailed('This launcher session is no longer active.'),
          );
          return;
        }
        yield PlayRuntimePreferenceSaved(
          platformLabel: target.platformShortName.toUpperCase(),
          runtimeDisplayName: selectedRuntime.displayName,
        );
      }
    } finally {
      lease.release();
    }

    final target = resolvedTarget!;
    final candidates = await _resolver.resolve(PlayIdentity.ofTarget(target));
    if (_closed) {
      yield const PlayCompleted(
        LaunchFailed('This launcher session is no longer active.'),
      );
      return;
    }
    final profile = candidates.preferred;
    if (profile == null) {
      yield PlayCompleted(LaunchUnsupportedPlatform(target.platformShortName));
      return;
    }

    final launchProvider = _providerFor(profile);
    if (launchProvider == null) {
      yield PlayCompleted(LaunchUnsupportedPlatform(target.platformShortName));
      return;
    }

    var dependencies = const <ResolvedRuntimeDependency>[];
    try {
      await for (final progress in launchProvider.prepare(profile, target)) {
        if (_closed) {
          yield const PlayCompleted(
            LaunchFailed('This launcher session is no longer active.'),
          );
          return;
        }
        switch (progress) {
          case DependencyActivity():
            yield PlayPreparingRuntime(progress);
          case DependencyResolutionFailed(:final message):
            yield PlayCompleted(LaunchFailed(message));
            return;
          case DependencyExecutableMissing(
            :final diagnostics,
            :final executableName,
          ):
            yield PlayCompleted(
              LaunchRuntimeMissing(diagnostics, runtimeName: executableName),
            );
            return;
          case DependenciesReady(dependencies: final resolved):
            dependencies = resolved;
        }
      }
    } on Object {
      yield const PlayCompleted(
        LaunchFailed('Something went wrong preparing the runtime.'),
      );
      return;
    }

    final activeLaunchSession = _activeLaunchSession;
    if (_closed) {
      yield const PlayCompleted(
        LaunchFailed('This launcher session is no longer active.'),
      );
      return;
    }
    final reservation = activeLaunchSession == null
        ? null
        : _GameplayLaunchReservation();
    if (reservation != null && !activeLaunchSession!.tryHold(reservation)) {
      yield const PlayCompleted(
        LaunchFailed('Another emulator is already open.'),
      );
      return;
    }

    LaunchSession? heldSession = reservation;
    var startedSessionCompleted = false;
    LocalPlaySession? localPlaySession;
    Stopwatch? activePlayStopwatch;
    late final LaunchResult result;
    try {
      if (_closed) {
        yield const PlayCompleted(
          LaunchFailed('This launcher session is no longer active.'),
        );
        return;
      }
      yield const PlayLaunching();
      if (_closed) {
        yield const PlayCompleted(
          LaunchFailed('This launcher session is no longer active.'),
        );
        return;
      }
      final startResult = await launchProvider.launch(
        profile: profile,
        target: target,
        dependencies: dependencies,
        controllerSnapshot: controllerSnapshot,
      );
      result = switch (startResult) {
        LaunchStarted(:final session) => await () async {
          if (reservation != null &&
              !activeLaunchSession!.replaceIfCurrent(reservation, session)) {
            await session.termination?.terminate();
            await session.completed;
            return const LaunchFailed('Another emulator is already open.');
          }
          heldSession = session;
          final playActivity = _playActivity;
          if (playActivity != null) {
            try {
              localPlaySession = await playActivity.recordStarted(
                target: target,
                startedAt: _now(),
              );
              if (localPlaySession != null) {
                activePlayStopwatch = Stopwatch()..start();
                unawaited(_playActivitySync?.syncPending());
              }
            } on Object {
              // A running emulator remains authoritative even if local activity
              // persistence is temporarily unavailable.
            }
          }
          final completed = await _awaitSession(session);
          startedSessionCompleted = true;
          return completed;
        }(),
        LaunchNotStarted(:final result) => result,
      };
    } finally {
      final session = heldSession;
      if (session != null) {
        activeLaunchSession?.clearIfCurrent(session);
      }
    }
    if (startedSessionCompleted && result is LaunchExited) {
      final endedAt = _now();
      final recordedSession = localPlaySession;
      if (recordedSession != null) {
        activePlayStopwatch?.stop();
        try {
          await _playActivity?.recordEnded(
            sessionId: recordedSession.sessionId,
            endedAt: endedAt,
            activeDurationSeconds: activePlayStopwatch?.elapsed.inSeconds ?? 0,
          );
          unawaited(_playActivitySync?.syncPending());
        } on Object {
          // Local aggregate history and the emulator result remain usable even
          // if the detailed session cannot be closed immediately.
        }
      }
      try {
        await _playHistory.recordEnded(target: target, endedAt: endedAt);
      } on Object {
        // Gameplay already completed. History persistence must not rewrite the
        // emulator outcome or prevent foreground recovery.
      }
      await _windowController?.reclaimForeground();
    }
    yield PlayCompleted(result);
  }

  bool _targetMatches(
    ResolvedPlayTarget target,
    PlayRequest request,
    InstallAuthorityContext authority,
  ) =>
      target.serverInstanceId == authority.connection.instanceId.value &&
      target.localProfileId == authority.localProfileId &&
      target.releaseId == request.releaseId &&
      target.titleId == request.titleId;

  @override
  void close() {
    if (_closed) return;
    _closed = true;
    _authorizer.close();
  }

  Future<LaunchResult> _awaitSession(LaunchSession session) async {
    return session.completed;
  }

  LaunchProvider? _providerFor(RuntimeProfile profile) {
    for (final provider in _launchProviders) {
      if (provider.supports(profile)) {
        return provider;
      }
    }
    return null;
  }
}

final class _GameplayLaunchReservation implements LaunchSession {
  final Completer<LaunchResult> _never = Completer<LaunchResult>();

  @override
  Future<LaunchResult> get completed => _never.future;

  @override
  LaunchForegroundControl? get foreground => null;

  @override
  LaunchTermination? get termination => null;
}
