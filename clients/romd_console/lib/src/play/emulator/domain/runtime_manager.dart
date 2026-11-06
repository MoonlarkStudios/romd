/// Describes a runtime (emulator/port) the client knows how to look for. Generic
/// — carries no platform/launch assumptions.
final class RuntimeDescriptor {
  const RuntimeDescriptor({
    required this.runtimeId,
    required this.executableNames,
    required this.overrideKey,
  });

  /// Stable id (also the managed-install subdirectory name), e.g. `bsnes`.
  final String runtimeId;

  /// Candidate executable filenames across OSes, e.g. `['bsnes', 'bsnes.exe']`.
  final List<String> executableNames;

  /// Build-define / env-var name an operator can set to an explicit path.
  final String overrideKey;
}

/// RetroArch. A libretro frontend that loads a platform-specific core;
/// it reads the ROM from `argv`, so it auto-loads and supervises cleanly.
const RuntimeDescriptor retroArchRuntimeDescriptor = RuntimeDescriptor(
  runtimeId: 'retroarch',
  executableNames: <String>[
    'RetroArch.app',
    'RetroArch-Linux-x86_64.AppImage',
    'retroarch',
    'retroarch.exe',
  ],
  overrideKey: 'ROMD_RETROARCH_PATH',
);

/// DuckStation. A standalone PlayStation emulator; it is not a libretro core,
/// so profiles using it resolve only the executable plus PS1 BIOS files.
const RuntimeDescriptor duckStationRuntimeDescriptor = RuntimeDescriptor(
  runtimeId: 'duckstation',
  executableNames: <String>[
    'DuckStation.app',
    'DuckStation',
    'DuckStation-x64.AppImage',
    'DuckStation-arm64.AppImage',
    'duckstation-qt',
    'duckstation',
    'duckstation.exe',
    'duckstation-qt-x64-ReleaseLTCG.exe',
    'duckstation-qt-x64-Release.exe',
  ],
  overrideKey: 'ROMD_DUCKSTATION_PATH',
);

/// PCSX2. A standalone PlayStation 2 emulator. The Qt CLI accepts direct boot
/// filenames plus a scoped data path, so ROMD can keep its BIOS, saves, and
/// config under a managed root.
const RuntimeDescriptor pcsx2RuntimeDescriptor = RuntimeDescriptor(
  runtimeId: 'pcsx2',
  executableNames: <String>[
    'PCSX2.app',
    'PCSX2-v2.6.3.app',
    'PCSX2',
    'pcsx2-qt',
    'pcsx2',
    'pcsx2-Qt.AppImage',
    'pcsx2.exe',
    'pcsx2-qt.exe',
  ],
  overrideKey: 'ROMD_PCSX2_PATH',
);

/// Dolphin. The first ROMD profile is deliberately GameCube-only; Wii input
/// requires a separate controller and launch policy.
const RuntimeDescriptor dolphinRuntimeDescriptor = RuntimeDescriptor(
  runtimeId: 'dolphin',
  executableNames: <String>[
    'Dolphin.app',
    'Dolphin',
    'dolphin-emu',
    'dolphin-emu.exe',
    'Dolphin.exe',
  ],
  overrideKey: 'ROMD_DOLPHIN_PATH',
);

/// Where a runtime executable was found (or that it wasn't). The resolution
/// order is: managed install → build-define → env var → PATH → common install
/// locations → not found.
enum RuntimeResolutionSource {
  managed,
  buildDefine,
  environment,
  path,
  commonInstallLocation,
  notFound,
}

final class RuntimeResolution {
  const RuntimeResolution({
    required this.runtimeId,
    required this.source,
    required this.executablePath,
    this.version,
    this.diagnostics = const <String>[],
  });

  final String runtimeId;
  final RuntimeResolutionSource source;

  /// Absolute path to the resolved executable, or null when [source] is
  /// [RuntimeResolutionSource.notFound].
  final String? executablePath;
  final String? version;

  /// Human-readable notes about where the manager looked — useful in dev
  /// "runtime missing" diagnostics. Never contains secrets.
  final List<String> diagnostics;

  bool get isAvailable => executablePath != null;
}

/// Locates a runtime on this device. v1 implements **discovery only**; a future
/// version adds `ensureAvailable` (download + verify a managed runtime) without
/// changing the play flow.
abstract interface class RuntimeManager {
  Future<RuntimeResolution> resolve(RuntimeDescriptor descriptor);
}
