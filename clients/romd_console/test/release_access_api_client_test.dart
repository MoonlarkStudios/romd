import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/release_access_api_client.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

void main() {
  test(
    'posts bodyless authenticated request and accepts exact allow',
    () async {
      final harness = await _AccessHarness.start(
        responseBody: _allowedResponse(),
      );
      addTearDown(harness.close);

      final result = await harness.getAccess();

      final request = harness.requests.single;
      expect(request.method, 'POST');
      expect(request.path, '/api/releases/${_releaseId.value}/access');
      expect(request.query, isEmpty);
      expect(request.authorization, 'Bearer access-token');
      expect(request.contentType, isNull);
      expect(request.body, isEmpty);
      final decision = _expectSuccess(result);
      expect(decision, isA<ReleaseAccessAllowed>());
      final allowed = decision as ReleaseAccessAllowed;
      expect(allowed.serverInstanceId, _serverInstanceId);
      expect(allowed.releaseId, _releaseId);
      expect(allowed.titleId, _titleId);
    },
  );

  test('accepts exact metadata-free revoke', () async {
    final result = await _resultFor(responseBody: _revokedResponse());

    final decision = _expectSuccess(result);
    expect(decision, isA<ReleaseAccessRevoked>());
    expect(decision.serverInstanceId, _serverInstanceId);
    expect(decision.releaseId, _releaseId);
  });

  test(
    'rejects malformed allowed and revoked shapes without a decision',
    () async {
      final cases = <String, Object?>{
        'non-object': <Object?>[],
        'missing allowed': <String, Object?>{
          'serverInstanceId': _serverInstanceId.value,
          'releaseId': _releaseId.value,
          'titleId': _titleId.value,
        },
        'allowed wrong type': <String, Object?>{
          'serverInstanceId': _serverInstanceId.value,
          'releaseId': _releaseId.value,
          'allowed': 1,
          'titleId': _titleId.value,
        },
        'allowed missing title': <String, Object?>{
          'serverInstanceId': _serverInstanceId.value,
          'releaseId': _releaseId.value,
          'allowed': true,
        },
        'allowed null title': <String, Object?>{
          'serverInstanceId': _serverInstanceId.value,
          'releaseId': _releaseId.value,
          'allowed': true,
          'titleId': null,
        },
        'allowed extra key': <String, Object?>{
          ..._allowedResponse(),
          'extra': true,
        },
        'revoked title absent violation': <String, Object?>{
          ..._revokedResponse(),
          'titleId': null,
        },
        'revoked extra key': <String, Object?>{
          ..._revokedResponse(),
          'extra': true,
        },
        'null instance': <String, Object?>{
          ..._allowedResponse(),
          'serverInstanceId': null,
        },
        'uppercase instance': <String, Object?>{
          ..._allowedResponse(),
          'serverInstanceId': _serverInstanceId.value.toUpperCase(),
        },
        'malformed release': <String, Object?>{
          ..._allowedResponse(),
          'releaseId': 'not-a-release',
        },
        'noncanonical release': <String, Object?>{
          ..._allowedResponse(),
          'releaseId': '${_releaseId.value}a',
        },
        'malformed title': <String, Object?>{
          ..._allowedResponse(),
          'titleId': 'not-a-title',
        },
      };

      for (final entry in cases.entries) {
        final result = await _resultFor(responseBody: entry.value);
        _expectFailure(
          result,
          ReleaseAccessFailureKind.malformedResponse,
          reason: entry.key,
        );
      }

      final truncated = await _resultFor(rawResponseBody: '{"allowed":');
      _expectFailure(
        truncated,
        ReleaseAccessFailureKind.malformedResponse,
        reason: 'truncated JSON',
      );
    },
  );

  test(
    'distinguishes valid foreign identities from malformed payloads',
    () async {
      final cases = <String, Map<String, Object?>>{
        'foreign server': <String, Object?>{
          ..._allowedResponse(),
          'serverInstanceId': _foreignServerInstanceId.value,
        },
        'foreign release': <String, Object?>{
          ..._allowedResponse(),
          'releaseId': _foreignReleaseId.value,
        },
        'foreign title': <String, Object?>{
          ..._allowedResponse(),
          'titleId': _foreignTitleId.value,
        },
        'foreign server revoke': <String, Object?>{
          ..._revokedResponse(),
          'serverInstanceId': _foreignServerInstanceId.value,
        },
        'foreign release revoke': <String, Object?>{
          ..._revokedResponse(),
          'releaseId': _foreignReleaseId.value,
        },
      };

      for (final entry in cases.entries) {
        final result = await _resultFor(responseBody: entry.value);
        _expectFailure(
          result,
          ReleaseAccessFailureKind.identityMismatch,
          reason: entry.key,
        );
      }
    },
  );

  test('classifies every specified HTTP failure without a decision', () async {
    const cases = <int, ReleaseAccessFailureKind>{
      HttpStatus.badRequest: ReleaseAccessFailureKind.badRequest,
      HttpStatus.unauthorized: ReleaseAccessFailureKind.unauthorized,
      HttpStatus.conflict: ReleaseAccessFailureKind.conflict,
      HttpStatus.forbidden: ReleaseAccessFailureKind.clientError,
      HttpStatus.internalServerError: ReleaseAccessFailureKind.serverError,
      HttpStatus.badGateway: ReleaseAccessFailureKind.serverError,
      HttpStatus.created: ReleaseAccessFailureKind.unexpectedStatus,
    };

    for (final entry in cases.entries) {
      final harness = await _AccessHarness.start(
        statusCode: entry.key,
        responseBody: _allowedResponse(),
      );
      try {
        final result = await harness.getAccess();
        _expectFailure(
          result,
          entry.value,
          statusCode: entry.key,
          reason: 'HTTP ${entry.key}',
        );
        expect(harness.requests, hasLength(1), reason: 'HTTP ${entry.key}');
      } finally {
        await harness.close();
      }
    }
  });

  test('preserves only an invalid_token bearer challenge for replay', () async {
    final harness = await _AccessHarness.start(
      statusCode: HttpStatus.unauthorized,
      responseBody: <String, Object?>{},
      challenge: 'Bearer realm="ROMD", error="invalid_token"',
    );
    addTearDown(harness.close);

    final result = await harness.getAccess();

    expect(result, isA<ReleaseAccessFailure>());
    expect((result as ReleaseAccessFailure).isInvalidToken, isTrue);
    expect(result.bearerError, 'invalid_token');
  });

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
        ..statusCode = HttpStatus.seeOther
        ..headers.set(HttpHeaders.locationHeader, targetUri.toString());
      await request.response.close();
    });
    addTearDown(() => source.close(force: true));
    final client = HttpReleaseAccessApiClient(
      consumerApiOrigin: _originFor(source),
    );
    addTearDown(client.close);

    final result = await client.getReleaseAccess(
      accessToken: 'access-token',
      expectedServerInstanceId: _serverInstanceId,
      releaseId: _releaseId,
      expectedTitleId: _titleId,
    );

    _expectFailure(
      result,
      ReleaseAccessFailureKind.redirect,
      statusCode: HttpStatus.seeOther,
    );
    expect(targetRequests, 0);
  });

  test('classifies transport failure without producing a decision', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final origin = _originFor(server);
    await server.close(force: true);
    final client = HttpReleaseAccessApiClient(consumerApiOrigin: origin);
    addTearDown(client.close);

    final result = await client.getReleaseAccess(
      accessToken: 'access-token',
      expectedServerInstanceId: _serverInstanceId,
      releaseId: _releaseId,
      expectedTitleId: _titleId,
    );

    _expectFailure(result, ReleaseAccessFailureKind.transport);
  });

  test(
    'timeout aborts only its stalled operation while an overlap succeeds',
    () async {
      final server = await _RawStalledHttpServer.start(
        stalledBodyPrefix: '{"serverInstanceId":"',
        completeResponses: <String, String>{
          '/api/releases/${_foreignReleaseId.value}/access': jsonEncode(
            _allowedResponseFor(
              releaseId: _foreignReleaseId,
              titleId: _foreignTitleId,
            ),
          ),
        },
      );
      final client = HttpReleaseAccessApiClient(
        consumerApiOrigin: server.origin,
        timeout: const Duration(milliseconds: 150),
      );
      addTearDown(() async {
        client.close();
        await server.close();
      });

      final stalledResultFuture = client.getReleaseAccess(
        accessToken: 'access-token',
        expectedServerInstanceId: _serverInstanceId,
        releaseId: _releaseId,
        expectedTitleId: _titleId,
      );
      await server.stalledRequestReached.future;
      final overlappingResult = await client.getReleaseAccess(
        accessToken: 'access-token',
        expectedServerInstanceId: _serverInstanceId,
        releaseId: _foreignReleaseId,
        expectedTitleId: _foreignTitleId,
      );
      final stalledResult = await stalledResultFuture;

      final overlappingDecision = _expectSuccess(overlappingResult);
      expect(overlappingDecision, isA<ReleaseAccessAllowed>());
      expect(overlappingDecision.releaseId, _foreignReleaseId);
      _expectFailure(stalledResult, ReleaseAccessFailureKind.timeout);
      await server.stalledClientDisconnected.future.timeout(
        const Duration(seconds: 2),
      );
    },
  );

  test('close aborts an active release-access operation', () async {
    final server = await _RawStalledHttpServer.start(
      stalledBodyPrefix: '{"serverInstanceId":"',
    );
    final client = HttpReleaseAccessApiClient(
      consumerApiOrigin: server.origin,
      timeout: const Duration(seconds: 5),
    );
    addTearDown(() async {
      client.close();
      await server.close();
    });

    final resultFuture = client.getReleaseAccess(
      accessToken: 'access-token',
      expectedServerInstanceId: _serverInstanceId,
      releaseId: _releaseId,
      expectedTitleId: _titleId,
    );
    await server.stalledRequestReached.future;
    client.close();

    final result = await resultFuture;
    _expectFailure(result, ReleaseAccessFailureKind.transport);
    await server.stalledClientDisconnected.future.timeout(
      const Duration(seconds: 2),
    );
  });

  test(
    'close aborts release access while the request is still opening',
    () async {
      var requestCount = 0;
      final server = await _bindServer((request) async {
        requestCount++;
        await request.drain<void>();
        request.response.statusCode = HttpStatus.ok;
        await request.response.close();
      });
      final client = HttpReleaseAccessApiClient(
        consumerApiOrigin: _originFor(server),
        timeout: const Duration(seconds: 5),
      );
      addTearDown(() async {
        client.close();
        await server.close(force: true);
      });

      final resultFuture = client.getReleaseAccess(
        accessToken: 'access-token',
        expectedServerInstanceId: _serverInstanceId,
        releaseId: _releaseId,
        expectedTitleId: _titleId,
      );
      client.close();

      final result = await resultFuture;
      _expectFailure(result, ReleaseAccessFailureKind.transport);
      expect(requestCount, 0);
    },
  );

  test('release access after close is a typed transport failure', () async {
    final server = await _bindServer((request) async {
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    final client = HttpReleaseAccessApiClient(
      consumerApiOrigin: _originFor(server),
    );
    addTearDown(() async {
      client.close();
      await server.close(force: true);
    });
    client.close();

    final result = await client.getReleaseAccess(
      accessToken: 'access-token',
      expectedServerInstanceId: _serverInstanceId,
      releaseId: _releaseId,
      expectedTitleId: _titleId,
    );

    _expectFailure(result, ReleaseAccessFailureKind.transport);
  });
}

final _serverInstanceId = RomdServerInstanceId.tryParse(
  'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
)!;
final _foreignServerInstanceId = RomdServerInstanceId.tryParse(
  'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
)!;
final _releaseId = RomdPublicId.tryParse(RomdPublicId.encode(1))!;
final _foreignReleaseId = RomdPublicId.tryParse(RomdPublicId.encode(2))!;
final _titleId = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
final _foreignTitleId = RomdPublicId.tryParse(RomdPublicId.encode(102))!;

Map<String, Object?> _allowedResponse() => <String, Object?>{
  'serverInstanceId': _serverInstanceId.value,
  'releaseId': _releaseId.value,
  'allowed': true,
  'titleId': _titleId.value,
};

Map<String, Object?> _allowedResponseFor({
  required RomdPublicId releaseId,
  required RomdPublicId titleId,
}) => <String, Object?>{
  'serverInstanceId': _serverInstanceId.value,
  'releaseId': releaseId.value,
  'allowed': true,
  'titleId': titleId.value,
};

Map<String, Object?> _revokedResponse() => <String, Object?>{
  'serverInstanceId': _serverInstanceId.value,
  'releaseId': _releaseId.value,
  'allowed': false,
};

final class _RecordedRequest {
  const _RecordedRequest({
    required this.method,
    required this.path,
    required this.query,
    required this.authorization,
    required this.contentType,
    required this.body,
  });

  final String method;
  final String path;
  final String query;
  final String? authorization;
  final String? contentType;
  final String body;
}

final class _AccessHarness {
  _AccessHarness._(this.server, this.client, this.requests);

  final HttpServer server;
  final HttpReleaseAccessApiClient client;
  final List<_RecordedRequest> requests;

  static Future<_AccessHarness> start({
    int statusCode = HttpStatus.ok,
    Object? responseBody,
    String? rawResponseBody,
    String? challenge,
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
          contentType: request.headers.value(HttpHeaders.contentTypeHeader),
          body: body,
        ),
      );
      request.response
        ..statusCode = statusCode
        ..headers.contentType = ContentType.json;
      if (challenge != null) {
        request.response.headers.set(
          HttpHeaders.wwwAuthenticateHeader,
          challenge,
        );
      }
      request.response.write(rawResponseBody ?? jsonEncode(responseBody));
      await request.response.close();
    });
    return _AccessHarness._(
      server,
      HttpReleaseAccessApiClient(consumerApiOrigin: _originFor(server)),
      requests,
    );
  }

  Future<ReleaseAccessResult> getAccess() => client.getReleaseAccess(
    accessToken: 'access-token',
    expectedServerInstanceId: _serverInstanceId,
    releaseId: _releaseId,
    expectedTitleId: _titleId,
  );

  Future<void> close() async {
    client.close();
    await server.close(force: true);
  }
}

