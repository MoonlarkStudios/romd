import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/retroarch_dependency_resolver.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

final class _ScriptedProvisioner implements RuntimeProvisioner {
  _ScriptedProvisioner(this.events);
  final List<RuntimeProvisionProgress> events;
  final List<String> coreIds = <String>[];
  @override
  Stream<RuntimeProvisionProgress> ensure({required String coreId}) {
    coreIds.add(coreId);
    return Stream<RuntimeProvisionProgress>.fromIterable(events);
  }
}

final class _RecordingRuntimeManager implements RuntimeManager {
  _RecordingRuntimeManager(this.resolution);
  final RuntimeResolution resolution;
  int resolveCalls = 0;
  @override
  Future<RuntimeResolution> resolve(RuntimeDescriptor descriptor) async {
    resolveCalls++;
    return resolution;
  }
}

final class _FakeBiosCatalog implements BiosCatalog {
  _FakeBiosCatalog([this.listings = const <BiosFileListing>[]]);
  final List<BiosFileListing> listings;
  final List<String> requestedPlatforms = <String>[];
  @override
  Future<List<BiosFileListing>> biosForPlatform(
    String platformShortName,
  ) async {
    requestedPlatforms.add(platformShortName);
    return listings;
  }
}

final class _ThrowingBiosCatalog implements BiosCatalog {
  @override
  Future<List<BiosFileListing>> biosForPlatform(String platformShortName) =>
      throw StateError('bios catalog offline');
}

/// Serves scripted bytes per grant URL; unknown URLs fail like a dead grant.
final class _FakeDownloadClient implements DownloadClient {
  _FakeDownloadClient([this.bytesByUrl = const <Uri, List<int>>{}]);
  final Map<Uri, List<int>> bytesByUrl;
  final List<Uri> requested = <Uri>[];
  @override
  Stream<int> download({required Uri url, required File destination}) async* {
    requested.add(url);
    final bytes = bytesByUrl[url];
    if (bytes == null) {
      throw const DownloadHttpError(500);
    }
    await destination.parent.create(recursive: true);
    await destination.writeAsBytes(bytes);
    yield bytes.length;
  }
}

const _found = RuntimeResolution(
  runtimeId: 'retroarch',
  source: RuntimeResolutionSource.managed,
  executablePath: '/rt/RetroArch.app',
);

const _external = RuntimeResolution(
  runtimeId: 'retroarch',
  source: RuntimeResolutionSource.environment,
  executablePath: '/external/RetroArch.app',
);

const _notFound = RuntimeResolution(
  runtimeId: 'retroarch',
  source: RuntimeResolutionSource.notFound,
  executablePath: null,
  diagnostics: <String>['looked in managed root', 'nothing on PATH'],
);

/// Requirement ids deliberately differ from the builtin `executable`/`core`/
/// `bios` literals — the resolver must take them from the profile, never
/// hardcode.
const _executableRequirement = ExecutableRequirement(
  id: 'ra-frontend',
  displayName: 'RetroArch',
  managedArtifactId: 'retroarch',
);

const _profile = RuntimeProfile(
  id: RuntimeProfileId('retroarch:snes:snes9x'),
  adapterId: RuntimeAdapterId('retroarch'),
  displayName: 'RetroArch (Snes9x)',
  supportedPlatforms: <String>{'snes'},
  requirements: <RuntimeDependencyRequirement>[
    _executableRequirement,
    LibretroCoreRequirement(
      id: 'ra-core',
      displayName: 'Snes9x',
      coreId: 'snes9x',
    ),
  ],
);

/// One md5-only spec (sha1 deliberately null → md5 matching must kick in) and
/// two full-hash specs.
const _psxSpecs = <BiosFileSpec>[
  BiosFileSpec(
    fileName: 'scph5500.bin',
    md5: '8dd7d5296a650fac7319bce665a6a53c',
  ),
  BiosFileSpec(
    fileName: 'scph5501.bin',
    sha1: '0555c6fae8906f3f09baf5988f00e55f88e9f30b',
    md5: '490f666e1afb15b7362b406ed1cea246',
  ),
  BiosFileSpec(
    fileName: 'scph5502.bin',
    md5: '32736f17079d0b2b7024407c39bd3050',
  ),
];

