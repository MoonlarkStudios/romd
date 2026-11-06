import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/reference_catalog.dart';
import 'package:romd_console/src/data/reference_catalog_canonical_json.dart';
import 'package:romd_console/src/data/reference_catalog_snapshot.dart';
import 'package:romd_console/src/presentation/reference_catalog_scope.dart';

void main() {
  test('canonical content matches the shared cross-language vectors', () async {
    final fixture =
        jsonDecode(
              await File(
                '../../tests/Fixtures/ReferenceCatalogCanonicalization.json',
              ).readAsString(),
            )
            as Map<String, dynamic>;
    expect(fixture['profile'], 'romd-reference-json-v1');
    for (final vector
        in (fixture['vectors'] as List<Object?>).cast<Map<String, dynamic>>()) {
      expect(
        canonicalReferenceJson(vector['input']),
        vector['canonical'],
        reason: vector['name'] as String,
      );
      expect(
        referenceCatalogContentHash(vector['input']),
        vector['sha256'],
        reason: vector['name'] as String,
      );
    }
    for (final vector
        in (fixture['rejections'] as List<Object?>)
            .cast<Map<String, dynamic>>()) {
      expect(
        () => canonicalReferenceJson(vector['input']),
        throwsFormatException,
        reason: vector['name'] as String,
      );
    }
  });

  test('canonical content rejects invalid numbers and Unicode', () {
    for (final value in <Object>[
      0.5,
      double.nan,
      double.infinity,
      9007199254740992,
      String.fromCharCode(0xd800),
      String.fromCharCode(0xdc00),
      <String, Object>{String.fromCharCode(0xd800): 'bad key'},
    ]) {
      expect(() => canonicalReferenceJson(value), throwsFormatException);
    }
    expect(canonicalReferenceJson([1.0, -0.0, 1e0]), '[1,0,1]');
  });

  test(
    'HTTP completes all six families and retains their published order',
    () async {
      final source = _catalog('a');
      ((source['systems'] as List<Object?>).first
          as Map<String, dynamic>)['futureDisplay'] = {
        'z': '未知 😀',
        'nullable': null,
      };
      final wire = await _SnapshotWire.start(source);
      addTearDown(wire.close);
      final client = wire.client();
      addTearDown(client.close);
      final catalog = (await client.getReferenceCatalog('memory-token'))!;
      expect(catalog.json, wire.catalog);
      expect(catalog.systems.map((row) => row['key']), ['z-local', 'a-local']);
      // The same rating code in different boards is a distinct natural identity.
      expect((catalog.json['ratings'] as List<Object?>).length, 2);
      expect(catalog.usedSnapshotRestart, isFalse);
      expect(wire.roots, 1);
      for (final path in referenceCatalogFamilies.values) {
        expect(wire.pages[path], 2, reason: path);
      }
      expect(wire.requests, 13);
    },
  );

  test(
    'HTTP traverses a real 100-item boundary for all six families',
    () async {
      final source = _catalog('a');
      for (final family in referenceCatalogFamilies.keys) {
        final template =
            (source[family] as List<Object?>).first as Map<String, dynamic>;
        source[family] = <Map<String, dynamic>>[
          for (var index = 0; index < 101; index++)
            if (family == 'ratings')
              {...template, 'board': 'board-$index', 'code': 'E'}
            else
              {
                ...template,
                'key': family == 'ratingBoards'
                    ? 'board-$index'
                    : '$family-$index',
              },
        ];
      }
      final wire = await _SnapshotWire.start(source, pageSize: 100);
      addTearDown(wire.close);
      final client = wire.client();
      addTearDown(client.close);
      final complete = (await client.getReferenceCatalog('memory-token'))!;
      expect(complete.json, source);
      expect(complete.systems.last['key'], 'systems-100');
      for (final family in referenceCatalogFamilies.entries) {
        expect(complete.json[family.key], hasLength(101));
        expect(wire.pages[family.value], 2);
      }
      expect(wire.requests, 13);
    },
  );

  for (final partialBody in [false, true]) {
    test(
      'continuation page deadline cancels a stalled ${partialBody ? 'body' : 'header'}',
      () async {
        final wire = await _SnapshotWire.start(_catalog('a'));
        addTearDown(wire.close);
        wire.override = (request) async {
          if (request.uri.path != '/api/catalog-snapshot/systems' ||
              request.uri.queryParameters['cursor'] != '1')
            return false;
          if (partialBody) {
            request.response
              ..contentLength = 1024
              ..write('{"');
            await request.response.flush();
          }
          return true;
        };
        final client = wire.client(timeout: const Duration(milliseconds: 150));
        addTearDown(client.close);

        await expectLater(
          client
              .getReferenceCatalog('memory-token')
              .timeout(const Duration(seconds: 3)),
          throwsA(isA<ConsumerApiException>()),
        );
        expect(wire.pages['systems'], 2);
        expect(wire.requests, 3);
        // The expired continuation must leave the shared client usable.
        expect(
          await client.getReferenceCatalog('memory-token', revision: 'a' * 64),
          isNull,
        );
      },
    );
  }

  test(
    'cached catalog identity is compared after an unconditional descriptor read',
    () async {
      final wire = await _SnapshotWire.start(_catalog('a'));
      addTearDown(wire.close);
      final client = wire.client();
      addTearDown(client.close);
      expect(
        await client.getReferenceCatalog('memory-token', revision: 'a' * 64),
        isNull,
      );
      expect(wire.roots, 1);
      expect(wire.pages, isEmpty);
    },
  );

  test(
    'an unexpected unconditional 304 fails instead of implying cached identity',
    () async {
      final wire = await _SnapshotWire.start(_catalog('a'));
      addTearDown(wire.close);
      wire.override = (request) async {
        if (request.uri.path != '/api/catalog-snapshot') return false;
        request.response.statusCode = HttpStatus.notModified;
        await request.response.close();
        return true;
      };
      final client = wire.client();
      addTearDown(client.close);
      await expectLater(
        client.getReferenceCatalog('memory-token', revision: 'a' * 64),
        throwsA(isA<HttpException>()),
      );
      expect(wire.requests, 1);
    },
  );

  test(
    'descriptor ETag must describe the manifest rather than the complete catalog',
    () async {
      final wire = await _SnapshotWire.start(_catalog('a'));
      addTearDown(wire.close);
      wire.override = (request) async {
        if (request.uri.path != '/api/catalog-snapshot') return false;
        await _json(request, _descriptor(wire.catalog), etag: '"${'a' * 64}"');
        return true;
      };
      final client = wire.client();
      addTearDown(client.close);
      await expectLater(
        client.getReferenceCatalog('memory-token'),
        throwsFormatException,
      );
      expect(wire.requests, 1);
    },
  );

  test(
    'HTTP snapshot conflict discards every old family and restarts once',
    () async {
      final wire = await _SnapshotWire.start(_catalog('a'));
      addTearDown(wire.close);
      var conflicted = false;
      wire.override = (request) async {
        if (!conflicted && request.uri.path.endsWith('/companies')) {
          conflicted = true;
          wire.catalog = _catalog('b', name: 'Changed after publication');
          await _conflict(request);
          return true;
        }
        return false;
      };
      final client = wire.client();
      addTearDown(client.close);
      final result = (await client.getReferenceCatalog('memory-token'))!;
      expect(result.revision, 'b' * 64);
      expect(result.json, wire.catalog);
      expect(result.systems.first['name'], 'Changed after publication');
      expect(result.usedSnapshotRestart, isTrue);
      expect(wire.roots, 2);
      expect(wire.pages['systems'], 4);
    },
  );

  test(
    'repeated HTTP snapshot conflicts fail after exactly two roots',
    () async {
      final wire = await _SnapshotWire.start(_catalog('a'));
      addTearDown(wire.close);
      wire.override = (request) async {
        if (!request.uri.path.endsWith('/systems')) return false;
        await _conflict(request);
        return true;
      };
      final client = wire.client();
      addTearDown(client.close);
      await expectLater(
        client.getReferenceCatalog('memory-token'),
        throwsA(isA<ReferenceSnapshotChanged>()),
      );
      expect(wire.roots, 2);
      expect(wire.pages['systems'], 2);
    },
  );

  test('a non-snapshot 409 is not retried', () async {
    final wire = await _SnapshotWire.start(_catalog('a'));
    addTearDown(wire.close);
    wire.override = (request) async {
      if (!request.uri.path.endsWith('/systems')) return false;
      await _json(request, {
        'errorCode': 'ReferenceCatalog.OtherConflict',
      }, status: HttpStatus.conflict);
      return true;
    };
    final client = wire.client();
    addTearDown(client.close);
    await expectLater(
      client.getReferenceCatalog('memory-token'),
      throwsA(isA<HttpException>()),
    );
    expect(wire.roots, 1);
  });

  for (final failure in [
    'count',
    'hash',
    'cycle',
    'duplicate',
    'zero-progress',
    'late-status',
    'malformed-company',
    'malformed-region',
    'malformed-language',
    'terminal-cursor',
    'missing-family-count',
  ]) {
    test(
      'HTTP $failure leaves the last complete cache and pointer intact',
      () async {
        final root = await Directory.systemTemp.createTemp(
          'romd-snapshot-http-',
        );
        addTearDown(() => root.delete(recursive: true));
        final wire = await _SnapshotWire.start(_catalog('a'));
        addTearDown(wire.close);
        final client = wire.client();
        addTearDown(client.close);
        final controller = ReferenceCatalogController(root)
          ..activate('server', client);
        addTearDown(controller.dispose);
        await controller.refresh('memory-token');
        final cache = ReferenceCatalogCache(root, 'server');
        final previous = await File(
          '${cache.directory.path}/current',
        ).readAsString();
        expect(previous, 'a' * 64);
        wire.catalog = _catalog('b');
        wire.override = (request) async {
          final path = request.uri.path;
          if (path == '/api/catalog-snapshot' &&
              [
                'count',
                'hash',
                'missing-family-count',
                'cycle',
              ].contains(failure)) {
            final descriptor = _descriptor(wire.catalog);
            final counts = descriptor['counts'] as Map<String, dynamic>;
            if (failure == 'count') counts['languages'] = 3;
            if (failure == 'cycle') counts['systems'] = 3;
            if (failure == 'hash') descriptor['contentHash'] = 'f' * 64;
            if (failure == 'missing-family-count') counts.remove('companies');
            await _json(request, descriptor, etag: _manifestEtag);
            return true;
          }
          if (failure == 'cycle' && path.endsWith('/systems')) {
            final page = request.uri.queryParameters['cursor'] == null ? 0 : 1;
            await _json(request, {
              'items': [(wire.catalog['systems'] as List<Object?>)[page]],
              'hasNextPage': true,
              'nextCursor': 'same-cursor',
            });
            return true;
          }
          if (failure == 'duplicate' &&
              path.endsWith('/systems') &&
              request.uri.queryParameters['cursor'] != null) {
            await _json(
              request,
              _page([(wire.catalog['systems'] as List<Object?>).first]),
            );
            return true;
          }
          if (failure == 'zero-progress' && path.endsWith('/systems')) {
            await _json(request, {
              'items': <Object>[],
              'hasNextPage': true,
              'nextCursor': 'new-cursor-${wire.requests}',
            });
            return true;
          }
          if (failure == 'terminal-cursor' && path.endsWith('/systems')) {
            await _json(request, {
              'items': wire.catalog['systems'],
              'hasNextPage': false,
              'nextCursor': 'unexpected',
            });
            return true;
          }
          if (failure == 'late-status' &&
              path.endsWith('/ratings') &&
              request.uri.queryParameters['cursor'] != null) {
            await _json(request, {
              'errorCode': 'Server.Error',
            }, status: HttpStatus.internalServerError);
            return true;
          }
          final brokenFamily = switch (failure) {
            'malformed-company' => 'companies',
            'malformed-region' => 'regions',
            'malformed-language' => 'languages',
            _ => null,
          };
          if (brokenFamily != null && path.endsWith('/$brokenFamily')) {
            final invalid = Map<String, dynamic>.from(
              (wire.catalog[brokenFamily] as List<Object?>).first
                  as Map<String, dynamic>,
            );
            invalid['name'] = false;
            await _json(request, _page([invalid]));
            return true;
          }
          return false;
        };
        await controller.refresh('memory-token');
        expect(controller.catalog!.revision, previous);
        expect((await cache.load())!.revision, previous);
        expect(
          await File('${cache.directory.path}/current').readAsString(),
          previous,
        );
        expect(wire.roots, 2);
        expect(wire.requests, lessThan(30));
        final stored = await File(
          '${cache.directory.path}/$previous.json',
        ).readAsString();
        expect(stored, isNot(contains('memory-token')));
      },
    );
  }

  test(
    'an empty family is a valid terminal page and participates in the digest',
    () async {
      final catalog = _catalog('a')
        ..['companies'] = <Object>[]
        ..['regions'] = <Object>[]
        ..['languages'] = <Object>[];
      final wire = await _SnapshotWire.start(catalog);
      addTearDown(wire.close);
      final client = wire.client();
      addTearDown(client.close);
      expect((await client.getReferenceCatalog('memory-token'))!.json, catalog);
      expect(wire.pages['companies'], 1);
      expect(wire.pages['regions'], 1);
      expect(wire.pages['languages'], 1);
    },
  );

  test(
    'a server switch stops old HTTP accumulation before the next page or promotion',
    () async {
      final root = await Directory.systemTemp.createTemp(
        'romd-snapshot-switch-',
      );
      addTearDown(() => root.delete(recursive: true));
      final old = await _SnapshotWire.start(_catalog('a'));
      final next = await _SnapshotWire.start(_catalog('b'));
      addTearDown(old.close);
      addTearDown(next.close);
      final oldClient = old.client();
      final nextClient = next.client();
      addTearDown(oldClient.close);
      addTearDown(nextClient.close);
      final started = Completer<void>();
      final release = Completer<void>();
      old.override = (request) async {
        if (!request.uri.path.endsWith('/systems')) return false;
        started.complete();
        await release.future;
        return false;
      };
      final controller = ReferenceCatalogController(root)
        ..activate('old', oldClient);
      addTearDown(controller.dispose);
      final pending = controller.refresh('old-token');
      await started.future;
      controller.activate('next', nextClient);
      await controller.refresh('next-token');
      release.complete();
      await pending;
      expect(controller.catalog!.revision, 'b' * 64);
      expect(await ReferenceCatalogCache(root, 'old').load(), isNull);
      expect(
        (await ReferenceCatalogCache(root, 'next').load())!.revision,
        'b' * 64,
      );
      expect(old.requests, 2);
    },
  );

  test(
    'HTTP artwork is verified before pointer promotion and receives no account token',
    () async {
      final bytes = Uint8List.fromList([10, 20, 30]);
      final hash = sha256.convert(bytes).toString();
      var assetReads = 0;
      final assets = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      addTearDown(() => assets.close(force: true));
      assets.listen((request) async {
        expect(request.headers.value(HttpHeaders.authorizationHeader), isNull);
        assetReads++;
        request.response.add(bytes);
        await request.response.close();
      });
      final catalog = _catalog('a');
      ((catalog['systems'] as List<Object?>).first
          as Map<String, dynamic>)['icon'] = {
        'url': 'http://${assets.address.host}:${assets.port}/icon',
        'sha256': hash,
        'contentType': 'image/png',
        'monochrome': false,
      };
      final wire = await _SnapshotWire.start(catalog);
      addTearDown(wire.close);
      final client = wire.client();
      addTearDown(client.close);
      final root = await Directory.systemTemp.createTemp(
        'romd-snapshot-assets-',
      );
      addTearDown(() => root.delete(recursive: true));
      final controller = ReferenceCatalogController(root)
        ..activate('server', client);
      addTearDown(controller.dispose);
      await controller.refresh('memory-token');
      final cache = ReferenceCatalogCache(root, 'server');
      expect((await cache.load())!.revision, 'a' * 64);
      expect(await cache.asset(hash).readAsBytes(), bytes);
      expect(assetReads, 1);
      expect(
        await File('${cache.directory.path}/${'a' * 64}.json').readAsString(),
        isNot(contains('memory-token')),
      );
    },
  );

  test('snapshot and asset expiry share one restart budget', () async {
    final wire = await _SnapshotWire.start(_catalog('a'));
    addTearDown(wire.close);
    final client = wire.client();
    addTearDown(client.close);
    final root = await Directory.systemTemp.createTemp('romd-snapshot-budget-');
    addTearDown(() => root.delete(recursive: true));
    final controller = ReferenceCatalogController(root)
      ..activate('server', client);
    addTearDown(controller.dispose);
    await controller.refresh('memory-token');
    wire.catalog = _catalog('b');
    var conflicted = false;
    wire.override = (request) async {
      if (!conflicted && request.uri.path.endsWith('/companies')) {
        conflicted = true;
        wire.catalog = _catalog('c');
        ((wire.catalog['systems'] as List<Object?>).first
            as Map<String, dynamic>)['icon'] = {
          'url': '/expired-icon',
          'sha256': 'd' * 64,
          'contentType': 'image/png',
          'monochrome': false,
        };
        await _conflict(request);
        return true;
      }
      if (request.uri.path == '/expired-icon') {
        expect(request.headers.value(HttpHeaders.authorizationHeader), isNull);
        request.response.statusCode = HttpStatus.notFound;
        await request.response.close();
        return true;
      }
      return false;
    };
    await controller.refresh('memory-token');
    expect(
      wire.roots,
      3,
    ); // Initial complete cache, then exactly two refresh roots.
    expect(controller.catalog!.revision, 'a' * 64);
    expect(
      (await ReferenceCatalogCache(root, 'server').load())!.revision,
      'a' * 64,
    );
  });
}

