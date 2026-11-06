import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'rom_delivery_format.dart';
import 'romd_public_id.dart';
import 'romd_server_instance_id.dart';

sealed class ReleaseManifestResult {
  const ReleaseManifestResult();
}

final class ReleaseManifestSuccess extends ReleaseManifestResult {
  const ReleaseManifestSuccess(this.manifest);

  final ServerBoundReleaseManifest manifest;
}

final class ReleaseManifestFailure extends ReleaseManifestResult {
  const ReleaseManifestFailure(this.kind, {this.statusCode, this.bearerError});

  final ReleaseManifestFailureKind kind;
  final int? statusCode;
  final String? bearerError;

  bool get isInvalidToken =>
      kind == ReleaseManifestFailureKind.unauthorized &&
      bearerError == 'invalid_token';
}

enum ReleaseManifestFailureKind {
  badRequest,
  unauthorized,
  notFound,
  conflict,
  clientError,
  serverError,
  redirect,
  unexpectedStatus,
  timeout,
  transport,
  malformedResponse,
  identityMismatch,
}

final class ServerBoundReleaseManifest {
  ServerBoundReleaseManifest({
    required this.serverInstanceId,
    required this.releaseId,
    required this.titleId,
    required this.systemKey,
    required this.name,
    required this.revision,
    required this.isComplete,
    required this.isDeliveryComplete,
    required this.runtime,
    required List<ServerBoundReleaseManifestItem> items,
  }) : items = List<ServerBoundReleaseManifestItem>.unmodifiable(items);

  final RomdServerInstanceId serverInstanceId;
  final RomdPublicId releaseId;
  final RomdPublicId titleId;
  final String systemKey;
  final String name;
  final String? revision;
  final bool isComplete;
  final bool isDeliveryComplete;
  final ServerBoundReleaseRuntime runtime;
  final List<ServerBoundReleaseManifestItem> items;
}

final class ServerBoundReleaseRuntime {
  const ServerBoundReleaseRuntime({
    required this.contentType,
    required this.launch,
    required this.packaging,
    required this.minimumInstallBytes,
  });

  final String contentType;
  final ServerBoundLaunchTarget? launch;
  final String packaging;
  final int minimumInstallBytes;
}

final class ServerBoundLaunchTarget {
  const ServerBoundLaunchTarget({
    required this.type,
    required this.relativePath,
  });

  final String type;
  final String relativePath;
}

final class ServerBoundReleaseManifestItem {
  ServerBoundReleaseManifestItem({
    required this.relativePath,
    required this.role,
    required this.provenance,
    required this.sizeBytes,
    required this.sha256,
    required this.isAvailable,
    required this.contentGrant,
    required List<ServerBoundReleaseManifestFormat> formats,
  }) : formats = List<ServerBoundReleaseManifestFormat>.unmodifiable(formats);

  /// Exact delivered file facts; provenance never changes its digest or size.
  final String relativePath;
  final String role;
  final ContentFileProvenance provenance;
  final int sizeBytes;
  final String? sha256;
  final bool isAvailable;
  final ServerBoundContentGrant? contentGrant;
  final List<ServerBoundReleaseManifestFormat> formats;
}

/// Format-specific physical facts observed from the server. Shape is validated
/// at the boundary; whether a non-Original format is coherent enough to install
/// is decided by the install plan, so an unusable entry never rejects the
/// manifest or blocks its Original.
final class ServerBoundReleaseManifestFormat {
  ServerBoundReleaseManifestFormat({
    required this.format,
    required this.state,
    required this.relativePath,
    required this.profileVersion,
    required List<String> runtimeApplicability,
    this.filename,
    this.sizeBytes,
    this.sha256,
    this.md5,
    this.sha1,
    this.contentGrant,
    this.runtime,
  }) : runtimeApplicability = List<String>.unmodifiable(runtimeApplicability);

  final RomDeliveryFormat format;
  final RomDeliveryState state;
  final String relativePath;
  final String profileVersion;
  final List<String> runtimeApplicability;
  final String? filename;
  final int? sizeBytes;
  final String? sha256;
  final String? md5;
  final String? sha1;
  final ServerBoundContentGrant? contentGrant;
  final ServerBoundReleaseRuntime? runtime;
}

final class ServerBoundContentGrant {
  const ServerBoundContentGrant({
    required this.downloadUrl,
    required this.expiresAt,
  });

  final Uri downloadUrl;
  final DateTime expiresAt;
}
