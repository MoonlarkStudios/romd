import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// Resolves BIOS files for a runtime profile into the directory that runtime
/// expects. Matching is by hash, never filename: DAT catalogs and emulator docs
/// can disagree on casing/naming while still describing the same firmware.
final class RuntimeBiosResolver {
  const RuntimeBiosResolver({
    required BiosCatalog biosCatalog,
    required DownloadClient downloadClient,
    ContentVerifier verifier = const ContentVerifier(),
  }) : _biosCatalog = biosCatalog,
       _downloads = downloadClient,
       _verifier = verifier;

  final BiosCatalog _biosCatalog;
  final DownloadClient _downloads;
  final ContentVerifier _verifier;

  Stream<DependencyProgress> resolve({
    required BiosSetRequirement requirement,
    required String platformShortName,
    required Directory biosDir,
  }) async* {
    if (requirement.acceptsAnyPlatformBios) {
      yield* _resolveAnyPlatformBios(
        requirement: requirement,
        platformShortName: platformShortName,
        biosDir: biosDir,
      );
      return;
    }

    final missing = await _missingSpecs(requirement, biosDir);
    if (missing.isNotEmpty) {
      yield* _acquireBiosFiles(
        requirement: requirement,
        platformShortName: platformShortName,
        biosDir: biosDir,
        missing: missing,
      );
    }
    if (!await isSatisfied(requirement, biosDir)) {
      yield const DependencyResolutionFailed(
        "Your ROMD library doesn't have the BIOS files this platform needs.",
      );
    }
  }

  Future<bool> isSatisfied(
    BiosSetRequirement requirement,
    Directory biosDir,
  ) async {
    if (requirement.acceptsAnyPlatformBios) {
      return _hasAnyLocalBiosFile(biosDir);
    }

    final missing = await _missingSpecs(requirement, biosDir);
    return switch (requirement.satisfaction) {
      BiosSatisfaction.all => missing.isEmpty,
      BiosSatisfaction.anyOne => missing.length < requirement.files.length,
    };
  }

  Stream<DependencyProgress> _resolveAnyPlatformBios({
    required BiosSetRequirement requirement,
    required String platformShortName,
    required Directory biosDir,
  }) async* {
    if (!await _hasAnyLocalBiosFile(biosDir)) {
      yield* _acquireAnyPlatformBiosFile(
        requirement: requirement,
        platformShortName: platformShortName,
        biosDir: biosDir,
      );
    }
    if (!await _hasAnyLocalBiosFile(biosDir)) {
      yield const DependencyResolutionFailed(
        "Your ROMD library doesn't have the BIOS files this platform needs.",
      );
    }
  }

  Stream<DependencyProgress> _acquireAnyPlatformBiosFile({
    required BiosSetRequirement requirement,
    required String platformShortName,
    required Directory biosDir,
  }) async* {
    final listings = await _biosCatalog.biosForPlatform(platformShortName);
    for (final listing in listings) {
      final downloadUrl = listing.downloadUrl;
      if (!listing.isAvailable || downloadUrl == null) {
        continue;
      }

      await biosDir.create(recursive: true);
      final fileName = _safeCatalogFileName(listing.fileName);
      final staged = File(p.join(biosDir.path, '$fileName.download'));
      await for (final received in _downloads.download(
        url: downloadUrl,
        destination: staged,
      )) {
        yield DependencyDownloading(
          label: requirement.displayName,
          receivedBytes: received,
          totalBytes: listing.sizeBytes > 0 ? listing.sizeBytes : null,
        );
      }

      if (listing.sha256 case final sha256?) {
        final verified = await _verifier.verify(
          file: staged,
          expectedSize: listing.sizeBytes,
          expectedSha256Hex: sha256,
        );
        if (!verified) {
          await _safeDelete(staged);
          continue;
        }
      }

      await staged.rename(p.join(biosDir.path, fileName));
      return;
    }
  }

  Stream<DependencyProgress> _acquireBiosFiles({
    required BiosSetRequirement requirement,
    required String platformShortName,
    required Directory biosDir,
    required List<BiosFileSpec> missing,
  }) async* {
    final listings = await _biosCatalog.biosForPlatform(platformShortName);
    for (final spec in missing) {
      final listing = _matchByHash(spec, listings);
      final downloadUrl = listing?.downloadUrl;
      if (listing == null || !listing.isAvailable || downloadUrl == null) {
        continue;
      }

      await biosDir.create(recursive: true);
      final staged = File(p.join(biosDir.path, '${spec.fileName}.download'));
      await for (final received in _downloads.download(
        url: downloadUrl,
        destination: staged,
      )) {
        yield DependencyDownloading(
          label: requirement.displayName,
          receivedBytes: received,
          totalBytes: listing.sizeBytes > 0 ? listing.sizeBytes : null,
        );
      }

      if (listing.sha256 case final sha256?) {
        final verified = await _verifier.verify(
          file: staged,
          expectedSize: listing.sizeBytes,
          expectedSha256Hex: sha256,
        );
        if (!verified) {
          await _safeDelete(staged);
          continue;
        }
      }
      await staged.rename(p.join(biosDir.path, spec.fileName));
    }
  }

  BiosFileListing? _matchByHash(
    BiosFileSpec spec,
    List<BiosFileListing> listings,
  ) {
    for (final listing in listings) {
      final matched = switch ((spec.sha1, listing.sha1)) {
        (final specSha1?, final listingSha1?) =>
          specSha1 == listingSha1.toLowerCase(),
        _ => switch ((spec.md5, listing.md5)) {
          (final specMd5?, final listingMd5?) =>
            specMd5 == listingMd5.toLowerCase(),
          _ => false,
        },
      };
      if (matched) {
        return listing;
      }
    }
    return null;
  }

  Future<List<BiosFileSpec>> _missingSpecs(
    BiosSetRequirement requirement,
    Directory biosDir,
  ) async {
    final missing = <BiosFileSpec>[];
    for (final spec in requirement.files) {
      if (!await File(p.join(biosDir.path, spec.fileName)).exists()) {
        missing.add(spec);
      }
    }
    return missing;
  }

  Future<bool> _hasAnyLocalBiosFile(Directory biosDir) async {
    if (!await biosDir.exists()) {
      return false;
    }

    await for (final entity in biosDir.list(followLinks: false)) {
      if (entity is File && !entity.path.endsWith('.download')) {
        return true;
      }
    }
    return false;
  }

  Future<void> _safeDelete(File file) async {
    try {
      if (await file.exists()) {
        await file.delete();
      }
    } on Object {
      // Best-effort cleanup; the .download suffix keeps a leftover inert.
    }
  }

  String _safeCatalogFileName(String fileName) {
    final name = p.basename(fileName.replaceAll('\\', '/')).trim();
    return name.isEmpty ? 'bios.bin' : name;
  }
}