const _manifestEtag =
    '"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"';

final class _SnapshotWire {
  _SnapshotWire(this.server, this.catalog, this.pageSize);
  final int pageSize;
  final HttpServer server;
  Map<String, dynamic> catalog;
  int roots = 0;
  int requests = 0;
  final pages = <String, int>{};
  Future<bool> Function(HttpRequest)? override;

  static Future<_SnapshotWire> start(
    Map<String, dynamic> catalog, {
    int pageSize = 1,
  }) async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final wire = _SnapshotWire(server, catalog, pageSize);
    server.listen(wire.handle);
    return wire;
  }

  RomdConsumerApiClient client({
    Duration timeout = const Duration(seconds: 15),
  }) => RomdConsumerApiClient(
    timeout: timeout,
    consumerApiOrigin: Uri.parse(
      'http://${server.address.host}:${server.port}',
    ),
  );

  Future<void> close() async {
    await server.close(force: true);
  }

  Future<void> handle(HttpRequest request) async {
    requests++;
    final path = request.uri.path;
    if (path.startsWith('/api/catalog-snapshot')) {
      expect(
        request.headers.value(HttpHeaders.authorizationHeader),
        startsWith('Bearer '),
      );
      expect(request.headers.value(HttpHeaders.ifNoneMatchHeader), isNull);
      if (path == '/api/catalog-snapshot') {
        roots++;
        expect(request.uri.queryParameters, isEmpty);
      } else {
        final family = path.split('/').last;
        pages.update(family, (count) => count + 1, ifAbsent: () => 1);
      }
    }
    if ((await override?.call(request)) ?? false) return;
    if (path == '/api/catalog-snapshot') {
      await _json(request, _descriptor(catalog), etag: _manifestEtag);
      return;
    }
    final family = referenceCatalogFamilies.entries
        .where((entry) => path == '/api/catalog-snapshot/${entry.value}')
        .firstOrNull;
    if (family == null) {
      request.response.statusCode = HttpStatus.notFound;
      await request.response.close();
      return;
    }
    expect(request.uri.queryParameters['revision'], catalog['revision']);
    expect(request.uri.queryParameters['limit'], '100');
    final rows = catalog[family.key] as List<Object?>;
    final offset = int.parse(request.uri.queryParameters['cursor'] ?? '0');
    final items = rows.skip(offset).take(pageSize).toList();
    final next = offset + items.length < rows.length
        ? '${offset + items.length}'
        : null;
    await _json(request, {
      'items': items,
      'hasNextPage': next != null,
      'nextCursor': next,
    });
  }
}

