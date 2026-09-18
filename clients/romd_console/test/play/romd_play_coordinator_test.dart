import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/install_mutation_serializer.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/installed_play_target_resolver.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/session/data/romd_play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/launch_authorization.dart';
import 'package:romd_console/src/play/session/domain/launch_provider.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_activity.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';

import '../helpers/play_fakes.dart';

final class _ScriptedDependencyResolver implements RuntimeDependencyResolver {
  _ScriptedDependencyResolver(this.events);
  final List<DependencyProgress> events;
  final List<RuntimeProfile> profiles = <RuntimeProfile>[];
  @override
  Stream<DependencyProgress> resolve(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) {
    profiles.add(profile);
    return Stream<DependencyProgress>.fromIterable(events);
  }
}

final class _ThrowingDependencyResolver implements RuntimeDependencyResolver {
  @override
  Stream<DependencyProgress> resolve(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) => Stream<DependencyProgress>.error(StateError('boom'));
}

final _releaseId = RomdPublicId.tryParse(RomdPublicId.encode(1))!;
final _titleId = RomdPublicId.tryParse(RomdPublicId.encode(2))!;
final _instanceId = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;
final _authority = InstallAuthorityContext(
  localProfileId: 'profile-1',
  connection: ProfileServerConnection(
    instanceId: _instanceId,
    origin: Uri.parse('https://library.example'),
    firstSeenAt: DateTime.utc(2026),
    lastSeenAt: DateTime.utc(2026),
  ),
  generation: 1,
);

PlayRequest _request(String platformShortName) => PlayRequest(
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  displayName: platformShortName,
);

ResolvedPlayTarget _target(String platformShortName) => ResolvedPlayTarget(
  serverInstanceId: _instanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  platformShortName: platformShortName,
  displayName: 'Some Game',
  localProfileId: 'profile-1',
  contentRoot: '/c',
  launchAbsolutePath: '/c/game.bin',
  saveRoot: '/s',
  stateRoot: '/st',
  configRoot: '/cfg',
);

final class _AllowAuthorizer implements LaunchAuthorizer {
  @override
  InstallAuthorityContext? get authority => _authority;

  @override
  Future<LaunchAuthorizationResult> authorize(PlayRequest request) async =>
      LaunchAuthorized(
        authority: _authority,
        releaseId: _releaseId,
        titleId: _titleId,
      );

  @override
  void close() {}
}

final class _TargetResolver implements InstalledPlayTargetResolver {
  _TargetResolver({this.target});

  final ResolvedPlayTarget? target;
  int calls = 0;

  @override
  Future<ResolvedPlayTarget?> resolveInstalledForLaunch({
    required InstallAuthorityContext authority,
    required String releaseId,
    required String titleId,
    required String displayName,
  }) async {
    calls++;
    return target ?? _target(displayName);
  }
}

final class _ResultAuthorizer implements LaunchAuthorizer {
  _ResultAuthorizer(this.result, {this.authorizationGate});

  final LaunchAuthorizationResult result;
  final Future<void>? authorizationGate;
  int calls = 0;

  @override
  InstallAuthorityContext? get authority => _authority;

  @override
  Future<LaunchAuthorizationResult> authorize(PlayRequest request) async {
    calls++;
    final gate = authorizationGate;
    if (gate != null) await gate;
    return result;
  }

  @override
  void close() {}
}

final class _NoopRules implements RuntimeOverrideRuleRepository {
  @override
  Future<List<RuntimeOverrideRule>> rulesMatching({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) async => const <RuntimeOverrideRule>[];

  @override
  Future<void> setRule({
    required RuntimeOverrideScope scope,
    required String scopeValue,
    required RuntimeProfileId profileId,
  }) async {}
}

final class _RecordingRules implements RuntimeOverrideRuleRepository {
  int writes = 0;

  @override
  Future<List<RuntimeOverrideRule>> rulesMatching({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) async => const <RuntimeOverrideRule>[];

  @override
  Future<void> setRule({
    required RuntimeOverrideScope scope,
    required String scopeValue,
    required RuntimeProfileId profileId,
  }) async {
    writes++;
  }
}

final class _RecordingPlayActivity implements PlayActivityRepository {
  final List<({ResolvedPlayTarget target, DateTime startedAt})> starts = [];
  final List<({String sessionId, DateTime endedAt, int duration})> ends = [];