Future<ReleaseAccessResult> _resultFor({
  int statusCode = HttpStatus.ok,
  Object? responseBody,
  String? rawResponseBody,
}) async {
  final harness = await _AccessHarness.start(
    statusCode: statusCode,
    responseBody: responseBody,
    rawResponseBody: rawResponseBody,
  );
  try {
    return await harness.getAccess();
  } finally {
    await harness.close();
  }
}

ReleaseAccessDecision _expectSuccess(ReleaseAccessResult result) {
  expect(result, isA<ReleaseAccessSuccess>());
  return (result as ReleaseAccessSuccess).decision;
}

void _expectFailure(
  ReleaseAccessResult result,
  ReleaseAccessFailureKind kind, {
  int? statusCode,
  String? reason,
}) {
  expect(result, isA<ReleaseAccessFailure>(), reason: reason);
  final failure = result as ReleaseAccessFailure;
  expect(failure.kind, kind, reason: reason);
  expect(failure.statusCode, statusCode, reason: reason);
  expect(result, isNot(isA<ReleaseAccessSuccess>()), reason: reason);
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
  _RawStalledHttpServer._(
    this._server,
    this._stalledBodyPrefix,
    this._completeResponses,
  );

  final ServerSocket _server;
  final String _stalledBodyPrefix;
  final Map<String, String> _completeResponses;
  final Set<Socket> _sockets = <Socket>{};
  final Set<Socket> _stalledSockets = <Socket>{};
  final Completer<void> stalledRequestReached = Completer<void>();
  final Completer<void> stalledClientDisconnected = Completer<void>();
  late final StreamSubscription<Socket> _subscription;

  static Future<_RawStalledHttpServer> start({
    required String stalledBodyPrefix,
    Map<String, String> completeResponses = const <String, String>{},
  }) async {
    final socket = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final server = _RawStalledHttpServer._(
      socket,
      stalledBodyPrefix,
      completeResponses,
    );
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
        final requestHead = utf8.decode(requestBytes, allowMalformed: true);
        if (!requestHead.contains('\r\n\r\n')) {
          return;
        }

        requestComplete = true;
        final requestLine = requestHead.substring(
          0,
          requestHead.indexOf('\r\n'),
        );
        final path = requestLine.split(' ')[1];
        final completeBody = _completeResponses[path];
        if (completeBody != null) {
          unawaited(_writeCompleteResponse(socket, completeBody));
          return;
        }

        _stalledSockets.add(socket);
        unawaited(_writeStalledResponse(socket));
      },
      onDone: () => _socketEnded(socket),
      onError: (Object _) => _socketEnded(socket),
      cancelOnError: true,
    );
  }

  Future<void> _writeCompleteResponse(Socket socket, String body) async {
    final bodyBytes = utf8.encode(body);
    socket.add(
      ascii.encode(
        'HTTP/1.1 200 OK\r\n'
        'Content-Type: application/json\r\n'
        'Content-Length: ${bodyBytes.length}\r\n'
        'Connection: close\r\n'
        '\r\n',
      ),
    );
    socket.add(bodyBytes);
    await socket.flush();
    await socket.close();
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
