import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// Every RetroArch profile needs the frontend itself; the core differs per
/// platform.
const RuntimeDependencyRequirement _retroArchExecutable = ExecutableRequirement(
  id: 'executable',
  displayName: 'RetroArch',
  managedArtifactId: 'retroarch',
);

const RuntimeDependencyRequirement _duckStationExecutable =
    ExecutableRequirement(id: 'executable', displayName: 'DuckStation');

const RuntimeDependencyRequirement _pcsx2Executable = ExecutableRequirement(
  id: 'executable',
  displayName: 'PCSX2',
  managedArtifactId: 'pcsx2',
);

const RuntimeDependencyRequirement _dolphinExecutable = ExecutableRequirement(
  id: 'executable',
  displayName: 'Dolphin',
  managedArtifactId: 'dolphin',
);

/// The three region PS1 BIOS files, shared by both PS1 profiles. Hashes are
/// the libretro-documented MD5s (docs.libretro.com, Beetle PSX HW). SHA1s are
/// deliberately absent — md5-only specs suffice for catalog matching.
const List<BiosFileSpec> _psxBiosFiles = <BiosFileSpec>[
  BiosFileSpec(
    fileName: 'scph5500.bin',
    md5: '8dd7d5296a650fac7319bce665a6a53c',
  ),
  BiosFileSpec(
    fileName: 'scph5501.bin',
    md5: '490f666e1afb15b7362b406ed1cea246',
  ),
  BiosFileSpec(
    fileName: 'scph5502.bin',
    md5: '32736f17079d0b2b7024407c39bd3050',
  ),
];

/// The RetroArch profiles the client ships with — the single source of truth
/// for which core plays which platform. The core rows are deliberately data
/// here (not derived from another catalog) so this file owns them outright.
///
/// Profile ids follow `retroarch:<platform>:<coreId>` and are immutable once
/// shipped: persisted user rules reference them, so changing a platform's
/// default core means adding a new profile, never renaming one.
final class BuiltinRuntimeProfiles {
  const BuiltinRuntimeProfiles._();

  static const RuntimeAdapterId retroArchAdapterId = RuntimeAdapterId(
    'retroarch',
  );
  static const RuntimeAdapterId duckStationAdapterId = RuntimeAdapterId(
    'duckstation',
  );
  static const RuntimeAdapterId pcsx2AdapterId = RuntimeAdapterId('pcsx2');
  static const RuntimeAdapterId dolphinAdapterId = RuntimeAdapterId('dolphin');

  static const List<RuntimeProfile> all = <RuntimeProfile>[
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:nes:mesen'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (Mesen)',
      supportedPlatforms: <String>{'nes'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'Mesen',
          coreId: 'mesen',
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:snes:snes9x'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (Snes9x)',
      supportedPlatforms: <String>{'snes'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'Snes9x',
          coreId: 'snes9x',
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:n64:mupen64plus_next'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (Mupen64Plus-Next)',
      supportedPlatforms: <String>{'n64'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'Mupen64Plus-Next',
          coreId: 'mupen64plus_next',
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:gb:sameboy'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (SameBoy)',
      supportedPlatforms: <String>{'gb'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'SameBoy',
          coreId: 'sameboy',
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:gbc:sameboy'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (SameBoy)',
      supportedPlatforms: <String>{'gbc'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'SameBoy',
          coreId: 'sameboy',
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:gba:mgba'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (mGBA)',
      supportedPlatforms: <String>{'gba'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'mGBA',
          coreId: 'mgba',
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:genesis:genesis_plus_gx'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (Genesis Plus GX)',
      supportedPlatforms: <String>{'genesis'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'Genesis Plus GX',
          coreId: 'genesis_plus_gx',
        ),
      ],
    ),
    // Alternate cores below. Order matters: with no override rule the first
    // profile per platform wins, so the shipped defaults above must stay
    // first and alternates are appended here.
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:snes:bsnes'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (bsnes)',
      supportedPlatforms: <String>{'snes'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'bsnes',
          coreId: 'bsnes',
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:nes:nestopia'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (Nestopia)',
      supportedPlatforms: <String>{'nes'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'Nestopia',
          coreId: 'nestopia',
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:genesis:picodrive'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (PicoDrive)',
      supportedPlatforms: <String>{'genesis'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'PicoDrive',
          coreId: 'picodrive',
        ),
      ],
    ),
    // PS1 (BIOS-dependent). SwanStation stays first to preserve the current
    // managed RetroArch default; DuckStation is the preferred standalone
    // emulator once a user has it installed or configures a path.
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:psx:swanstation'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (SwanStation)',
      supportedPlatforms: <String>{'psx'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'SwanStation',
          coreId: 'swanstation',
        ),
        BiosSetRequirement(
          id: 'bios',
          displayName: 'PlayStation BIOS',
          files: _psxBiosFiles,
          satisfaction: BiosSatisfaction.anyOne,
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('duckstation:psx:standalone'),
      adapterId: duckStationAdapterId,
      displayName: 'DuckStation',
      supportedPlatforms: <String>{'psx'},
      requirements: <RuntimeDependencyRequirement>[
        _duckStationExecutable,
        BiosSetRequirement(
          id: 'bios',
          displayName: 'PlayStation BIOS',
          files: _psxBiosFiles,
          satisfaction: BiosSatisfaction.anyOne,
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('retroarch:psx:mednafen_psx_hw'),
      adapterId: retroArchAdapterId,
      displayName: 'RetroArch (Beetle PSX HW)',
      supportedPlatforms: <String>{'psx'},
      requirements: <RuntimeDependencyRequirement>[
        _retroArchExecutable,
        LibretroCoreRequirement(
          id: 'core',
          displayName: 'Beetle PSX HW',
          coreId: 'mednafen_psx_hw',
        ),
        BiosSetRequirement(
          id: 'bios',
          displayName: 'PlayStation BIOS',
          files: _psxBiosFiles,
          satisfaction: BiosSatisfaction.all,
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('pcsx2:ps2:standalone'),
      adapterId: pcsx2AdapterId,
      displayName: 'PCSX2',
      supportedPlatforms: <String>{'ps2'},
      requirements: <RuntimeDependencyRequirement>[
        _pcsx2Executable,
        BiosSetRequirement(
          id: 'bios',
          displayName: 'PlayStation 2 BIOS',
          files: <BiosFileSpec>[],
          satisfaction: BiosSatisfaction.anyOne,
          acceptsAnyPlatformBios: true,
        ),
      ],
    ),
    RuntimeProfile(
      id: RuntimeProfileId('dolphin:gc:standalone'),
      adapterId: dolphinAdapterId,
      displayName: 'Dolphin',
      supportedPlatforms: <String>{'gc'},
      requirements: <RuntimeDependencyRequirement>[_dolphinExecutable],
    ),
  ];
}
