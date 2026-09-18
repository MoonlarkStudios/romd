import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/runtime_bios_resolver.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/standalone_runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

final class _RecordingRuntimeManager implements RuntimeManager {
  _RecordingRuntimeManager(this.resolution);
  final RuntimeResolution resolution;
  RuntimeDescriptor? descriptor;
  int resolveCalls = 0;

  @override
  Future<RuntimeResolution> resolve(RuntimeDescriptor descriptor) async {
    resolveCalls++;
    this.descriptor = descriptor;
    return resolution;
  }
}

final class _FakeBiosCatalog implements BiosCatalog {
  final List<String> requestedPlatforms = <String>[];

  @override
  Future<List<BiosFileListing>> biosForPlatform(
    String platformShortName,
  ) async {
    requestedPlatforms.add(platformShortName);
    return const <BiosFileListing>[];
  }
}

final class _UnusedDownloadClient implements DownloadClient {
  @override
  Stream<int> download({required Uri url, required File destination}) =>
      throw UnimplementedError();
}

final class _ScriptedProvisioner implements RuntimeExecutableProvisioner {
  _ScriptedProvisioner(this.events);

  final List<RuntimeProvisionProgress> events;
  int ensureCalls = 0;

  @override
  Stream<RuntimeProvisionProgress> ensure() async* {
    ensureCalls++;
    for (final event in events) {
      yield event;
    }
  }
}

const _found = RuntimeResolution(
  runtimeId: 'duckstation',
  source: RuntimeResolutionSource.environment,
  executablePath: '/Applications/DuckStation.app',
);

const _notFound = RuntimeResolution(
  runtimeId: 'duckstation',
  source: RuntimeResolutionSource.notFound,
  executablePath: null,
  diagnostics: <String>['ROMD_DUCKSTATION_PATH missing'],
);

const _profile = RuntimeProfile(
  id: RuntimeProfileId('duckstation:psx:standalone'),
  adapterId: RuntimeAdapterId('duckstation'),
  displayName: 'DuckStation',
  supportedPlatforms: <String>{'psx'},
  requirements: <RuntimeDependencyRequirement>[
    ExecutableRequirement(id: 'duck-executable', displayName: 'DuckStation'),
    BiosSetRequirement(
      id: 'duck-bios',
      displayName: 'PlayStation BIOS',
      files: <BiosFileSpec>[
        BiosFileSpec(
          fileName: 'scph5500.bin',
          md5: '8dd7d5296a650fac7319bce665a6a53c',
        ),
      ],
      satisfaction: BiosSatisfaction.anyOne,
    ),
  ],
);

ResolvedPlayTarget _target(Directory tmp) => ResolvedPlayTarget(
  releaseId: 'rel-1',
  titleId: 'title-1',
  platformShortName: 'psx',
  displayName: 'Ridge Racer',
  localProfileId: 'profile-1',
  contentRoot: p.join(tmp.path, 'content'),
  launchAbsolutePath: p.join(tmp.path, 'content', 'ridge.cue'),
  saveRoot: p.join(tmp.path, 'saves'),
  stateRoot: p.join(tmp.path, 'states'),
  configRoot: p.join(tmp.path, 'config'),
);

void main() {
  late Directory tmp;
  late Directory biosDir;
  late _FakeBiosCatalog biosCatalog;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_standalone_dep_test');
    biosDir = Directory(p.join(tmp.path, 'duckstation', 'bios'))
      ..createSync(recursive: true);
    File(p.join(biosDir.path, 'scph5500.bin')).writeAsStringSync('bios');
    biosCatalog = _FakeBiosCatalog();
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  StandaloneRuntimeDependencyResolver resolver(
    RuntimeManager runtimeManager, {
    RuntimeExecutableProvisioner? provisioner,
  }) => StandaloneRuntimeDependencyResolver(
    runtimeDescriptor: duckStationRuntimeDescriptor,
    runtimeManager: runtimeManager,
    biosResolver: RuntimeBiosResolver(
      biosCatalog: biosCatalog,
      downloadClient: _UnusedDownloadClient(),
    ),
    biosDirectory: (_) => biosDir,
    provisioner: provisioner,
  );

  test('resolves standalone executable and BIOS directory', () async {
    final manager = _RecordingRuntimeManager(_found);

    final events = await resolver(
      manager,
    ).resolve(_profile, _target(tmp)).toList();

    expect(manager.descriptor, duckStationRuntimeDescriptor);
    expect(biosCatalog.requestedPlatforms, isEmpty);
    final ready = events.single as DependenciesReady;
    expect(ready.dependencies, hasLength(2));
    expect(ready.dependencies[0].requirementId, 'duck-executable');
    expect(ready.dependencies[0].path, '/Applications/DuckStation.app');
    expect(
      ready.dependencies[0].provenance,
      RuntimeDependencyProvenance.external,
    );
    expect(ready.dependencies[1].requirementId, 'duck-bios');
    expect(ready.dependencies[1].path, biosDir.path);
  });

  test('missing executable stops before BIOS acquisition', () async {
    final events = await resolver(
      _RecordingRuntimeManager(_notFound),
    ).resolve(_profile, _target(tmp)).toList();

    final missing = events.single as DependencyExecutableMissing;
    expect(missing.executableName, 'DuckStation');
    expect(missing.diagnostics, <String>['ROMD_DUCKSTATION_PATH missing']);
    expect(biosCatalog.requestedPlatforms, isEmpty);
  });

  test('reports missing BIOS when no required file is available', () async {
    File(p.join(biosDir.path, 'scph5500.bin')).deleteSync();

    final events = await resolver(
      _RecordingRuntimeManager(_found),
    ).resolve(_profile, _target(tmp)).toList();

    expect(events.single, isA<DependencyResolutionFailed>());
  });

  test('managed provisioner runs before executable resolution', () async {
    final provisioner = _ScriptedProvisioner(const <RuntimeProvisionProgress>[
      RuntimeProvisionStarted(),
      RuntimeProvisionDownloading(label: 'DuckStation', receivedBytes: 4),
      RuntimeProvisionUnpacking('DuckStation'),
      RuntimeExecutableReady(executablePath: '/managed/DuckStation.app'),
    ]);
    final manager = _RecordingRuntimeManager(_found);

    final events = await resolver(
      manager,
      provisioner: provisioner,
    ).resolve(_profile, _target(tmp)).toList();

    expect(provisioner.ensureCalls, 1);
    expect(manager.resolveCalls, 1);
    expect(events[0], isA<DependencyDownloading>());
    expect((events[0] as DependencyDownloading).label, 'DuckStation');
    expect(events[1], isA<DependencyUnpacking>());
    expect(
      (events.last as DependenciesReady).dependencies.first.path,
      _found.executablePath,
    );
  });

  test(
    'managed provision failure stops before executable resolution',
    () async {
      final manager = _RecordingRuntimeManager(_found);
      final provisioner = _ScriptedProvisioner(const <RuntimeProvisionProgress>[
        RuntimeProvisionFailed("DuckStation isn't available."),
      ]);

      final events = await resolver(
        manager,
        provisioner: provisioner,
      ).resolve(_profile, _target(tmp)).toList();

      expect(manager.resolveCalls, 0);
      expect(events.single, isA<DependencyResolutionFailed>());
      expect(biosCatalog.requestedPlatforms, isEmpty);
    },
  );
}
