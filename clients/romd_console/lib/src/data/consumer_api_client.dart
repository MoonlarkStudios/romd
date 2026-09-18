import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/session/domain/play_activity.dart';

import '../domain/console_game.dart';
import '../domain/consumer_account.dart';
import '../domain/consumer_host_health.dart';
import '../domain/device_authorization.dart';
import 'reference_catalog.dart';

abstract interface class ConsumerApiClient {
  Future<ConsumerHostHealth> getHealth();

  Future<ConsoleGamePage> searchCatalog({
    required String accessToken,
    String? query,
    String? platformId,
    String? completeness,
    String? sortBy,
    String? cursor,
    int limit = 48,
  });

  Future<ConsolePlatformPage> listPlatforms({
    required String accessToken,
    String? cursor,
    int limit = 50,
  });

  Future<List<ConsoleCollection>> listCollections({
    required String accessToken,
    String? platformId,
  });

  Future<ConsoleGamePage> listCollectionTitles({
    required String accessToken,
    required String collectionId,
    String? cursor,
    int limit = 24,
  });

  Future<ConsoleGameDetail> getTitle({
    required String accessToken,
    required String titleId,
  });

  Future<ConsoleReleaseManifest> issueReleaseManifest({
    required String accessToken,
    required String releaseId,
  });

  /// Lists the BIOS files the server knows for a platform, with content
  /// grants for the available ones.
  Future<List<BiosFileListing>> listPlatformBios({
    required String accessToken,
    required String platformShortName,
  });

  /// Starts the OpenIddict device authorization flow for the console client.
  Future<DeviceAuthorizationResult> requestDeviceAuthorization();

  /// Polls the token endpoint once for a pending device authorization.
  Future<DeviceTokenResult> redeemDeviceCode({required String deviceCode});

  /// Exchanges a stored refresh token for a new session (used to restore sessions).
  Future<ConsumerLoginResult> refreshSession({required String refreshToken});

  Future<void> revokeSession({required String refreshToken});

  void close();
}

abstract interface class PlayActivityApiClient {
  Future<void> upsertPlaySession({
    required String accessToken,
    required LocalPlaySession session,
  });
}

