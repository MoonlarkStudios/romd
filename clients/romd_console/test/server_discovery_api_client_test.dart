import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/server_discovery_api_client.dart';

void main() {
  test(
    'gets the anonymous exact identity contract without credentials',
    () async {
      final harness = await _DiscoveryHarness.start(
        responseBody: const <String, Object?>{'instanceId': _instanceId},
      );
      addTearDown(harness.close);

      final result = await harness.client.discover();

      final request = harness.requests.single;
      expect(request.method, 'GET');
      expect(request.path, '/api/server/identity');
      expect(request.query, isEmpty);
      expect(request.authorization, isNull);
      expect(request.cookie, isNull);
      expect(request.body, isEmpty);
      expect(result, isA<ServerDiscoverySuccess>());
      expect(
        (result as ServerDiscoverySuccess).serverInstanceId.value,
        _instanceId,
      );
    },
  );

  test('rejects every malformed identity response', () async {
    final cases = <String, Object?>{
      'non-object': <Object?>[],
      'missing key': const <String, Object?>{},
      'extra key': const <String, Object?>{
        'instanceId': _instanceId,
        'extra': true,
      },
      'null identity': const <String, Object?>{'instanceId': null},
      'wrong type': const <String, Object?>{'instanceId': 1},
      'uppercase UUID': const <String, Object?>{
        'instanceId': 'AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA',
      },
      'UUID-N': const <String, Object?>{
        'instanceId': 'aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa',
      },
      'malformed UUID': const <String, Object?>{'instanceId': 'not-a-uuid'},
    };

    for (final entry in cases.entries) {
      final result = await _resultFor(responseBody: entry.value);
      _expectFailure(
        result,
        ServerDiscoveryFailureKind.malformedResponse,
        reason: entry.key,
      );
    }

    final truncated = await _resultFor(rawResponseBody: '{"instanceId":');
    _expectFailure(
      truncated,
      ServerDiscoveryFailureKind.malformedResponse,
      reason: 'truncated JSON',
    );
  });

  test('classifies non-200 responses without parsing their bodies', () async {
    for (final statusCode in <int>[
      HttpStatus.noContent,
      HttpStatus.badRequest,
      HttpStatus.unauthorized,
      HttpStatus.conflict,
      HttpStatus.internalServerError,
    ]) {
      final result = await _resultFor(
        statusCode: statusCode,
        responseBody: const <String, Object?>{'instanceId': _instanceId},
      );
      _expectFailure(
        result,
        ServerDiscoveryFailureKind.httpError,
        statusCode: statusCode,
        reason: 'HTTP $statusCode',
      );
    }
  });

  test(
    'does not retry a WWW-Authenticate challenge or send authorization',
    () async {
      var requestCount = 0;
      final authorizationValues = <String?>[];
      final server = await _bindServer((request) async {
        requestCount++;
        authorizationValues.add(
          request.headers.value(HttpHeaders.authorizationHeader),
        );
        await request.drain<void>();
        request.response
          ..statusCode = HttpStatus.unauthorized
          ..headers.set(
            HttpHeaders.wwwAuthenticateHeader,
            'Basic realm="ROMD"',
          );
        await request.response.close();
      });
      final client = HttpServerDiscoveryApiClient(
        consumerApiOrigin: _originFor(server),
      );
      addTearDown(() async {
        client.close();
        await server.close(force: true);
      });

      final result = await client.discover();

      _expectFailure(
        result,
        ServerDiscoveryFailureKind.httpError,
        statusCode: HttpStatus.unauthorized,
      );
      expect(requestCount, 1);
      expect(authorizationValues, <String?>[null]);
    },
  );

  test('does not follow redirects or contact the redirect target', () async {
    var targetRequests = 0;
    final target = await _bindServer((request) async {
      targetRequests++;
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    addTearDown(() => target.close(force: true));
    final targetUri = _originFor(target).resolve('/redirect-target');
    final source = await _bindServer((request) async {
      await request.drain<void>();
      request.response
        ..statusCode = HttpStatus.temporaryRedirect
        ..headers.set(HttpHeaders.locationHeader, targetUri.toString());
      await request.response.close();
    });
    addTearDown(() => source.close(force: true));
    final client = HttpServerDiscoveryApiClient(
      consumerApiOrigin: _originFor(source),
    );
    addTearDown(client.close);

    final result = await client.discover();

    _expectFailure(
      result,
      ServerDiscoveryFailureKind.redirect,
      statusCode: HttpStatus.temporaryRedirect,
    );
    expect(targetRequests, 0);
  });

  test('classifies transport failure without producing identity', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final origin = _originFor(server);
    await server.close(force: true);
    final client = HttpServerDiscoveryApiClient(consumerApiOrigin: origin);
    addTearDown(client.close);

    final result = await client.discover();

    _expectFailure(result, ServerDiscoveryFailureKind.transport);
    expect(result, isNot(isA<ServerDiscoverySuccess>()));
  });

  test('classifies timeout and aborts the stalled network operation', () async {
    final server = await _RawStalledHttpServer.start(
      stalledBodyPrefix: '{"instanceId":"',
    );
    final client = HttpServerDiscoveryApiClient(
      consumerApiOrigin: server.origin,
      timeout: const Duration(milliseconds: 100),
    );
    addTearDown(() async {
      client.close();
      await server.close();
    });

    final resultFuture = client.discover();
    await server.stalledRequestReached.future;
    final result = await resultFuture;

    _expectFailure(result, ServerDiscoveryFailureKind.timeout);
    await server.stalledClientDisconnected.future.timeout(
      const Duration(seconds: 2),
    );
  });

  test('close aborts an active discovery operation', () async {
    final server = await _RawStalledHttpServer.start(
      stalledBodyPrefix: '{"instanceId":"',
    );
    final client = HttpServerDiscoveryApiClient(
      consumerApiOrigin: server.origin,
      timeout: const Duration(seconds: 5),
    );
    addTearDown(() async {
      client.close();
      await server.close();
    });

    final resultFuture = client.discover();
    await server.stalledRequestReached.future;
    client.close();

    final result = await resultFuture;
    _expectFailure(result, ServerDiscoveryFailureKind.transport);
    await server.stalledClientDisconnected.future.timeout(
      const Duration(seconds: 2),
    );
  });

  test('close aborts discovery while the request is still opening', () async {
    var requestCount = 0;
    final server = await _bindServer((request) async {
      requestCount++;
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    final client = HttpServerDiscoveryApiClient(
      consumerApiOrigin: _originFor(server),
      timeout: const Duration(seconds: 5),
    );
    addTearDown(() async {
      client.close();
      await server.close(force: true);
    });

    final resultFuture = client.discover();
    client.close();

    final result = await resultFuture;
    _expectFailure(result, ServerDiscoveryFailureKind.transport);
    expect(requestCount, 0);
  });

  test('discover after close is a typed transport failure', () async {
    final server = await _bindServer((request) async {
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    final client = HttpServerDiscoveryApiClient(
      consumerApiOrigin: _originFor(server),
    );
    addTearDown(() async {
      client.close();
      await server.close(force: true);
    });
    client.close();

    final result = await client.discover();

    _expectFailure(result, ServerDiscoveryFailureKind.transport);
  });
}

const _instanceId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';

final class _RecordedRequest {
  const _RecordedRequest({
    required this.method,
    required this.path,
    required this.query,
    required this.authorization,
    required this.cookie,
    required this.body,
  });

  final String method;
  final String path;
  final String query;
  final String? authorization;
  final String? cookie;
  final String body;
}

final class _DiscoveryHarness {
  _DiscoveryHarness._(this.server, this.client, this.requests);

  final HttpServer server;
  final HttpServerDiscoveryApiClient client;
  final List<_RecordedRequest> requests;

  static Future<_DiscoveryHarness> start({
    int statusCode = HttpStatus.ok,
    Object? responseBody,
    String? rawResponseBody,
  }) async {
    final requests = <_RecordedRequest>[];
    final server = await _bindServer((request) async {
      final body = await utf8.decodeStream(request);
      requests.add(
        _RecordedRequest(
          method: request.method,
          path: request.uri.path,
          query: request.uri.query,
          authorization: request.headers.value(HttpHeaders.authorizationHeader),
          cookie: request.headers.value(HttpHeaders.cookieHeader),
          body: body,
        ),
      );
      request.response
        ..statusCode = statusCode
        ..headers.contentType = ContentType.json
        ..write(rawResponseBody ?? jsonEncode(responseBody));
      await request.response.close();
    });
    return _DiscoveryHarness._(
      server,
      HttpServerDiscoveryApiClient(consumerApiOrigin: _originFor(server)),
      requests,
    );
  }

  Future<void> close() async {
    client.close();
    await server.close(force: true);
  }
}

Future<ServerDiscoveryResult> _resultFor({
  int statusCode = HttpStatus.ok,
  Object? responseBody,
  String? rawResponseBody,
}) async {
  final harness = await _DiscoveryHarness.start(
    statusCode: statusCode,
    responseBody: responseBody,
    rawResponseBody: rawResponseBody,
  );
  try {
    return await harness.client.discover();
  } finally {
    await harness.close();
  }
}

void _expectFailure(
  ServerDiscoveryResult result,
  ServerDiscoveryFailureKind kind, {
  int? statusCode,
  String? reason,
}) {
  expect(result, isA<ServerDiscoveryFailure>(), reason: reason);
  final failure = result as ServerDiscoveryFailure;
  expect(failure.kind, kind, reason: reason);
  expect(failure.statusCode, statusCode, reason: reason);
  expect(result, isNot(isA<ServerDiscoverySuccess>()), reason: reason);
}

Future<HttpServer> _bindServer(
  Future<void> Function(HttpRequest request) handler,
) async {
  final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  server.listen(handler);
  return server;
}

Uri _originFor(HttpServer server) =>
    Uri.parse('http://${server.address.host}:${server.port}');

final class _RawStalledHttpServer {
  _RawStalledHttpServer._(this._server, this._stalledBodyPrefix);

  final ServerSocket _server;
  final String _stalledBodyPrefix;
  final Set<Socket> _sockets = <Socket>{};
  final Set<Socket> _stalledSockets = <Socket>{};
  final Completer<void> stalledRequestReached = Completer<void>();
  final Completer<void> stalledClientDisconnected = Completer<void>();
  late final StreamSubscription<Socket> _subscription;

  static Future<_RawStalledHttpServer> start({
    required String stalledBodyPrefix,
  }) async {
    final socket = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final server = _RawStalledHttpServer._(socket, stalledBodyPrefix);
    server._subscription = socket.listen(server._accept);
    return server;
  }

  Uri get origin => Uri.parse('http://${_server.address.host}:${_server.port}');

  void _accept(Socket socket) {
    _sockets.add(socket);
    final requestBytes = <int>[];
    var requestComplete = false;
    socket.listen(
      (chunk) {
        if (requestComplete) {
          return;
        }
        requestBytes.addAll(chunk);
        if (!utf8
            .decode(requestBytes, allowMalformed: true)
            .contains('\r\n\r\n')) {
          return;
        }

        requestComplete = true;
        _stalledSockets.add(socket);
        unawaited(_writeStalledResponse(socket));
      },
      onDone: () => _socketEnded(socket),
      onError: (Object _) => _socketEnded(socket),
      cancelOnError: true,
    );
  }

  Future<void> _writeStalledResponse(Socket socket) async {
    final bodyBytes = utf8.encode(_stalledBodyPrefix);
    socket.add(
      ascii.encode(
        'HTTP/1.1 200 OK\r\n'
        'Content-Type: application/json\r\n'
        'Content-Length: ${bodyBytes.length + 1024}\r\n'
        'Connection: close\r\n'
        '\r\n',
      ),
    );
    socket.add(bodyBytes);
    await socket.flush();
    if (!stalledRequestReached.isCompleted) {
      stalledRequestReached.complete();
    }
  }

  void _socketEnded(Socket socket) {
    _sockets.remove(socket);
    if (_stalledSockets.remove(socket) &&
        !stalledClientDisconnected.isCompleted) {
      stalledClientDisconnected.complete();
    }
  }

  Future<void> close() async {
    await _subscription.cancel();
    for (final socket in List<Socket>.of(_sockets)) {
      socket.destroy();
    }
    await _server.close();
  }
}
