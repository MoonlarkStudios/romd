import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/disc_sets/disc_set_api.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_failure.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_models.dart';

import 'disc_set_fixture.dart';

void main() {
  late HttpServer http;
  late HttpDiscSetApi api;
  late void Function(HttpRequest) respond;
  setUp(() async {
    http = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    api = HttpDiscSetApi(Uri.parse('http://localhost:${http.port}'), server);
    respond = (request) {
      request.response.headers.contentType = ContentType.json;
      request.response.write(jsonEncode(statusFixture()));
      request.response.close();
    };
    http.listen((request) => respond(request));
  });
  tearDown(() async {
    api.close();
    await http.close(force: true);
  });
  test('status validates expected server and set identity', () async {
    final status = await api.status('token', setId);
    expect(status.discSetId, setId);
  });
  test(
    'list consumes versioned status, not filename inferred relationships',
    () async {
      respond = (request) {
        expect(request.uri.path, '/api/disc-sets');
        expect(request.headers.value('authorization'), 'Bearer token');
        request.response.write(
          jsonEncode({
            'items': [statusFixture()],
            'nextCursor': null,
            'hasNextPage': false,
          }),
        );
        request.response.close();
      };
      expect((await api.list('token', titleId)).single.discs.length, 2);
    },
  );
  test(
    'selection POST binds exact selected members and required snapshot',
    () async {
      final selected = plan();
      respond = (request) async {
        expect(request.method, 'POST');
        expect(request.uri.path, '/api/disc-sets/$setId/selection-manifest');
        final body = DiscSetWire.map(
          jsonDecode(await utf8.decodeStream(request)),
        );
        expect(body['requiredSnapshotId'], selected.snapshotId);
        expect(body['selectedReleaseIds'], releases);
        request.response.write(jsonEncode(fixture()));
        await request.response.close();
      };
      expect(
        (await api.select('token', setId, selected.renewalRequest)).snapshotId,
        selected.snapshotId,
      );
    },
  );
  test(
    'changed snapshot on renewal is malformed rather than silently replanned',
    () async {
      respond = (request) {
        request.response.write(jsonEncode(fixture(snapshotSeed: 'changed')));
        request.response.close();
      };
      await expectLater(
        api.select('token', setId, plan().renewalRequest),
        throwsA(
          isA<DiscSetException>().having(
            (e) => e.kind,
            'kind',
            DiscSetFailureKind.malformed,
          ),
        ),
      );
    },
  );
  test(
    'access validates echoed exact identity and nullable catalog layout versions',
    () async {
      final p = plan();
      respond = (request) {
        request.response.write(
          jsonEncode(
            {
              ...p.accessRequest,
              'discSetId': setId,
              'allowed': true,
              'titleId': titleId,
              'systemKey': 'psx',
            }..remove('allowPartial'),
          ),
        );
        request.response.close();
      };
      expect(await api.access('token', p), isTrue);
    },
  );
  test(
    'explicit denial accepts null metadata but never foreign snapshot',
    () async {
      final p = plan();
      respond = (request) {
        request.response.write(
          jsonEncode(
            {
              ...p.accessRequest,
              'discSetId': setId,
              'allowed': false,
              'titleId': null,
              'systemKey': null,
            }..remove('allowPartial'),
          ),
        );
        request.response.close();
      };
      expect(await api.access('token', p), isFalse);
      respond = (request) {
        request.response.write(
          jsonEncode(
            {
              ...p.accessRequest,
              'discSetId': setId,
              'allowed': false,
              'deliverySnapshotId': digest('foreign'),
            }..remove('allowPartial'),
          ),
        );
        request.response.close();
      };
      await expectLater(
        api.access('token', p),
        throwsA(isA<DiscSetException>()),
      );
    },
  );
  test('redirect is refused without forwarding a credential', () async {
    var requests = 0;
    respond = (request) {
      requests++;
      request.response.statusCode = 302;
      request.response.headers.set('location', '/other');
      request.response.close();
    };
    await expectLater(
      api.status('secret', setId),
      throwsA(isA<DiscSetException>()),
    );
    expect(requests, 1);
  });
  // The server answers a stale revision pin with 409 and an unpublished set with 404.
  for (final (code, kind) in [
    (409, DiscSetFailureKind.stale),
    (404, DiscSetFailureKind.denied),
  ]) {
    test('selection HTTP $code is the ${kind.name} failure', () async {
      respond = (request) async {
        await request.drain<void>();
        request.response.statusCode = code;
        await request.response.close();
      };
      await expectLater(
        api.select('token', setId, plan().renewalRequest),
        throwsA(
          isA<DiscSetException>().having(
            (error) => error.kind,
            'kind',
            kind,
          ),
        ),
      );
    });
  }
  for (final code in [401, 403, 404, 409, 500]) {
    test(
      'HTTP $code is unavailable/denied result rather than an allow',
      () async {
        respond = (request) {
          request.response.statusCode = code;
          request.response.close();
        };
        await expectLater(
          api.access('token', plan()),
          throwsA(isA<DiscSetException>()),
        );
      },
    );
  }
  test('unknown status version and wrong server fail closed', () async {
    respond = (request) {
      request.response.write(
        jsonEncode({...statusFixture(), 'contractVersion': 2}),
      );
      request.response.close();
    };
    await expectLater(
      api.status('token', setId),
      throwsA(isA<DiscSetException>()),
    );
    respond = (request) {
      request.response.write(
        jsonEncode({
          ...statusFixture(),
          'serverInstanceId': '33333333-3333-4333-8333-333333333333',
        }),
      );
      request.response.close();
    };
    await expectLater(
      api.status('token', setId),
      throwsA(isA<DiscSetException>()),
    );
  });
}
