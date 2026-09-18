/// Backend-resolved presentation artwork. Box art remains a contained fallback.
final class ConsoleArtwork {
  const ConsoleArtwork({
    required this.role,
    required this.url,
    required this.fit,
    required this.contentVersion,
    required this.assetId,
    required this.width,
    required this.height,
    required this.originalWidth,
    required this.originalHeight,
    required this.fallbackReason,
    required this.variants,
  });

  final String role;
  final Uri? url;
  final String fit;
  final String? contentVersion;
  final String? assetId;
  final int? width;
  final int? height;
  final int? originalWidth;
  final int? originalHeight;
  final String fallbackReason;
  final List<ConsoleArtworkVariant> variants;
  bool get contain => fit == 'Contain';

  ConsoleArtwork withUrl(Uri localUrl) => ConsoleArtwork(
    role: role,
    url: localUrl,
    fit: fit,
    contentVersion: contentVersion,
    assetId: assetId,
    width: width,
    height: height,
    originalWidth: originalWidth,
    originalHeight: originalHeight,
    fallbackReason: fallbackReason,
    variants: variants,
  );

  Map<String, Object?> toJson() => {
    'role': role,
    'url': url?.toString(),
    'fit': fit,
    'contentVersion': contentVersion,
    'assetId': assetId,
    'width': width,
    'height': height,
    'originalWidth': originalWidth,
    'originalHeight': originalHeight,
    'fallbackReason': fallbackReason,
    'variants': variants.map((v) => v.toJson()).toList(),
  };

  static List<ConsoleArtwork> readList(Object? value, Uri origin) =>
      value is List<Object?>
      ? value
            .map((v) => read(v, origin))
            .whereType<ConsoleArtwork>()
            .toList(growable: false)
      : const [];

  static ConsoleArtwork? read(
    Object? value,
    Uri origin, {
    bool allowLocal = false,
  }) {
    if (value is! Map<String, Object?>) return null;
    final role = value['role'];
    final fit = value['fit'];
    if ((role != 'Poster' && role != 'Hero') ||
        (fit != 'Contain' && fit != 'Cover'))
      return null;
    Uri? resolve(Object? raw) {
      if (raw is! String || raw.isEmpty) return null;
      final uri = Uri.tryParse(raw);
      if (uri == null) return null;
      final resolved = origin.resolveUri(uri);
      if (allowLocal && resolved.scheme == 'file') return resolved;
      return (resolved.scheme == 'http' || resolved.scheme == 'https') &&
              resolved.origin == origin.origin &&
              resolved.userInfo.isEmpty
          ? resolved
          : null;
    }

    int? dimension(Object? raw) => raw is int && raw > 0 ? raw : null;
    final variants = <ConsoleArtworkVariant>[];
    if (value['variants'] case final List<Object?> items) {
      for (final item in items) {
        if (item case <String, Object?>{
          'name': final String name,
          'contentVersion': final String version,
          'contentType': final String type,
          'width': final int width,
          'height': final int height,
        }) {
          final uri = resolve(item['url']);
          if (uri != null && width > 0 && height > 0) {
            variants.add(
              ConsoleArtworkVariant(
                name: name,
                url: uri,
                contentVersion: version,
                contentType: type,
                width: width,
                height: height,
              ),
            );
          }
        }
      }
    }
    return ConsoleArtwork(
      role: role as String,
      fit: fit as String,
      url: resolve(value['url']),
      assetId: value['assetId'] is String ? value['assetId'] as String : null,
      contentVersion: value['contentVersion'] is String
          ? value['contentVersion'] as String
          : null,
      width: dimension(value['width']),
      height: dimension(value['height']),
      originalWidth: dimension(value['originalWidth']),
      originalHeight: dimension(value['originalHeight']),
      fallbackReason: value['fallbackReason'] is String
          ? value['fallbackReason'] as String
          : 'NoArtwork',
      variants: variants,
    );
  }
}

final class ConsoleArtworkVariant {
  const ConsoleArtworkVariant({
    required this.name,
    required this.url,
    required this.contentVersion,
    required this.contentType,
    required this.width,
    required this.height,
  });
  final String name;
  final Uri url;
  final String contentVersion;
  final String contentType;
  final int width;
  final int height;
  Map<String, Object?> toJson() => {
    'name': name,
    'url': url.toString(),
    'contentVersion': contentVersion,
    'contentType': contentType,
    'width': width,
    'height': height,
  };
}

extension ResolvedArtworkRoles on List<ConsoleArtwork> {
  ConsoleArtwork? forRole(String role) {
    for (final item in this) {
      if (item.role == role) return item;
    }
    return null;
  }
}
