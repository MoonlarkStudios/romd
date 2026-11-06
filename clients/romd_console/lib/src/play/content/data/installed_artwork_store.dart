import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/domain/console_artwork.dart';

/// Durable artwork lives outside verified ROM roots and is scoped to a server
/// instance and release. Only the two resolved delivery images are retained.
final class InstalledArtworkStore {
  InstalledArtworkStore(this.baseDir, {HttpClient Function()? createClient})
    : _createClient = createClient ?? HttpClient.new;
  final Directory baseDir;
  final HttpClient Function() _createClient;
  static const _maximumBytes = 8 * 1024 * 1024;

  Directory _directory(String server, String release) => Directory(
    p.join(
      baseDir.path,
      'artwork',
      sha256.convert(utf8.encode(server)).toString(),
      sha256.convert(utf8.encode(release)).toString(),
    ),
  );

  static String _identity(ConsoleArtwork artwork) => sha256
      .convert(
        utf8.encode(
          '${artwork.assetId}\u0000${artwork.contentVersion}\u0000${artwork.url}',
        ),
      )
      .toString();

  List<ConsoleArtwork> read(String server, String release) {
    final directory = _directory(server, release);
    return _readSourceManifest(server, release)
        .map(
          (art) => art.withUrl(
            File(p.join(directory.path, '${_identity(art)}.image')).uri,
          ),
        )
        .toList(growable: false);
  }

  List<ConsoleArtwork> _readSourceManifest(String server, String release) {
    final directory = _directory(server, release);
    final manifest = File(p.join(directory.path, 'manifest.json'));
    try {
      if (!manifest.existsSync() || manifest.lengthSync() > 65536)
        return const [];
      final raw = jsonDecode(manifest.readAsStringSync());
      if (raw is! Map<String, Object?> ||
          raw['server'] != server ||
          raw['release'] != release)
        return const [];
      final origin = Uri.tryParse(raw['origin'] as String? ?? '');
      if (origin == null || !origin.hasAuthority) return const [];
      return ConsoleArtwork.readList(raw['artwork'], origin)
          .where(
            (art) =>
                art.url != null &&
                File(
                  p.join(directory.path, '${_identity(art)}.image'),
                ).existsSync(),
          )
          .toList(growable: false);
    } on Object {
      // Missing/corrupt cache affects presentation only, never install authority.
      return const [];
    }
  }

  Future<bool> remove(String server, String release) async {
    try {
      final directory = _directory(server, release);
      if (await directory.exists()) await directory.delete(recursive: true);
      return true;
    } on FileSystemException {
      return false;
    }
  }

  Future<List<ConsoleArtwork>> retain({
    required String server,
    required String release,
    required Uri origin,
    required List<ConsoleArtwork> artwork,
  }) async {
    if (artwork.isEmpty) return read(server, release);
    if (artwork.every((art) => art.url == null)) {
      await remove(server, release);
      return const [];
    }
    final directory = _directory(server, release);
    final previous = _readSourceManifest(server, release);
    final retained = <ConsoleArtwork>[];
    final client = _createClient()
      ..connectionTimeout = const Duration(seconds: 10);
    try {
      await directory.create(recursive: true);
      for (final art
          in artwork
              .where((a) => a.role == 'Poster' || a.role == 'Hero')
              .take(2)) {
        final uri = art.url;
        if (uri == null ||
            !['http', 'https'].contains(uri.scheme) ||
            uri.origin != origin.origin ||
            uri.userInfo.isNotEmpty)
          continue;
        final file = File(p.join(directory.path, '${_identity(art)}.image'));
        try {
          if (!await file.exists()) {
            final bytes = await _download(
              client,
              uri,
            ).timeout(const Duration(seconds: 15));
            final staging = File('${file.path}.partial');
            await staging.writeAsBytes(bytes, flush: true);
            await staging.rename(file.path);
          }
          retained.add(art);
        } on Object {
          // A failed replacement preserves this role even if another role succeeds.
          final fallback = previous.forRole(art.role);
          if (fallback != null) retained.add(fallback);
        }
      }
      {
        final manifest = File(p.join(directory.path, 'manifest.json'));
        final staging = File('${manifest.path}.partial');
        await staging.writeAsString(
          jsonEncode({
            'server': server,
            'release': release,
            'origin': origin.toString(),
            'artwork': retained.map((a) => a.toJson()).toList(),
          }),
          flush: true,
        );
        await staging.rename(manifest.path);
        final keep = retained.map((a) => '${_identity(a)}.image').toSet();
        await for (final entry in directory.list()) {
          if (entry is File &&
              entry.path.endsWith('.image') &&
              !keep.contains(p.basename(entry.path))) {
            await entry.delete();
          }
        }
      }
    } on FileSystemException {
      // Preserve existing manifest when local storage cannot accept artwork.
    } finally {
      client.close(force: true);
    }
    return read(server, release);
  }

  Future<List<int>> _download(HttpClient client, Uri uri) async {
    final request = await client.getUrl(uri);
    request.followRedirects = false;
    final response = await request.close();
    if (response.statusCode != HttpStatus.ok ||
        response.contentLength > _maximumBytes ||
        ![
          'image/png',
          'image/jpeg',
          'image/webp',
        ].contains(response.headers.contentType?.mimeType)) {
      throw const FormatException('Invalid artwork response');
    }
    final bytes = <int>[];
    await for (final chunk in response) {
      if (bytes.length + chunk.length > _maximumBytes)
        throw const FormatException('Artwork too large');
      bytes.addAll(chunk);
    }
    if (bytes.isEmpty) throw const FormatException('Empty artwork');
    return bytes;
  }
}
