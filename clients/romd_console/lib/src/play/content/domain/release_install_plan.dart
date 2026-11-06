import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/domain/rom_delivery_format.dart';
import 'package:romd_console/src/domain/server_bound_release_manifest.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// The delivered format chosen for one install, before any download. The
/// served manifest's logical Original identity is kept beside the physical
/// manifest that is downloaded, verified, persisted and launched, so neither
/// identity is ever rewritten into the other.
final class ReleaseInstallPlan {
  const ReleaseInstallPlan._({
    required this.format,
    required this.original,
    required this.delivered,
  });

  final RomDeliveryFormat format;

  /// The manifest exactly as served: logical Original items and launch path.
  final ServerBoundReleaseManifest original;

  /// What is materialized: [format]'s grants, sizes, digests and launch path.
  /// For Original this is [original]; its validity is judged downstream.
  final ServerBoundReleaseManifest delivered;

  /// Runtime-driven selection. CHD only when [runtime] opens delivered CHD and
  /// the manifest's CHD entry is available and coherent; otherwise Original.
  static ReleaseInstallPlan select(
    ServerBoundReleaseManifest manifest, {
    required RuntimeProfile? runtime,
  }) => _chd(manifest, runtime) ?? _original(manifest);

  /// Re-plans a freshly issued manifest for an already chosen [format]. Null
  /// when that format is no longer deliverable; never substitutes another.
  static ReleaseInstallPlan? forFormat(
    ServerBoundReleaseManifest manifest,
    RomDeliveryFormat format, {
    required RuntimeProfile? runtime,
  }) => switch (format) {
    RomDeliveryFormat.original => _original(manifest),
    RomDeliveryFormat.chd => _chd(manifest, runtime),
    RomDeliveryFormat.rvz => null,
  };

  static ReleaseInstallPlan _original(ServerBoundReleaseManifest manifest) =>
      ReleaseInstallPlan._(
        format: RomDeliveryFormat.original,
        original: manifest,
        delivered: manifest,
      );

  static ReleaseInstallPlan? _chd(
    ServerBoundReleaseManifest manifest,
    RuntimeProfile? runtime,
  ) {
    if (runtime == null ||
        !BuiltinRuntimeProfiles.opensDeliveredFormat(
          runtime,
          RomDeliveryFormat.chd,
          platformShortName: manifest.systemKey,
        ) ||
        !manifest.isComplete ||
        manifest.items.length != 1 ||
        manifest.runtime.contentType != 'single_rom') {
      return null;
    }
    final item = manifest.items.single;
    // The logical Original identity must be known to keep it beside the CHD.
    if (item.provenance != ContentFileProvenance.original ||
        !item.isAvailable ||
        item.sha256 == null ||
        manifest.runtime.launch?.relativePath != item.relativePath) {
      return null;
    }
    final chd = item.formats.where(
      (option) =>
          option.format == RomDeliveryFormat.chd &&
          option.state == RomDeliveryState.available,
    );
    if (chd.length != 1) return null;
    final option = chd.single;
    final filename = option.filename;
    final size = option.sizeBytes;
    final sha256 = option.sha256;
    final grant = option.contentGrant;
    final physicalRuntime = option.runtime;
    final launch = physicalRuntime?.launch;
    if (grant == null ||
        size == null ||
        size <= 0 ||
        !_initialized(sha256) ||
        !_initialized(option.md5) ||
        !_initialized(option.sha1) ||
        filename != option.relativePath.split('/').last ||
        !option.relativePath.toLowerCase().endsWith('.chd') ||
        !option.runtimeApplicability.contains(runtime.adapterId.value) ||
        physicalRuntime == null ||
        physicalRuntime.contentType != 'single_rom' ||
        physicalRuntime.packaging != 'direct_files' ||
        physicalRuntime.minimumInstallBytes != size ||
        launch == null ||
        launch.type != 'file' ||
        launch.relativePath != option.relativePath) {
      return null;
    }
    return ReleaseInstallPlan._(
      format: RomDeliveryFormat.chd,
      original: manifest,
      delivered: ServerBoundReleaseManifest(
        serverInstanceId: manifest.serverInstanceId,
        releaseId: manifest.releaseId,
        titleId: manifest.titleId,
        systemKey: manifest.systemKey,
        name: manifest.name,
        revision: manifest.revision,
        isComplete: manifest.isComplete,
        isDeliveryComplete: true,
        runtime: physicalRuntime,
        items: <ServerBoundReleaseManifestItem>[
          ServerBoundReleaseManifestItem(
            relativePath: option.relativePath,
            role: item.role,
            provenance: item.provenance,
            formats: const [],
            sizeBytes: size,
            sha256: sha256,
            isAvailable: true,
            contentGrant: grant,
          ),
        ],
      ),
    );
  }

  /// Boundary-validated hex digests that are present and not all zeroes.
  static bool _initialized(String? digest) =>
      digest != null && digest.codeUnits.any((unit) => unit != 0x30);
}
