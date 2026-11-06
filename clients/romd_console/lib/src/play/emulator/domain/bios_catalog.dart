/// One BIOS file the ROMD library can (or cannot) supply for a platform.
/// Hashes are lowercase hex; a null hash means the server doesn't know it.
/// [downloadUrl] is a self-authorizing content grant (same contract as
/// release-manifest item grants), present only when [isAvailable].
final class BiosFileListing {
  const BiosFileListing({
    required this.biosId,
    required this.name,
    required this.fileName,
    required this.sizeBytes,
    required this.isAvailable,
    this.sha1,
    this.md5,
    this.sha256,
    this.downloadUrl,
  });

  final String biosId;
  final String name;
  final String fileName;
  final int sizeBytes;
  final String? sha1;
  final String? md5;
  final String? sha256;
  final bool isAvailable;
  final Uri? downloadUrl;
}

/// Read seam for the server's per-platform BIOS listing. Resolvers match the
/// listings to a profile's BIOS file specs by hash — never by file name.
abstract interface class BiosCatalog {
  Future<List<BiosFileListing>> biosForPlatform(String platformShortName);
}
