import 'dart:async';
import 'dart:io';

import 'package:drift/drift.dart' hide isNull, isNotNull;
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/disc_sets/disc_set_playback.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_failure.dart';
import 'package:romd_console/src/play/emulator/data/adapters/retroarch/retroarch_config_writer.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/launch_provider.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

import 'disc_set_fixture.dart';

final class Provider implements LaunchProvider {
  int prepares = 0, launches = 0;
  ResolvedPlayTarget? target;
  Future<void> Function()? beforeReady;
  Future<void> Function()? beforeStart;
  RunningSession? running;
  @override
  bool supports(RuntimeProfile profile) => true;
  @override
  Stream<DependencyProgress> prepare(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) async* {
    prepares++;
    this.target = target;
    await beforeReady?.call();
    yield const DependenciesReady([]);
  }

  @override
  Future<LaunchStartResult> launch({
    required RuntimeProfile profile,
    required ResolvedPlayTarget target,
    required List<ResolvedRuntimeDependency> dependencies,
    ReviewedLaunchSnapshot? controllerSnapshot,
  }) async {
    launches++;
    await beforeStart?.call();
    return running == null
        ? const LaunchNotStarted(LaunchFailed('synthetic launch'))
        : LaunchStarted(running!);
  }
}

final class RunningSession implements LaunchSession, LaunchTermination {
  final done = Completer<LaunchResult>();
  int terminations = 0;
  @override
  Future<LaunchResult> get completed => done.future;
  @override
  LaunchTermination get termination => this;
  @override
  LaunchForegroundControl? get foreground => null;
  @override
  Future<bool> terminate() async {
    terminations++;
    if (!done.isCompleted) done.complete(const LaunchExited(0));
    return true;
  }
}