RuntimeProfile _psxProfile(BiosSatisfaction satisfaction) => RuntimeProfile(
  id: const RuntimeProfileId('retroarch:psx:swanstation'),
  adapterId: const RuntimeAdapterId('retroarch'),
  displayName: 'RetroArch (SwanStation)',
  supportedPlatforms: const <String>{'psx'},
  requirements: <RuntimeDependencyRequirement>[
    _executableRequirement,
    const LibretroCoreRequirement(
      id: 'ra-core',
      displayName: 'SwanStation',
      coreId: 'swanstation',
    ),
    BiosSetRequirement(
      id: 'ra-bios',
      displayName: 'PlayStation BIOS',
      files: _psxSpecs,
      satisfaction: satisfaction,
    ),
  ],
);

const List<RuntimeProvisionProgress> _happyProvision =
    <RuntimeProvisionProgress>[
      RuntimeProvisionStarted(),
      RuntimeProvisionDownloading(
        label: 'RetroArch',
        receivedBytes: 10,
        totalBytes: 100,
      ),
      RuntimeProvisionUnpacking('RetroArch'),
      RuntimeProvisionReady(
        retroArchPath: '/rt/RetroArch.app',
        corePath: '/rt/cores/snes9x_libretro.dylib',
      ),
    ];

ResolvedPlayTarget _target(String platformShortName) => ResolvedPlayTarget(
  releaseId: 'rel-1',
  titleId: 'title-1',
  platformShortName: platformShortName,
  displayName: 'Some Game',
  localProfileId: 'profile-1',
  contentRoot: '/c',
  launchAbsolutePath: '/c/game.bin',
  saveRoot: '/s',
  stateRoot: '/st',
  configRoot: '/cfg',
);

