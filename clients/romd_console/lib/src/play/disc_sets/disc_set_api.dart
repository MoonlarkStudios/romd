import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:romd_console/src/data/owned_http_operation.dart';

import 'domain/disc_set_failure.dart';
import 'domain/disc_set_models.dart';

abstract interface class DiscSetApi {
  Future<List<DiscSetStatus>> list(String token, String titleId);
  Future<DiscSetStatus> status(String token, String discSetId);
  Future<DiscSetPlan> select(
    String token,
    String discSetId,
    Map<String, Object?> selection,
  );
  Future<bool> access(String token, DiscSetPlan plan);
  void close();
}

/// Consumer-host-only adapter. No redirects, implicit version fallback or
/// credentials on delivery URLs. A 409 is unavailable, never a revoke.
final class HttpDiscSetApi implements DiscSetApi {
  HttpDiscSetApi(
    this.origin,
    this.serverInstanceId, {
    this.timeout = const Duration(seconds: 15),
  });
  final Uri origin;
  final String serverInstanceId;
  final Duration timeout;
  final Set<OwnedHttpOperation> _operations = {};
  bool _closed = false;
  Future<Object?> _send(
    String token,
    String path, [
    Map<String, Object?>? body,
  ]) async {
    if (_closed)
      throw const DiscSetException(DiscSetFailureKind.unavailable);
    final operation = OwnedHttpOperation();
    _operations.add(operation);
    try {
      return await (() async {
        final request = body == null
            ? await operation.getUrl(origin.resolve(path))
            : await operation.postUrl(origin.resolve(path));
        request.followRedirects = false;
        request.headers.set(HttpHeaders.authorizationHeader, 'Bearer $token');
        request.headers.set(HttpHeaders.acceptHeader, 'application/json');
        if (body != null) {
          request.headers.contentType = ContentType.json;
          request.write(jsonEncode(body));
        }
        final response = await request.close();
        if (response.statusCode != 200) {
          await response.drain<void>();
          throw DiscSetException(switch (response.statusCode) {
            401 => DiscSetFailureKind.unauthorized,
            403 || 404 => DiscSetFailureKind.denied,
            409 => DiscSetFailureKind.stale,
            _ => DiscSetFailureKind.unavailable,
          });
        }
        final bytes = <int>[];
        await for (final chunk in response) {
          bytes.addAll(chunk);
          if (bytes.length > 8 * 1024 * 1024)
            throw const FormatException('Oversize response.');
        }
        return jsonDecode(utf8.decode(bytes));
      })().timeout(
        timeout,
        onTimeout: () {
          operation.abort();
          throw const DiscSetException(DiscSetFailureKind.unavailable);
        },
      );
    } on DiscSetException {
      rethrow;
    } on FormatException {
      throw const DiscSetException(DiscSetFailureKind.malformed);
    } on Object {
      throw const DiscSetException(DiscSetFailureKind.unavailable);
    } finally {
      _operations.remove(operation);
      operation.dispose();
    }
  }

  DiscSetStatus _status(Object? raw, {String? titleId, String? discSetId}) {
    try {
      final status = DiscSetStatus(DiscSetWire.map(raw));
      if (status.serverInstanceId != serverInstanceId ||
          (titleId != null && status.titleId != titleId) ||
          (discSetId != null && status.discSetId != discSetId))
        throw const FormatException('Identity mismatch.');
      return status;
    } on FormatException {
      throw const DiscSetException(DiscSetFailureKind.malformed);
    }
  }

  @override
  Future<List<DiscSetStatus>> list(String token, String titleId) async {
    DiscSetWire.id({'id': titleId}, 'id');
    final result = <DiscSetStatus>[];
    final cursors = <String>{};
    String? cursor;
    try {
      do {
        final query = Uri(
          queryParameters: {
            'titleId': titleId,
            'limit': '100',
            if (cursor != null) 'cursor': cursor,
          },
        ).query;
        final raw = DiscSetWire.map(
          await _send(token, '/api/disc-sets?$query'),
        );
        if (raw.length != 3 ||
            !raw.containsKey('nextCursor') ||
            raw['hasNextPage'] is! bool)
          throw const DiscSetException(DiscSetFailureKind.malformed);
        final page = DiscSetWire.maps(raw['items']);
        final more = raw['hasNextPage']! as bool;
        final next = raw['nextCursor'];
        if (page.length > 100 ||
            (more &&
                (page.isEmpty ||
                    next is! String ||
                    next.isEmpty ||
                    next.length > 1024)) ||
            (!more && next != null))
          throw const DiscSetException(DiscSetFailureKind.malformed);
        for (final item in page) {
          final status = _status(item, titleId: titleId);
          if (result.any((s) => s.discSetId == status.discSetId))
            throw const DiscSetException(DiscSetFailureKind.malformed);
          result.add(status);
          if (result.length > 1000)
            throw const DiscSetException(DiscSetFailureKind.malformed);
        }
        if (!more) return result;
        cursor = next as String;
        if (!cursors.add(cursor))
          throw const DiscSetException(DiscSetFailureKind.malformed);
      } while (result.length < 1000);
    } on FormatException {
      throw const DiscSetException(DiscSetFailureKind.malformed);
    }
    throw const DiscSetException(DiscSetFailureKind.malformed);
  }

