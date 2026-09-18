import 'dart:io';

/// One credential-empty HTTP transport owned by exactly one API operation.
///
/// Aborting this object cannot close or mutate another in-flight operation.
final class OwnedHttpOperation {
  OwnedHttpOperation() : _client = HttpClient();

  final HttpClient _client;
  HttpClientRequest? _request;
  bool _aborted = false;

  Future<HttpClientRequest> getUrl(Uri url) => _open(_client.getUrl(url));

  Future<HttpClientRequest> postUrl(Uri url) => _open(_client.postUrl(url));

  Future<HttpClientRequest> _open(Future<HttpClientRequest> pending) async {
    final request = await pending;
    if (_aborted) {
      request.abort(const OwnedHttpOperationAbortedException());
      throw const OwnedHttpOperationAbortedException();
    }

    _request = request;
    return request;
  }

  void abort() {
    if (_aborted) {
      return;
    }

    _aborted = true;
    _request?.abort(const OwnedHttpOperationAbortedException());
    _client.close(force: true);
  }

  void dispose() => _client.close(force: true);
}

final class OwnedHttpOperationAbortedException implements Exception {
  const OwnedHttpOperationAbortedException();
}
