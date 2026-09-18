import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../domain/release_access.dart';
import '../domain/romd_public_id.dart';
import '../domain/romd_server_instance_id.dart';
import 'owned_http_operation.dart';

abstract interface class ReleaseAccessApiClient {
  Future<ReleaseAccessResult> getReleaseAccess({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,
  });

  void close();
}

final class HttpReleaseAccessApiClient implements ReleaseAccessApiClient {
  HttpReleaseAccessApiClient({
    required Uri consumerApiOrigin,
    Duration timeout = const Duration(seconds: 15),
  }) : _consumerApiOrigin = consumerApiOrigin,
       _timeout = timeout;

  final Uri _consumerApiOrigin;
  final Duration _timeout;
  final Set<OwnedHttpOperation> _activeOperations = <OwnedHttpOperation>{};
  bool _closed = false;

  @override
  Future<ReleaseAccessResult> getReleaseAccess({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,
  }) async {
    if (_closed) {
      return const ReleaseAccessFailure(ReleaseAccessFailureKind.transport);
    }

    final operation = OwnedHttpOperation();
    _activeOperations.add(operation);
    try {
      return await _sendRequest(
        operation: operation,
        accessToken: accessToken,
        expectedServerInstanceId: expectedServerInstanceId,
        releaseId: releaseId,
        expectedTitleId: expectedTitleId,
      ).timeout(
        _timeout,
        onTimeout: () {
          operation.abort();
          return const ReleaseAccessFailure(ReleaseAccessFailureKind.timeout);
        },
      );
    } on TimeoutException {
      operation.abort();
      return const ReleaseAccessFailure(ReleaseAccessFailureKind.timeout);
    } on FormatException {
      return const ReleaseAccessFailure(
        ReleaseAccessFailureKind.malformedResponse,
      );
    } on OwnedHttpOperationAbortedException {
      return const ReleaseAccessFailure(ReleaseAccessFailureKind.transport);
    } on IOException {
      return const ReleaseAccessFailure(ReleaseAccessFailureKind.transport);
    } finally {
      _activeOperations.remove(operation);
      operation.dispose();
    }
  }

  Future<ReleaseAccessResult> _sendRequest({
    required OwnedHttpOperation operation,
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,
  }) async {
    final request = await operation.postUrl(
      _consumerApiOrigin.resolve('/api/releases/${releaseId.value}/access'),
    );
    request.followRedirects = false;
    request.headers
      ..set(HttpHeaders.acceptHeader, 'application/json')
      ..set(HttpHeaders.authorizationHeader, 'Bearer $accessToken');

    final response = await request.close();
    if (response.statusCode != HttpStatus.ok) {
      final bearerError = _readBearerError(response.headers);
      await response.drain<void>();
      return _failureForStatus(response.statusCode, bearerError: bearerError);
    }

    final body = await utf8.decodeStream(response);
    return _parseResponse(
      jsonDecode(body),
      expectedServerInstanceId: expectedServerInstanceId,
      expectedReleaseId: releaseId,
      expectedTitleId: expectedTitleId,
    );
  }

  ReleaseAccessFailure _failureForStatus(
    int statusCode, {
    String? bearerError,
  }) {
    if (statusCode >= HttpStatus.multipleChoices &&
        statusCode < HttpStatus.badRequest) {
      return ReleaseAccessFailure(
        ReleaseAccessFailureKind.redirect,
        statusCode: statusCode,
      );
    }

    return switch (statusCode) {
      HttpStatus.badRequest => const ReleaseAccessFailure(
        ReleaseAccessFailureKind.badRequest,
        statusCode: HttpStatus.badRequest,
      ),
      HttpStatus.unauthorized => ReleaseAccessFailure(
        ReleaseAccessFailureKind.unauthorized,
        statusCode: HttpStatus.unauthorized,
        bearerError: bearerError,
      ),
      HttpStatus.conflict => const ReleaseAccessFailure(
        ReleaseAccessFailureKind.conflict,
        statusCode: HttpStatus.conflict,
      ),
      >= HttpStatus.badRequest && < HttpStatus.internalServerError =>
        ReleaseAccessFailure(
          ReleaseAccessFailureKind.clientError,
          statusCode: statusCode,
        ),
      >= HttpStatus.internalServerError => ReleaseAccessFailure(
        ReleaseAccessFailureKind.serverError,
        statusCode: statusCode,
      ),
      _ => ReleaseAccessFailure(
        ReleaseAccessFailureKind.unexpectedStatus,
        statusCode: statusCode,
      ),
    };
  }

  ReleaseAccessResult _parseResponse(
    Object? decoded, {
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId expectedReleaseId,
    required RomdPublicId expectedTitleId,
  }) {
    if (decoded is! Map<String, Object?>) {
      return const ReleaseAccessFailure(
        ReleaseAccessFailureKind.malformedResponse,
      );
    }

    final allowed = decoded['allowed'];
    final expectedKeys = switch (allowed) {
      true => const <String>{
        'serverInstanceId',
        'releaseId',
        'allowed',
        'titleId',
      },
      false => const <String>{'serverInstanceId', 'releaseId', 'allowed'},
      _ => null,
    };
    if (expectedKeys == null || !_hasExactKeys(decoded, expectedKeys)) {
      return const ReleaseAccessFailure(
        ReleaseAccessFailureKind.malformedResponse,
      );
    }

    final serverInstanceValue = decoded['serverInstanceId'];
    final releaseValue = decoded['releaseId'];
    final serverInstanceId = serverInstanceValue is String
        ? RomdServerInstanceId.tryParse(serverInstanceValue)
        : null;
    final releaseId = releaseValue is String
        ? RomdPublicId.tryParse(releaseValue)
        : null;
    if (serverInstanceId == null || releaseId == null) {
      return const ReleaseAccessFailure(
        ReleaseAccessFailureKind.malformedResponse,
      );
    }

    RomdPublicId? titleId;
    if (allowed == true) {
      final titleValue = decoded['titleId'];
      titleId = titleValue is String ? RomdPublicId.tryParse(titleValue) : null;
      if (titleId == null) {
        return const ReleaseAccessFailure(
          ReleaseAccessFailureKind.malformedResponse,
        );
      }
    }

    if (serverInstanceId != expectedServerInstanceId ||
        releaseId != expectedReleaseId ||
        (allowed == true && titleId != expectedTitleId)) {
      return const ReleaseAccessFailure(
        ReleaseAccessFailureKind.identityMismatch,
      );
    }

    final decision = allowed == true
        ? ReleaseAccessAllowed(
            serverInstanceId: serverInstanceId,
            releaseId: releaseId,
            titleId: titleId!,
          )
        : ReleaseAccessRevoked(
            serverInstanceId: serverInstanceId,
            releaseId: releaseId,
          );
    return ReleaseAccessSuccess(decision);
  }

  bool _hasExactKeys(Map<String, Object?> value, Set<String> expectedKeys) =>
      value.length == expectedKeys.length &&
      value.keys.every(expectedKeys.contains);

  String? _readBearerError(HttpHeaders headers) {
    for (final challenge
        in headers[HttpHeaders.wwwAuthenticateHeader] ?? const <String>[]) {
      if (!challenge.trimLeft().toLowerCase().startsWith('bearer ')) continue;
      final match = RegExp(
        r'(?:^|,)\s*error\s*=\s*"([^"]+)"',
        caseSensitive: false,
      ).firstMatch(challenge.substring(challenge.indexOf(' ') + 1));
      if (match != null) return match.group(1)?.toLowerCase();
    }
    return null;
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
