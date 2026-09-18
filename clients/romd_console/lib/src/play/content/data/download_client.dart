import 'dart:io';

/// Streams content-grant downloads to disk.
///
/// Content-grant URLs are **self-authorizing** (the path token is the auth), so
/// no `Authorization` header is attached and redirects are followed without
/// re-adding credentials. Grant URLs are never placed in exceptions or logs.
abstract interface class DownloadClient {
  /// Streams the bytes at [url] into [destination], yielding the cumulative
  /// received byte count. Throws a [DownloadException] on failure.
  Stream<int> download({required Uri url, required File destination});
}

/// Failure modes a caller must distinguish — notably [DownloadGrantExpired],
/// which signals the install flow to re-issue the manifest.
sealed class DownloadException implements Exception {
  const DownloadException();
}

/// The grant URL returned 401/404 — the grant has likely expired or been
/// revoked; the caller should re-issue the manifest and retry.
final class DownloadGrantExpired extends DownloadException {
  const DownloadGrantExpired();
}

/// A non-success HTTP status other than 401/404.
final class DownloadHttpError extends DownloadException {
  const DownloadHttpError(this.statusCode);
  final int statusCode;
}

/// A transport-level failure (connection dropped, mid-stream error). The URL is
/// deliberately omitted to avoid leaking a grant token.
final class DownloadNetworkError extends DownloadException {
  const DownloadNetworkError();
}

final class HttpDownloadClient implements DownloadClient {
  HttpDownloadClient({HttpClient? httpClient})
    : _httpClient = httpClient ?? HttpClient();

  final HttpClient _httpClient;

  @override
  Stream<int> download({required Uri url, required File destination}) async* {
    if (!url.isAbsolute ||
        !url.hasAuthority ||
        (url.scheme != 'https' && url.scheme != 'http')) {
      throw const DownloadNetworkError();
    }
    await destination.parent.create(recursive: true);

    final HttpClientResponse response;
    try {
      // followRedirects defaults true; we never attach an Authorization header,
      // so a redirect to storage carries no credentials to leak.
      final request = await _httpClient.getUrl(url);
      response = await request.close();
    } on Object {
      throw const DownloadNetworkError();
    }

    final status = response.statusCode;
    if (status == HttpStatus.unauthorized || status == HttpStatus.notFound) {
      await response.drain<void>();
      throw const DownloadGrantExpired();
    }
    if (status < 200 || status >= 300) {
      await response.drain<void>();
      throw DownloadHttpError(status);
    }

    final sink = destination.openWrite();
    var received = 0;
    try {
      await for (final chunk in response) {
        sink.add(chunk);
        received += chunk.length;
        yield received;
      }
      await sink.close();
    } on Object {
      try {
        await sink.close();
      } on Object {
        // Closing a sink that already errored can rethrow; ignore.
      }
      await _safeDelete(destination);
      throw const DownloadNetworkError();
    }
  }

  Future<void> _safeDelete(File file) async {
    try {
      if (await file.exists()) {
        await file.delete();
      }
    } on Object {
      // Best-effort cleanup of a partial file.
    }
  }

  void close() => _httpClient.close(force: true);
}