  @override
  Future<DiscSetStatus> status(String token, String discSetId) async {
    DiscSetWire.id({'id': discSetId}, 'id');
    return _status(
      await _send(token, '/api/disc-sets/$discSetId/status'),
      discSetId: discSetId,
    );
  }

  @override
  Future<DiscSetPlan> select(
    String token,
    String discSetId,
    Map<String, Object?> selection,
  ) async {
    DiscSetWire.id({'id': discSetId}, 'id');
    try {
      final plan = DiscSetPlan.parse(
        await _send(
          token,
          '/api/disc-sets/$discSetId/selection-manifest',
          selection,
        ),
        origin: origin,
      );
      if (plan.serverInstanceId != serverInstanceId ||
          plan.discSetId != discSetId ||
          jsonEncode(plan.selectedReleaseIds) !=
              jsonEncode(selection['selectedReleaseIds']) ||
          (selection['requiredSnapshotId'] != null &&
              plan.snapshotId != selection['requiredSnapshotId']) ||
          plan.revision != selection['revision'] ||
          plan.observationId != selection['observationId'] ||
          (plan.allowPartial && selection['allowPartial'] != true))
        throw const FormatException('Selection identity mismatch.');
      return plan;
    } on FormatException {
      throw const DiscSetException(DiscSetFailureKind.malformed);
    }
  }

  @override
  Future<bool> access(String token, DiscSetPlan plan) async {
    final raw = DiscSetWire.map(
      await _send(
        token,
        '/api/disc-sets/${plan.discSetId}/access/preview',
        plan.accessRequest,
      ),
    );
    try {
      DiscSetWire.version(raw);
      final allowed = raw['allowed'];
      final keys = {
        'contractVersion',
        'serverInstanceId',
        'discSetId',
        'revision',
        'deliverySnapshotId',
        'selectedReleaseIds',
        'layoutVersions',
        'allowed',
        if (allowed == true) ...['titleId', 'systemKey'],
      };
      final normalized = {...raw};
      if (allowed == false) {
        if (normalized['titleId'] == null) normalized.remove('titleId');
        if (normalized['systemKey'] == null) normalized.remove('systemKey');
      }
      if (allowed is! bool ||
          normalized.length != keys.length ||
          normalized.keys.any((k) => !keys.contains(k)) ||
          raw['serverInstanceId'] != plan.serverInstanceId ||
          raw['discSetId'] != plan.discSetId ||
          raw['revision'] != plan.revision ||
          jsonEncode(raw['selectedReleaseIds']) !=
              jsonEncode(plan.selectedReleaseIds) ||
          jsonEncode(_layoutIdentities(raw['layoutVersions'])) !=
              jsonEncode(
                _layoutIdentities(plan.accessRequest['layoutVersions']),
              ) ||
          raw['deliverySnapshotId'] != plan.snapshotId ||
          (allowed &&
              (raw['titleId'] != plan.titleId ||
                  raw['systemKey'] != plan.systemKey))) {
        throw const FormatException('Access mismatch.');
      }
      return allowed;
    } on FormatException {
      throw const DiscSetException(DiscSetFailureKind.malformed);
    }
  }

  Object _layoutIdentities(Object? value) => DiscSetWire.maps(value).map((v) {
    DiscSetWire.exact(
      v,
      {'releaseId'},
      optional: {'layoutId', 'layoutRevision'},
    );
    return [v['releaseId'], v['layoutId'], v['layoutRevision']];
  }).toList();

  @override
  void close() {
    _closed = true;
    for (final operation in _operations.toList()) {
      operation.abort();
    }
  }
}
