import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/device_authorization.dart';

void main() {
  test('sign-out revokes the server session with its refresh token', () async {
    Map<String, String>? form;
    final server = await _bindServer((request) async {
      expect(request.method, 'POST');
      expect(request.uri.path, '/connect/revocation');
      form = Uri.splitQueryString(await utf8.decoder.bind(request).join());
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    addTearDown(() => server.close(force: true));
    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);
    await client.revokeSession(refreshToken: 'session-refresh');
    expect(form?['token'], 'session-refresh');
    expect(form?['token_type_hint'], 'refresh_token');
    expect(form?['client_id'], 'romd-console');
  });

  test('getHealth returns healthy status from the consumer host', () async {
    final server = await _bindServer((request) async {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(jsonEncode(<String, Object?>{'status': 'Healthy'}));
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final health = await client.getHealth();

    expect(health.isHealthy, isTrue);
    expect(health.status, 'Healthy');
  });

  test('getHealth returns unavailable for non-OK responses', () async {
    final server = await _bindServer((request) async {
      request.response.statusCode = HttpStatus.internalServerError;
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final health = await client.getHealth();

    expect(health.isHealthy, isFalse);
    expect(health.status, 'Unavailable');
    expect(health.reason, 'HTTP 500');
  });

  test('searchCatalog parses consumer title cards', () async {
    final server = await _bindServer((request) async {
      expect(request.method, 'GET');
      expect(request.uri.path, '/api/catalog');
      expect(request.uri.queryParameters['limit'], '48');
      expect(request.uri.queryParameters['systemKey'], 'future-console');
      expect(request.uri.queryParameters.containsKey('platformId'), isFalse);
      expect(
        request.headers.value(HttpHeaders.authorizationHeader),
        'Bearer access-token',
      );

      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, Object?>{
            'items': <Object?>[
              <String, Object?>{
                'id': 'title-1',
                'system': {
                  'key': 'platform-1',
                  'name': 'SNES',
                  'compactLabel': 'SNES',
                },
                'name': 'Chrono Trigger',
                'coverUrl': '/media/ignored-legacy',
                'artwork': [
                  {
                    'role': 'Poster',
                    'url': '/media/cover-1',
                    'fit': 'Contain',
                    'fallbackReason': 'LegacyCover',
                    'variants': <Object?>[],
                  },
                ],
                'genre': 'RPG',
                'releaseDate': '1995-03-11',
                'rating': 9.4,
                'releaseCount': 1,
                'defaultReleaseId': 'release-1',
              },
            ],
            'nextCursor': 'cursor-2',
            'hasNextPage': true,
          }),
        );
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final page = await client.searchCatalog(
      accessToken: 'access-token',
      platformId: 'future-console',
    );

    expect(page.items, hasLength(1));
    expect(page.nextCursor, 'cursor-2');
    expect(page.hasNextPage, isTrue);
    expect(page.items.single.title, 'Chrono Trigger');
    expect(page.items.single.platformName, 'SNES');
    expect(page.items.single.releaseYear, 1995);
    expect(page.items.single.coverUrl?.path, '/media/cover-1');
    expect(page.items.single.canLaunch, isTrue);
  });

  test('catalog 401 preserves the invalid_token bearer challenge', () async {
    final server = await _bindServer((request) async {
      request.response
        ..statusCode = HttpStatus.unauthorized
        ..headers.set(
          HttpHeaders.wwwAuthenticateHeader,
          'Bearer realm="ROMD", error="invalid_token"',
        );
      await request.response.close();
    });
    addTearDown(() => server.close(force: true));
    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    expect(
      client.searchCatalog(accessToken: 'expired-token'),
      throwsA(
        isA<ConsumerApiException>()
            .having((error) => error.statusCode, 'status', 401)
            .having(
              (error) => error.bearerError,
              'bearer error',
              'invalid_token',
            )
            .having((error) => error.isInvalidToken, 'invalid token', isTrue),
      ),
    );
  });

  test('getTitle parses consumer title details', () async {
    final server = await _bindServer((request) async {
      expect(request.method, 'GET');
      expect(request.uri.path, '/api/titles/title-1');
      expect(
        request.headers.value(HttpHeaders.authorizationHeader),
        'Bearer access-token',
      );

      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, Object?>{
            'id': 'title-1',
            'system': {
              'key': 'platform-1',
              'name': 'SNES',
              'compactLabel': 'SNES',
            },
            'name': 'Chrono Trigger',
            'description': 'A time-spanning RPG adventure.',
            'publisher': 'Square',
            'developer': 'Square',
            'genre': 'RPG',
            'releaseDate': '1995-03-11',
            'players': 1,
            'rating': 9.4,
            'media': <Object?>[
              <String, Object?>{
                'id': 'media-1',
                'type': 'cover',
                'url': '/media/media-1',
                'isPrimary': true,
              },
            ],
            'releases': <Object?>[
              <String, Object?>{
                'id': 'release-1',
                'name': 'Chrono Trigger',
                'revision': null,
                'regions': <String>['USA'],
                'languages': <String>['English'],
                'sizeBytes': 4194304,
                'isComplete': true,
              },
            ],
            'defaultReleaseId': 'release-1',
          }),
        );
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final detail = await client.getTitle(
      accessToken: 'access-token',
      titleId: 'title-1',
    );

    expect(detail.title, 'Chrono Trigger');
    expect(detail.description, 'A time-spanning RPG adventure.');
    expect(detail.media.single.url.path, '/media/media-1');
    expect(detail.releases.single.isComplete, isTrue);
    expect(detail.releases.single.regions, <String>['USA']);
  });

  test('listCollections parses curated collection summaries', () async {
    final server = await _bindServer((request) async {
      expect(request.method, 'GET');
      expect(request.uri.path, '/api/collections');
      expect(
        request.headers.value(HttpHeaders.authorizationHeader),
        'Bearer access-token',
      );

      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<Object?>[
            <String, Object?>{
              'id': 'collection-1',
              'name': 'Favorites',
              'description': 'Curated RPG picks',
              'system': null,
              'coverUrl': '/media/collection-cover',
              'heroUrl': null,
              'itemCount': 12,
              'isFeatured': false,
            },
          ]),
        );
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final collections = await client.listCollections(
      accessToken: 'access-token',
    );

    expect(collections, hasLength(1));
    expect(collections.single.name, 'Favorites');
    expect(collections.single.coverUrl?.path, '/media/collection-cover');
    expect(collections.single.itemCount, 12);
    expect(collections.single.isFeatured, isFalse);
  });

  test('listCollectionTitles parses paged collection titles', () async {
    final server = await _bindServer((request) async {
      expect(request.method, 'GET');
      expect(request.uri.path, '/api/collections/collection-1/titles');
      expect(request.uri.queryParameters['limit'], '24');

      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, Object?>{
            'items': <Object?>[
              <String, Object?>{
                'id': 'title-1',
                'system': {
                  'key': 'platform-1',
                  'name': 'SNES',
                  'compactLabel': 'SNES',
                },
                'name': 'Chrono Trigger',
                'coverUrl': null,
                'genre': 'RPG',
                'releaseDate': '1995-03-11',
                'rating': 9.4,
                'releaseCount': 1,
                'defaultReleaseId': 'release-1',
              },
            ],
            'nextCursor': null,
            'hasNextPage': false,
          }),
        );
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final page = await client.listCollectionTitles(
      accessToken: 'access-token',
      collectionId: 'collection-1',
    );

    expect(page.items.single.title, 'Chrono Trigger');
    expect(page.hasNextPage, isFalse);
  });

  test('issueReleaseManifest posts to the release manifest endpoint', () async {
    final server = await _bindServer((request) async {
      expect(request.method, 'POST');
      expect(request.uri.path, '/api/releases/release-1/manifest');
      expect(
        request.headers.value(HttpHeaders.authorizationHeader),
        'Bearer access-token',
      );

      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, Object?>{
            'releaseId': 'release-1',
            'titleId': 'title-1',

            'systemKey': 'snes',
            'name': 'Chrono Trigger',
            'revision': null,
            'isComplete': true,
            'runtime': <String, Object?>{
              'contentType': 'single_rom',
              'launch': <String, Object?>{
                'type': 'file',
                'relativePath': 'chrono.sfc',
              },
              'packaging': 'direct_files',
              'minimumInstallBytes': 4194304,
            },
            'items': <Object?>[
              <String, Object?>{
                'relativePath': 'chrono.sfc',
                'role': 'rom',
                'sizeBytes': 4194304,
                'sha256': 'abc123',
                'isAvailable': true,
                'contentGrant': <String, Object?>{
                  'downloadUrl': '/delivery/content/token-1',
                  'expiresAt': '2026-01-01T12:00:00Z',
                },
              },
            ],
          }),
        );
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final manifest = await client.issueReleaseManifest(
      accessToken: 'access-token',
      releaseId: 'release-1',
    );

    expect(manifest.releaseId, 'release-1');
    expect(manifest.systemKey, 'snes');
    expect(manifest.runtime.contentType, 'single_rom');
    expect(manifest.runtime.launch?.type, 'file');
    expect(manifest.runtime.launch?.relativePath, 'chrono.sfc');
    expect(manifest.runtime.packaging, 'direct_files');
    expect(manifest.runtime.minimumInstallBytes, 4194304);
    expect(manifest.availableItemCount, 1);
    expect(manifest.items.single.role, 'rom');
    expect(
      manifest.items.single.downloadUrl?.path,
      '/delivery/content/token-1',
    );
  });

  test('listPlatformBios parses listings with normalized hashes', () async {
    final server = await _bindServer((request) async {
      expect(request.method, 'GET');
      expect(request.uri.path, '/api/systems/psx/bios');
      expect(
        request.headers.value(HttpHeaders.authorizationHeader),
        'Bearer access-token',
      );

      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, Object?>{
            'systemKey': 'psx',
            'items': <Object?>[
              <String, Object?>{
                'biosId': 'bios-1',
                'name': 'PS1 BIOS (JP)',
                'fileName': 'scph5500.bin',
                'sizeBytes': 524288,
                'sha1': null,
                'md5': '8DD7D5296A650FAC7319BCE665A6A53C',
                'sha256': null,
                'isAvailable': true,
                'contentGrant': <String, Object?>{
                  'downloadUrl': '/delivery/content/token-9',
                  'expiresAt': '2026-01-01T12:00:00Z',
                },
              },
              <String, Object?>{
                'biosId': 'bios-2',
                'name': 'PS1 BIOS (NA)',
                'fileName': 'scph5501.bin',
                'sizeBytes': 524288,
                'sha1': null,
                'md5': null,
                'sha256': null,
                'isAvailable': false,
                'contentGrant': null,
              },
            ],
          }),
        );
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final listings = await client.listPlatformBios(
      accessToken: 'access-token',
      platformShortName: 'psx',
    );

    expect(listings, hasLength(2));
    expect(listings[0].fileName, 'scph5500.bin');
    expect(listings[0].md5, '8dd7d5296a650fac7319bce665a6a53c');
    expect(listings[0].isAvailable, isTrue);
    expect(listings[0].downloadUrl?.path, '/delivery/content/token-9');
    expect(listings[1].isAvailable, isFalse);
    expect(listings[1].md5, isNull);
    expect(listings[1].downloadUrl, isNull);
  });

  test('requestDeviceAuthorization parses the device authorization', () async {
    final now = DateTime(2026, 1, 1, 12);
    final server = await _bindServer((request) async {
      expect(request.method, 'POST');
      expect(request.uri.path, '/connect/device');

      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, Object?>{
            'device_code': 'device-code-1',
            'user_code': 'WXYZ-1234',
            'verification_uri': 'http://localhost/connect/verify',
            'interval': 5,
            'expires_in': 600,
          }),
        );
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(
      consumerApiOrigin: _originFor(server),
      now: () => now,
    );
    addTearDown(client.close);

    final result = await client.requestDeviceAuthorization();

    final authorization = switch (result) {
      DeviceAuthorizationSuccess(:final authorization) => authorization,
      DeviceAuthorizationFailure(:final message) => fail(message),
    };
    expect(authorization.deviceCode, 'device-code-1');
    expect(authorization.userCode, 'WXYZ-1234');
    expect(authorization.verificationUri.path, '/connect/verify');
    expect(authorization.interval, const Duration(seconds: 5));
    expect(authorization.expiresAt, now.add(const Duration(seconds: 600)));
  });

  test('redeemDeviceCode reports pending while awaiting approval', () async {
    final server = await _bindServer((request) async {
      request.response
        ..statusCode = HttpStatus.badRequest
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, Object?>{'error': 'authorization_pending'}),
        );
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(consumerApiOrigin: _originFor(server));
    addTearDown(client.close);

    final result = await client.redeemDeviceCode(deviceCode: 'device-code-1');

    expect(result, isA<DeviceTokenPending>());
  });

  test(
    'refreshSession classifies terminal and transient OAuth failures',
    () async {
      final cases = <(int, String, ConsumerLoginFailureKind)>[
        (
          HttpStatus.badRequest,
          'invalid_grant',
          ConsumerLoginFailureKind.invalidGrant,
        ),
        (
          HttpStatus.badRequest,
          'temporarily_unavailable',
          ConsumerLoginFailureKind.transient,
        ),
        (
          HttpStatus.tooManyRequests,
          'rate_limited',
          ConsumerLoginFailureKind.transient,
        ),
        (
          HttpStatus.serviceUnavailable,
          'server_error',
          ConsumerLoginFailureKind.transient,
        ),
        (
          HttpStatus.badRequest,
          'invalid_client',
          ConsumerLoginFailureKind.protocol,
        ),
      ];

      for (final (status, error, expectedKind) in cases) {
        final server = await _bindServer((request) async {
          request.response
            ..statusCode = status
            ..headers.contentType = ContentType.json
            ..write(jsonEncode(<String, Object?>{'error': error}));
          await request.response.close();
        });
        final client = RomdConsumerApiClient(
          consumerApiOrigin: _originFor(server),
        );
        try {
          final result = await client.refreshSession(
            refreshToken: 'refresh-token',
          );

          expect(result, isA<ConsumerLoginFailure>(), reason: error);
          expect(
            (result as ConsumerLoginFailure).kind,
            expectedKind,
            reason: error,
          );
        } finally {
          client.close();
          await server.close(force: true);
        }
      }
    },
  );

  test('redeemDeviceCode returns a session with the linked account', () async {
    final now = DateTime(2026, 1, 1, 12);
    final server = await _bindServer((request) async {
      switch (request.uri.path) {
        case '/connect/token':
          request.response
            ..statusCode = HttpStatus.ok
            ..headers.contentType = ContentType.json
            ..write(
              jsonEncode(<String, Object?>{
                'access_token': 'access-token',
                'token_type': 'Bearer',
                'refresh_token': 'refresh-token',
                'expires_in': 900,
              }),
            );
        case '/api/me':
          expect(
            request.headers.value(HttpHeaders.authorizationHeader),
            'Bearer access-token',
          );
          request.response
            ..statusCode = HttpStatus.ok
            ..headers.contentType = ContentType.json
            ..write(
              jsonEncode(<String, Object?>{
                'id': 'romd-user-1',
                'email': 'player@example.com',
                'username': 'player',
                'roles': <String>['User'],
              }),
            );
        default:
          request.response.statusCode = HttpStatus.notFound;
      }
      await request.response.close();
    });
    addTearDown(() async {
      await server.close(force: true);
    });

    final client = RomdConsumerApiClient(
      consumerApiOrigin: _originFor(server),
      now: () => now,
    );
    addTearDown(client.close);

    final result = await client.redeemDeviceCode(deviceCode: 'device-code-1');

    final session = switch (result) {
      DeviceTokenSuccess(:final session) => session,
      _ => fail('Expected a successful device token result, got $result'),
    };
    expect(session.token, 'access-token');
    expect(session.refreshToken, 'refresh-token');
    expect(session.expiresAt, now.add(const Duration(seconds: 900)));
    expect(session.account.id, 'romd-user-1');
    expect(session.account.username, 'player');
    expect(session.account.email, 'player@example.com');
  });
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