void main() {
  late Harness h;
  late Provider provider;
  setUp(() async {
    h = await Harness.create();
    provider = Provider();
    await h.install();
  });
  tearDown(() => h.close());
  test(
    'shipped default preserves SwanStation order but blocks unqualified playback',
    () async {
      final profiles = BuiltinRuntimeProfiles.all
          .where((p) => p.supportsPlatform('psx'))
          .toList();
      expect(profiles.first.id.value, 'retroarch:psx:swanstation');
      expect(profiles.first.supportsOriginalDiscSets, isTrue);
      final playback = DiscSetPlayback(
        service: h.service,
        providers: [provider],
      );
      final result =
          (await playback.play((await h.service.installed()).single).toList())
                  .last
              as PlayCompleted;
      expect(
        result.result,
        isA<LaunchFailed>().having(
          (r) => r.message,
          'message',
          contains('qualification'),
        ),
      );
      expect(provider.prepares, 0);
      expect(provider.launches, 0);
    },
  );
  test(
    'explicit denial happens before provisioning/config/filesystem effects',
    () async {
      h.api.allowed = false;
      final playback = DiscSetPlayback(
        service: h.service,
        providers: [provider],
        runtimeQualified: true,
      );
      final result =
          (await playback.play((await h.service.installed()).single).toList())
                  .last
              as PlayCompleted;
      expect(result.result, isA<LaunchAccessNotGranted>());
      expect(provider.prepares, 0);
      expect(provider.launches, 0);
      expect(
        await h.service.store
            .durable(h.api.returned, 'profile-a', 'config')
            .exists(),
        isFalse,
      );
      expect(h.service.activeSession.hasSession, isFalse);
    },
  );
  test(
    'synthetic provider receives explicit group identity and verified M3U',
    () async {
      final local = (await h.service.installed()).single;
      final playback = DiscSetPlayback(
        service: h.service,
        providers: [provider],
        runtimeQualified: true,
      );
      await playback.play(local).toList();
      expect(provider.prepares, 1);
      expect(provider.launches, 1);
      final target = provider.target!;
      expect(target.releaseId, isNull);
      expect(target.discSetIdentity!.discSetId, setId);
      expect(target.discSetIdentity!.snapshotId, local.plan.snapshotId);
      expect(target.launchAbsolutePath, endsWith('/playlist.m3u'));
      expect(target.saveRoot, contains('/disc-sets/$setId/saves'));
      expect(target.stateRoot, contains('/${local.plan.snapshotId}'));
      expect(target.configRoot, isNot(startsWith(target.contentRoot)));
      expect(h.service.activeSession.hasSession, isFalse);
    },
  );
  test(
    'corrupt playlist blocks runtime preparation after policy check',
    () async {
      final local = (await h.service.installed()).single;
      await h.service.store
          .file(h.service.store.root(local.plan), 'playlist.m3u')
          .writeAsString('tampered');
      await DiscSetPlayback(
        service: h.service,
        providers: [provider],
        runtimeQualified: true,
      ).play(local).toList();
      expect(provider.prepares, 0);
      expect(provider.launches, 0);
    },
  );
  test('newer revoke during provisioning blocks process creation', () async {
    final local = (await h.service.installed()).single;
    provider.beforeReady = () async {
      h.api.allowed = false;
      expect(await h.service.authorize(local), isFalse);
      h.api.error = const DiscSetException(DiscSetFailureKind.unavailable);
    };
    final result =
        (await DiscSetPlayback(
              service: h.service,
              providers: [provider],
              runtimeQualified: true,
            ).play(local).toList()).last
            as PlayCompleted;
    expect(result.result, isA<LaunchAccessNotGranted>());
    expect(provider.prepares, 1);
    expect(provider.launches, 0);
    expect((await h.service.installed()).single.authorized, isFalse);
    expect(h.service.activeSession.hasSession, isFalse);
  });
  test('process lifetime does not hold the group authority fence', () async {
    final local = (await h.service.installed()).single;
    final running = RunningSession();
    provider.running = running;
    final started = Completer<void>();
    provider.beforeStart = () async => started.complete();
    final playback = DiscSetPlayback(
      service: h.service,
      providers: [provider],
      runtimeQualified: true,
    ).play(local).toList();
    try {
      await started.future;
      h.api.allowed = false;
      expect(
        await h.service.authorize(local).timeout(const Duration(seconds: 2)),
        isFalse,
      );
      expect((await h.service.installed()).single.authorized, isFalse);
      expect(running.done.isCompleted, isFalse);
    } finally {
      if (!running.done.isCompleted)
        running.done.complete(const LaunchExited(0));
      await playback;
    }
    expect(h.service.activeSession.hasSession, isFalse);
  });
  test(
    'profile switch during process start terminates and joins the stale session',
    () async {
      final local = (await h.service.installed()).single;
      final running = RunningSession();
      provider.running = running;
      provider.beforeStart = () async {
        await (h.db.update(
          h.db.localProfiles,
        )..where((r) => r.id.equals('profile-a'))).write(
          const LocalProfilesCompanion(serverSelectionGeneration: Value(1)),
        );
      };
      final result =
          (await DiscSetPlayback(
                service: h.service,
                providers: [provider],
                runtimeQualified: true,
              ).play(local).toList()).last
              as PlayCompleted;
      expect(result.result, isA<LaunchAccessNotGranted>());
      expect(provider.launches, 1);
      expect(running.terminations, 1);
      expect(running.done.isCompleted, isTrue);
      expect(h.service.activeSession.hasSession, isFalse);
    },
  );
  test(
    'group config owns shared playlist memory card and controller disc menu',
    () async {
      final local = (await h.service.installed()).single;
      await DiscSetPlayback(
        service: h.service,
        providers: [provider],
        runtimeQualified: true,
      ).play(local).toList();
      final target = provider.target!;
      final path = await const RetroArchConfigWriter().write(target);
      final config = await File(path).readAsString();
      expect(config, contains('savefile_directory = "${target.saveRoot}"'));
      expect(config, contains('input_menu_toggle_gamepad_combo = "4"'));
      expect(config, contains('notification_show_disk_control = "true"'));
      expect(config, contains('global_core_options = "true"'));
      expect(config, contains('game_specific_options = "false"'));
      expect(
        config,
        contains(
          'core_options_path = "${target.configRoot}/disc-set-core-options.cfg"',
        ),
      );
      expect(
        config,
        contains('rgui_config_directory = "${target.configRoot}"'),
      );
      expect(
        config,
        contains('input_remapping_directory = "${target.configRoot}/remaps"'),
      );
      final options = await File(
        '${target.configRoot}/disc-set-core-options.cfg',
      ).readAsString();
      expect(
        options,
        contains('swanstation_MemoryCards_Card1Type = "Libretro"'),
      );
      expect(
        options,
        contains('swanstation_MemoryCards_UsePlaylistTitle = "true"'),
      );
    },
  );
}
