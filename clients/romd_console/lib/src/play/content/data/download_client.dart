import 'dart:async';
import 'dart:io';

/// Streams content-grant downloads to disk.
///
/// Content-grant URLs are **self-authorizing** (the path token is the auth), so
/// no `Authorization` header is attached and redirects are followed without
/// re-adding credentials. Grant URLs are never placed in exceptions or logs.
abstract interface class DownloadClient {
  /// Streams the bytes at [url] into [destination], yielding the cumulative
  /// received byte count. Throws a [DownloadException] on failure. Cancelling
  /// the subscription closes the transfer and removes its partial output.
  /// When [maximumBytes] is supplied, reject overflow before writing that chunk.
  Stream<int> download({
    required Uri url,
    required File destination,
    int? maximumBytes,
  });
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

/// The streamed response exceeds the caller's verified content budget.
final class DownloadSizeExceeded extends DownloadException {
  const DownloadSizeExceeded();
}

final class HttpDownloadClient implements DownloadClient {
  HttpDownloadClient({
    HttpClient? httpClient,
    DateTime Function()? now,
    Timer Function(Duration, void Function())? createRetryTimer,
  }) : _httpClient = httpClient ?? HttpClient(),
       _now = now ?? DateTime.now,
       _createRetryTimer = createRetryTimer ?? Timer.new;

  final HttpClient _httpClient;
  final DateTime Function() _now;
  final Timer Function(Duration, void Function()) _createRetryTimer;

  @override
  Stream<int> download({
    required Uri url,
    required File destination,
    int? maximumBytes,
  }) => _HttpDownload(
    _httpClient,
    url,
    destination,
    maximumBytes,
    _now,
    _createRetryTimer,
  ).stream;

  void close() => _httpClient.close(force: true);
}

/// Each subscription owns its request, response and file. Cancelling a download
/// must never close the shared HTTP client or wait for a stalled server.
final class _HttpDownload {
  _HttpDownload(
    this._httpClient,
    this._url,
    this._destination,
    this._maximumBytes,
    this._now,
    this._createRetryTimer,
  ) {
    if (_maximumBytes != null && _maximumBytes < 0) {
      throw ArgumentError.value(
        _maximumBytes,
        'maximumBytes',
        'Must be nonnegative',
      );
    }
    _progress = StreamController<int>(
      sync: true,
      onListen: () => scheduleMicrotask(_run),
      onPause: () => _resumed ??= Completer<void>(),
      onResume: () {
        _resumed?.complete();
        _resumed = null;
      },
      onCancel: _cancel,
    );
  }

  final HttpClient _httpClient;
  final Uri _url;
  final File _destination;
  final int? _maximumBytes;
  final DateTime Function() _now;
  final Timer Function(Duration, void Function()) _createRetryTimer;
  static const _maximumRetries = 3;
  static const _maximumRetryWait = Duration(minutes: 1);
  static const _defaultRetryWait = Duration(seconds: 15);
  late final StreamController<int> _progress;
  final _finished = Completer<void>();
  HttpClientRequest? _request;
  HttpClientResponse? _response;
  Completer<void>? _resumed;
  void Function()? _cancelPending;
  var _cancelled = false;

  Stream<int> get stream => _progress.stream;

  Future<void> _cancel() {
    if (_finished.isCompleted) return _finished.future;
    _cancelled = true;
    _abortRequest();
    _cancelPending?.call();
    return _finished.future;
  }

  void _abortRequest() {
    final request = _request;
    _request = null;
    request?.abort();
  }

  // Only the current network/pause await has a cancellation callback. Keeping
  // callbacks on one long-lived cancellation future would retain every chunk.
  Future<T> _interruptible<T>(Future<T> future) async {
    final result = Completer<T>();
    void cancel() {
      if (!result.isCompleted) {
        result.completeError(const _DownloadCancelled());
      }
    }

    _cancelPending = cancel;
    unawaited(
      future.then<void>(
        (value) {
          if (!result.isCompleted) result.complete(value);
        },
        onError: (Object error, StackTrace stack) {
          if (!result.isCompleted) result.completeError(error, stack);
        },
      ),
    );
    if (_cancelled) cancel();
    try {
      return await result.future;
    } finally {
      _cancelPending = null;
    }
  }

  Future<void> _waitUntilResumed() async {
    if (_cancelled) throw const _DownloadCancelled();
    final resumed = _resumed;
    if (resumed != null) await _interruptible(resumed.future);
  }