  @override
  Future<LocalPlaySession?> recordStarted({
    required ResolvedPlayTarget target,
    required DateTime startedAt,
  }) async {
    starts.add((target: target, startedAt: startedAt));
    return LocalPlaySession(
      sessionId: '11111111-2222-4333-8444-555555555555',
      localProfileId: target.localProfileId,
      serverInstanceId: _instanceId,
      clientId: 'ottercade.console',
      titleId: target.titleId,
      releaseId: target.releaseId,
      startedAt: startedAt,
      endedAt: null,
      activeDurationSeconds: null,
    );
  }

  @override
  Future<void> recordEnded({
    required String sessionId,
    required DateTime endedAt,
    required int activeDurationSeconds,
  }) async => ends.add((
    sessionId: sessionId,
    endedAt: endedAt,
    duration: activeDurationSeconds,
  ));

  @override
  Future<void> clearSessions() async {}

  @override
  Future<void> deleteSession(String sessionId) async {}

  @override
  Future<bool> isSyncEnabled() async => false;

  @override
  Future<void> markAttemptFailed(String sessionId, DateTime retryAt) async {}

  @override
  Future<void> markSynced(String sessionId) async {}

  @override
  Future<List<LocalPlaySession>> pendingSnapshots({int limit = 50}) async => [];

  @override
  Future<void> setSyncEnabled({
    required bool enabled,
    PlayActivityEnableScope scope = PlayActivityEnableScope.futureOnly,
  }) async {}
}

RomdPlayCoordinator _coordinator({
  required RuntimeDependencyResolver dependencyResolver,
  required RecordingLaunch launch,
  RecordingProfilePlayHistoryRepository? playHistory,
  ActiveLaunchSession? activeLaunchSession,
  RecordingWindowController? windowController,
  LaunchAuthorizer? authorizer,
  InstalledPlayTargetResolver? targetResolver,
  InstallMutationSerializer? mutationSerializer,
  RuntimeOverrideRuleRepository? runtimeRules,
  PlayActivityRepository? playActivity,
  DateTime Function()? now,
}) => RomdPlayCoordinator(
  resolver: const LocalRuntimeResolver(),
  launchProviders: <LaunchProvider>[_provider(dependencyResolver, launch)],
  playHistory: playHistory ?? RecordingProfilePlayHistoryRepository(),
  authorizer: authorizer ?? _AllowAuthorizer(),
  installedTargetResolver: targetResolver ?? _TargetResolver(),
  mutationSerializer: mutationSerializer ?? InstallMutationSerializer(),
  runtimeRules: runtimeRules ?? _NoopRules(),
  playActivity: playActivity,
  activeLaunchSession: activeLaunchSession,
  windowController: windowController,
  now: now,
);

LaunchProvider _provider(
  RuntimeDependencyResolver dependencyResolver,
  RecordingLaunch launch,
) => ScriptedLaunchProvider(
  dependencyResolver: dependencyResolver,
  recorder: launch,
);

const List<ResolvedRuntimeDependency> _resolvedDependencies =
    <ResolvedRuntimeDependency>[
      ResolvedRuntimeDependency(
        requirementId: 'executable',
        path: '/rt/RetroArch.app',
      ),
      ResolvedRuntimeDependency(
        requirementId: 'core',
        path: '/rt/cores/snes9x_libretro.dylib',
      ),
    ];

const List<DependencyProgress> _happyResolution = <DependencyProgress>[
  DependencyDownloading(label: 'RetroArch', receivedBytes: 10),
  DependencyUnpacking('RetroArch'),
  DependenciesReady(_resolvedDependencies),
];

final class _CompletingTermination implements LaunchTermination {
  _CompletingTermination(this._completion);

  final Completer<void> _completion;
  int calls = 0;

  @override
  Future<bool> terminate() {
    calls += 1;
    if (!_completion.isCompleted) {
      _completion.complete();
    }
    return Future<bool>.value(true);
  }
}

final class _OccupiedSession implements LaunchSession {
  const _OccupiedSession();

  @override
  Future<LaunchResult> get completed => Completer<LaunchResult>().future;

  @override
  LaunchForegroundControl? get foreground => null;

