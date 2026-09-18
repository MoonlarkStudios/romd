import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';

void main() {
  const catalog = RuntimeCatalog();

  test('RetroArch on macOS → stable universal Metal dmg', () {
    final a = catalog.retroArch(os: 'macos', arch: 'arm64')!;
    expect(a.kind, RuntimeArchiveKind.dmgApp);
    expect(a.installRelativePath, 'RetroArch.app');
    expect(
      a.url.toString(),
      'https://buildbot.libretro.com/stable/${RuntimeCatalog.retroArchStableVersion}'
      '/apple/osx/universal/RetroArch_Metal.dmg',
    );
    expect(a.sha256, RuntimeCatalog.retroArchArchiveSha256);
    expect(a.installFingerprint, contains(a.sha256!));
  });

  test('RetroArch on Linux x86_64 → pinned stable AppImage archive', () {
    final artifact = catalog.retroArch(os: 'linux', arch: 'x86_64')!;

    expect(artifact.kind, RuntimeArchiveKind.sevenZipAppImage);
    expect(artifact.installRelativePath, 'RetroArch-Linux-x86_64.AppImage');
    expect(artifact.sha256, RuntimeCatalog.retroArchLinuxX64ArchiveSha256);
    expect(
      artifact.url.toString(),
      'https://buildbot.libretro.com/stable/'
      '${RuntimeCatalog.retroArchStableVersion}'
      '/linux/x86_64/RetroArch.7z',
    );
  });

  test('RetroArch on unsupported OSes and Linux architectures → null', () {
    expect(catalog.retroArch(os: 'linux', arch: 'arm64'), isNull);
    expect(catalog.retroArch(os: 'windows', arch: 'x86_64'), isNull);
  });

  test('core on macOS → nightly per-arch dylib zip', () {
    final arm = catalog.core(coreId: 'snes9x', os: 'macos', arch: 'arm64')!;
    expect(arm.kind, RuntimeArchiveKind.dylibZip);
    expect(arm.installRelativePath, p.join('cores', 'snes9x_libretro.dylib'));
    expect(
      arm.url.toString(),
      'https://buildbot.libretro.com/nightly/apple/osx/arm64/latest/snes9x_libretro.dylib.zip',
    );

    final x64 = catalog.core(coreId: 'snes9x', os: 'macos', arch: 'x86_64')!;
    expect(
      x64.url.toString(),
      'https://buildbot.libretro.com/nightly/apple/osx/x86_64/latest/snes9x_libretro.dylib.zip',
    );
  });

  test('core on Linux x86_64 → nightly shared-library zip', () {
    final artifact = catalog.core(
      coreId: 'snes9x',
      os: 'linux',
      arch: 'x86_64',
    )!;

    expect(artifact.kind, RuntimeArchiveKind.soZip);
    expect(artifact.installRelativePath, p.join('cores', 'snes9x_libretro.so'));
    expect(
      artifact.url.toString(),
      'https://buildbot.libretro.com/nightly/linux/x86_64'
      '/latest/snes9x_libretro.so.zip',
    );
  });

  test('core with unknown or unsupported platform architecture → null', () {
    expect(
      catalog.core(coreId: 'snes9x', os: 'macos', arch: 'riscv64'),
      isNull,
    );
    expect(catalog.core(coreId: 'snes9x', os: 'linux', arch: 'arm64'), isNull);
    expect(
      catalog.core(coreId: 'snes9x', os: 'windows', arch: 'x86_64'),
      isNull,
    );
  });

  test('DuckStation on macOS → pinned, hashed GitHub app zip', () {
    final artifact = catalog.duckStation(os: 'macos', arch: 'arm64')!;

    expect(artifact.kind, RuntimeArchiveKind.zipApp);
    expect(artifact.installRelativePath, 'DuckStation.app');
    expect(
      artifact.url.toString(),
      'https://github.com/stenzek/duckstation/releases/download/'
      '${RuntimeCatalog.duckStationPinnedBuild}'
      '/duckstation-mac-release.zip',
    );
    expect(artifact.sha256, RuntimeCatalog.duckStationArchiveSha256);
    expect(artifact.installFingerprint, contains(artifact.sha256!));
  });

  test('DuckStation on other OSes → null until unpacking is supported', () {
    expect(catalog.duckStation(os: 'linux', arch: 'x86_64'), isNull);
    expect(catalog.duckStation(os: 'windows', arch: 'x86_64'), isNull);
  });

  test('PCSX2 on macOS → stable tar.xz app archive', () {
    final artifact = catalog.pcsx2(os: 'macos', arch: 'arm64')!;

    expect(artifact.kind, RuntimeArchiveKind.tarXzApp);
    expect(artifact.installRelativePath, 'PCSX2.app');
    expect(artifact.sha256, RuntimeCatalog.pcsx2ArchiveSha256);
    expect(artifact.installFingerprint, contains(artifact.sha256!));
    expect(
      artifact.url.toString(),
      'https://github.com/PCSX2/pcsx2/releases/download/'
      'v${RuntimeCatalog.pcsx2StableVersion}/'
      'pcsx2-v${RuntimeCatalog.pcsx2StableVersion}-macos-Qt.tar.xz',
    );
  });

  test('PCSX2 on other OSes → null until unpacking is supported', () {
    expect(catalog.pcsx2(os: 'linux', arch: 'x86_64'), isNull);
    expect(catalog.pcsx2(os: 'windows', arch: 'x86_64'), isNull);
  });

  test('Dolphin on macOS → pinned official universal dmg', () {
    final artifact = catalog.dolphin(os: 'macos', arch: 'arm64')!;

    expect(artifact.kind, RuntimeArchiveKind.dmgApp);
    expect(artifact.installRelativePath, 'Dolphin.app');
    expect(artifact.sha256, RuntimeCatalog.dolphinArchiveSha256);
    expect(RuntimeCatalog.dolphinExecutableSha256, hasLength(64));
    expect(artifact.installFingerprint, contains(artifact.sha256!));
    expect(
      artifact.url.toString(),
      'https://dl.dolphin-emu.org/releases/'
      '${RuntimeCatalog.dolphinStableVersion}/'
      'dolphin-${RuntimeCatalog.dolphinStableVersion}-universal.dmg',
    );
  });

  test('Dolphin on other OSes → null until a platform contract exists', () {
    expect(catalog.dolphin(os: 'linux', arch: 'x86_64'), isNull);
    expect(catalog.dolphin(os: 'windows', arch: 'x86_64'), isNull);
  });
}