  Future<void> _run() async {
    RandomAccessFile? file;
    StreamIterator<List<int>>? body;
    var ownsDestination = false;
    var completed = false;
    DownloadException? failure;
    try {
      if (!_url.isAbsolute ||
          !_url.hasAuthority ||
          (_url.scheme != 'https' && _url.scheme != 'http')) {
        throw const DownloadNetworkError();
      }
      await _destination.parent.create(recursive: true);
      await _waitUntilResumed();

      final activeResponse = await _openAvailableResponse();
      final status = activeResponse.statusCode;
      if (status == HttpStatus.unauthorized || status == HttpStatus.notFound) {
        throw const DownloadGrantExpired();
      }
      if (status < 200 || status >= 300) {
        throw DownloadHttpError(status);
      }

      file = await _destination.open(mode: FileMode.write);
      ownsDestination = true;
      var received = 0;
      while (true) {
        await _waitUntilResumed();
        final iterator = body ??= StreamIterator(activeResponse);
        if (!await _interruptible(iterator.moveNext())) break;
        final chunk = iterator.current;
        final maximumBytes = _maximumBytes;
        if (maximumBytes != null && chunk.length > maximumBytes - received) {
          throw const DownloadSizeExceeded();
        }
        // Await each write before requesting another chunk: an IOSink.add loop
        // can otherwise queue the entire response behind a slow disk.
        await file.writeFrom(chunk);
        await _waitUntilResumed();
        received += chunk.length;
        _progress.add(received);
      }
      await file.close();
      file = null;
      completed = !_cancelled;
    } on _DownloadCancelled {
      // Subscription cancellation is not a download error.
    } on DownloadException catch (error) {
      failure = error;
    } on Object {
      // Includes transport and filesystem errors; never expose a grant URL.
      failure = const DownloadNetworkError();
    } finally {
      if (!completed) _abortRequest();
      try {
        if (body != null) {
          await body.cancel();
        } else if (_response != null) {
          // StreamIterator does not listen until moveNext. Status/open failures
          // still need a subscription to cancel the unconsumed response.
          final unconsumed = _response!;
          await unconsumed.listen(null, onError: (Object _) {}).cancel();
        }
      } on Object {
        failure ??= const DownloadNetworkError();
      }
      try {
        await file?.close();
      } on Object {
        failure ??= const DownloadNetworkError();
      }
      if (ownsDestination && !completed) await _safeDelete();
      _finished.complete();
    }
    if (!_cancelled) {
      if (failure != null) _progress.addError(failure);
      unawaited(_progress.close());
    }
  }

  /// Retry 503 responses before opening the destination.
  /// Earlier manifest items stay on disk while this item's grant is retried.
  Future<HttpClientResponse> _openAvailableResponse() async {
    var waited = Duration.zero;
    for (var retries = 0; ; retries++) {
      final response = await _openResponse();
      if (response.statusCode != HttpStatus.serviceUnavailable) return response;
      final delay = _retryWait(response);
      // Never shorten Retry-After to fit our budget: fail if it cannot be honored.
      if (retries == _maximumRetries || delay > _maximumRetryWait - waited) {
        throw const DownloadHttpError(HttpStatus.serviceUnavailable);
      }
      _abortRequest();
      await _discardResponse(response);
      _response = null;
      await _waitUntilResumed();
      final ready = Completer<void>();
      final timer = _createRetryTimer(delay, ready.complete);
      try {
        await _interruptible(ready.future);
      } finally {
        timer.cancel();
      }
      waited += delay;
      // Re-open the original grant, including its redirect chain. Expiry (401/404)
      // is returned to the install service's existing manifest refresh path.
    }
  }

  Duration _retryWait(HttpClientResponse response) {
    try {
      final value = response.headers
          .value(HttpHeaders.retryAfterHeader)
          ?.trim();
      if (value == null) return _defaultRetryWait;
      if (RegExp(r'^[0-9]+$').hasMatch(value)) {
        final seconds = int.tryParse(value);
        if (seconds == null || seconds > _maximumRetryWait.inSeconds) {
          return _maximumRetryWait + const Duration(seconds: 1);
        }
        return Duration(seconds: seconds);
      }
      final delay = HttpDate.parse(value).difference(_now().toUtc());
      return delay.isNegative ? Duration.zero : delay;
    } on HttpException {
      // Includes duplicate Retry-After fields, which HttpHeaders.value rejects.
      return _defaultRetryWait;
    }
  }

  Future<HttpClientResponse> _openResponse() async {
    var url = _url;
    final visited = <Uri>{};
    for (var redirects = 0; ; redirects++) {
      if (!visited.add(url) ||
          !url.isAbsolute ||
          !url.hasAuthority ||
          (url.scheme != 'https' && url.scheme != 'http')) {
        throw const DownloadNetworkError();
      }
      await _waitUntilResumed();
      final request = await _interruptible(
        _httpClient.getUrl(url).then((request) {
          if (_cancelled) {
            request.abort();
          } else {
            _request = request;
          }
          return request;
        }),
      );
      // Own each hop: Dart's automatic redirect creates a child request which
      // aborting the original request cannot reliably close across origins.
      request.followRedirects = false;
      final response = await _interruptible(
        request.close().then((value) {
          if (_cancelled) {
            // abort() has no effect once headers have arrived. A response can
            // also complete after cancellation has already returned.
            unawaited(_discardResponse(value));
          } else {
            _response = value;
          }
          return value;
        }),
      );
      final redirect = const <int>{
        HttpStatus.movedPermanently,
        HttpStatus.found,
        HttpStatus.seeOther,
        HttpStatus.temporaryRedirect,
        HttpStatus.permanentRedirect,
      }.contains(response.statusCode);
      if (!redirect) return response;
      // Match HttpClientRequest's default maximum of five redirects.
      if (redirects >= 5) throw const DownloadNetworkError();
      final location = response.headers.value(HttpHeaders.locationHeader);
      if (location == null) throw const DownloadNetworkError();
      final next = url.resolve(location).removeFragment();
      await _discardResponse(response);
      _response = null;
      _request = null;
      url = next;
      // Each new GET is credential-free, including cross-origin redirects.
    }
  }

  Future<void> _discardResponse(HttpClientResponse response) async {
    try {
      await response.listen(null, onError: (Object _) {}).cancel();
    } on Object {
      // A late response belongs to an already-cancelled subscription.
    }
  }

  Future<void> _safeDelete() async {
    try {
      if (await _destination.exists()) await _destination.delete();
    } on Object {
      // Best-effort cleanup after the file has been closed.
    }
  }
}

final class _DownloadCancelled implements Exception {
  const _DownloadCancelled();
}