  @override
  LaunchTermination? get termination => null;
}

Future<void> _waitForActiveSession(ActiveLaunchSession activeSession) async {
  for (var i = 0; i < 20; i++) {
    if (activeSession.current != null) {
      return;
    }
    await Future<void>.delayed(Duration.zero);
  }
  fail('Timed out waiting for an active launch session.');
}

void main() {
  test(
    'passes the reviewed controller snapshot through runtime preparation',
    () async {
      final dependencyResolver = _ScriptedDependencyResolver(_happyResolution);
      final launch = RecordingLaunch(const LaunchExited(0));
      final provider = ScriptedLaunchProvider(
        dependencyResolver: dependencyResolver,
        recorder: launch,
      );
      final history = RecordingProfilePlayHistoryRepository();
      final coordinator = RomdPlayCoordinator(
        resolver: const LocalRuntimeResolver(),
        launchProviders: <LaunchProvider>[provider],
        playHistory: history,
        authorizer: _AllowAuthorizer(),
        installedTargetResolver: _TargetResolver(),
        mutationSerializer: InstallMutationSerializer(),
        runtimeRules: _NoopRules(),
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: 0,
        claims: const <ControllerSlotClaim?>[],
        devices: const <ConnectedGamepad>[],
      );

      await coordinator
          .play(_request('snes'), controllerSnapshot: snapshot)
          .toList();

      expect(provider.lastControllerSnapshot, same(snapshot));
      expect(history.ended.single.target.localProfileId, 'profile-1');
    },
  );

  test(
    'unsupported platform completes immediately without resolving or launching',
    () async {
      final dependencyResolver = _ScriptedDependencyResolver(_happyResolution);
      final launch = RecordingLaunch(const LaunchExited(0));

      final events = await _coordinator(
        dependencyResolver: dependencyResolver,
        launch: launch,
      ).play(_request('saturn')).toList();

      expect(events, hasLength(1));
      final completed = events.single as PlayCompleted;
      expect(completed.result, isA<LaunchUnsupportedPlatform>());
      expect(
        (completed.result as LaunchUnsupportedPlatform).platformShortName,
        'saturn',
      );
      expect(dependencyResolver.profiles, isEmpty);
      expect(launch.plans, isEmpty);
    },
  );

  test('a profile without a registered dependency resolver completes as '
      'LaunchUnsupportedPlatform', () async {
    final launch = RecordingLaunch(const LaunchExited(0));
    final coordinator = RomdPlayCoordinator(
      resolver: const LocalRuntimeResolver(),
      launchProviders: const <LaunchProvider>[],
      playHistory: RecordingProfilePlayHistoryRepository(),
      authorizer: _AllowAuthorizer(),
      installedTargetResolver: _TargetResolver(),
      mutationSerializer: InstallMutationSerializer(),
      runtimeRules: _NoopRules(),
    );

    final events = await coordinator.play(_request('snes')).toList();

    expect(events, hasLength(1));
    final completed = events.single as PlayCompleted;
    expect(
      (completed.result as LaunchUnsupportedPlatform).platformShortName,
      'snes',
    );
    expect(launch.plans, isEmpty);
  });

  test('happy path forwards activities, launches, and completes with the '
      "resolver's dependencies", () async {
    final dependencyResolver = _ScriptedDependencyResolver(_happyResolution);
    final launch = RecordingLaunch(const LaunchExited(0));

    final events = await _coordinator(
      dependencyResolver: dependencyResolver,
      launch: launch,
    ).play(_request('snes')).toList();

    expect(events, hasLength(4));
    expect(
      (events[0] as PlayPreparingRuntime).activity,
      isA<DependencyDownloading>(),
    );
    expect(
      (events[1] as PlayPreparingRuntime).activity,
      isA<DependencyUnpacking>(),
    );
    expect(events[2], isA<PlayLaunching>());
    expect((events[3] as PlayCompleted).result, isA<LaunchExited>());

    final plan = launch.plans.single;
    expect(plan.profile.id.value, 'retroarch:snes:snes9x');
    expect(plan.dependencies, same(_resolvedDependencies));
    expect(plan.dependencyPath('core'), '/rt/cores/snes9x_libretro.dylib');
    expect(plan.target.platformShortName, 'snes');
    expect(dependencyResolver.profiles.single.id.value, plan.profile.id.value);
  });

  test(
    'started session exit marks play history and reclaims foreground once',
    () async {
      final history = RecordingProfilePlayHistoryRepository();
      final windowController = RecordingWindowController();
      final launch = RecordingLaunch(const LaunchExited(0));

      final events = await _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: launch,
        playHistory: history,
        windowController: windowController,
      ).play(_request('snes')).toList();

      expect((events.last as PlayCompleted).result, isA<LaunchExited>());
      expect(history.ended.single.target.releaseId, _releaseId.value);
      expect(history.ended.single.target.localProfileId, 'profile-1');
      expect(history.ended.single.target.serverInstanceId, _instanceId.value);
      expect(history.ended.single.target.titleId, _titleId.value);
      expect(windowController.reclaimCount, 1);
      expect(launch.sessions, hasLength(1));
      expect(launch.sessions.single.completedReads, 1);
      expect(launch.sessions.single.completionStarts, 1);
    },
  );