Map<String, dynamic> _descriptor(Map<String, dynamic> catalog) => {
  'schemaVersion': catalog['schemaVersion'],
  'builtInVersion': catalog['builtInVersion'],
  'revision': 'e' * 64,
  'catalogRevision': catalog['revision'],
  'contentHash': referenceCatalogContentHash(catalog),
  'counts': <String, dynamic>{
    for (final family in referenceCatalogFamilies.keys)
      family: (catalog[family] as List<Object?>).length,
  },
};

Future<void> _json(
  HttpRequest request,
  Map<String, dynamic> body, {
  int status = HttpStatus.ok,
  String? etag,
}) async {
  request.response
    ..statusCode = status
    ..headers.contentType = ContentType.json;
  if (etag != null) request.response.headers.set(HttpHeaders.etagHeader, etag);
  request.response.write(jsonEncode(body));
  await request.response.close();
}

Future<void> _conflict(HttpRequest request) => _json(request, {
  'errorCode': 'ReferenceCatalog.SnapshotChanged',
}, status: HttpStatus.conflict);

Map<String, dynamic> _page(List<Object?> items) => {
  'items': items,
  'hasNextPage': false,
  'nextCursor': null,
};

Map<String, dynamic> _catalog(
  String revision, {
  String name = 'Élan 😀 <>&',
}) => {
  'schemaVersion': 1,
  'builtInVersion': 7,
  'revision': revision * 64,
  'systems': <Map<String, dynamic>>[
    _system('z-local', name),
    _system('a-local', 'Other'),
  ],
  'companies': <Map<String, dynamic>>[_named('z-company'), _named('a-company')],
  'regions': <Map<String, dynamic>>[
    {..._named('z-region'), 'sortOrder': 1, 'languageCode': null},
    {..._named('a-region'), 'sortOrder': 2, 'languageCode': null},
  ],
  'languages': <Map<String, dynamic>>[
    {..._named('z-language'), 'sortOrder': 1, 'languageCode': 'zz'},
    {..._named('a-language'), 'sortOrder': 2, 'languageCode': 'aa'},
  ],
  'ratingBoards': <Map<String, dynamic>>[_named('ESRB'), _named('PEGI')],
  'ratings': <Map<String, dynamic>>[
    for (final board in ['ESRB', 'PEGI'])
      {
        'board': board,
        'code': 'E',
        'name': 'Everyone',
        'description': 'quoted " / \\',
        'icon': null,
        'designation': null,
        'minimumAge': null,
        'retired': false,
      },
  ],
};

Map<String, dynamic> _system(String key, String name) => {
  'key': key,
  'name': name,
  'compactLabel': 'FUT',
  'description': null,
  'icon': null,
  'manufacturerKeys': <Object>[],
  'manufacturers': <Object>[],
  'retired': false,
};

Map<String, dynamic> _named(String key) => {
  'key': key,
  'name': key,
  'description': null,
  'retired': false,
};
