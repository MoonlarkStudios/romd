import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../domain/romd_server_instance_id.dart';
import 'owned_http_operation.dart';

enum ServerDiscoveryFailureKind {
  redirect,
  httpError,
  timeout,
  transport,
  malformedResponse,
}

sealed class ServerDiscoveryResult {
  const ServerDiscoveryResult();
}

final class ServerDiscoverySuccess extends ServerDiscoveryResult {
  const ServerDiscoverySuccess(this.serverInstanceId);

  final RomdServerInstanceId serverInstanceId;
}

final class ServerDiscoveryFailure extends ServerDiscoveryResult {
  const ServerDiscoveryFailure(this.kind, {this.statusCode});

  final ServerDiscoveryFailureKind kind;
  final int? statusCode;
}

abstract interface class ServerDiscoveryApiClient {
  Future<ServerDiscoveryResult> discover();

  void close();
}

final class HttpServerDiscoveryApiClient implements ServerDiscoveryApiClient {
  HttpServerDiscoveryApiClient({
    required Uri consumerApiOrigin,
    Duration timeout = const Duration(seconds: 15),
  }) : _consumerApiOrigin = consumerApiOrigin,
       _timeout = timeout;

  final Uri _consumerApiOrigin;
  final Duration _timeout;
  final Set<OwnedHttpOperation> _activeOperations = <OwnedHttpOperation>{};
  bool _closed = false;

  @override
  Future<ServerDiscoveryResult> discover() async {
    if (_closed) {
      return const ServerDiscoveryFailure(ServerDiscoveryFailureKind.transport);
    }

    final operation = OwnedHttpOperation();
    _activeOperations.add(operation);
    try {
      return await _sendRequest(operation).timeout(
        _timeout,
        onTimeout: () {
          operation.abort();
          return const ServerDiscoveryFailure(
            ServerDiscoveryFailureKind.timeout,
          );
        },
      );
    } on TimeoutException {
      operation.abort();
      return const ServerDiscoveryFailure(ServerDiscoveryFailureKind.timeout);
    } on FormatException {
      return const ServerDiscoveryFailure(
        ServerDiscoveryFailureKind.malformedResponse,
      );
    } on OwnedHttpOperationAbortedException {
      return const ServerDiscoveryFailure(ServerDiscoveryFailureKind.transport);
    } on IOException {
      return const ServerDiscoveryFailure(ServerDiscoveryFailureKind.transport);
    } finally {
      _activeOperations.remove(operation);
      operation.dispose();
    }
  }

  Future<ServerDiscoveryResult> _sendRequest(
    OwnedHttpOperation operation,
  ) async {
    final request = await operation.getUrl(
      _consumerApiOrigin.resolve('/api/server/identity'),
    );
    request.followRedirects = false;
    request.headers
      ..removeAll(HttpHeaders.authorizationHeader)
      ..removeAll(HttpHeaders.cookieHeader)
      ..set(HttpHeaders.acceptHeader, 'application/json');

    final response = await request.close();
    if (response.statusCode != HttpStatus.ok) {
      await response.drain<void>();
      final kind =
          response.statusCode >= HttpStatus.multipleChoices &&
              response.statusCode < HttpStatus.badRequest
          ? ServerDiscoveryFailureKind.redirect
          : ServerDiscoveryFailureKind.httpError;
      return ServerDiscoveryFailure(kind, statusCode: response.statusCode);
    }

    final body = await utf8.decodeStream(response);
    final decoded = jsonDecode(body);
    if (decoded is! Map<String, Object?> ||
        decoded.length != 1 ||
        !decoded.containsKey('instanceId')) {
      return const ServerDiscoveryFailure(
        ServerDiscoveryFailureKind.malformedResponse,
      );
    }

    final instanceValue = decoded['instanceId'];
    final serverInstanceId = instanceValue is String
        ? RomdServerInstanceId.tryParse(instanceValue)
        : null;
    if (serverInstanceId == null) {
      return const ServerDiscoveryFailure(
        ServerDiscoveryFailureKind.malformedResponse,
      );
    }

    return ServerDiscoverySuccess(serverInstanceId);
  }

  @override
  void close() {
    if (_closed) {
      return;
    }

    _closed = true;
    for (final operation in List<OwnedHttpOperation>.of(_activeOperations)) {
      operation.abort();
    }
  }
}