  test('nonzero process exit records exact profile history once', () async {
    final history = RecordingProfilePlayHistoryRepository();
    final endedAt = DateTime.utc(2026, 7, 18, 12);
    final events = await _coordinator(
      dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
      launch: RecordingLaunch(const LaunchExited(7)),
      playHistory: history,
      now: () => endedAt,
    ).play(_request('snes')).toList();

    expect(((events.last as PlayCompleted).result as LaunchExited).exitCode, 7);
    expect(history.ended, hasLength(1));
    expect(history.ended.single.endedAt, endedAt);
    expect(history.ended.single.target.localProfileId, 'profile-1');
    expect(history.ended.single.target.serverInstanceId, _instanceId.value);
    expect(history.ended.single.target.titleId, _titleId.value);
    expect(history.ended.single.target.releaseId, _releaseId.value);
  });

  test(
    'started process records and closes one detailed play session',
    () async {
      final activity = _RecordingPlayActivity();
      final times = <DateTime>[
        DateTime.utc(2026, 7, 18, 12),
        DateTime.utc(2026, 7, 18, 12, 3),
      ].iterator;

      final events = await _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: RecordingLaunch(const LaunchExited(7)),
        playActivity: activity,
        now: () {
          times.moveNext();
          return times.current;
        },
      ).play(_request('snes')).toList();

      expect(
        ((events.last as PlayCompleted).result as LaunchExited).exitCode,
        7,
      );
      expect(activity.starts, hasLength(1));
      expect(activity.starts.single.startedAt, DateTime.utc(2026, 7, 18, 12));
      expect(activity.starts.single.target.titleId, _titleId.value);
      expect(activity.ends, hasLength(1));
      expect(
        activity.ends.single.sessionId,
        '11111111-2222-4333-8444-555555555555',
      );
      expect(activity.ends.single.endedAt, DateTime.utc(2026, 7, 18, 12, 3));
      expect(activity.ends.single.duration, greaterThanOrEqualTo(0));
    },
  );

