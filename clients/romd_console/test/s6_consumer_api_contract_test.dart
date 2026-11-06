import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/release_access_api_client.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/disc_sets/disc_set_api.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_failure.dart';

import 'disc_sets/disc_set_fixture.dart' as disc;

void main() {
  test(
    'release access uses authenticated bodyless GET with exact identity',
    () async {
      final releaseId = RomdPublicId.tryParse(RomdPublicId.encode(43))!;
      final titleId = RomdPublicId.tryParse(RomdPublicId.encode(42))!;
      var requests = 0;
      final server = await _serve((request) async {
        requests++;
        expect(request.method, 'GET');
        expect(request.uri.path, '/api/releases/${releaseId.value}/access');
        expect(request.uri.query, isEmpty);
        expect(
          request.headers.value(HttpHeaders.authorizationHeader),
          'Bearer token',
        );
        expect(request.headers.contentType, isNull);
        expect(await utf8.decodeStream(request), isEmpty);
        await _reply(request, {
          'serverInstanceId': disc.server,
          'releaseId': releaseId.value,
          'titleId': titleId.value,
          'allowed': true,
        });
      });
      addTearDown(() => server.close(force: true));
      final client = HttpReleaseAccessApiClient(
        consumerApiOrigin: _origin(server),
      );
      addTearDown(client.close);

      final result = await client.getReleaseAccess(
        accessToken: 'token',
        expectedServerInstanceId: RomdServerInstanceId.tryParse(disc.server)!,
        releaseId: releaseId,
        expectedTitleId: titleId,
      );

      expect(result, isA<ReleaseAccessSuccess>());
      final decision = (result as ReleaseAccessSuccess).decision;
      expect(decision, isA<ReleaseAccessAllowed>());
      expect((decision as ReleaseAccessAllowed).titleId, titleId);
      expect(requests, 1);
    },
  );

  test('GET access refuses redirects without forwarding credentials', () async {
    var targetRequests = 0;
    final target = await _serve((request) async {
      targetRequests++;
      await _reply(request, {});
    });
    addTearDown(() => target.close(force: true));
    final source = await _serve((request) async {
      expect(request.method, 'GET');
      await request.drain<void>();
      request.response
        ..statusCode = HttpStatus.temporaryRedirect
        ..headers.set(
          HttpHeaders.locationHeader,
          _origin(target).resolve('/other'),
        );
      await request.response.close();
    });
    addTearDown(() => source.close(force: true));
    final client = HttpReleaseAccessApiClient(
      consumerApiOrigin: _origin(source),
    );
    addTearDown(client.close);

    final result = await client.getReleaseAccess(
      accessToken: 'secret',
      expectedServerInstanceId: RomdServerInstanceId.tryParse(disc.server)!,
      releaseId: RomdPublicId.tryParse(disc.releases.first)!,
      expectedTitleId: RomdPublicId.tryParse(disc.titleId)!,
    );

    expect(result, isA<ReleaseAccessFailure>());
    expect(
      (result as ReleaseAccessFailure).kind,
      ReleaseAccessFailureKind.redirect,
    );
    expect(targetRequests, 0);
  });

  test('collections drain short explicit pages and preserve filters', () async {
    final cursors = <String?>[];
    final server = await _serve((request) async {
      expect(request.method, 'GET');
      expect(request.uri.path, '/api/collections');
      expect(request.uri.queryParameters['systemKey'], 'snes');
      expect(
        request.headers.value(HttpHeaders.authorizationHeader),
        'Bearer token',
      );
      final cursor = request.uri.queryParameters['cursor'];
      cursors.add(cursor);
      await _reply(
        request,
        _page([
          _collection(cursor == null ? 'first' : 'second'),
        ], nextCursor: cursor == null ? 'opaque +/cursor=' : null),
      );
    });
    addTearDown(() => server.close(force: true));
    final client = RomdConsumerApiClient(consumerApiOrigin: _origin(server));
    addTearDown(client.close);

    final result = await client.listCollections(
      accessToken: 'token',
      platformId: 'snes',
    );

    expect(result.map((item) => item.name), ['first', 'second']);
    expect(cursors, [null, 'opaque +/cursor=']);
  });

  for (final response in <Object?>[
    <Object?>[],
    {'items': <Object?>[], 'hasNextPage': false, 'unexpected': null},
    {'items': <Object?>[], 'hasNextPage': true, 'nextCursor': null},
    {'items': <Object?>[], 'hasNextPage': false, 'nextCursor': 'unexpected'},
    {'items': <Object?>[], 'hasNextPage': 'false', 'nextCursor': null},
  ]) {
    test('collections reject malformed explicit page $response', () async {
      final server = await _serve((request) => _reply(request, response));
      addTearDown(() => server.close(force: true));
      final client = RomdConsumerApiClient(consumerApiOrigin: _origin(server));
      addTearDown(client.close);

      await expectLater(
        client.listCollections(accessToken: 'token'),
        throwsA(isA<ConsumerApiException>()),
      );
    });
  }

  test('collections reject repeated cursors without looping', () async {
    var requests = 0;
    final server = await _serve((request) async {
      requests++;
      await _reply(
        request,
        _page([_collection('item-$requests')], nextCursor: 'repeated'),
      );
    });
    addTearDown(() => server.close(force: true));
    final client = RomdConsumerApiClient(consumerApiOrigin: _origin(server));
    addTearDown(client.close);

    await expectLater(
      client.listCollections(accessToken: 'token'),
      throwsA(isA<ConsumerApiException>()),
    );
    expect(requests, 2);
  });

  test(
    'disc sets use top-level explicit continuation, not page length',
    () async {
      final cursors = <String?>[];
      final secondId = RomdPublicId.encode(45);
      final server = await _serve((request) async {
        expect(request.method, 'GET');
        expect(request.uri.path, '/api/disc-sets');
        expect(request.uri.queryParameters['titleId'], disc.titleId);
        expect(request.uri.queryParameters.containsKey('after'), isFalse);
        expect(
          request.headers.value(HttpHeaders.authorizationHeader),
          'Bearer token',
        );
        final cursor = request.uri.queryParameters['cursor'];
        cursors.add(cursor);
        await _reply(
          request,
          _page([
            {
              ...disc.statusFixture(),
              if (cursor != null) 'discSetId': secondId,
            },
          ], nextCursor: cursor == null ? 'opaque +/disc=' : null),
        );
      });
      addTearDown(() => server.close(force: true));
      final api = HttpDiscSetApi(_origin(server), disc.server);
      addTearDown(api.close);

      final result = await api.list('token', disc.titleId);

      expect(result.map((item) => item.discSetId), [disc.setId, secondId]);
      expect(cursors, [null, 'opaque +/disc=']);
    },
  );

  test('a full terminal disc page makes exactly one request', () async {
    var requests = 0;
    final server = await _serve((request) async {
      requests++;
      await _reply(
        request,
        _page([
          for (var index = 1; index <= 100; index++)
            {
              ...disc.statusFixture(),
              'discSetId': RomdPublicId.encode(index + 100),
            },
        ]),
      );
    });
    addTearDown(() => server.close(force: true));
    final api = HttpDiscSetApi(_origin(server), disc.server);
    addTearDown(api.close);

    expect(await api.list('token', disc.titleId), hasLength(100));
    expect(requests, 1);
  });

  for (final response in <Object?>[
    <Object?>[],
    {'items': <Object?>[], 'hasNextPage': false, 'unexpected': null},
    {'items': <Object?>[], 'hasNextPage': true, 'nextCursor': null},
    {'items': <Object?>[], 'hasNextPage': false, 'nextCursor': 'unexpected'},
    {'items': <Object?>[], 'hasNextPage': 0, 'nextCursor': null},
  ]) {
    test('disc sets reject malformed explicit page $response', () async {
      final server = await _serve((request) => _reply(request, response));
      addTearDown(() => server.close(force: true));
      final api = HttpDiscSetApi(_origin(server), disc.server);
      addTearDown(api.close);

      await expectLater(
        api.list('token', disc.titleId),
        throwsA(isA<DiscSetException>()),
      );
    });
  }

  for (final scenario in [
    'duplicate',
    'repeated cursor',
    'foreign server',
    'foreign title',
  ]) {
    test(
      'disc pages reject $scenario before returning partial results',
      () async {
        var requests = 0;
        final server = await _serve((request) async {
          requests++;
          final item = {
            ...disc.statusFixture(),
            if (requests > 1 && scenario != 'duplicate')
              'discSetId': RomdPublicId.encode(46),
            if (scenario == 'foreign server')
              'serverInstanceId': '33333333-3333-4333-8333-333333333333',
            if (scenario == 'foreign title') 'titleId': RomdPublicId.encode(47),
          };
          await _reply(
            request,
            _page(
              [item],
              nextCursor: requests == 1 || scenario == 'repeated cursor'
                  ? 'same'
                  : null,
            ),
          );
        });
        addTearDown(() => server.close(force: true));
        final api = HttpDiscSetApi(_origin(server), disc.server);
        addTearDown(api.close);

        await expectLater(
          api.list('token', disc.titleId),
          throwsA(isA<DiscSetException>()),
        );
        expect(requests, scenario.startsWith('foreign') ? 1 : 2);
      },
    );
  }

  test('disc access preview preserves exact snapshot request body', () async {
    final plan = disc.plan();
    final server = await _serve((request) async {
      expect(request.method, 'POST');
      expect(request.uri.path, '/api/disc-sets/${disc.setId}/access/preview');
      expect(jsonDecode(await utf8.decodeStream(request)), plan.accessRequest);
      final response = {
        ...plan.accessRequest,
        'discSetId': disc.setId,
        'allowed': true,
        'titleId': disc.titleId,
        'systemKey': 'psx',
      }..remove('allowPartial');
      await _reply(request, response);
    });
    addTearDown(() => server.close(force: true));
    final api = HttpDiscSetApi(_origin(server), disc.server);
    addTearDown(api.close);

    expect(await api.access('token', plan), isTrue);
  });

  test('title detail drains both embedded page continuations', () async {
    final paths = <String>[];
    Map<String, Object?> media(String id) => {
      'id': id,
      'type': 'Screenshot',
      'url': '/media/$id',
      'isPrimary': false,
    };
    Map<String, Object?> release(String id) => {
      'id': id,
      'name': id,
      'regions': <String>[],
      'languages': <String>[],
      'sizeBytes': '1',
      'isComplete': true,
    };
    final server = await _serve((request) async {
      paths.add(request.uri.path);
      if (request.uri.path == '/api/titles/title-1') {
        await _reply(request, {
          'id': 'title-1',
          'system': {'key': 'snes', 'name': 'SNES'},
          'name': 'Title',
          'defaultReleaseId': 'release-2',
          'media': _page([media('media-1')], nextCursor: 'media cursor'),
          'releases': _page([
            release('release-1'),
          ], nextCursor: 'release cursor'),
        });
      } else if (request.uri.path.endsWith('/media')) {
        expect(request.uri.queryParameters['cursor'], 'media cursor');
        await _reply(request, _page([media('media-2')]));
      } else {
        expect(request.uri.path, '/api/titles/title-1/releases');
        expect(request.uri.queryParameters['cursor'], 'release cursor');
        await _reply(request, _page([release('release-2')]));
      }
    });
    addTearDown(() => server.close(force: true));
    final client = RomdConsumerApiClient(consumerApiOrigin: _origin(server));
    addTearDown(client.close);

    final detail = await client.getTitle(
      accessToken: 'token',
      titleId: 'title-1',
    );

    expect(detail.media.map((item) => item.id), ['media-1', 'media-2']);
    expect(detail.releases.map((item) => item.id), ['release-1', 'release-2']);
    expect(detail.defaultReleaseId, 'release-2');
    expect(paths, [
      '/api/titles/title-1',
      '/api/titles/title-1/media',
      '/api/titles/title-1/releases',
    ]);
  });
}

Map<String, Object?> _page(List<Object?> items, {String? nextCursor}) => {
  'items': items,
  'hasNextPage': nextCursor != null,
  'nextCursor': nextCursor,
};

Map<String, Object?> _collection(String id) => {
  'id': id,
  'name': id,
  'description': null,
  'system': null,
  'coverUrl': null,
  'heroUrl': null,
  'itemCount': 0,
  'isFeatured': false,
};

Future<HttpServer> _serve(Future<void> Function(HttpRequest) respond) async {
  final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  server.listen((request) async {
    try {
      await respond(request);
    } catch (error, stack) {
      await request.response.close();
      Error.throwWithStackTrace(error, stack);
    }
  });
  return server;
}

Uri _origin(HttpServer server) => Uri.parse('http://127.0.0.1:${server.port}');

Future<void> _reply(HttpRequest request, Object? body) async {
  request.response.headers.contentType = ContentType.json;
  request.response.write(jsonEncode(body));
  await request.response.close();
}
