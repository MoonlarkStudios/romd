import 'package:path/path.dart' as p;

/// How a downloaded runtime artifact is packaged.
enum RuntimeArchiveKind {
  dmgApp,
  dylibZip,
  zipApp,
  tarXzApp,
  sevenZipAppImage,
  soZip,
}

/// A downloadable runtime artifact and where its unpacked form is placed under
/// the managed runtime install root.
final class RuntimeArtifact {
  const RuntimeArtifact({
    required this.url,
    required this.kind,
    required this.installRelativePath,
    this.sha256,
  });

  final Uri url;
  final RuntimeArchiveKind kind;

  /// e.g. `RetroArch.app` or `cores/snes9x_libretro.dylib`.
  final String installRelativePath;

  /// Expected archive digest. Managed artifacts with a digest are rejected
  /// before unpacking when their downloaded bytes do not match.
  final String? sha256;

  String get installFingerprint => <String>[
    url.toString(),
    kind.name,
    installRelativePath,
    if (sha256 case final digest?) digest.toLowerCase(),
  ].join('\n');
}

/// Pinned download sources for ROMD-managed runtimes (the libretro buildbot).
///
/// RetroArch and its cores support macOS plus Linux x86_64. Standalone
/// emulators remain macOS-only until their Linux artifact contracts are added.
final class RuntimeCatalog {
  const RuntimeCatalog();

  /// The RetroArch frontend is pinned to a stable, **notarized**, universal
  /// build (verified launchable under Gatekeeper in the M0 spike).
  static const String retroArchStableVersion = '1.22.2';
  static const String retroArchArchiveSha256 =
      '81b79121ba26d539064ae13b4d0419a120c3d165afbe656cf5f5412b15fdb434';
  static const String retroArchLinuxX64ArchiveSha256 =
      '7d62da9a21397d6e1b9490785cedbeafd262781b50115076736fbe8a77ef30e9';
  static const String duckStationPinnedBuild = 'v0.1-10998';
  static const String duckStationArchiveSha256 =
      'ed8d58d6075bccb4d201b9ba3fab4e3cc1e967db311851cb44b131f7abf3f4e3';

  RuntimeArtifact? retroArch({required String os, required String arch}) {
    if (os == 'linux' && arch == 'x86_64') {
      return RuntimeArtifact(
        url: Uri.parse(
          'https://buildbot.libretro.com/stable/$retroArchStableVersion'
          '/linux/x86_64/RetroArch.7z',
        ),
        kind: RuntimeArchiveKind.sevenZipAppImage,
        installRelativePath: 'RetroArch-Linux-x86_64.AppImage',
        sha256: retroArchLinuxX64ArchiveSha256,
      );
    }
    if (os == 'macos') {
      return RuntimeArtifact(
        url: Uri.parse(
          'https://buildbot.libretro.com/stable/$retroArchStableVersion'
          '/apple/osx/universal/RetroArch_Metal.dmg',
        ),
        kind: RuntimeArchiveKind.dmgApp,
        installRelativePath: 'RetroArch.app',
        sha256: retroArchArchiveSha256,
      );
    }
    return null;
  }

  /// Cores are published per-arch on the **nightly** channel — there is no
  /// stable or universal macOS core channel. The libretro API is stable, so a
  /// nightly core runs under the pinned stable frontend; this mirrors what
  /// RetroArch's own core updater does.
  RuntimeArtifact? core({
    required String coreId,
    required String os,
    required String arch,
  }) {
    if (os == 'linux' && arch == 'x86_64') {
      return RuntimeArtifact(
        url: Uri.parse(
          'https://buildbot.libretro.com/nightly/linux/x86_64'
          '/latest/${coreId}_libretro.so.zip',
        ),
        kind: RuntimeArchiveKind.soZip,
        installRelativePath: p.join('cores', '${coreId}_libretro.so'),
      );
    }
    if (os == 'macos') {
      final osxArch = switch (arch) {
        'arm64' => 'arm64',
        'x86_64' => 'x86_64',
        _ => null,
      };
      if (osxArch == null) {
        return null;
      }
      return RuntimeArtifact(
        url: Uri.parse(
          'https://buildbot.libretro.com/nightly/apple/osx/$osxArch'
          '/latest/${coreId}_libretro.dylib.zip',
        ),
        kind: RuntimeArchiveKind.dylibZip,
        installRelativePath: p.join('cores', '${coreId}_libretro.dylib'),
      );
    }
    return null;
  }

  /// Exact universal macOS build matching source revision 9b0a4ec55.
  RuntimeArtifact? duckStation({required String os, required String arch}) {
    if (os != 'macos') {
      return null;
    }
    return RuntimeArtifact(
      url: Uri.parse(
        'https://github.com/stenzek/duckstation/releases/download/'
        '$duckStationPinnedBuild'
        '/duckstation-mac-release.zip',
      ),
      kind: RuntimeArchiveKind.zipApp,
      installRelativePath: 'DuckStation.app',
      sha256: duckStationArchiveSha256,
    );
  }

  /// PCSX2 stable macOS builds are distributed as a tar.xz containing a
  /// versioned `.app` bundle; ROMD installs it as `PCSX2.app` so discovery is
  /// stable across upstream version names.
  static const String pcsx2StableVersion = '2.6.3';
  static const String pcsx2ArchiveSha256 =
      'cb7b9e6330f1abf0cf92c94065f7eb983d0fa8affcfe6b0ccb9c2a4ebf067f1a';

  RuntimeArtifact? pcsx2({required String os, required String arch}) {
    if (os != 'macos') {
      return null;
    }
    return RuntimeArtifact(
      url: Uri.parse(
        'https://github.com/PCSX2/pcsx2/releases/download/'
        'v$pcsx2StableVersion/pcsx2-v$pcsx2StableVersion-macos-Qt.tar.xz',
      ),
      kind: RuntimeArchiveKind.tarXzApp,
      installRelativePath: 'PCSX2.app',
      sha256: pcsx2ArchiveSha256,
    );
  }

  /// Official universal macOS release. GameCube and Wii share this artifact,
  /// but ROMD's first shipped profile is intentionally GameCube-only.
  static const String dolphinStableVersion = '2606';
  static const String dolphinArchiveSha256 =
      '908f60ddcccec46507f2ed629ad0cd82f4065e801efa7828676ae89274cc740a';
  static const String dolphinExecutableSha256 =
      '354f1f443e419393086e1b6b3aa58fa407d695f87acca4c90754c013ae69fcc2';

  RuntimeArtifact? dolphin({required String os, required String arch}) {
    if (os != 'macos') {
      return null;
    }
    return RuntimeArtifact(
      url: Uri.parse(
        'https://dl.dolphin-emu.org/releases/$dolphinStableVersion/'
        'dolphin-$dolphinStableVersion-universal.dmg',
      ),
      kind: RuntimeArchiveKind.dmgApp,
      installRelativePath: 'Dolphin.app',
      sha256: dolphinArchiveSha256,
    );
  }
}
