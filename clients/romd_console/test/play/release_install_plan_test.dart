import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/domain/rom_delivery_format.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/domain/server_bound_release_manifest.dart';
import 'package:romd_console/src/play/content/domain/release_install_plan.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

final _pcsx2 = BuiltinRuntimeProfiles.all.singleWhere(
  (profile) => profile.adapterId == BuiltinRuntimeProfiles.pcsx2AdapterId,
);

/// A hypothetical second PS2 runtime: same platform, different adapter.
const _otherPs2 = RuntimeProfile(
  id: RuntimeProfileId('other:ps2:standalone'),
  adapterId: RuntimeAdapterId('other'),
  displayName: 'Other PS2',
  supportedPlatforms: <String>{'ps2'},
  requirements: <RuntimeDependencyRequirement>[],
);

const _isoSha =
    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const _chdSha =
    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';

ServerBoundContentGrant _grant(String token) => ServerBoundContentGrant(
  downloadUrl: Uri.parse('https://library.example/delivery/content/$token'),
  expiresAt: DateTime.utc(2030),
);

ServerBoundReleaseManifestFormat _chd({
  RomDeliveryState state = RomDeliveryState.available,
  String path = 'disc/game.chd',
  String? filename = 'game.chd',
  int? size = 4,
  String? sha256 = _chdSha,
  String? md5 = 'cccccccccccccccccccccccccccccccc',
  String? sha1 = 'dddddddddddddddddddddddddddddddddddddddd',
  bool grant = true,
  List<String> applicability = const <String>['pcsx2'],
  ServerBoundReleaseRuntime? runtime,
  bool noRuntime = false,
}) => ServerBoundReleaseManifestFormat(
  format: RomDeliveryFormat.chd,
  state: state,
  relativePath: path,
  profileVersion: 'chd-observed/v1',
  runtimeApplicability: applicability,
  filename: filename,
  sizeBytes: size,
  sha256: sha256,
  md5: md5,
  sha1: sha1,
  contentGrant: grant ? _grant('chd.sig') : null,
  runtime: noRuntime
      ? null
      : runtime ??
            ServerBoundReleaseRuntime(
              contentType: 'single_rom',
              launch: ServerBoundLaunchTarget(type: 'file', relativePath: path),
              packaging: 'direct_files',
              minimumInstallBytes: size ?? 0,
            ),
);

/// A single-file runtime launching [path], as the server attaches to each
/// available format of a single disc on a platform with a stored delivery.
ServerBoundReleaseRuntime _ownRuntime(String path, int size) =>
    ServerBoundReleaseRuntime(
      contentType: 'single_rom',
      launch: ServerBoundLaunchTarget(type: 'file', relativePath: path),
      packaging: 'direct_files',
      minimumInstallBytes: size,
    );

/// The available Original entry, carrying its own runtime as served.
ServerBoundReleaseManifestFormat _originalWithRuntime() =>
    ServerBoundReleaseManifestFormat(
      format: RomDeliveryFormat.original,
      state: RomDeliveryState.available,
      relativePath: 'disc/game.iso',
      profileVersion: 'identity/v1',
      runtimeApplicability: const <String>[],
      filename: 'game.iso',
      sizeBytes: 5,
      sha256: _isoSha,
      contentGrant: _grant('original.sig'),
      runtime: _ownRuntime('disc/game.iso', 5),
    );

/// An available RVZ entry carrying its own runtime, as served for GameCube.
ServerBoundReleaseManifestFormat _rvzWithRuntime() =>
    ServerBoundReleaseManifestFormat(
      format: RomDeliveryFormat.rvz,
      state: RomDeliveryState.available,
      relativePath: 'disc/game.rvz',
      profileVersion: 'rvz-import/v1',
      runtimeApplicability: const <String>['dolphin'],
      filename: 'game.rvz',
      sizeBytes: 3,
      sha256: _chdSha,
      md5: 'cccccccccccccccccccccccccccccccc',
      sha1: 'dddddddddddddddddddddddddddddddddddddddd',
      contentGrant: _grant('rvz.sig'),
      runtime: _ownRuntime('disc/game.rvz', 3),
    );