final class RomdConsumerApiClient
    implements
        ConsumerApiClient,
        PlayActivityApiClient,
        ReferenceCatalogApiClient {
  RomdConsumerApiClient({
    required Uri consumerApiOrigin,
    HttpClient? httpClient,
    DateTime Function()? now,
  }) : _consumerApiOrigin = consumerApiOrigin,
       _httpClient = httpClient ?? HttpClient(),
       _now = now ?? DateTime.now;

  @override
  Future<ReferenceCatalog?> getReferenceCatalog(
    String token, {
    String? revision,
  }) async {
    final request = await _httpClient.getUrl(
      _consumerApiOrigin.resolve('/api/catalog-snapshot'),
    );
    request.headers.set(HttpHeaders.authorizationHeader, 'Bearer $token');
    if (revision != null)
      request.headers.set(HttpHeaders.ifNoneMatchHeader, '"$revision"');
    final response = await request.close();
    if (response.statusCode == 304) {
      await response.drain<void>();
      return null;
    }
    if (response.statusCode != 200) {
      await response.drain<void>();
      throw const HttpException('Reference catalog unavailable');
    }
    final bytes = await readReferenceResponse(response, 4 * 1024 * 1024);
    return ReferenceCatalog(
      jsonDecode(utf8.decode(bytes)) as Map<String, dynamic>,
    );
  }

  @override
  Future<Uint8List> getReferenceAsset(String url) async {
    final uri = _consumerApiOrigin.resolve(url);
    if (uri.scheme != 'http' && uri.scheme != 'https')
      throw const FormatException('Invalid reference asset URL');
    // Display assets are public. Never forward account tokens to asset origins.
    final request = await _httpClient.getUrl(uri);
    final response = await request.close();
    if (response.statusCode != 200) {
      await response.drain<void>();
      if (response.statusCode == 404) throw const ReferenceAssetUnavailable();
      throw const HttpException('Reference asset unavailable');
    }
    return readReferenceResponse(response, 2 * 1024 * 1024);
  }

  static const _clientId = 'romd-console';
  static const _scope = 'offline_access email profile roles';

  final Uri _consumerApiOrigin;
  final HttpClient _httpClient;
  final DateTime Function() _now;

  @override
  Future<ConsumerHostHealth> getHealth() async {
    try {
      final request = await _httpClient.getUrl(
        _consumerApiOrigin.resolve('/health'),
      );
      request.headers.set(HttpHeaders.acceptHeader, 'application/json');

      final response = await request.close();
      final body = await utf8.decodeStream(response);

      if (response.statusCode != HttpStatus.ok) {
        return ConsumerHostHealth.unavailable('HTTP ${response.statusCode}');
      }

      final decoded = jsonDecode(body);
      if (decoded case <String, Object?>{'status': final String status}) {
        return ConsumerHostHealth(status: status);
      }

      return const ConsumerHostHealth.unavailable('Unexpected health response');
    } on Object catch (error) {
      return ConsumerHostHealth.unavailable(error.toString());
    }
  }

  @override
  Future<ConsoleGamePage> searchCatalog({
    required String accessToken,
    String? query,
    String? platformId,
    String? completeness,
    String? sortBy,
    String? cursor,
    int limit = 48,
  }) async {
    final decoded = await _getJson(
      '/api/catalog',
      accessToken: accessToken,
      queryParameters: <String, String>{
        if (query != null && query.trim().isNotEmpty) 'query': query.trim(),
        if (platformId != null) 'systemKey': platformId,
        if (completeness != null) 'completeness': completeness,
        if (sortBy != null) 'sortBy': sortBy,
        if (cursor != null) 'cursor': cursor,
        'limit': limit.toString(),
      },
    );

    if (decoded case <String, Object?>{
      'items': final List<Object?> items,
      'hasNextPage': final bool hasNextPage,
    }) {
      return ConsoleGamePage(
        items: items
            .map(_readCatalogCard)
            .whereType<ConsoleGame>()
            .toList(growable: false),
        nextCursor: _readString(decoded['nextCursor']),
        hasNextPage: hasNextPage,
      );
    }

    throw const ConsumerApiException('Unexpected catalog response.');
  }

  @override
  Future<void> upsertPlaySession({
    required String accessToken,
    required LocalPlaySession session,
  }) async {
    final request = await _httpClient.putUrl(
      _consumerApiOrigin.resolve(
        '/api/me/activity/play-sessions/${Uri.encodeComponent(session.sessionId)}',
      ),
    );
    request.headers
      ..set(HttpHeaders.acceptHeader, 'application/json')
      ..set(HttpHeaders.authorizationHeader, 'Bearer $accessToken')
      ..set(HttpHeaders.contentTypeHeader, 'application/json');
    request.write(
      jsonEncode(<String, Object?>{
        'clientId': session.clientId,
        'titleId': session.titleId,
        'releaseId': session.releaseId,
        'startedAt': session.startedAt.toUtc().toIso8601String(),
        'endedAt': session.endedAt?.toUtc().toIso8601String(),
        'activeDurationSeconds': session.activeDurationSeconds,
      }),
    );
    final response = await request.close();
    await response.drain<void>();
    if (response.statusCode != HttpStatus.ok) {
      throw ConsumerApiException.http(
        response.statusCode,
        bearerError: _readBearerError(response.headers),
      );
    }
  }

  @override
  Future<ConsolePlatformPage> listPlatforms({
    required String accessToken,
    String? cursor,
    int limit = 50,
  }) async {
    final decoded = await _getJson(
      '/api/me/library/systems',
      accessToken: accessToken,
      queryParameters: <String, String>{
        if (cursor != null) 'cursor': cursor,
        'limit': limit.toString(),
      },
    );

    if (decoded case <String, Object?>{
      'items': final List<Object?> items,
      'hasNextPage': final bool hasNextPage,
    }) {
      return ConsolePlatformPage(
        items: items
            .map(_readPlatform)
            .whereType<ConsolePlatform>()
            .toList(growable: false),
        nextCursor: _readString(decoded['nextCursor']),
        hasNextPage: hasNextPage,
      );
    }

    throw const ConsumerApiException('Unexpected platforms response.');
  }

  @override
  Future<List<ConsoleCollection>> listCollections({
    required String accessToken,
    String? platformId,
  }) async {
    final decoded = await _getJson(
      '/api/collections',
      accessToken: accessToken,
      queryParameters: <String, String>{
        if (platformId != null) 'systemKey': platformId,
      },
    );

    if (decoded case final List<Object?> items) {
      return items
          .map(_readCollection)
          .whereType<ConsoleCollection>()
          .toList(growable: false);
    }

    throw const ConsumerApiException('Unexpected collections response.');
  }

  @override
  Future<ConsoleGamePage> listCollectionTitles({
    required String accessToken,
    required String collectionId,
    String? cursor,
    int limit = 24,
  }) async {
    final decoded = await _getJson(
      '/api/collections/${Uri.encodeComponent(collectionId)}/titles',
      accessToken: accessToken,
      queryParameters: <String, String>{
        if (cursor != null) 'cursor': cursor,
        'limit': limit.toString(),
      },
    );

    if (decoded case <String, Object?>{
      'items': final List<Object?> items,
      'hasNextPage': final bool hasNextPage,
    }) {
      return ConsoleGamePage(
        items: items
            .map(_readCatalogCard)
            .whereType<ConsoleGame>()
            .toList(growable: false),
        nextCursor: _readString(decoded['nextCursor']),
        hasNextPage: hasNextPage,
      );
    }

    throw const ConsumerApiException('Unexpected collection titles response.');
  }

  @override
  Future<ConsoleGameDetail> getTitle({
    required String accessToken,
    required String titleId,
  }) async {
    final decoded = await _getJson(
      '/api/titles/${Uri.encodeComponent(titleId)}',
      accessToken: accessToken,
    );

    final detail = _readTitleDetail(decoded);
    if (detail == null) {
      throw const ConsumerApiException('Unexpected title response.');
    }

    return detail;
  }

  @override
  Future<ConsoleReleaseManifest> issueReleaseManifest({
    required String accessToken,
    required String releaseId,
  }) async {
    final decoded = await _postJson(
      '/api/releases/${Uri.encodeComponent(releaseId)}/manifest',
      accessToken: accessToken,
    );

    final manifest = _readReleaseManifest(decoded);
    if (manifest == null) {
      throw const ConsumerApiException('Unexpected release manifest response.');
    }

    return manifest;
  }

  @override
  Future<List<BiosFileListing>> listPlatformBios({
    required String accessToken,
    required String platformShortName,
  }) async {
    final decoded = await _getJson(
      '/api/systems/${Uri.encodeComponent(platformShortName)}/bios',
      accessToken: accessToken,
    );

    if (decoded case <String, Object?>{'items': final List<Object?> items}) {
      return items
          .map(_readBiosListing)
          .whereType<BiosFileListing>()
          .toList(growable: false);
    }

    throw const ConsumerApiException('Unexpected platform BIOS response.');
  }

  @override
  Future<DeviceAuthorizationResult> requestDeviceAuthorization() async {
    try {
      final response = await _postForm('/connect/device', <String, String>{
        'client_id': _clientId,
        'scope': _scope,
      });
      final body = await utf8.decodeStream(response);

      if (response.statusCode != HttpStatus.ok) {
        return DeviceAuthorizationFailure('HTTP ${response.statusCode}');
      }

      final decoded = jsonDecode(body);
      if (decoded case <String, Object?>{
        'device_code': final String deviceCode,
        'user_code': final String userCode,
        'verification_uri': final String verificationUri,
      }) {
        return DeviceAuthorizationSuccess(
          DeviceAuthorization(
            deviceCode: deviceCode,
            userCode: userCode,
            verificationUri: Uri.parse(verificationUri),
            interval: Duration(seconds: _readInt(decoded['interval']) ?? 5),
            expiresAt: _now().add(
              Duration(seconds: _readInt(decoded['expires_in']) ?? 300),
            ),
          ),
        );
      }

      return const DeviceAuthorizationFailure(
        'Unexpected device authorization response.',
      );
    } on Object catch (error) {
      return DeviceAuthorizationFailure(error.toString());
    }
  }

  @override
  Future<DeviceTokenResult> redeemDeviceCode({
    required String deviceCode,
  }) async {
    try {
      final response = await _postForm('/connect/token', <String, String>{
        'grant_type': 'urn:ietf:params:oauth:grant-type:device_code',
        'client_id': _clientId,
        'device_code': deviceCode,
        'device_name': Platform.localHostname,
      });
      final body = await utf8.decodeStream(response);

      if (response.statusCode == HttpStatus.ok) {
        final session = await _buildSession(body);
        return session == null
            ? const DeviceTokenFailure('Unexpected token response.')
            : DeviceTokenSuccess(session);
      }

      return switch (_readError(body)) {
        'authorization_pending' => const DeviceTokenPending(),
        'slow_down' => const DeviceTokenSlowDown(),
        'expired_token' => const DeviceTokenFailure(
          'The sign-in code expired. Start again.',
        ),
        'access_denied' => const DeviceTokenFailure('Sign-in was declined.'),
        final error? => DeviceTokenFailure(error),
        _ => DeviceTokenFailure('HTTP ${response.statusCode}'),
      };
    } on Object catch (error) {
      return DeviceTokenFailure(error.toString());
    }
  }

  @override
  Future<void> revokeSession({required String refreshToken}) async {
    final response = await _postForm('/connect/revocation', <String, String>{
      'client_id': _clientId,
      'token': refreshToken,
      'token_type_hint': 'refresh_token',
    });
    await response.drain<void>();
    if (response.statusCode != HttpStatus.ok) {
      throw HttpException('Session revocation failed: ${response.statusCode}');
    }
  }

  @override
  Future<ConsumerLoginResult> refreshSession({
    required String refreshToken,
  }) async {
    try {
      final response = await _postForm('/connect/token', <String, String>{
        'grant_type': 'refresh_token',
        'client_id': _clientId,
        'refresh_token': refreshToken,
      });
      final body = await utf8.decodeStream(response);

      if (response.statusCode != HttpStatus.ok) {
        final error = _readError(body);
        final transient =
            error == 'temporarily_unavailable' ||
            error == 'server_error' ||
            response.statusCode == HttpStatus.requestTimeout ||
            response.statusCode == HttpStatus.tooManyRequests ||
            response.statusCode >= HttpStatus.internalServerError;
        return ConsumerLoginFailure(
          error == 'invalid_grant'
              ? 'Session expired.'
              : 'Token endpoint returned HTTP ${response.statusCode}.',
          kind: error == 'invalid_grant'
              ? ConsumerLoginFailureKind.invalidGrant
              : transient
              ? ConsumerLoginFailureKind.transient
              : ConsumerLoginFailureKind.protocol,
        );
      }

      final session = await _buildSession(body);
      return session == null
          ? const ConsumerLoginFailure(
              'Unexpected token response.',
              kind: ConsumerLoginFailureKind.protocol,
            )
          : ConsumerLoginSuccess(session);
    } on Object {
      return const ConsumerLoginFailure(
        'Token endpoint unavailable.',
        kind: ConsumerLoginFailureKind.transient,
      );
    }
  }

  @override
  void close() {
    _httpClient.close(force: true);
  }

  Future<HttpClientResponse> _postForm(
    String path,
    Map<String, String> fields,
  ) async {
    final request = await _httpClient.postUrl(_consumerApiOrigin.resolve(path));
    request.headers
      ..set(HttpHeaders.acceptHeader, 'application/json')
      ..set(HttpHeaders.contentTypeHeader, 'application/x-www-form-urlencoded');
    request.write(
      fields.entries
          .map(
            (entry) =>
                '${Uri.encodeQueryComponent(entry.key)}=${Uri.encodeQueryComponent(entry.value)}',
          )
          .join('&'),
    );

    return request.close();
  }

  Future<Object?> _getJson(
    String path, {
    required String accessToken,
    Map<String, String>? queryParameters,
  }) async {
    final resolved = _consumerApiOrigin.resolve(path);
    final uri = queryParameters == null
        ? resolved
        : resolved.replace(queryParameters: queryParameters);
    final request = await _httpClient.getUrl(uri);
    request.headers
      ..set(HttpHeaders.acceptHeader, 'application/json')
      ..set(HttpHeaders.authorizationHeader, 'Bearer $accessToken');

    final response = await request.close();
    final body = await utf8.decodeStream(response);

    if (response.statusCode != HttpStatus.ok) {
      throw ConsumerApiException.http(
        response.statusCode,
        bearerError: _readBearerError(response.headers),
      );
    }

    return jsonDecode(body);
  }

  Future<Object?> _postJson(String path, {required String accessToken}) async {
    final request = await _httpClient.postUrl(_consumerApiOrigin.resolve(path));
    request.headers
      ..set(HttpHeaders.acceptHeader, 'application/json')
      ..set(HttpHeaders.authorizationHeader, 'Bearer $accessToken')
      ..set(HttpHeaders.contentLengthHeader, '0');

    final response = await request.close();
    final body = await utf8.decodeStream(response);

    if (response.statusCode != HttpStatus.ok) {
      throw ConsumerApiException.http(
        response.statusCode,
        bearerError: _readBearerError(response.headers),
      );
    }

    return jsonDecode(body);
  }

  Future<ConsumerLoginSession?> _buildSession(String tokenBody) async {
    final decoded = jsonDecode(tokenBody);
    if (decoded is! Map<String, Object?>) {
      return null;
    }

    final token = decoded['access_token'];
    final tokenType = decoded['token_type'];
    final refreshToken = decoded['refresh_token'];
    final expiresIn = _readInt(decoded['expires_in']);

    if (token is! String ||
        tokenType is! String ||
        refreshToken is! String ||
        expiresIn == null) {
      return null;
    }

    final account = await _fetchAccount(token);
    if (account == null) {
      return null;
    }

    return ConsumerLoginSession(
      token: token,
      tokenType: tokenType,
      refreshToken: refreshToken,
      expiresAt: _now().add(Duration(seconds: expiresIn)),
      account: account,
    );
  }

  Future<ConsumerAccount?> _fetchAccount(String accessToken) async {
    final request = await _httpClient.getUrl(
      _consumerApiOrigin.resolve('/api/me'),
    );
    request.headers
      ..set(HttpHeaders.acceptHeader, 'application/json')
      ..set(HttpHeaders.authorizationHeader, 'Bearer $accessToken');

    final response = await request.close();
    final body = await utf8.decodeStream(response);

    if (response.statusCode != HttpStatus.ok) {
      return null;
    }

    final decoded = jsonDecode(body);
    if (decoded case <String, Object?>{
      'id': final String id,
      'email': final String email,
    }) {
      // username is optional: older hosts omit it. Fall back to the email when absent.
      final username = decoded['username'];
      return ConsumerAccount(
        id: id,
        email: email,
        username: username is String && username.isNotEmpty ? username : email,
      );
    }

    return null;
  }

  String? _readError(String body) {
    try {
      final decoded = jsonDecode(body);
      if (decoded case <String, Object?>{'error': final String error}) {
        return error;
      }
    } on Object {
      // Fall through to null for non-JSON error bodies.
    }

    return null;
  }

  String? _readBearerError(HttpHeaders headers) {
    final challenge = headers.value(HttpHeaders.wwwAuthenticateHeader);
    if (challenge == null ||
        !challenge.trimLeft().toLowerCase().startsWith('bearer ')) {
      return null;
    }
    return RegExp(
      r'(?:^|,)\s*error\s*=\s*"?([^",\s]+)',
      caseSensitive: false,
    ).firstMatch(challenge.substring(challenge.indexOf(' ') + 1))?.group(1);
  }

  int? _readInt(Object? value) => switch (value) {
    final int intValue => intValue,
    final String stringValue => int.tryParse(stringValue),
    _ => null,
  };

  ConsoleGame? _readCatalogCard(Object? value) {
    if (value case <String, Object?>{
      'id': final String id,
      'system': <String, Object?>{
        'key': final String platformId,
        'name': final String platformName,
      },
      'name': final String name,
      'releaseCount': final int releaseCount,
    }) {
      return ConsoleGame(
        id: id,
        platformId: platformId,
        platformName: platformName,
        title: name,
        releaseDate: _readDate(value['releaseDate']),
        coverUrl: ConsoleArtwork.readList(
          value['artwork'],
          _consumerApiOrigin,
        ).forRole('Poster')?.url,
        artwork: ConsoleArtwork.readList(value['artwork'], _consumerApiOrigin),
        genre: _readString(value['genre']),
        rating: _readDouble(value['rating']),
        releaseCount: releaseCount,
        defaultReleaseId: _readString(value['defaultReleaseId']),
      );
    }

    return null;
  }

  ConsolePlatform? _readPlatform(Object? value) {
    if (value case <String, Object?>{
      'key': final String id,
      'name': final String name,
      'shortName': final String shortName,
      'titleCount': final int titleCount,
    }) {
      return ConsolePlatform(
        id: id,
        name: name,
        shortName: shortName,
        manufacturer: _readString(value['manufacturer']),
        titleCount: titleCount,
        coverUrl: _readUri(value['coverUrl']),
      );
    }

    return null;
  }

  String? _readSystemField(Object? value, String field) =>
      value is Map<String, Object?> ? _readString(value[field]) : null;

  ConsoleCollection? _readCollection(Object? value) {
    if (value case <String, Object?>{
      'id': final String id,
      'name': final String name,
      'itemCount': final int itemCount,
    }) {
      return ConsoleCollection(
        id: id,
        name: name,
        description: _readString(value['description']),
        platformId: _readSystemField(value['system'], 'key'),
        platformName: _readSystemField(value['system'], 'name'),
        coverUrl: _readUri(value['coverUrl']),
        heroUrl: _readUri(value['heroUrl']),
        itemCount: itemCount,
        isFeatured: value['isFeatured'] is bool
            ? value['isFeatured'] as bool
            : true,
      );
    }

    return null;
  }

  ConsoleGameDetail? _readTitleDetail(Object? value) {
    if (value case <String, Object?>{
      'id': final String id,
      'system': <String, Object?>{
        'key': final String platformId,
        'name': final String platformName,
      },
      'name': final String name,
      'media': final List<Object?> media,
      'releases': final List<Object?> releases,
    }) {
      return ConsoleGameDetail(
        id: id,
        platformId: platformId,
        platformName: platformName,
        title: name,
        description: _readString(value['description']),
        publisher: _readString(value['publisher']),
        developer: _readString(value['developer']),
        genre: _readString(value['genre']),
        releaseDate: _readDate(value['releaseDate']),
        players: _readInt(value['players']),
        rating: _readDouble(value['rating']),
        artwork: ConsoleArtwork.readList(value['artwork'], _consumerApiOrigin),
        media: media
            .map(_readMediaRef)
            .whereType<ConsoleMediaRef>()
            .toList(growable: false),
        releases: releases
            .map(_readRelease)
            .whereType<ConsoleRelease>()
            .toList(growable: false),
        defaultReleaseId: _readString(value['defaultReleaseId']),
      );
    }

    return null;
  }

  ConsoleMediaRef? _readMediaRef(Object? value) {
    if (value case <String, Object?>{
      'id': final String id,
      'type': final String type,
      'url': final String url,
      'isPrimary': final bool isPrimary,
    }) {
      return ConsoleMediaRef(
        id: id,
        type: type,
        url: _consumerApiOrigin.resolve(url),
        isPrimary: isPrimary,
      );
    }

    return null;
  }

  ConsoleRelease? _readRelease(Object? value) {
    if (value case <String, Object?>{
      'id': final String id,
      'name': final String name,
      'regions': final List<Object?> regions,
      'languages': final List<Object?> languages,
      'sizeBytes': final Object? sizeBytes,
      'isComplete': final bool isComplete,
    }) {
      return ConsoleRelease(
        id: id,
        name: name,
        revision: _readString(value['revision']),
        regions: regions.whereType<String>().toList(growable: false),
        languages: languages.whereType<String>().toList(growable: false),
        sizeBytes: _readInt(sizeBytes) ?? 0,
        isComplete: isComplete,
      );
    }

    return null;
  }

  ConsoleReleaseManifest? _readReleaseManifest(Object? value) {
    if (value case <String, Object?>{
      'releaseId': final String releaseId,
      'titleId': final String titleId,
      'systemKey': final String systemKey,
      'name': final String name,
      'isComplete': final bool isComplete,
      'runtime': final Map<String, Object?> runtime,
      'items': final List<Object?> items,
    }) {
      final parsedRuntime = _readReleaseRuntime(runtime);
      if (parsedRuntime == null) {
        return null;
      }

      return ConsoleReleaseManifest(
        releaseId: releaseId,
        titleId: titleId,
        systemKey: systemKey,
        name: name,
        revision: _readString(value['revision']),
        isComplete: isComplete,
        runtime: parsedRuntime,
        items: items
            .map(_readReleaseManifestItem)
            .whereType<ConsoleReleaseManifestItem>()
            .toList(growable: false),
      );
    }

    return null;
  }

  ConsoleReleaseRuntime? _readReleaseRuntime(Object? value) {
    if (value case <String, Object?>{
      'contentType': final String contentType,
      'packaging': final String packaging,
      'minimumInstallBytes': final Object? minimumInstallBytes,
    }) {
      return ConsoleReleaseRuntime(
        contentType: contentType,
        launch: _readLaunchTarget(value['launch']),
        packaging: packaging,
        minimumInstallBytes: _readInt(minimumInstallBytes) ?? 0,
      );
    }

    return null;
  }

  ConsoleLaunchTarget? _readLaunchTarget(Object? value) {
    if (value case <String, Object?>{
      'type': final String type,
      'relativePath': final String relativePath,
    }) {
      return ConsoleLaunchTarget(type: type, relativePath: relativePath);
    }

    return null;
  }

  ConsoleReleaseManifestItem? _readReleaseManifestItem(Object? value) {
    if (value case <String, Object?>{
      'relativePath': final String relativePath,
      'role': final String role,
      'sizeBytes': final Object? sizeBytes,
      'isAvailable': final bool isAvailable,
    }) {
      final contentGrant = value['contentGrant'];
      final downloadUrl = contentGrant is Map<String, Object?>
          ? _readUri(contentGrant['downloadUrl'])
          : null;

      return ConsoleReleaseManifestItem(
        relativePath: relativePath,
        role: role,
        sizeBytes: _readInt(sizeBytes) ?? 0,
        sha256: _readString(value['sha256']),
        isAvailable: isAvailable,
        downloadUrl: downloadUrl,
      );
    }

    return null;
  }

  BiosFileListing? _readBiosListing(Object? value) {
    if (value case <String, Object?>{
      'biosId': final String biosId,
      'name': final String name,
      'fileName': final String fileName,
      'sizeBytes': final Object? sizeBytes,
      'isAvailable': final bool isAvailable,
    }) {
      final contentGrant = value['contentGrant'];
      final downloadUrl = contentGrant is Map<String, Object?>
          ? _readUri(contentGrant['downloadUrl'])
          : null;

      return BiosFileListing(
        biosId: biosId,
        name: name,
        fileName: fileName,
        sizeBytes: _readInt(sizeBytes) ?? 0,
        sha1: _readString(value['sha1'])?.toLowerCase(),
        md5: _readString(value['md5'])?.toLowerCase(),
        sha256: _readString(value['sha256'])?.toLowerCase(),
        isAvailable: isAvailable,
        downloadUrl: downloadUrl,
      );
    }

    return null;
  }

  DateTime? _readDate(Object? value) => switch (value) {
    final String text when text.isNotEmpty => DateTime.tryParse(text),
    _ => null,
  };

  double? _readDouble(Object? value) => switch (value) {
    final double doubleValue => doubleValue,
    final int intValue => intValue.toDouble(),
    final String stringValue => double.tryParse(stringValue),
    _ => null,
  };

  String? _readString(Object? value) => switch (value) {
    final String text when text.isNotEmpty => text,
    _ => null,
  };

  Uri? _readUri(Object? value) {
    final text = _readString(value);
    if (text == null) {
      return null;
    }

    return _consumerApiOrigin.resolve(text);
  }
}

final class ConsumerApiException implements Exception {
  const ConsumerApiException(this.message, {this.statusCode, this.bearerError});

  factory ConsumerApiException.http(int statusCode, {String? bearerError}) =>
      ConsumerApiException(
        'HTTP $statusCode',
        statusCode: statusCode,
        bearerError: bearerError,
      );

  final String message;
  final int? statusCode;
  final String? bearerError;

  bool get isInvalidToken =>
      statusCode == HttpStatus.unauthorized && bearerError == 'invalid_token';

  @override
  String toString() => message;
}