void main() {
  late Directory tmp;
  late Directory biosRoot;
  late Directory psxBiosDir;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_dep_test');
    biosRoot = Directory(p.join(tmp.path, 'bios'));
    psxBiosDir = Directory(p.join(biosRoot.path, 'psx'));
  });
  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  RetroArchDependencyResolver resolver({
    required RuntimeProvisioner provisioner,
    required RuntimeManager runtimeManager,
    BiosCatalog? biosCatalog,
    DownloadClient? downloadClient,
    Map<String, String> coreOverridesByPlatform = const <String, String>{},
  }) => RetroArchDependencyResolver(
    provisioner: provisioner,
    runtimeManager: runtimeManager,
    biosCatalog: biosCatalog ?? _FakeBiosCatalog(),
    biosRoot: biosRoot,
    downloadClient: downloadClient ?? _FakeDownloadClient(),
    coreOverridesByPlatform: coreOverridesByPlatform,
  );

  void seedBiosFile(String fileName) {
    psxBiosDir.createSync(recursive: true);
    File(p.join(psxBiosDir.path, fileName)).writeAsStringSync(fileName);
  }

  test(
    'happy path forwards provision activity then resolves both requirements',
    () async {
      final provisioner = _ScriptedProvisioner(_happyProvision);
      final manager = _RecordingRuntimeManager(_found);

      final events = await resolver(
        provisioner: provisioner,
        runtimeManager: manager,
      ).resolve(_profile, _target('snes')).toList();

      expect(events, hasLength(3));
      final downloading = events[0] as DependencyDownloading;
      expect(downloading.label, 'RetroArch');
      expect(downloading.receivedBytes, 10);
      expect(downloading.totalBytes, 100);
      expect((events[1] as DependencyUnpacking).label, 'RetroArch');

      final ready = events[2] as DependenciesReady;
      expect(ready.dependencies, hasLength(2));
      expect(ready.dependencies[0].requirementId, 'ra-frontend');
      expect(ready.dependencies[0].path, '/rt/RetroArch.app');
      expect(
        ready.dependencies[0].provenance,
        RuntimeDependencyProvenance.managed,
      );
      expect(ready.dependencies[1].requirementId, 'ra-core');
      expect(ready.dependencies[1].path, '/rt/cores/snes9x_libretro.dylib');
    },
  );

  test('non-managed executable resolution is marked external', () async {
    final events = await resolver(
      provisioner: _ScriptedProvisioner(_happyProvision),
      runtimeManager: _RecordingRuntimeManager(_external),
    ).resolve(_profile, _target('snes')).toList();

    final ready = events.last as DependenciesReady;
    expect(ready.dependencies.first.path, '/external/RetroArch.app');
    expect(
      ready.dependencies.first.provenance,
      RuntimeDependencyProvenance.external,
    );
  });

  test('the n64 profile provisions the mupen64plus_next core', () async {
    final provisioner = _ScriptedProvisioner(_happyProvision);
    final n64Profile = BuiltinRuntimeProfiles.all.firstWhere(
      (profile) => profile.supportsPlatform('n64'),
    );

    await resolver(
      provisioner: provisioner,
      runtimeManager: _RecordingRuntimeManager(_found),
    ).resolve(n64Profile, _target('n64')).toList();

    expect(provisioner.coreIds, <String>['mupen64plus_next']);
  });

  test(
    'a platform core override wins while managed provisioning still runs',
    () async {
      final provisioner = _ScriptedProvisioner(_happyProvision);

      final events = await resolver(
        provisioner: provisioner,
        runtimeManager: _RecordingRuntimeManager(_found),
        coreOverridesByPlatform: <String, String>{
          'snes': '/dev/snes9x_libretro.dylib',
        },
      ).resolve(_profile, _target('snes')).toList();

      expect(provisioner.coreIds, <String>['snes9x']);
      final ready = events.last as DependenciesReady;
      expect(ready.dependencies[1].path, '/dev/snes9x_libretro.dylib');
    },
  );

  test(
    'a provision failure terminates without resolving the executable',
    () async {
      final manager = _RecordingRuntimeManager(_found);

      final events = await resolver(
        provisioner: _ScriptedProvisioner(const <RuntimeProvisionProgress>[
          RuntimeProvisionStarted(),
          RuntimeProvisionFailed('Runtime install failed. Try again.'),
        ]),
        runtimeManager: manager,
      ).resolve(_profile, _target('snes')).toList();

      expect(events, hasLength(1));
      expect(
        (events.single as DependencyResolutionFailed).message,
        'Runtime install failed. Try again.',
      );
      expect(manager.resolveCalls, 0);
    },
  );

  test('a missing executable surfaces the resolution diagnostics', () async {
    final events = await resolver(
      provisioner: _ScriptedProvisioner(_happyProvision),
      runtimeManager: _RecordingRuntimeManager(_notFound),
    ).resolve(_profile, _target('snes')).toList();

    final missing = events.last as DependencyExecutableMissing;
    expect(missing.executableName, 'RetroArch');
    expect(missing.diagnostics, <String>[
      'looked in managed root',
      'nothing on PATH',
    ]);
  });

  test(
    'a provision stream that ends without ready omits the core entry',
    () async {
      final events = await resolver(
        provisioner: _ScriptedProvisioner(const <RuntimeProvisionProgress>[
          RuntimeProvisionStarted(),
          RuntimeProvisionDownloading(label: 'Snes9x', receivedBytes: 5),
        ]),
        runtimeManager: _RecordingRuntimeManager(_found),
      ).resolve(_profile, _target('snes')).toList();

      final ready = events.last as DependenciesReady;
      expect(ready.dependencies, hasLength(1));
      expect(ready.dependencies.single.requirementId, 'ra-frontend');
    },
  );

  group('bios acquisition', () {
    test('all files already on disk resolve without a catalog call', () async {
      seedBiosFile('scph5500.bin');
      seedBiosFile('scph5501.bin');
      seedBiosFile('scph5502.bin');
      final catalog = _FakeBiosCatalog();
      final downloads = _FakeDownloadClient();

      final events = await resolver(
        provisioner: _ScriptedProvisioner(_happyProvision),
        runtimeManager: _RecordingRuntimeManager(_found),
        biosCatalog: catalog,
        downloadClient: downloads,
      ).resolve(_psxProfile(BiosSatisfaction.all), _target('psx')).toList();

      expect(catalog.requestedPlatforms, isEmpty);
      expect(downloads.requested, isEmpty);
      final ready = events.last as DependenciesReady;
      final bios = ready.dependencies.singleWhere(
        (dependency) => dependency.requirementId == 'ra-bios',
      );
      expect(bios.path, psxBiosDir.path);
    });

    test(
      'a missing file is fetched by md5 match and written under the spec name',
      () async {
        seedBiosFile('scph5501.bin');
        seedBiosFile('scph5502.bin');
        final grantUrl = Uri.parse('https://romd.example/grants/jp-bios');
        final bytes = utf8.encode('jp-bios-payload');
        final catalog = _FakeBiosCatalog(<BiosFileListing>[
          BiosFileListing(
            biosId: 'bios-jp',
            name: 'PS1 BIOS (JP)',
            // The library's dump name differs from the spec name on purpose:
            // the core locates the file by the spec's expected name.
            fileName: 'ps-one-jp.rom',
            sizeBytes: bytes.length,
            sha1: 'ffffffffffffffffffffffffffffffffffffffff',
            md5: '8DD7D5296A650FAC7319BCE665A6A53C',
            sha256: sha256.convert(bytes).toString(),
            isAvailable: true,
            downloadUrl: grantUrl,
          ),
        ]);
        final downloads = _FakeDownloadClient(<Uri, List<int>>{
          grantUrl: bytes,
        });

        final events = await resolver(
          provisioner: _ScriptedProvisioner(_happyProvision),
          runtimeManager: _RecordingRuntimeManager(_found),
          biosCatalog: catalog,
          downloadClient: downloads,
        ).resolve(_psxProfile(BiosSatisfaction.all), _target('psx')).toList();

        expect(catalog.requestedPlatforms, <String>['psx']);
        expect(downloads.requested, <Uri>[grantUrl]);
        final written = File(p.join(psxBiosDir.path, 'scph5500.bin'));
        expect(written.existsSync(), isTrue);
        expect(written.readAsBytesSync(), bytes);
        expect(
          File(p.join(psxBiosDir.path, 'scph5500.bin.download')).existsSync(),
          isFalse,
        );

        final biosProgress = events
            .whereType<DependencyDownloading>()
            .where((event) => event.label == 'PlayStation BIOS')
            .toList();
        expect(biosProgress, hasLength(1));
        expect(biosProgress.single.receivedBytes, bytes.length);
        expect(biosProgress.single.totalBytes, bytes.length);

        final ready = events.last as DependenciesReady;
        expect(
          ready.dependencies.map((dependency) => dependency.requirementId),
          contains('ra-bios'),
        );
      },
    );

    test(
      'anyOne is satisfied by one available file among unavailable ones',
      () async {
        final grantUrl = Uri.parse('https://romd.example/grants/jp-bios');
        final bytes = utf8.encode('jp-bios-payload');
        final catalog = _FakeBiosCatalog(<BiosFileListing>[
          BiosFileListing(
            biosId: 'bios-jp',
            name: 'PS1 BIOS (JP)',
            fileName: 'scph5500.bin',
            sizeBytes: bytes.length,
            md5: '8dd7d5296a650fac7319bce665a6a53c',
            sha256: sha256.convert(bytes).toString(),
            isAvailable: true,
            downloadUrl: grantUrl,
          ),
          const BiosFileListing(
            biosId: 'bios-na',
            name: 'PS1 BIOS (NA)',
            fileName: 'scph5501.bin',
            sizeBytes: 524288,
            md5: '490f666e1afb15b7362b406ed1cea246',
            isAvailable: false,
          ),
        ]);

        final events =
            await resolver(
                  provisioner: _ScriptedProvisioner(_happyProvision),
                  runtimeManager: _RecordingRuntimeManager(_found),
                  biosCatalog: catalog,
                  downloadClient: _FakeDownloadClient(<Uri, List<int>>{
                    grantUrl: bytes,
                  }),
                )
                .resolve(_psxProfile(BiosSatisfaction.anyOne), _target('psx'))
                .toList();

        final ready = events.last as DependenciesReady;
        final bios = ready.dependencies.singleWhere(
          (dependency) => dependency.requirementId == 'ra-bios',
        );
        expect(bios.path, psxBiosDir.path);
      },
    );

    test('all-satisfaction fails with the fixed message when a file is '
        'unavailable, before the executable is resolved', () async {
      seedBiosFile('scph5500.bin');
      seedBiosFile('scph5502.bin');
      final manager = _RecordingRuntimeManager(_found);
      final catalog = _FakeBiosCatalog(const <BiosFileListing>[
        BiosFileListing(
          biosId: 'bios-na',
          name: 'PS1 BIOS (NA)',
          fileName: 'scph5501.bin',
          sizeBytes: 524288,
          md5: '490f666e1afb15b7362b406ed1cea246',
          isAvailable: false,
        ),
      ]);

      final events = await resolver(
        provisioner: _ScriptedProvisioner(_happyProvision),
        runtimeManager: manager,
        biosCatalog: catalog,
      ).resolve(_psxProfile(BiosSatisfaction.all), _target('psx')).toList();

      expect(
        (events.last as DependencyResolutionFailed).message,
        "Your ROMD library doesn't have the BIOS files this platform needs.",
      );
      expect(events.whereType<DependenciesReady>(), isEmpty);
      expect(manager.resolveCalls, 0);
    });

    test('a catalog error propagates out of the stream', () async {
      final stream = resolver(
        provisioner: _ScriptedProvisioner(_happyProvision),
        runtimeManager: _RecordingRuntimeManager(_found),
        biosCatalog: _ThrowingBiosCatalog(),
      ).resolve(_psxProfile(BiosSatisfaction.anyOne), _target('psx'));

      await expectLater(stream.toList(), throwsA(isA<StateError>()));
    });
  });
}