ServerBoundReleaseManifest _manifest({
  String systemKey = 'ps2',
  List<ServerBoundReleaseManifestFormat>? formats,
  bool originalAvailable = true,
  bool complete = true,
  bool secondItem = false,
  String? contentType,
}) {
  final items = <ServerBoundReleaseManifestItem>[
    ServerBoundReleaseManifestItem(
      provenance: ContentFileProvenance.original,

      relativePath: 'disc/game.iso',
      role: 'rom',
      sizeBytes: 5,
      sha256: originalAvailable ? _isoSha : null,
      isAvailable: originalAvailable,
      contentGrant: originalAvailable ? _grant('iso.sig') : null,
      formats: formats ?? <ServerBoundReleaseManifestFormat>[_chd()],
    ),
    if (secondItem)
      ServerBoundReleaseManifestItem(
        provenance: ContentFileProvenance.original,
        formats: [
          ServerBoundReleaseManifestFormat(
            format: RomDeliveryFormat.original,
            state: RomDeliveryState.available,
            relativePath: 'disc/extra.bin',
            profileVersion: 'identity/v1',
            runtimeApplicability: const [],
            filename: ('disc/extra.bin').split('/').last,
            sizeBytes: 1,
            sha256: _isoSha,
            contentGrant: _grant('extra.sig'),
          ),
        ],

        relativePath: 'disc/extra.bin',
        role: 'rom',
        sizeBytes: 1,
        sha256: _isoSha,
        isAvailable: true,
        contentGrant: _grant('extra.sig'),
      ),
  ];
  return ServerBoundReleaseManifest(
    isDeliveryComplete: complete,

    serverInstanceId: RomdServerInstanceId.tryParse(
      '11111111-1111-4111-8111-111111111111',
    )!,
    releaseId: RomdPublicId.tryParse(RomdPublicId.encode(1))!,
    titleId: RomdPublicId.tryParse(RomdPublicId.encode(2))!,
    systemKey: systemKey,
    name: 'Disc Game',
    revision: null,
    isComplete: complete,
    runtime: ServerBoundReleaseRuntime(
      contentType: contentType ?? (secondItem ? 'unknown' : 'single_rom'),
      launch: const ServerBoundLaunchTarget(
        type: 'file',
        relativePath: 'disc/game.iso',
      ),
      packaging: 'direct_files',
      minimumInstallBytes: secondItem ? 6 : 5,
    ),
    items: items,
  );
}

void _expectOriginal(ReleaseInstallPlan plan, ServerBoundReleaseManifest m) {
  expect(plan.format, RomDeliveryFormat.original);
  expect(plan.original, same(m));
  expect(plan.delivered, same(m));
}