  test(
    'a competing active session rejects before starting a new process',
    () async {
      final activeSession = ActiveLaunchSession()
        ..hold(const _OccupiedSession());
      final completion = Completer<void>();
      final termination = _CompletingTermination(completion);
      final launch = RecordingLaunch(
        const LaunchExited(0),
        termination: termination,
      );
      final history = RecordingProfilePlayHistoryRepository();

      final events = await _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: launch,
        playHistory: history,
        activeLaunchSession: activeSession,
      ).play(_request('snes')).toList();

      final result = (events.last as PlayCompleted).result;
      expect(result, isA<LaunchFailed>());
      expect((result as LaunchFailed).message, contains('already open'));
      expect(launch.plans, isEmpty);
      expect(termination.calls, 0);
      expect(activeSession.current, isA<_OccupiedSession>());
      expect(history.ended, isEmpty);
    },
  );

  test(
    'LaunchNotStarted results never record history or reclaim foreground',
    () async {
      for (final result in <LaunchResult>[
        const LaunchExited(0),
        const LaunchExited(7),
        const LaunchRuntimeMissing(<String>['none'], runtimeName: 'RetroArch'),
        const LaunchFailed("Couldn't launch the game."),
        const LaunchUnsupportedPlatform('snes'),
      ]) {
        final history = RecordingProfilePlayHistoryRepository();
        final windowController = RecordingWindowController();
        final launch = RecordingLaunch.notStarted(result);
        final activity = _RecordingPlayActivity();

        await _coordinator(
          dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
          launch: launch,
          playHistory: history,
          windowController: windowController,
          playActivity: activity,
        ).play(_request('snes')).toList();

        expect(launch.plans, hasLength(1));
        expect(launch.sessions, isEmpty);
        expect(history.ended, isEmpty);
        expect(activity.starts, isEmpty);
        expect(activity.ends, isEmpty);
        expect(windowController.reclaimCount, 0);
      }
    },
  );

  test(
    'started session completion only marks history for LaunchExited',
    () async {
      final history = RecordingProfilePlayHistoryRepository();
      final windowController = RecordingWindowController();
      final launch = RecordingLaunch(const LaunchFailed("Couldn't launch."));

      await _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: launch,
        playHistory: history,
        windowController: windowController,
      ).play(_request('snes')).toList();

      expect(launch.sessions, hasLength(1));
      expect(launch.sessions.single.completedReads, 1);
      expect(history.ended, isEmpty);
      expect(windowController.reclaimCount, 0);
    },
  );

  test(
    'active session termination completes the play stream through exit',
    () async {
      final activeSession = ActiveLaunchSession();
      final history = RecordingProfilePlayHistoryRepository();
      final windowController = RecordingWindowController();
      final completion = Completer<void>();
      final termination = _CompletingTermination(completion);
      final launch = RecordingLaunch(
        const LaunchExited(0),
        gate: completion.future,
        termination: termination,
      );

      final events = _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: launch,
        playHistory: history,
        activeLaunchSession: activeSession,
        windowController: windowController,
      ).play(_request('snes')).toList();

      await _waitForActiveSession(activeSession);
      final terminateResult = await activeSession.terminate();
      final completedEvents = await events;

      expect(terminateResult, isA<ActiveLaunchTerminationDelivered>());
      expect(
        (terminateResult as ActiveLaunchTerminationDelivered).delivered,
        isTrue,
      );
      expect(termination.calls, 1);
      expect(activeSession.current, isNull);
      expect(
        (completedEvents.last as PlayCompleted).result,
        isA<LaunchExited>(),
      );
      expect(history.ended.single.target.releaseId, _releaseId.value);
      expect(windowController.reclaimCount, 1);
    },
  );

  test(
    'a resolution failure completes as LaunchFailed without launching',
    () async {
      final launch = RecordingLaunch(const LaunchExited(0));

      final events = await _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(
          const <DependencyProgress>[
            DependencyResolutionFailed('Runtime install failed. Try again.'),
          ],
        ),
        launch: launch,
      ).play(_request('snes')).toList();

      final completed = events.last as PlayCompleted;
      expect(
        (completed.result as LaunchFailed).message,
        'Runtime install failed. Try again.',
      );
      expect(launch.plans, isEmpty);
    },
  );

  test(
    'a missing executable completes as LaunchRuntimeMissing without launching',
    () async {
      final launch = RecordingLaunch(const LaunchExited(0));

      final events = await _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(
          const <DependencyProgress>[
            DependencyExecutableMissing(<String>[
              'nothing on PATH',
            ], executableName: 'RetroArch'),
          ],
        ),
        launch: launch,
      ).play(_request('snes')).toList();

      final completed = events.last as PlayCompleted;
      expect((completed.result as LaunchRuntimeMissing).diagnostics, <String>[
        'nothing on PATH',
      ]);
      expect(
        (completed.result as LaunchRuntimeMissing).runtimeName,
        'RetroArch',
      );
      expect(launch.plans, isEmpty);
    },
  );

  test('a resolver error completes as a sanitized LaunchFailed', () async {
    final launch = RecordingLaunch(const LaunchExited(0));

    final events = await _coordinator(
      dependencyResolver: _ThrowingDependencyResolver(),
      launch: launch,
    ).play(_request('snes')).toList();

    expect(events, hasLength(1));
    final completed = events.single as PlayCompleted;
    expect(
      (completed.result as LaunchFailed).message,
      'Something went wrong preparing the runtime.',
    );
    expect(launch.plans, isEmpty);
  });

  test(
    'authorization denial has zero installed/runtime/process side effects',
    () async {
      for (final denied in <LaunchAuthorizationResult>[
        const AuthorizationNotGranted(),
        const AuthorizationRevoked(),
        const LaunchAuthorizationUnavailable(),
      ]) {
        final authorizer = _ResultAuthorizer(denied);
        final targetResolver = _TargetResolver();
        final dependencyResolver = _ScriptedDependencyResolver(
          _happyResolution,
        );
        final launch = RecordingLaunch(const LaunchExited(0));
        final history = RecordingProfilePlayHistoryRepository();

        final events = await _coordinator(
          dependencyResolver: dependencyResolver,
          launch: launch,
          playHistory: history,
          authorizer: authorizer,
          targetResolver: targetResolver,
        ).play(_request('snes')).toList();

        expect(authorizer.calls, 1);
        expect(targetResolver.calls, 0);
        expect(dependencyResolver.profiles, isEmpty);
        expect(launch.plans, isEmpty);
        expect(history.ended, isEmpty);
        expect(events, hasLength(1));
      }
    },
  );

  test('Run With denial writes no runtime preference', () async {
    final rules = _RecordingRules();
    final events =
        await _coordinator(
              dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
              launch: RecordingLaunch(const LaunchExited(0)),
              authorizer: _ResultAuthorizer(const AuthorizationRevoked()),
              runtimeRules: rules,
            )
            .play(
              PlayRequest(
                releaseId: _releaseId.value,
                titleId: _titleId.value,
                displayName: 'snes',
                requestedRuntimeProfileId: const RuntimeProfileId(
                  'retroarch:snes:snes9x',
                ),
              ),
            )
            .toList();

    expect((events.single as PlayCompleted).result, isA<LaunchAccessRevoked>());
    expect(rules.writes, 0);
  });

  test(
    'fabricated resolved target identity blocks before runtime work',
    () async {
      final valid = _target('snes');
      final mismatches = <ResolvedPlayTarget>[
        ResolvedPlayTarget(
          serverInstanceId: '22222222-2222-4222-8222-222222222222',
          releaseId: valid.releaseId,
          titleId: valid.titleId,
          platformShortName: valid.platformShortName,
          displayName: valid.displayName,
          localProfileId: valid.localProfileId,
          contentRoot: valid.contentRoot,
          launchAbsolutePath: valid.launchAbsolutePath,
          saveRoot: valid.saveRoot,
          stateRoot: valid.stateRoot,
          configRoot: valid.configRoot,
        ),
        ResolvedPlayTarget(
          serverInstanceId: valid.serverInstanceId,
          releaseId: RomdPublicId.encode(99),
          titleId: valid.titleId,
          platformShortName: valid.platformShortName,
          displayName: valid.displayName,
          localProfileId: valid.localProfileId,
          contentRoot: valid.contentRoot,
          launchAbsolutePath: valid.launchAbsolutePath,
          saveRoot: valid.saveRoot,
          stateRoot: valid.stateRoot,
          configRoot: valid.configRoot,
        ),
        ResolvedPlayTarget(
          serverInstanceId: valid.serverInstanceId,
          releaseId: valid.releaseId,
          titleId: RomdPublicId.encode(98),
          platformShortName: valid.platformShortName,
          displayName: valid.displayName,
          localProfileId: valid.localProfileId,
          contentRoot: valid.contentRoot,
          launchAbsolutePath: valid.launchAbsolutePath,
          saveRoot: valid.saveRoot,
          stateRoot: valid.stateRoot,
          configRoot: valid.configRoot,
        ),
        ResolvedPlayTarget(
          serverInstanceId: valid.serverInstanceId,
          releaseId: valid.releaseId,
          titleId: valid.titleId,
          platformShortName: valid.platformShortName,
          displayName: valid.displayName,
          localProfileId: 'profile-2',
          contentRoot: valid.contentRoot,
          launchAbsolutePath: valid.launchAbsolutePath,
          saveRoot: valid.saveRoot,
          stateRoot: valid.stateRoot,
          configRoot: valid.configRoot,
        ),
      ];

      for (final mismatch in mismatches) {
        final dependencies = _ScriptedDependencyResolver(_happyResolution);
        final launch = RecordingLaunch(const LaunchExited(0));
        final events = await _coordinator(
          dependencyResolver: dependencies,
          launch: launch,
          targetResolver: _TargetResolver(target: mismatch),
        ).play(_request('snes')).toList();
        expect((events.single as PlayCompleted).result, isA<LaunchFailed>());
        expect(dependencies.profiles, isEmpty);
        expect(launch.plans, isEmpty);
      }
    },
  );

  test(
    'authorization permit identity mismatch blocks before install probing',
    () async {
      final mismatchedPermit = _ResultAuthorizer(
        LaunchAuthorized(
          authority: _authority,
          releaseId: RomdPublicId.tryParse(RomdPublicId.encode(55))!,
          titleId: _titleId,
        ),
      );
      final targetResolver = _TargetResolver();
      final events = await _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: RecordingLaunch(const LaunchExited(0)),
        authorizer: mismatchedPermit,
        targetResolver: targetResolver,
      ).play(_request('snes')).toList();

      expect((events.single as PlayCompleted).result, isA<LaunchFailed>());
      expect(targetResolver.calls, 0);
    },
  );

  test(
    'the exact install serializer holds competing launches through precheck',
    () async {
      final gate = Completer<void>();
      final firstAuthorizer = _ResultAuthorizer(
        LaunchAuthorized(
          authority: _authority,
          releaseId: _releaseId,
          titleId: _titleId,
        ),
        authorizationGate: gate.future,
      );
      final secondAuthorizer = _ResultAuthorizer(
        LaunchAuthorized(
          authority: _authority,
          releaseId: _releaseId,
          titleId: _titleId,
        ),
      );
      final serializer = InstallMutationSerializer();
      final first = _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: RecordingLaunch(const LaunchExited(0)),
        authorizer: firstAuthorizer,
        mutationSerializer: serializer,
      ).play(_request('snes')).toList();
      for (var i = 0; i < 10 && firstAuthorizer.calls == 0; i++) {
        await Future<void>.delayed(Duration.zero);
      }
      final second = _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: RecordingLaunch(const LaunchExited(0)),
        authorizer: secondAuthorizer,
        mutationSerializer: serializer,
      ).play(_request('snes')).toList();
      await Future<void>.delayed(Duration.zero);
      expect(firstAuthorizer.calls, 1);
      expect(secondAuthorizer.calls, 0);
      gate.complete();
      await Future.wait(<Future<List<PlayProgress>>>[first, second]);
      expect(secondAuthorizer.calls, 1);
    },
  );

  test('one app-scoped active session blocks another graph', () async {
    final activeSession = ActiveLaunchSession();
    final firstGate = Completer<void>();
    final firstLaunch = RecordingLaunch(
      const LaunchExited(0),
      gate: firstGate.future,
    );
    final first = _coordinator(
      dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
      launch: firstLaunch,
      activeLaunchSession: activeSession,
    ).play(_request('snes')).toList();
    await _waitForActiveSession(activeSession);

    final secondLaunch = RecordingLaunch(const LaunchExited(0));
    final secondEvents = await _coordinator(
      dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
      launch: secondLaunch,
      activeLaunchSession: activeSession,
    ).play(_request('snes')).toList();

    expect((secondEvents.last as PlayCompleted).result, isA<LaunchFailed>());
    expect(secondLaunch.plans, isEmpty);
    firstGate.complete();
    await first;
  });

  test(
    'closing a graph while gameplay runs retains original history owner',
    () async {
      final activeSession = ActiveLaunchSession();
      final gate = Completer<void>();
      final history = RecordingProfilePlayHistoryRepository();
      final coordinator = _coordinator(
        dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
        launch: RecordingLaunch(const LaunchExited(0), gate: gate.future),
        activeLaunchSession: activeSession,
        playHistory: history,
      );
      final events = coordinator.play(_request('snes')).toList();
      await _waitForActiveSession(activeSession);
      coordinator.close();
      gate.complete();
      final completed = await events;

      expect((completed.last as PlayCompleted).result, isA<LaunchExited>());
      expect(history.ended.single.target.localProfileId, 'profile-1');
      expect(history.ended.single.target.serverInstanceId, _instanceId.value);
    },
  );

  test('closing a stale graph stops an in-flight prelaunch attempt', () async {
    final gate = Completer<void>();
    final authorizer = _ResultAuthorizer(
      LaunchAuthorized(
        authority: _authority,
        releaseId: _releaseId,
        titleId: _titleId,
      ),
      authorizationGate: gate.future,
    );
    final targetResolver = _TargetResolver();
    final launch = RecordingLaunch(const LaunchExited(0));
    final coordinator = _coordinator(
      dependencyResolver: _ScriptedDependencyResolver(_happyResolution),
      launch: launch,
      authorizer: authorizer,
      targetResolver: targetResolver,
    );
    final events = coordinator.play(_request('snes')).toList();
    for (var i = 0; i < 10 && authorizer.calls == 0; i++) {
      await Future<void>.delayed(Duration.zero);
    }
    coordinator.close();
    gate.complete();
    final completed = await events;

    expect((completed.single as PlayCompleted).result, isA<LaunchFailed>());
    expect(targetResolver.calls, 0);
    expect(launch.plans, isEmpty);
  });
}
