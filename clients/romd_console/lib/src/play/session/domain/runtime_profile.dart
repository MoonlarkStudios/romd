/// Typed ids for runtime adapters and profiles. Extension types so equality
/// and hashing delegate to the underlying [String] (both are used as map
/// keys) with zero runtime cost.
extension type const RuntimeAdapterId(String value) {}

extension type const RuntimeProfileId(String value) {}

/// One artifact a profile needs before it can launch. Sealed so the resolver
/// and adapter can pattern-match on the concrete kind — only kinds with a
/// consumer today exist; generatedArtifact arrives with the slice that uses it.
sealed class RuntimeDependencyRequirement {
  const RuntimeDependencyRequirement({
    required this.id,
    required this.displayName,
  });

  /// Semantic id within the profile, e.g. `executable` or `core`.
  final String id;

  /// User-facing name, e.g. `RetroArch` or `Mesen`.
  final String displayName;
}

/// The runtime frontend executable itself, e.g. RetroArch.
final class ExecutableRequirement extends RuntimeDependencyRequirement {
  const ExecutableRequirement({
    required super.id,
    required super.displayName,
    this.managedArtifactId,
  });

  /// Id used to acquire this artifact from the managed catalog (e.g.
  /// `retroarch`); null when the artifact cannot be managed-installed.
  final String? managedArtifactId;
}

/// A libretro core the frontend loads, e.g. `snes9x`.
final class LibretroCoreRequirement extends RuntimeDependencyRequirement {
  const LibretroCoreRequirement({
    required super.id,
    required super.displayName,
    required this.coreId,
  });

  /// The libretro core id, used both to provision the core and to locate its
  /// dylib.
  final String coreId;
}

/// How many of a [BiosSetRequirement]'s files must be present to launch.
enum BiosSatisfaction { all, anyOne }

/// One expected BIOS file. Hashes are lowercase hex; either may be absent
/// when the reference source doesn't document it.
final class BiosFileSpec {
  const BiosFileSpec({required this.fileName, this.sha1, this.md5});

  /// The file name the core expects inside the system directory.
  final String fileName;

  final String? sha1;
  final String? md5;
}

/// A set of BIOS files a core needs, with the platform's satisfaction rule
/// (some cores boot with any one region BIOS, others require the full set).
final class BiosSetRequirement extends RuntimeDependencyRequirement {
  const BiosSetRequirement({
    required super.id,
    required super.displayName,
    required this.files,
    required this.satisfaction,
    this.acceptsAnyPlatformBios = false,
  });

  final List<BiosFileSpec> files;
  final BiosSatisfaction satisfaction;

  /// True for emulators that scan a directory for any valid firmware dump
  /// instead of requiring one documented filename. ROMD then preserves the
  /// catalog filename when acquiring the user's available BIOS file.
  final bool acceptsAnyPlatformBios;
}

/// A concrete, launchable runtime configuration: which adapter runs it, which
/// platforms it plays, and which artifacts it needs.
///
/// Built-in profile ids are **immutable once shipped** — persisted user rules
/// will reference them. Changing a platform's default core means adding a new
/// profile, never renaming one.
final class RuntimeProfile {
  const RuntimeProfile({
    required this.id,
    required this.adapterId,
    required this.displayName,
    required this.supportedPlatforms,
    required this.requirements,
  });

  final RuntimeProfileId id;
  final RuntimeAdapterId adapterId;
  final String displayName;

  /// ROMD platform short names this profile can play, e.g. `{'snes'}`.
  final Set<String> supportedPlatforms;

  final List<RuntimeDependencyRequirement> requirements;

  /// The frontend executable requirement, or null for profiles that are
  /// resolved through a non-process runtime.
  ExecutableRequirement? get executableRequirement {
    for (final requirement in requirements) {
      if (requirement is ExecutableRequirement) {
        return requirement;
      }
    }
    return null;
  }

  /// The libretro-core requirement, or null for non-libretro profiles. The
  /// launch pipeline needs it to locate the core dylib.
  LibretroCoreRequirement? get coreRequirement {
    for (final requirement in requirements) {
      if (requirement is LibretroCoreRequirement) {
        return requirement;
      }
    }
    return null;
  }

  /// The BIOS-set requirement, or null for platforms that boot without one.
  BiosSetRequirement? get biosRequirement {
    for (final requirement in requirements) {
      if (requirement is BiosSetRequirement) {
        return requirement;
      }
    }
    return null;
  }

  /// Same conservative normalization as the resolver input: trim + lowercase.
  bool supportsPlatform(String platformShortName) =>
      supportedPlatforms.contains(platformShortName.trim().toLowerCase());
}
