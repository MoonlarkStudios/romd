import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

void main() {
  group('built-in runtime profiles', () {
    const expectedCoreByPlatform = <String, String>{
      'nes': 'mesen',
      'snes': 'snes9x',
      'n64': 'mupen64plus_next',
      'gb': 'sameboy',
      'gbc': 'sameboy',
      'gba': 'mgba',
      'genesis': 'genesis_plus_gx',
      'psx': 'swanstation',
    };

    RuntimeProfile profileForPlatform(String platform) => BuiltinRuntimeProfiles
        .all
        .firstWhere((profile) => profile.supportsPlatform(platform));

    test(
      'covers all cartridge/PS1 platforms with the correct default core',
      () {
        expect(BuiltinRuntimeProfiles.all, hasLength(15));
        for (final entry in expectedCoreByPlatform.entries) {
          final profile = profileForPlatform(entry.key);
          expect(profile.adapterId, const RuntimeAdapterId('retroarch'));
          expect(profile.coreRequirement?.coreId, entry.value);
        }
      },
    );

    test('every shipped adapter has an explicit durable-root contract', () {
      // Closed-world guard: adding an adapter requires consciously extending
      // save/state routing and its adapter/config-byte tests before this list.
      const adaptersWithDurableRootCoverage = <RuntimeAdapterId>{
        BuiltinRuntimeProfiles.retroArchAdapterId,
        BuiltinRuntimeProfiles.duckStationAdapterId,
        BuiltinRuntimeProfiles.pcsx2AdapterId,
        BuiltinRuntimeProfiles.dolphinAdapterId,
      };
      final shippedAdapters = BuiltinRuntimeProfiles.all
          .map((profile) => profile.adapterId)
          .toSet();

      expect(shippedAdapters, adaptersWithDurableRootCoverage);
    });

    test('n64 maps to mupen64plus_next', () {
      final n64 = profileForPlatform('n64');
      expect(n64.id, const RuntimeProfileId('retroarch:n64:mupen64plus_next'));
      expect(n64.coreRequirement?.coreId, 'mupen64plus_next');
    });

    test('alternate cores ship as profiles appended after the defaults', () {
      final ids = BuiltinRuntimeProfiles.all
          .map((profile) => profile.id.value)
          .toList(growable: false);
      const alternates = <String>[
        'retroarch:snes:bsnes',
        'retroarch:nes:nestopia',
        'retroarch:genesis:picodrive',
        'duckstation:psx:standalone',
        'retroarch:psx:mednafen_psx_hw',
      ];
      expect(ids, containsAll(alternates));
      // The first profile per platform is the no-rule default; alternates
      // must never precede the shipped defaults.
      expect(
        ids.indexOf('retroarch:snes:snes9x'),
        lessThan(ids.indexOf('retroarch:snes:bsnes')),
      );
      expect(
        ids.indexOf('retroarch:nes:mesen'),
        lessThan(ids.indexOf('retroarch:nes:nestopia')),
      );
      expect(
        ids.indexOf('retroarch:genesis:genesis_plus_gx'),
        lessThan(ids.indexOf('retroarch:genesis:picodrive')),
      );
      expect(
        ids.indexOf('retroarch:psx:swanstation'),
        lessThan(ids.indexOf('duckstation:psx:standalone')),
      );
      expect(
        ids.indexOf('duckstation:psx:standalone'),
        lessThan(ids.indexOf('retroarch:psx:mednafen_psx_hw')),
      );
    });

    test(
      'every RetroArch profile id follows retroarch:<platform>:<coreId>',
      () {
        for (final profile in BuiltinRuntimeProfiles.all.where(
          (profile) =>
              profile.adapterId == BuiltinRuntimeProfiles.retroArchAdapterId,
        )) {
          final platform = profile.supportedPlatforms.single;
          final coreId = profile.coreRequirement!.coreId;
          expect(profile.id.value, 'retroarch:$platform:$coreId');
        }
      },
    );

    test('RetroArch profiles require the managed RetroArch executable', () {
      for (final profile in BuiltinRuntimeProfiles.all.where(
        (profile) =>
            profile.adapterId == BuiltinRuntimeProfiles.retroArchAdapterId,
      )) {
        final executable = profile.requirements
            .whereType<ExecutableRequirement>()
            .single;
        expect(executable.id, 'executable');
        expect(executable.displayName, 'RetroArch');
        expect(executable.managedArtifactId, 'retroarch');
      }
    });

    test('DuckStation profile requires a standalone executable', () {
      final profile = BuiltinRuntimeProfiles.all.singleWhere(
        (profile) => profile.id.value == 'duckstation:psx:standalone',
      );
      final executable = profile.requirements
          .whereType<ExecutableRequirement>()
          .single;
      expect(profile.adapterId, BuiltinRuntimeProfiles.duckStationAdapterId);
      expect(executable.displayName, 'DuckStation');
      expect(executable.managedArtifactId, isNull);
      expect(profile.coreRequirement, isNull);
    });

    test('PCSX2 profile requires a managed standalone executable', () {
      final profile = BuiltinRuntimeProfiles.all.singleWhere(
        (profile) => profile.id.value == 'pcsx2:ps2:standalone',
      );
      final executable = profile.executableRequirement!;
      expect(profile.adapterId, BuiltinRuntimeProfiles.pcsx2AdapterId);
      expect(executable.displayName, 'PCSX2');
      expect(executable.managedArtifactId, 'pcsx2');
      expect(profile.coreRequirement, isNull);
    });

    test('Dolphin profile is managed and GameCube-only', () {
      final profile = BuiltinRuntimeProfiles.all.singleWhere(
        (profile) => profile.id.value == 'dolphin:gc:standalone',
      );
      final executable = profile.executableRequirement!;
      expect(profile.adapterId, BuiltinRuntimeProfiles.dolphinAdapterId);
      expect(profile.supportedPlatforms, <String>{'gc'});
      expect(executable.displayName, 'Dolphin');
      expect(executable.managedArtifactId, 'dolphin');
      expect(profile.coreRequirement, isNull);
      expect(profile.biosRequirement, isNull);
      expect(profile.supportsPlatform('wii'), isFalse);
    });

    test('supportsPlatform normalizes conservatively', () {
      final n64 = profileForPlatform('n64');
      expect(n64.supportsPlatform(' N64 '), isTrue);
      expect(n64.supportsPlatform('snes'), isFalse);
    });

    test('only PlayStation profiles carry a BIOS requirement', () {
      for (final profile in BuiltinRuntimeProfiles.all) {
        if (profile.supportsPlatform('psx') ||
            profile.supportsPlatform('ps2')) {
          expect(profile.biosRequirement, isNotNull);
        } else {
          expect(profile.biosRequirement, isNull);
        }
      }
    });

    group('psx profiles', () {
      const expectedFileNames = <String>[
        'scph5500.bin',
        'scph5501.bin',
        'scph5502.bin',
      ];

      RuntimeProfile psxProfile(String id) => BuiltinRuntimeProfiles.all
          .firstWhere((profile) => profile.id.value == id);

      test('swanstation is the default and boots on any one region BIOS', () {
        final swanstation = profileForPlatform('psx');
        expect(swanstation.id.value, 'retroarch:psx:swanstation');
        expect(swanstation.displayName, 'RetroArch (SwanStation)');
        expect(swanstation.coreRequirement?.displayName, 'SwanStation');
        final bios = swanstation.biosRequirement!;
        expect(bios.id, 'bios');
        expect(bios.displayName, 'PlayStation BIOS');
        expect(bios.satisfaction, BiosSatisfaction.anyOne);
        expect(bios.files.map((spec) => spec.fileName), expectedFileNames);
      });

      test('beetle psx hw is the alternate and requires the full set', () {
        final beetle = psxProfile('retroarch:psx:mednafen_psx_hw');
        expect(beetle.displayName, 'RetroArch (Beetle PSX HW)');
        expect(beetle.coreRequirement?.coreId, 'mednafen_psx_hw');
        final bios = beetle.biosRequirement!;
        expect(bios.satisfaction, BiosSatisfaction.all);
        expect(bios.files.map((spec) => spec.fileName), expectedFileNames);
      });

      test(
        'duckstation is a standalone alternate with the shared BIOS set',
        () {
          final duckstation = psxProfile('duckstation:psx:standalone');
          expect(duckstation.displayName, 'DuckStation');
          expect(duckstation.coreRequirement, isNull);
          final bios = duckstation.biosRequirement!;
          expect(bios.satisfaction, BiosSatisfaction.anyOne);
          expect(bios.files.map((spec) => spec.fileName), expectedFileNames);
        },
      );

      test('every psx BIOS spec carries a libretro-documented md5', () {
        final bios = profileForPlatform('psx').biosRequirement!;
        for (final spec in bios.files) {
          expect(spec.md5, isNotNull);
          expect(spec.md5, matches(RegExp(r'^[0-9a-f]{32}$')));
        }
      });
    });

    group('ps2 profile', () {
      test('pcsx2 is the default and accepts any platform BIOS file', () {
        final pcsx2 = profileForPlatform('ps2');
        expect(pcsx2.id.value, 'pcsx2:ps2:standalone');
        expect(pcsx2.displayName, 'PCSX2');
        expect(pcsx2.adapterId, BuiltinRuntimeProfiles.pcsx2AdapterId);
        final bios = pcsx2.biosRequirement!;
        expect(bios.displayName, 'PlayStation 2 BIOS');
        expect(bios.files, isEmpty);
        expect(bios.acceptsAnyPlatformBios, isTrue);
      });
    });
  });
}
