import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/release_manifest_api_client.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/domain/server_bound_release_manifest.dart';

void main() {
  test(
    'posts bodyless request and accepts the exact server-bound manifest',
    () async {
      final harness = await _ManifestHarness.start(responseBody: _manifest());
      addTearDown(harness.close);

      final result = await harness.getManifest();

      final request = harness.requests.single;
      expect(request.method, 'POST');
      expect(request.path, '/api/releases/${_releaseId.value}/manifest');
      expect(request.query, isEmpty);
      expect(request.authorization, 'Bearer access-token');
      expect(request.contentType, isNull);
      expect(request.body, isEmpty);
      final manifest = _expectSuccess(result);
      expect(manifest.serverInstanceId, _serverInstanceId);
      expect(manifest.releaseId, _releaseId);
      expect(manifest.titleId, _titleId);
      expect(manifest.systemKey, 'snes');
      expect(manifest.name, 'Chrono Trigger');
      expect(manifest.revision, 'Rev 1');
      expect(manifest.isComplete, isTrue);
      expect(manifest.runtime.contentType, 'single_rom');
      expect(manifest.runtime.packaging, 'direct_files');
      expect(manifest.runtime.minimumInstallBytes, 12);
      expect(manifest.runtime.launch?.type, 'file');
      expect(manifest.runtime.launch?.relativePath, 'chrono/game.sfc');
      expect(manifest.items, hasLength(1));
      final item = manifest.items.single;
      expect(item.relativePath, 'chrono/game.sfc');
      expect(item.role, 'rom');
      expect(item.sizeBytes, 12);
      expect(item.sha256, _sha256);
      expect(item.isAvailable, isTrue);
      final downloadUrl = item.contentGrant?.downloadUrl;
      final consumerOrigin = _originFor(harness.server);
      expect(downloadUrl, consumerOrigin.resolve(_grantUrl));
      expect(downloadUrl?.scheme, consumerOrigin.scheme);
      expect(downloadUrl?.host, consumerOrigin.host);
      expect(downloadUrl?.port, consumerOrigin.port);
      expect(downloadUrl?.path, _grantUrl);
      expect(downloadUrl?.hasQuery, isFalse);
      expect(downloadUrl?.hasFragment, isFalse);
      expect(item.contentGrant?.expiresAt.isUtc, isTrue);
      expect(() => manifest.items.add(item), throwsA(isA<UnsupportedError>()));
    },
  );

  test(
    'accepts only schema-permitted optional absence and null variants',
    () async {
      final cases = <String, Map<String, Object?>>{};

      final revisionAbsent = _manifest();
      revisionAbsent.remove('revision');
      cases['revision absent'] = revisionAbsent;

      final revisionNull = _manifest();
      revisionNull['revision'] = null;
      cases['revision null'] = revisionNull;

      final launchAbsent = _manifest();
      _runtime(launchAbsent)
        ..['contentType'] = 'unknown'
        ..remove('launch')
        ..['minimumInstallBytes'] = 0;
      launchAbsent['items'] = <Object?>[];
      cases['launch absent'] = launchAbsent;

      final launchNull = _manifest();
      _runtime(launchNull)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 0;
      launchNull['items'] = <Object?>[];
      cases['launch null'] = launchNull;

      final optionalsAbsent = _unavailableManifest();
      _item(optionalsAbsent)
        ..remove('sha256')
        ..remove('contentGrant');
      cases['item optionals absent'] = optionalsAbsent;

      final optionalsNull = _unavailableManifest();
      _item(optionalsNull)
        ..['sha256'] = null
        ..['contentGrant'] = null;
      cases['item optionals null'] = optionalsNull;

      final numericStrings = _manifest();
      _runtime(numericStrings)['minimumInstallBytes'] = '12';
      _item(numericStrings)['sizeBytes'] = '12';
      cases['int64 canonical strings'] = numericStrings;

      final emptyUnknown = _manifest();
      _runtime(emptyUnknown)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 0;
      emptyUnknown['items'] = <Object?>[];
      cases['empty unknown runtime'] = emptyUnknown;

      final multiUnknown = _manifest();
      final second = _copyMap(_item(multiUnknown));
      second['relativePath'] = 'chrono/second.sfc';
      multiUnknown['items'] = <Object?>[_copyMap(_item(multiUnknown)), second];
      _runtime(multiUnknown)
        ..['contentType'] = 'unknown'
        ..['minimumInstallBytes'] = 24;
      cases['multi-item unknown runtime with launch'] = multiUnknown;

      for (final entry in cases.entries) {
        final result = await _resultFor(responseBody: entry.value);
        expect(result, isA<ReleaseManifestSuccess>(), reason: entry.key);
      }
    },
  );

  test('rejects malformed root shapes atomically', () async {
    final cases = <String, Object?>{
      'non-object': <Object?>[],
      'missing root': _without(_manifest(), 'name'),
      'extra root': <String, Object?>{..._manifest(), 'extra': true},
      'wrong server type': _with(_manifest(), 'serverInstanceId', 1),
      'uppercase server': _with(
        _manifest(),
        'serverInstanceId',
        _serverInstanceId.value.toUpperCase(),
      ),
      'malformed release': _with(_manifest(), 'releaseId', 'bad-release'),
      'malformed title': _with(_manifest(), 'titleId', 'bad-title'),
      'malformed platform': _with(_manifest(), 'platformId', 'bad-platform'),
      'empty short name': _with(_manifest(), 'systemKey', '  '),
      'empty name': _with(_manifest(), 'name', ''),
      'wrong revision type': _with(_manifest(), 'revision', 1),
      'wrong complete type': _with(_manifest(), 'isComplete', 1),
      'wrong runtime type': _with(_manifest(), 'runtime', <Object?>[]),
      'wrong items type': _with(_manifest(), 'items', <String, Object?>{}),
    };

    for (final entry in cases.entries) {
      final result = await _resultFor(responseBody: entry.value);
      _expectFailure(
        result,
        ReleaseManifestFailureKind.malformedResponse,
        reason: entry.key,
      );
    }

    final truncated = await _resultFor(rawResponseBody: '{"serverInstanceId":');
    _expectFailure(
      truncated,
      ReleaseManifestFailureKind.malformedResponse,
      reason: 'truncated JSON',
    );
  });

  test('rejects malformed runtime and launch shapes atomically', () async {
    final cases = <String, Map<String, Object?>>{};

    for (final key in <String>[
      'contentType',
      'packaging',
      'minimumInstallBytes',
    ]) {
      final value = _manifest();
      _runtime(value).remove(key);
      cases['runtime missing $key'] = value;
    }

    final runtimeExtra = _manifest();
    _runtime(runtimeExtra)['extra'] = true;
    cases['runtime extra'] = runtimeExtra;

    final unsupportedContentType = _manifest();
    _runtime(unsupportedContentType)['contentType'] = 'archive';
    cases['unsupported content type'] = unsupportedContentType;

    final unsupportedPackaging = _manifest();
    _runtime(unsupportedPackaging)['packaging'] = 'zip';
    cases['unsupported packaging'] = unsupportedPackaging;

    for (final key in <String>['contentType', 'packaging']) {
      final wrongType = _manifest();
      _runtime(wrongType)[key] = 1;
      cases['runtime $key type'] = wrongType;
      final empty = _manifest();
      _runtime(empty)[key] = ' ';
      cases['runtime $key empty'] = empty;
    }

    for (final numeric in <Object?>[
      -1,
      '-1',
      '01',
      '+1',
      '1.0',
      1.5,
      '9223372036854775808',
    ]) {
      final value = _manifest();
      _runtime(value)['minimumInstallBytes'] = numeric;
      cases['minimum int64 $numeric'] = value;
    }

    final launchWrongType = _manifest();
    _runtime(launchWrongType)['launch'] = 'file';
    cases['launch wrong type'] = launchWrongType;

    for (final key in <String>['type', 'relativePath']) {
      final value = _manifest();
      _launch(value).remove(key);
      cases['launch missing $key'] = value;
    }

    final launchExtra = _manifest();
    _launch(launchExtra)['extra'] = true;
    cases['launch extra'] = launchExtra;

    final launchEmptyType = _manifest();
    _launch(launchEmptyType)['type'] = ' ';
    cases['launch empty type'] = launchEmptyType;

    final unsupportedLaunchType = _manifest();
    _launch(unsupportedLaunchType)['type'] = 'command';
    cases['unsupported launch type'] = unsupportedLaunchType;

    final singleWithoutLaunch = _manifest();
    _runtime(singleWithoutLaunch)['launch'] = null;
    cases['single item without launch'] = singleWithoutLaunch;

    final singleWithMultipleItems = _manifest();
    final secondSingleItem = _copyMap(_item(singleWithMultipleItems));
    secondSingleItem['relativePath'] = 'chrono/second.sfc';
    singleWithMultipleItems['items'] = <Object?>[
      _copyMap(_item(singleWithMultipleItems)),
      secondSingleItem,
    ];
    _runtime(singleWithMultipleItems)['minimumInstallBytes'] = 24;
    cases['single content type with multiple items'] = singleWithMultipleItems;

    final unknownWithSingleItem = _manifest();
    _runtime(unknownWithSingleItem)
      ..['contentType'] = 'unknown'
      ..['launch'] = null;
    cases['unknown content type with one item'] = unknownWithSingleItem;

    for (final path in <String>[
      '',
      '/absolute.rom',
      r'C:\game.rom',
      r'dir\game.rom',
      '../game.rom',
      'dir/../game.rom',
      'dir//game.rom',
      'dir/./game.rom',
      'dir/\u0000game.rom',
      'NUL.rom',
      'dir/AUX',
      'name?.rom',
      'name.',
    ]) {
      final value = _manifest();
      _launch(value)['relativePath'] = path.replaceAll(r'\u0000', '\u0000');
      cases['unsafe launch $path'] = value;
    }

    final launchNotItem = _manifest();
    _launch(launchNotItem)['relativePath'] = 'different.rom';
    cases['launch not an item'] = launchNotItem;

    for (final entry in cases.entries) {
      final result = await _resultFor(responseBody: entry.value);
      _expectFailure(
        result,
        ReleaseManifestFailureKind.malformedResponse,
        reason: entry.key,
      );
    }
  });

  test(
    'rejects malformed items grants and cross-field inconsistencies',
    () async {
      final cases = <String, Map<String, Object?>>{};

      final nonObjectItem = _manifest();
      nonObjectItem['items'] = <Object?>['item'];
      cases['non-object item'] = nonObjectItem;

      final malformedSecondItem = _manifest();
      final secondItem = _copyMap(_item(malformedSecondItem));
      secondItem
        ..['relativePath'] = 'chrono/second.sfc'
        ..remove('role');
      malformedSecondItem['items'] = <Object?>[
        _copyMap(_item(malformedSecondItem)),
        secondItem,
      ];
      _runtime(malformedSecondItem)['minimumInstallBytes'] = 24;
      cases['malformed second item rejects entire list'] = malformedSecondItem;

      for (final key in <String>[
        'relativePath',
        'role',
        'sizeBytes',
        'isAvailable',
      ]) {
        final value = _manifest();
        _item(value).remove(key);
        cases['item missing $key'] = value;
      }

      final itemExtra = _manifest();
      _item(itemExtra)['extra'] = true;
      cases['item extra'] = itemExtra;

      final itemRoleEmpty = _manifest();
      _item(itemRoleEmpty)['role'] = ' ';
      cases['item empty role'] = itemRoleEmpty;

      final unsupportedRole = _manifest();
      _item(unsupportedRole)['role'] = 'bios';
      cases['unsupported item role'] = unsupportedRole;

      final itemAvailableType = _manifest();
      _item(itemAvailableType)['isAvailable'] = 1;
      cases['item availability type'] = itemAvailableType;

      for (final numeric in <Object?>[
        -1,
        '-1',
        '01',
        '+1',
        '1.0',
        1.5,
        '9223372036854775808',
      ]) {
        final value = _manifest();
        _item(value)['sizeBytes'] = numeric;
        cases['item int64 $numeric'] = value;
      }

      for (final path in <String>[
        '',
        '/absolute.rom',
        r'C:\game.rom',
        r'dir\game.rom',
        '../game.rom',
        'dir/../game.rom',
        'dir//game.rom',
        'dir/./game.rom',
        'NUL.rom',
        'dir/AUX',
        'name?.rom',
        'name.',
      ]) {
        final value = _manifest();
        _item(value)['relativePath'] = path;
        _launch(value)['relativePath'] = path;
        cases['unsafe item $path'] = value;
      }

      final duplicatePaths = _manifest();
      duplicatePaths['items'] = <Object?>[
        _copyMap(_item(duplicatePaths)),
        _copyMap(_item(duplicatePaths)),
      ];
      _runtime(duplicatePaths)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 24;
      cases['duplicate item paths'] = duplicatePaths;

      final caseCollision = _manifest();
      final caseVariant = _copyMap(_item(caseCollision));
      caseVariant['relativePath'] = 'CHRONO/GAME.SFC';
      caseCollision['items'] = <Object?>[
        _copyMap(_item(caseCollision)),
        caseVariant,
      ];
      _runtime(caseCollision)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 24;
      cases['portable case-only path collision'] = caseCollision;

      final fileDirectoryCollision = _manifest();
      final filePath = _copyMap(_item(fileDirectoryCollision));
      filePath['relativePath'] = 'content';
      final nestedPath = _copyMap(_item(fileDirectoryCollision));
      nestedPath['relativePath'] = 'CONTENT/game.sfc';
      fileDirectoryCollision['items'] = <Object?>[filePath, nestedPath];
      _runtime(fileDirectoryCollision)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 24;
      cases['portable file-directory collision'] = fileDirectoryCollision;

      final wrongShaType = _manifest();
      _item(wrongShaType)['sha256'] = 1;
      cases['sha type'] = wrongShaType;
      for (final sha in <String>[
        '',
        'aa',
        List<String>.filled(64, 'A').join(),
        List<String>.filled(64, 'g').join(),
      ]) {
        final value = _manifest();
        _item(value)['sha256'] = sha;
        cases['sha value $sha'] = value;
      }

      final availableNoSha = _manifest();
      _item(availableNoSha)['sha256'] = null;
      cases['available without sha'] = availableNoSha;

      final availableNoGrant = _manifest();
      _item(availableNoGrant)['contentGrant'] = null;
      cases['available without grant'] = availableNoGrant;

      final unavailableSha = _unavailableManifest();
      _item(unavailableSha)['sha256'] = _sha256;
      cases['unavailable with sha'] = unavailableSha;

      final unavailableGrant = _unavailableManifest();
      _item(unavailableGrant)['contentGrant'] = _grant();
      cases['unavailable with grant'] = unavailableGrant;

      final grantWrongType = _manifest();
      _item(grantWrongType)['contentGrant'] = 'grant';
      cases['grant wrong type'] = grantWrongType;

      for (final key in <String>['downloadUrl', 'expiresAt']) {
        final value = _manifest();
        _grantFrom(value).remove(key);
        cases['grant missing $key'] = value;
      }

      final grantExtra = _manifest();
      _grantFrom(grantExtra)['extra'] = true;
      cases['grant extra'] = grantExtra;

      for (final url in <Object?>[
        1,
        '',
        'https://other.test/delivery/content/payload.signature',
        '//other.test/delivery/content/payload.signature',
        '/delivery/content/',
        '/delivery/content/payload.signature/extra',
        '/delivery/content/payload.signature?query=1',
        '/delivery/content/payload.signature#fragment',
        '/delivery/bios/payload.signature',
      ]) {
        final value = _manifest();
        _grantFrom(value)['downloadUrl'] = url;
        cases['grant URL $url'] = value;
      }

      for (final expiry in <Object?>[
        1,
        '',
        'not-a-date',
        '2026-02-31T12:00:00+00:00',
        '2026-07-17T25:00:00+00:00',
        '2026-07-17T12:00:00+15:00',
        '2026-07-17T12:00:00+14:01',
        '2026-07-17T12:00:00',
        '2026-07-17',
      ]) {
        final value = _manifest();
        _grantFrom(value)['expiresAt'] = expiry;
        cases['grant expiry $expiry'] = value;
      }

      final byteMismatch = _manifest();
      _runtime(byteMismatch)['minimumInstallBytes'] = '13';
      cases['minimum bytes mismatch'] = byteMismatch;

      final sumOverflow = _manifest();
      final first = _copyMap(_item(sumOverflow));
      first['relativePath'] = 'first.rom';
      final second = _copyMap(_item(sumOverflow));
      second['relativePath'] = 'second.rom';
      first['sizeBytes'] = '9223372036854775807';
      second['sizeBytes'] = '1';
      sumOverflow['items'] = <Object?>[first, second];
      _runtime(sumOverflow)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = '9223372036854775807';
      cases['item sum overflow'] = sumOverflow;

      for (final entry in cases.entries) {
        final result = await _resultFor(responseBody: entry.value);
        _expectFailure(
          result,
          ReleaseManifestFailureKind.malformedResponse,
          reason: entry.key,
        );
      }
    },
  );

  test(
    'defers Unicode normalization collisions to Gate 2 filesystem preflight',
    () async {
      final value = _manifest();
      final composed = _copyMap(_item(value));
      composed['relativePath'] = 'café.sfc';
      final decomposed = _copyMap(_item(value));
      decomposed['relativePath'] = 'cafe\u0301.sfc';
      value['items'] = <Object?>[composed, decomposed];
      _runtime(value)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 24;

      final result = await _resultFor(responseBody: value);

      final manifest = _expectSuccess(result);
      expect(manifest.items, hasLength(2));
      expect(manifest.items.map((item) => item.relativePath).toSet(), <String>{
        'café.sfc',
        'cafe\u0301.sfc',
      });
    },
  );

  test(
    'reports valid foreign expected identities as identity mismatch',
    () async {
      final cases = <String, Map<String, Object?>>{
        'server': _with(
          _manifest(),
          'serverInstanceId',
          _foreignServerInstanceId.value,
        ),
        'release': _with(_manifest(), 'releaseId', _foreignReleaseId.value),
        'title': _with(_manifest(), 'titleId', _foreignTitleId.value),
        'short name': _with(_manifest(), 'systemKey', 'psx'),
      };

      for (final entry in cases.entries) {
        final result = await _resultFor(responseBody: entry.value);
        _expectFailure(
          result,
          ReleaseManifestFailureKind.identityMismatch,
          reason: entry.key,
        );
      }
    },
  );

  test(
    'classifies specified HTTP failures without parsing manifest data',
    () async {
      const cases = <int, ReleaseManifestFailureKind>{
        HttpStatus.badRequest: ReleaseManifestFailureKind.badRequest,
        HttpStatus.unauthorized: ReleaseManifestFailureKind.unauthorized,
        HttpStatus.notFound: ReleaseManifestFailureKind.notFound,
        HttpStatus.conflict: ReleaseManifestFailureKind.conflict,
        HttpStatus.forbidden: ReleaseManifestFailureKind.clientError,
        HttpStatus.internalServerError: ReleaseManifestFailureKind.serverError,
        HttpStatus.badGateway: ReleaseManifestFailureKind.serverError,
        HttpStatus.created: ReleaseManifestFailureKind.unexpectedStatus,
      };

      for (final entry in cases.entries) {
        final harness = await _ManifestHarness.start(
          statusCode: entry.key,
          responseBody: _manifest(),
          challenge: entry.key == HttpStatus.unauthorized,
        );
        try {
          final result = await harness.getManifest();
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
    },
  );

  test('preserves invalid_token bearer challenge for replay', () async {
    final harness = await _ManifestHarness.start(
      statusCode: HttpStatus.unauthorized,
      responseBody: <String, Object?>{},
      bearerChallenge: 'Bearer error="invalid_token"',
    );
    addTearDown(harness.close);

    final result = await harness.getManifest();

    expect(result, isA<ReleaseManifestFailure>());
    expect((result as ReleaseManifestFailure).isInvalidToken, isTrue);
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
    final source = await _bindServer((request) async {
      await request.drain<void>();
      request.response
        ..statusCode = HttpStatus.seeOther
        ..headers.set(
          HttpHeaders.locationHeader,
          _originFor(target).resolve('/target').toString(),
        );
      await request.response.close();
    });
    final client = HttpReleaseManifestApiClient(
      consumerApiOrigin: _originFor(source),
    );
    addTearDown(() async {
      client.close();
      await source.close(force: true);
    });

    final result = await _getManifest(client);

    _expectFailure(
      result,
      ReleaseManifestFailureKind.redirect,
      statusCode: HttpStatus.seeOther,
    );
    expect(targetRequests, 0);
  });

  test('classifies transport failure without a manifest', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final origin = _originFor(server);
    await server.close(force: true);
    final client = HttpReleaseManifestApiClient(consumerApiOrigin: origin);
    addTearDown(client.close);

    final result = await _getManifest(client);

    _expectFailure(result, ReleaseManifestFailureKind.transport);
  });

  test(
    'timeout aborts only its stalled operation while an overlap succeeds',
    () async {
      final server = await _RawStalledHttpServer.start(
        stalledBodyPrefix: '{"serverInstanceId":"',
        completeResponses: <String, String>{
          '/api/releases/${_foreignReleaseId.value}/manifest': jsonEncode(
            _manifest(
              releaseId: _foreignReleaseId,
              titleId: _foreignTitleId,
              platformShortName: 'psx',
            ),
          ),
        },
      );
      final client = HttpReleaseManifestApiClient(
        consumerApiOrigin: server.origin,
        timeout: const Duration(milliseconds: 150),
      );
      addTearDown(() async {
        client.close();
        await server.close();
      });

      final stalledFuture = _getManifest(client);
      await server.stalledRequestReached.future;
      final overlap = await client.getReleaseManifest(
        accessToken: 'access-token',
        expectedServerInstanceId: _serverInstanceId,
        releaseId: _foreignReleaseId,
        expectedTitleId: _foreignTitleId,

        expectedSystemKey: 'psx',
      );
      final stalled = await stalledFuture;

      expect(overlap, isA<ReleaseManifestSuccess>());
      _expectFailure(stalled, ReleaseManifestFailureKind.timeout);
      await server.stalledClientDisconnected.future.timeout(
        const Duration(seconds: 2),
      );
    },
  );

  test('close aborts an active stalled manifest operation', () async {
    final server = await _RawStalledHttpServer.start(
      stalledBodyPrefix: '{"serverInstanceId":"',
    );
    final client = HttpReleaseManifestApiClient(
      consumerApiOrigin: server.origin,
      timeout: const Duration(seconds: 5),
    );
    addTearDown(() async {
      client.close();
      await server.close();
    });

    final resultFuture = _getManifest(client);
    await server.stalledRequestReached.future;
    client.close();

    final result = await resultFuture;
    _expectFailure(result, ReleaseManifestFailureKind.transport);
    await server.stalledClientDisconnected.future.timeout(
      const Duration(seconds: 2),
    );
  });

  test('close aborts a manifest request while it is opening', () async {
    var requestCount = 0;
    final server = await _bindServer((request) async {
      requestCount++;
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    final client = HttpReleaseManifestApiClient(
      consumerApiOrigin: _originFor(server),
      timeout: const Duration(seconds: 5),
    );
    addTearDown(() async {
      client.close();
      await server.close(force: true);
    });

    final resultFuture = _getManifest(client);
    client.close();

    final result = await resultFuture;
    _expectFailure(result, ReleaseManifestFailureKind.transport);
    expect(requestCount, 0);
  });

  test('manifest request after close is a typed transport failure', () async {
    final server = await _bindServer((request) async {
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    final client = HttpReleaseManifestApiClient(
      consumerApiOrigin: _originFor(server),
    );
    addTearDown(() async {
      client.close();
      await server.close(force: true);
    });
    client.close();

    final result = await _getManifest(client);

    _expectFailure(result, ReleaseManifestFailureKind.transport);
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

const _sha256 =
    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const _grantUrl = '/delivery/content/payload.signature';

Map<String, Object?> _manifest({
  RomdPublicId? releaseId,
  RomdPublicId? titleId,
  String platformShortName = 'snes',
}) => <String, Object?>{
  'serverInstanceId': _serverInstanceId.value,
  'releaseId': (releaseId ?? _releaseId).value,
  'titleId': (titleId ?? _titleId).value,

  'systemKey': platformShortName,
  'name': 'Chrono Trigger',
  'revision': 'Rev 1',
  'isComplete': true,
  'runtime': <String, Object?>{
    'contentType': 'single_rom',
    'launch': <String, Object?>{
      'type': 'file',
      'relativePath': 'chrono/game.sfc',
    },
    'packaging': 'direct_files',
    'minimumInstallBytes': 12,
  },
  'items': <Object?>[
    <String, Object?>{
      'relativePath': 'chrono/game.sfc',
      'role': 'rom',
      'sizeBytes': 12,
      'sha256': _sha256,
      'isAvailable': true,
      'contentGrant': _grant(),
    },
  ],
};

Map<String, Object?> _unavailableManifest() {
  final value = _manifest();
  _item(value)
    ..['sha256'] = null
    ..['isAvailable'] = false
    ..['contentGrant'] = null;
  return value;
}

Map<String, Object?> _grant() => <String, Object?>{
  'downloadUrl': _grantUrl,
  'expiresAt': '2026-07-17T12:00:00+00:00',
};

Map<String, Object?> _runtime(Map<String, Object?> value) =>
    value['runtime']! as Map<String, Object?>;

Map<String, Object?> _launch(Map<String, Object?> value) =>
    _runtime(value)['launch']! as Map<String, Object?>;

Map<String, Object?> _item(Map<String, Object?> value) =>
    (value['items']! as List<Object?>).single as Map<String, Object?>;

Map<String, Object?> _grantFrom(Map<String, Object?> value) =>
    _item(value)['contentGrant']! as Map<String, Object?>;

Map<String, Object?> _without(Map<String, Object?> value, String key) {
  value.remove(key);
  return value;
}

Map<String, Object?> _with(
  Map<String, Object?> value,
  String key,
  Object? replacement,
) {
  value[key] = replacement;
  return value;
}

Map<String, Object?> _copyMap(Map<String, Object?> value) =>
    jsonDecode(jsonEncode(value))! as Map<String, Object?>;

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

final class _ManifestHarness {
  _ManifestHarness._(this.server, this.client, this.requests);

  final HttpServer server;
  final HttpReleaseManifestApiClient client;
  final List<_RecordedRequest> requests;

  static Future<_ManifestHarness> start({
    int statusCode = HttpStatus.ok,
    Object? responseBody,
    String? rawResponseBody,
    bool challenge = false,
    String? bearerChallenge,
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
      if (challenge) {
        request.response.headers.set(
          HttpHeaders.wwwAuthenticateHeader,
          'Basic realm="ROMD"',
        );
      }
      if (bearerChallenge != null) {
        request.response.headers.set(
          HttpHeaders.wwwAuthenticateHeader,
          bearerChallenge,
        );
      }
      request.response.write(rawResponseBody ?? jsonEncode(responseBody));
      await request.response.close();
    });
    return _ManifestHarness._(
      server,
      HttpReleaseManifestApiClient(consumerApiOrigin: _originFor(server)),
      requests,
    );
  }

  Future<ReleaseManifestResult> getManifest() => _getManifest(client);

  Future<void> close() async {
    client.close();
    await server.close(force: true);
  }
}

Future<ReleaseManifestResult> _resultFor({
  int statusCode = HttpStatus.ok,
  Object? responseBody,
  String? rawResponseBody,
}) async {
  final harness = await _ManifestHarness.start(
    statusCode: statusCode,
    responseBody: responseBody,
    rawResponseBody: rawResponseBody,
  );
  try {
    return await harness.getManifest();
  } finally {
    await harness.close();
  }
}

Future<ReleaseManifestResult> _getManifest(
  HttpReleaseManifestApiClient client,
) => client.getReleaseManifest(
  accessToken: 'access-token',
  expectedServerInstanceId: _serverInstanceId,
  releaseId: _releaseId,
  expectedTitleId: _titleId,

  expectedSystemKey: 'snes',
);

ServerBoundReleaseManifest _expectSuccess(ReleaseManifestResult result) {
  expect(result, isA<ReleaseManifestSuccess>());
  return (result as ReleaseManifestSuccess).manifest;
}

void _expectFailure(
  ReleaseManifestResult result,
  ReleaseManifestFailureKind kind, {
  int? statusCode,
  String? reason,
}) {
  expect(result, isA<ReleaseManifestFailure>(), reason: reason);
  final failure = result as ReleaseManifestFailure;
  expect(failure.kind, kind, reason: reason);
  expect(failure.statusCode, statusCode, reason: reason);
  expect(result, isNot(isA<ReleaseManifestSuccess>()), reason: reason);
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