void main() {
  test('PCSX2 selects the coherent CHD and keeps the Original apart', () {
    final manifest = _manifest();
    final plan = ReleaseInstallPlan.select(manifest, runtime: _pcsx2);

    expect(plan.format, RomDeliveryFormat.chd);
    expect(plan.original, same(manifest));
    final delivered = plan.delivered.items.single;
    expect(delivered.relativePath, 'disc/game.chd');
    expect(delivered.sizeBytes, 4);
    expect(delivered.sha256, _chdSha);
    expect(
      delivered.contentGrant?.downloadUrl.path,
      '/delivery/content/chd.sig',
    );
    expect(plan.delivered.runtime.launch?.relativePath, 'disc/game.chd');
    expect(plan.delivered.runtime.minimumInstallBytes, 4);
    expect(plan.original.items.single.sha256, _isoSha);
    expect(plan.original.runtime.launch?.relativePath, 'disc/game.iso');
  });

  test('other runtimes and no runtime select Original', () {
    final manifest = _manifest();
    final snes9x = BuiltinRuntimeProfiles.all.firstWhere(
      (profile) => profile.supportsPlatform('snes'),
    );
    for (final runtime in <RuntimeProfile?>[null, _otherPs2, snes9x]) {
      _expectOriginal(
        ReleaseInstallPlan.select(manifest, runtime: runtime),
        manifest,
      );
    }
  });

  test('PCSX2 still selects the CHD when the Original carries a runtime', () {
    final manifest = _manifest(formats: [_originalWithRuntime(), _chd()]);

    final plan = ReleaseInstallPlan.select(manifest, runtime: _pcsx2);
    expect(plan.format, RomDeliveryFormat.chd);
    expect(plan.original, same(manifest));
    expect(plan.delivered.items.single.relativePath, 'disc/game.chd');
    expect(plan.delivered.runtime.launch?.relativePath, 'disc/game.chd');
    for (final runtime in <RuntimeProfile?>[null, _otherPs2]) {
      _expectOriginal(
        ReleaseInstallPlan.select(manifest, runtime: runtime),
        manifest,
      );
    }
  });

  test('a GameCube disc whose formats carry runtimes plans Original', () {
    final dolphin = BuiltinRuntimeProfiles.all.singleWhere(
      (profile) => profile.adapterId == BuiltinRuntimeProfiles.dolphinAdapterId,
    );
    final manifest = _manifest(
      systemKey: 'gc',
      formats: [_originalWithRuntime(), _rvzWithRuntime()],
    );

    for (final runtime in <RuntimeProfile?>[null, dolphin, _pcsx2]) {
      _expectOriginal(
        ReleaseInstallPlan.select(manifest, runtime: runtime),
        manifest,
      );
    }
    _expectOriginal(
      ReleaseInstallPlan.forFormat(
        manifest,
        RomDeliveryFormat.original,
        runtime: dolphin,
      )!,
      manifest,
    );
    expect(
      ReleaseInstallPlan.forFormat(
        manifest,
        RomDeliveryFormat.rvz,
        runtime: dolphin,
      ),
      isNull,
    );
  });

  test('PCSX2 on a non-PS2 manifest selects Original', () {
    final manifest = _manifest(systemKey: 'psx');
    _expectOriginal(
      ReleaseInstallPlan.select(manifest, runtime: _pcsx2),
      manifest,
    );
  });

  final unusable = <String, ServerBoundReleaseManifest>{
    'no CHD entry': _manifest(formats: const []),
    'preparing CHD': _manifest(
      formats: [_chd(state: RomDeliveryState.preparing)],
    ),
    'unsupported CHD': _manifest(
      formats: [_chd(state: RomDeliveryState.unsupported)],
    ),
    'incomplete manifest': _manifest(complete: false),
    'multi-file release': _manifest(
      secondItem: true,
      formats: [_chd(noRuntime: true)],
    ),
    // Only the item-count guard applies: single_rom, coherent CHD runtime.
    'two items with a coherent CHD': _manifest(
      secondItem: true,
      contentType: 'single_rom',
    ),
    'unavailable Original identity': _manifest(originalAvailable: false),
    'null runtime': _manifest(formats: [_chd(noRuntime: true)]),
    'missing grant': _manifest(formats: [_chd(grant: false)]),
    'missing size': _manifest(formats: [_chd(size: null)]),
    'zero size': _manifest(formats: [_chd(size: 0)]),
    'missing sha256': _manifest(formats: [_chd(sha256: null)]),
    'missing md5': _manifest(formats: [_chd(md5: null)]),
    'missing sha1': _manifest(formats: [_chd(sha1: null)]),
    'zeroed sha256': _manifest(formats: [_chd(sha256: '0' * 64)]),
    'zeroed md5': _manifest(formats: [_chd(md5: '0' * 32)]),
    'zeroed sha1': _manifest(formats: [_chd(sha1: '0' * 40)]),
    'filename mismatch': _manifest(formats: [_chd(filename: 'other.chd')]),
    'missing filename': _manifest(formats: [_chd(filename: null)]),
    'non-CHD path': _manifest(
      formats: [_chd(path: 'disc/game.bin', filename: 'game.bin')],
    ),
    'server does not list PCSX2': _manifest(
      formats: [_chd(applicability: const <String>[])],
    ),
    'runtime launch path mismatch': _manifest(
      formats: [
        _chd(
          runtime: const ServerBoundReleaseRuntime(
            contentType: 'single_rom',
            launch: ServerBoundLaunchTarget(
              type: 'file',
              relativePath: 'disc/game.iso',
            ),
            packaging: 'direct_files',
            minimumInstallBytes: 4,
          ),
        ),
      ],
    ),
    'runtime size mismatch': _manifest(
      formats: [
        _chd(
          runtime: const ServerBoundReleaseRuntime(
            contentType: 'single_rom',
            launch: ServerBoundLaunchTarget(
              type: 'file',
              relativePath: 'disc/game.chd',
            ),
            packaging: 'direct_files',
            minimumInstallBytes: 5,
          ),
        ),
      ],
    ),
    'unknown runtime packaging': _manifest(
      formats: [
        _chd(
          runtime: const ServerBoundReleaseRuntime(
            contentType: 'single_rom',
            launch: ServerBoundLaunchTarget(
              type: 'file',
              relativePath: 'disc/game.chd',
            ),
            packaging: 'zip',
            minimumInstallBytes: 4,
          ),
        ),
      ],
    ),
    'unknown launch type': _manifest(
      formats: [
        _chd(
          runtime: const ServerBoundReleaseRuntime(
            contentType: 'single_rom',
            launch: ServerBoundLaunchTarget(
              type: 'folder',
              relativePath: 'disc/game.chd',
            ),
            packaging: 'direct_files',
            minimumInstallBytes: 4,
          ),
        ),
      ],
    ),
  };
  for (final entry in unusable.entries) {
    test('PCSX2 falls back to Original for ${entry.key}', () {
      _expectOriginal(
        ReleaseInstallPlan.select(entry.value, runtime: _pcsx2),
        entry.value,
      );
    });
  }

  test('forFormat re-plans the chosen format and never substitutes', () {
    final fresh = _manifest();
    expect(
      ReleaseInstallPlan.forFormat(
        fresh,
        RomDeliveryFormat.chd,
        runtime: _pcsx2,
      )?.format,
      RomDeliveryFormat.chd,
    );
    final withdrawn = _manifest(
      formats: [_chd(state: RomDeliveryState.unsupported)],
    );
    expect(
      ReleaseInstallPlan.forFormat(
        withdrawn,
        RomDeliveryFormat.chd,
        runtime: _pcsx2,
      ),
      isNull,
    );
    expect(
      ReleaseInstallPlan.forFormat(
        fresh,
        RomDeliveryFormat.chd,
        runtime: _otherPs2,
      ),
      isNull,
    );
    expect(
      ReleaseInstallPlan.forFormat(
        fresh,
        RomDeliveryFormat.original,
        runtime: _pcsx2,
      )?.format,
      RomDeliveryFormat.original,
    );
    expect(
      ReleaseInstallPlan.forFormat(
        fresh,
        RomDeliveryFormat.rvz,
        runtime: _pcsx2,
      ),
      isNull,
    );
  });

  test('only the PCSX2 adapter opens delivered CHD', () {
    for (final profile in BuiltinRuntimeProfiles.all) {
      for (final platform in profile.supportedPlatforms) {
        expect(
          BuiltinRuntimeProfiles.opensDeliveredFormat(
            profile,
            RomDeliveryFormat.original,
            platformShortName: platform,
          ),
          isTrue,
        );
        expect(
          BuiltinRuntimeProfiles.opensDeliveredFormat(
            profile,
            RomDeliveryFormat.chd,
            platformShortName: platform,
          ),
          profile.id == const RuntimeProfileId('pcsx2:ps2:standalone'),
          reason: profile.id.value,
        );
        expect(
          BuiltinRuntimeProfiles.opensDeliveredFormat(
            profile,
            RomDeliveryFormat.rvz,
            platformShortName: platform,
          ),
          isFalse,
        );
      }
    }
    expect(
      BuiltinRuntimeProfiles.opensDeliveredFormat(
        _otherPs2,
        RomDeliveryFormat.chd,
        platformShortName: 'ps2',
      ),
      isFalse,
    );
  });
}
