import 'dart:ffi' show Abi;
import 'dart:io';

import 'package:path/path.dart' as p;

import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';

/// Injectable view of the host used for runtime discovery, so resolution is
/// deterministic under test (no real filesystem / env).
abstract interface class RuntimeProbeEnvironment {
  /// `Platform.operatingSystem` value: `macos` / `linux` / `windows`.
  String get operatingSystem;

  /// CPU architecture for runtime/core selection: `arm64` / `x86_64` / `unknown`.
  String get architecture;

  /// Process environment variables.
  Map<String, String> get environment;

  /// Compile-time `--dart-define` override for [key], or null if unset.
  String? buildDefine(String key);

  /// Reserved base for ROMD-managed runtimes (`<appSupport>/runtimes`). v1 never
  /// installs here, but the path is checked first so the future managed-install
  /// flow needs no call-site changes.
  String get managedRuntimesRoot;

  /// `PATH` split into directories.
  List<String> get pathDirectories;

  bool fileExists(String path);
}

final class DefaultRuntimeManager implements RuntimeManager {
  const DefaultRuntimeManager({required this.environment});

  final RuntimeProbeEnvironment environment;

  @override
  Future<RuntimeResolution> resolve(RuntimeDescriptor descriptor) async {
    final diagnostics = <String>[];

    // 1. ROMD-managed install (reserved location; absent in v1).
    final managed = _firstExisting(
      descriptor.executableNames.map(
        (name) =>
            p.join(environment.managedRuntimesRoot, descriptor.runtimeId, name),
      ),
    );
    if (managed != null) {
      return _resolved(
        descriptor,
        RuntimeResolutionSource.managed,
        managed,
        diagnostics,
      );
    }

    // 2. Build-define override.
    final define = environment.buildDefine(descriptor.overrideKey);
    if (define != null && define.isNotEmpty) {
      if (environment.fileExists(define)) {
        return _resolved(
          descriptor,
          RuntimeResolutionSource.buildDefine,
          define,
          diagnostics,
        );
      }
      diagnostics.add(
        '${descriptor.overrideKey} build-define set but file not found',
      );
    }

    // 3. Environment-variable override.
    final envOverride = environment.environment[descriptor.overrideKey];
    if (envOverride != null && envOverride.isNotEmpty) {
      if (environment.fileExists(envOverride)) {
        return _resolved(
          descriptor,
          RuntimeResolutionSource.environment,
          envOverride,
          diagnostics,
        );
      }
      diagnostics.add(
        '${descriptor.overrideKey} env var set but file not found',
      );
    }

    // 4. PATH search.
    final onPath = _firstExisting(<String>[
      for (final dir in environment.pathDirectories)
        for (final name in descriptor.executableNames) p.join(dir, name),
    ]);
    if (onPath != null) {
      return _resolved(
        descriptor,
        RuntimeResolutionSource.path,
        onPath,
        diagnostics,
      );
    }

    // 5. Common per-OS install locations.
    final common = _firstExisting(<String>[
      for (final dir in _commonDirectories(environment.operatingSystem))
        for (final name in descriptor.executableNames) p.join(dir, name),
    ]);
    if (common != null) {
      return _resolved(
        descriptor,
        RuntimeResolutionSource.commonInstallLocation,
        common,
        diagnostics,
      );
    }

    diagnostics.add('no executable found on PATH or common install locations');
    return RuntimeResolution(
      runtimeId: descriptor.runtimeId,
      source: RuntimeResolutionSource.notFound,
      executablePath: null,
      diagnostics: diagnostics,
    );
  }

  String? _firstExisting(Iterable<String> candidates) {
    for (final candidate in candidates) {
      if (environment.fileExists(candidate)) {
        return candidate;
      }
    }
    return null;
  }

  RuntimeResolution _resolved(
    RuntimeDescriptor descriptor,
    RuntimeResolutionSource source,
    String path,
    List<String> diagnostics,
  ) => RuntimeResolution(
    runtimeId: descriptor.runtimeId,
    source: source,
    executablePath: path,
    diagnostics: diagnostics,
  );

  List<String> _commonDirectories(String os) {
    final home = environment.environment['HOME'] ?? '';
    switch (os) {
      case 'macos':
        return <String>[
          '/Applications',
          if (home.isNotEmpty) p.join(home, 'Applications'),
          '/opt/homebrew/bin',
          '/usr/local/bin',
        ];
      case 'linux':
        return <String>[
          '/usr/bin',
          '/usr/local/bin',
          if (home.isNotEmpty) p.join(home, '.local/bin'),
        ];
      case 'windows':
        final programFiles = environment.environment['ProgramFiles'];
        final localAppData = environment.environment['LOCALAPPDATA'];
        return <String>[
          if (programFiles != null) programFiles,
          if (programFiles != null) p.join(programFiles, 'RetroArch'),
          if (programFiles != null) p.join(programFiles, 'PCSX2'),
          if (localAppData != null) p.join(localAppData, 'Programs'),
          if (localAppData != null) p.join(localAppData, 'Programs', 'PCSX2'),
        ];
      default:
        return const <String>[];
    }
  }
}

/// Production [RuntimeProbeEnvironment] backed by `dart:io`.
final class SystemRuntimeProbeEnvironment implements RuntimeProbeEnvironment {
  const SystemRuntimeProbeEnvironment({required this.managedRuntimesRoot});

  // `String.fromEnvironment` requires a literal key, so each supported runtime's
  // define is wired explicitly.
  static const String _retroArchDefine = String.fromEnvironment(
    'ROMD_RETROARCH_PATH',
  );
  static const String _duckStationDefine = String.fromEnvironment(
    'ROMD_DUCKSTATION_PATH',
  );
  static const String _pcsx2Define = String.fromEnvironment('ROMD_PCSX2_PATH');
  static const String _dolphinDefine = String.fromEnvironment(
    'ROMD_DOLPHIN_PATH',
  );

  @override
  final String managedRuntimesRoot;

  @override
  String get operatingSystem => Platform.operatingSystem;

  @override
  String get architecture => switch (Abi.current()) {
    Abi.macosArm64 || Abi.linuxArm64 || Abi.windowsArm64 => 'arm64',
    Abi.macosX64 || Abi.linuxX64 || Abi.windowsX64 => 'x86_64',
    _ => 'unknown',
  };

  @override
  Map<String, String> get environment => Platform.environment;

  @override
  String? buildDefine(String key) => switch (key) {
    'ROMD_RETROARCH_PATH' when _retroArchDefine.isNotEmpty => _retroArchDefine,
    'ROMD_DUCKSTATION_PATH' when _duckStationDefine.isNotEmpty =>
      _duckStationDefine,
    'ROMD_PCSX2_PATH' when _pcsx2Define.isNotEmpty => _pcsx2Define,
    'ROMD_DOLPHIN_PATH' when _dolphinDefine.isNotEmpty => _dolphinDefine,
    _ => null,
  };

  @override
  List<String> get pathDirectories {
    final raw = Platform.environment['PATH'];
    if (raw == null || raw.isEmpty) {
      return const <String>[];
    }
    return raw
        .split(Platform.isWindows ? ';' : ':')
        .where((d) => d.isNotEmpty)
        .toList();
  }

  @override
  bool fileExists(String path) =>
      // Accept files, directories (macOS `.app` bundles), and symlinks.
      FileSystemEntity.typeSync(path) != FileSystemEntityType.notFound;
}
