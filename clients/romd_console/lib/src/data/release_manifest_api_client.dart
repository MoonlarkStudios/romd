import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:romd_console/src/domain/content_file_provenance.dart';

import '../domain/rom_delivery_format.dart';
import '../domain/romd_public_id.dart';
import '../domain/romd_server_instance_id.dart';
import '../domain/server_bound_release_manifest.dart';
import '../domain/system_key.dart';
import 'owned_http_operation.dart';

abstract interface class ReleaseManifestApiClient {
  Future<ReleaseManifestResult> getReleaseManifest({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,

    required String expectedSystemKey,
  });

  void close();
}

final class HttpReleaseManifestApiClient implements ReleaseManifestApiClient {
  HttpReleaseManifestApiClient({
    required Uri consumerApiOrigin,
    Duration timeout = const Duration(seconds: 15),
  }) : _consumerApiOrigin = consumerApiOrigin,
       _timeout = timeout;

  static const int _maxInt64 = 9223372036854775807;
  static final RegExp _canonicalNonNegativeInteger = RegExp(r'^(0|[1-9]\d*)$');
  static final RegExp _sha256 = RegExp(r'^[0-9a-f]{64}$');
  static final RegExp _fileExtension = RegExp(r'^\.[a-z0-9]{1,15}$');
  static final RegExp _portableUnsafePathCharacter = RegExp(r'[<>:"|?*]');
  static final RegExp _portableReservedPathSegment = RegExp(
    r'^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)',
    caseSensitive: false,
  );
  static final RegExp _dateTimeOffset = RegExp(
    r'^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})'
    r'(?:\.(\d{1,7}))?(Z|([+-])(\d{2}):(\d{2}))$',
  );

  final Uri _consumerApiOrigin;
  final Duration _timeout;
  final Set<OwnedHttpOperation> _activeOperations = <OwnedHttpOperation>{};
  bool _closed = false;

  @override
  Future<ReleaseManifestResult> getReleaseManifest({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,

    required String expectedSystemKey,
  }) async {
    if (_closed) {
      return const ReleaseManifestFailure(ReleaseManifestFailureKind.transport);
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

        expectedSystemKey: expectedSystemKey,
      ).timeout(
        _timeout,
        onTimeout: () {
          operation.abort();
          return const ReleaseManifestFailure(
            ReleaseManifestFailureKind.timeout,
          );
        },
      );
    } on TimeoutException {
      operation.abort();
      return const ReleaseManifestFailure(ReleaseManifestFailureKind.timeout);
    } on FormatException {
      return const ReleaseManifestFailure(
        ReleaseManifestFailureKind.malformedResponse,
      );
    } on OwnedHttpOperationAbortedException {
      return const ReleaseManifestFailure(ReleaseManifestFailureKind.transport);
    } on IOException {
      return const ReleaseManifestFailure(ReleaseManifestFailureKind.transport);
    } finally {
      _activeOperations.remove(operation);
      operation.dispose();
    }
  }

  Future<ReleaseManifestResult> _sendRequest({
    required OwnedHttpOperation operation,
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,

    required String expectedSystemKey,
  }) async {
    final request = await operation.postUrl(
      _consumerApiOrigin.resolve('/api/releases/${releaseId.value}/manifest'),
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

      expectedSystemKey: expectedSystemKey,
    );
  }

  ReleaseManifestFailure _failureForStatus(
    int statusCode, {
    String? bearerError,
  }) {
    if (statusCode >= HttpStatus.multipleChoices &&
        statusCode < HttpStatus.badRequest) {
      return ReleaseManifestFailure(
        ReleaseManifestFailureKind.redirect,
        statusCode: statusCode,
      );
    }

    return switch (statusCode) {
      HttpStatus.badRequest => const ReleaseManifestFailure(
        ReleaseManifestFailureKind.badRequest,
        statusCode: HttpStatus.badRequest,
      ),
      HttpStatus.unauthorized => ReleaseManifestFailure(
        ReleaseManifestFailureKind.unauthorized,
        statusCode: HttpStatus.unauthorized,
        bearerError: bearerError,
      ),
      HttpStatus.notFound => const ReleaseManifestFailure(
        ReleaseManifestFailureKind.notFound,
        statusCode: HttpStatus.notFound,
      ),
      HttpStatus.conflict => const ReleaseManifestFailure(
        ReleaseManifestFailureKind.conflict,
        statusCode: HttpStatus.conflict,
      ),
      >= HttpStatus.badRequest && < HttpStatus.internalServerError =>
        ReleaseManifestFailure(
          ReleaseManifestFailureKind.clientError,
          statusCode: statusCode,
        ),
      >= HttpStatus.internalServerError => ReleaseManifestFailure(
        ReleaseManifestFailureKind.serverError,
        statusCode: statusCode,
      ),
      _ => ReleaseManifestFailure(
        ReleaseManifestFailureKind.unexpectedStatus,
        statusCode: statusCode,
      ),
    };
  }

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

  ReleaseManifestResult _parseResponse(
    Object? decoded, {
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId expectedReleaseId,
    required RomdPublicId expectedTitleId,

    required String expectedSystemKey,
  }) {
    final value = _requireMap(decoded);
    _requireExactKeys(
      value,
      required: const <String>{
        'serverInstanceId',
        'releaseId',
        'titleId',
        'systemKey',
        'name',
        'isComplete',
        'isDeliveryComplete',
        'runtime',
        'items',
      },
      optional: const <String>{'revision'},
    );

    final serverInstanceId = _requireServerInstanceId(
      value,
      'serverInstanceId',
    );
    final releaseId = _requirePublicId(value, 'releaseId');
    final titleId = _requirePublicId(value, 'titleId');
    final systemKey = _requireNonEmptyString(value, 'systemKey');
    if (!isSystemKey(systemKey))
      throw const FormatException('Invalid system key.');
    final name = _requireNonEmptyString(value, 'name');
    final revision = _optionalNullableString(value, 'revision');
    final isComplete = _requireBool(value, 'isComplete');
    final isDeliveryComplete = _requireBool(value, 'isDeliveryComplete');
    final runtime = _parseRuntime(value['runtime']);
    final items = _parseItems(value['items']);

    if (isDeliveryComplete !=
            (items.isNotEmpty && items.every((item) => item.isAvailable)) ||
        (isComplete &&
            items.any(
              (item) => item.provenance == ContentFileProvenance.derived,
            ))) {
      throw const FormatException(
        'Incoherent delivery completeness or preservation.',
      );
    }
    final itemPaths = items.map((item) => item.relativePath).toSet();
    final portableCollisionKeys = items
        .map((item) => _portablePathCollisionKey(item.relativePath))
        .toSet();
    if (itemPaths.length != items.length ||
        portableCollisionKeys.length != items.length ||
        _hasFileDirectoryCollision(portableCollisionKeys) ||
        (runtime.launch != null &&
            !itemPaths.contains(runtime.launch!.relativePath))) {
      throw const FormatException('Incoherent manifest paths.');
    }

    final hasSingleItem = items.length == 1;
    if ((runtime.contentType == 'single_rom' &&
            (!hasSingleItem || runtime.launch == null)) ||
        (runtime.contentType == 'unknown' && hasSingleItem)) {
      throw const FormatException('Incoherent manifest runtime cardinality.');
    }

    var itemBytes = 0;
    for (final item in items) {
      itemBytes += item.sizeBytes;
      if (itemBytes > _maxInt64) {
        throw const FormatException('Manifest size overflow.');
      }
    }
    if (runtime.minimumInstallBytes != itemBytes) {
      throw const FormatException('Manifest size mismatch.');
    }

    if (serverInstanceId != expectedServerInstanceId ||
        releaseId != expectedReleaseId ||
        titleId != expectedTitleId ||
        systemKey != expectedSystemKey) {
      return const ReleaseManifestFailure(
        ReleaseManifestFailureKind.identityMismatch,
      );
    }

    return ReleaseManifestSuccess(
      ServerBoundReleaseManifest(
        serverInstanceId: serverInstanceId,
        releaseId: releaseId,
        titleId: titleId,
        systemKey: systemKey,
        name: name,
        revision: revision,
        isComplete: isComplete,
        isDeliveryComplete: isDeliveryComplete,
        runtime: runtime,
        items: items,
      ),
    );
  }

  ServerBoundReleaseRuntime _parseRuntime(Object? decoded) {
    final runtime = _parseRuntimeShape(decoded);
    if (runtime.contentType != 'single_rom' &&
        runtime.contentType != 'unknown') {
      throw const FormatException('Unsupported manifest content type.');
    }
    if (runtime.packaging != 'direct_files') {
      throw const FormatException('Unsupported manifest packaging.');
    }
    if (runtime.launch != null && runtime.launch!.type != 'file') {
      throw const FormatException('Unsupported launch type.');
    }
    return runtime;
  }

  /// Shape-only runtime parsing. Vocabulary is enforced for the release
  /// runtime; a per-format runtime is judged by the install plan instead.
  ServerBoundReleaseRuntime _parseRuntimeShape(Object? decoded) {
    final value = _requireMap(decoded);
    _requireExactKeys(
      value,
      required: const <String>{
        'contentType',
        'packaging',
        'minimumInstallBytes',
      },
      optional: const <String>{'launch'},
    );
    return ServerBoundReleaseRuntime(
      contentType: _requireNonEmptyString(value, 'contentType'),
      launch: _parseOptionalLaunch(value),
      packaging: _requireNonEmptyString(value, 'packaging'),
      minimumInstallBytes: _requireNonNegativeInt64(
        value,
        'minimumInstallBytes',
      ),
    );
  }

  ServerBoundLaunchTarget? _parseOptionalLaunch(Map<String, Object?> value) {
    if (!value.containsKey('launch') || value['launch'] == null) {
      return null;
    }
    final launch = _requireMap(value['launch']);
    _requireExactKeys(launch, required: const <String>{'type', 'relativePath'});
    return ServerBoundLaunchTarget(
      type: _requireNonEmptyString(launch, 'type'),
      relativePath: _requireSafeRelativePath(launch, 'relativePath'),
    );
  }

  List<ServerBoundReleaseManifestItem> _parseItems(Object? decoded) {
    if (decoded is! List<Object?>) {
      throw const FormatException('Expected manifest item list.');
    }

    final items = <ServerBoundReleaseManifestItem>[];
    for (final rawItem in decoded) {
      items.add(_parseItem(rawItem));
    }
    return items;
  }

  ServerBoundReleaseManifestItem _parseItem(Object? decoded) {
    final value = _requireMap(decoded);
    _requireExactKeys(
      value,
      required: const <String>{
        'relativePath',
        'role',
        'provenance',
        'formats',
        'sizeBytes',
        'isAvailable',
      },
      optional: const <String>{'sha256', 'contentGrant'},
    );

    final provenance = ContentFileProvenance.parse(value['provenance']);
    final isAvailable = _requireBool(value, 'isAvailable');
    final sha256 = _optionalNullableSha256(value, 'sha256');
    final contentGrant = _parseOptionalContentGrant(
      value,
      provenance: provenance,
    );
    if (isAvailable != (sha256 != null) ||
        isAvailable != (contentGrant != null)) {
      throw const FormatException('Incoherent item availability.');
    }

    final role = _requireNonEmptyString(value, 'role');
    if (role != 'rom' && role != 'disk') {
      throw const FormatException('Unsupported manifest item role.');
    }
    final relativePath = _requireSafeRelativePath(value, 'relativePath');
    final sizeBytes = _requireNonNegativeInt64(value, 'sizeBytes');
    return ServerBoundReleaseManifestItem(
      relativePath: relativePath,
      role: role,
      provenance: provenance,
      sizeBytes: sizeBytes,
      sha256: sha256,
      isAvailable: isAvailable,
      contentGrant: contentGrant,
      formats: _parseItemFormats(
        value,
        relativePath: relativePath,
        sizeBytes: sizeBytes,
        sha256: sha256,
        isAvailable: isAvailable,
        provenance: provenance,
      ),
    );
  }

  /// Wire shape (keys, types, names, hashes, paths, grant URLs) is strict for
  /// every format. Original and RVZ coherence stays atomic as before; CHD
  /// coherence is left to the install plan so an unusable CHD entry only makes
  /// CHD unselectable.
  List<ServerBoundReleaseManifestFormat> _parseItemFormats(
    Map<String, Object?> itemValue, {
    required String relativePath,
    required int sizeBytes,
    required String? sha256,
    required bool isAvailable,
    required ContentFileProvenance provenance,
  }) {
    final formats = itemValue['formats'];
    if (formats is! List<Object?>) {
      throw const FormatException('Expected delivery format list.');
    }
    if (provenance == ContentFileProvenance.derived) {
      if (formats.isNotEmpty ||
          !isAvailable ||
          !relativePath.toLowerCase().endsWith('.cue')) {
        throw const FormatException('Incoherent derived descriptor.');
      }
      return const [];
    }
    final seen = <RomDeliveryFormat>{};
    final parsed = <ServerBoundReleaseManifestFormat>[];
    for (final raw in formats) {
      final value = _requireMap(raw);
      _requireExactKeys(
        value,
        required: const <String>{
          'format',
          'label',
          'state',
          'relativePath',
          'profileVersion',
          'runtimeApplicability',
        },
        optional: const <String>{
          'fileExtension',
          'filename',
          'sizeBytes',
          'sha256',
          'md5',
          'sha1',
          'contentGrant',
          'runtime',
        },
      );
      final format = RomDeliveryFormat.tryParse(
        _requireNonEmptyString(value, 'format'),
      );
      final state = RomDeliveryState.tryParse(
        _requireNonEmptyString(value, 'state'),
      );
      if (format == null || state == null || !seen.add(format)) {
        throw const FormatException('Invalid delivery format or state.');
      }
      // Display data from the server's format catalog; selection never reads
      // it, so it is validated for shape only.
      _requireNonEmptyString(value, 'label');
      final fileExtension = _optionalNullableString(value, 'fileExtension');
      if (fileExtension != null && !_fileExtension.hasMatch(fileExtension)) {
        throw const FormatException('Invalid delivery file extension.');
      }
      final path = _requireSafeRelativePath(value, 'relativePath');
      final profileVersion = _requireNonEmptyString(value, 'profileVersion');
      final applicability = value['runtimeApplicability'];
      if (applicability is! List<Object?> ||
          applicability.any(
            (entry) => entry is! String || entry.trim().isEmpty,
          ) ||
          applicability.toSet().length != applicability.length) {
        throw const FormatException('Invalid runtime applicability.');
      }
      final filename = _optionalNullableString(value, 'filename');
      if (filename != null &&
          _requireSafeRelativePath(value, 'filename').contains('/')) {
        throw const FormatException('Unsafe delivery filename.');
      }
      final size = value['sizeBytes'] == null
          ? null
          : _requireDeliveryByteCount(value);
      final formatSha256 = _optionalNullableSha256(value, 'sha256');
      final md5 = _validateOptionalChecksum(value, 'md5', 32);
      final sha1 = _validateOptionalChecksum(value, 'sha1', 40);
      final grant = _parseOptionalContentGrant(
        value,
        provenance: ContentFileProvenance.original,
      );
      final runtime = value['runtime'] == null
          ? null
          : _parseRuntimeShape(value['runtime']);
      final available = state == RomDeliveryState.available;
      if (format != RomDeliveryFormat.chd &&
          ((available &&
                  (filename == null ||
                      size == null ||
                      formatSha256 == null ||
                      grant == null)) ||
              (!available && grant != null))) {
        throw const FormatException('Incoherent delivery availability.');
      }
      if (format == RomDeliveryFormat.original &&
          (path != relativePath ||
              available != isAvailable ||
              (available && (size != sizeBytes || formatSha256 != sha256)))) {
        throw const FormatException('Incoherent original delivery metadata.');
      }
      parsed.add(
        ServerBoundReleaseManifestFormat(
          format: format,
          state: state,
          relativePath: path,
          profileVersion: profileVersion,
          runtimeApplicability: applicability.cast<String>(),
          filename: filename,
          sizeBytes: size,
          sha256: formatSha256,
          md5: md5,
          sha1: sha1,
          contentGrant: grant,
          runtime: runtime,
        ),
      );
    }
    if ((seen.isNotEmpty || isAvailable) &&
        !seen.contains(RomDeliveryFormat.original)) {
      throw const FormatException('Missing original delivery format.');
    }
    return List<ServerBoundReleaseManifestFormat>.unmodifiable(parsed);
  }

  int _requireDeliveryByteCount(Map<String, Object?> value) {
    if (value['sizeBytes'] is! String) {
      throw const FormatException('Expected decimal delivery byte count.');
    }
    return _requireNonNegativeInt64(value, 'sizeBytes');
  }

  String? _validateOptionalChecksum(
    Map<String, Object?> value,
    String key,
    int length,
  ) {
    final checksum = _optionalNullableString(value, key);
    if (checksum != null &&
        (checksum.length != length ||
            !RegExp(r'^[0-9a-f]+$').hasMatch(checksum))) {
      throw const FormatException('Invalid artifact checksum.');
    }
    return checksum;
  }

  ServerBoundContentGrant? _parseOptionalContentGrant(
    Map<String, Object?> value, {
    required ContentFileProvenance provenance,
  }) {
    if (!value.containsKey('contentGrant') || value['contentGrant'] == null) {
      return null;
    }
    final grant = _requireMap(value['contentGrant']);
    _requireExactKeys(
      grant,
      required: const <String>{'downloadUrl', 'expiresAt'},
    );
    final downloadUrlText = _requireNonEmptyString(grant, 'downloadUrl');
    final expectedRoute = provenance == ContentFileProvenance.original
        ? 'content'
        : 'derived-descriptor';
    if (!RegExp(
      '^/delivery/$expectedRoute/[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\$',
    ).hasMatch(downloadUrlText)) {
      throw const FormatException('Invalid content grant URL.');
    }
    final downloadUrl = Uri.tryParse(downloadUrlText);
    if (downloadUrl == null ||
        downloadUrl.hasScheme ||
        downloadUrl.hasAuthority ||
        downloadUrl.hasQuery ||
        downloadUrl.hasFragment) {
      throw const FormatException('Invalid content grant URL.');
    }
    final resolvedDownloadUrl = _consumerApiOrigin.resolveUri(downloadUrl);
    if (resolvedDownloadUrl.scheme != _consumerApiOrigin.scheme ||
        resolvedDownloadUrl.userInfo != _consumerApiOrigin.userInfo ||
        resolvedDownloadUrl.host != _consumerApiOrigin.host ||
        resolvedDownloadUrl.port != _consumerApiOrigin.port ||
        resolvedDownloadUrl.hasQuery ||
        resolvedDownloadUrl.hasFragment) {
      throw const FormatException('Invalid content grant URL.');
    }

    final expiresAtText = _requireNonEmptyString(grant, 'expiresAt');
    final expiresAt = _parseDateTimeOffset(expiresAtText);
    return ServerBoundContentGrant(
      downloadUrl: resolvedDownloadUrl,
      expiresAt: expiresAt,
    );
  }

  DateTime _parseDateTimeOffset(String value) {
    final match = _dateTimeOffset.firstMatch(value);
    if (match == null) {
      throw const FormatException('Invalid content grant expiry.');
    }

    final year = int.parse(match.group(1)!);
    final month = int.parse(match.group(2)!);
    final day = int.parse(match.group(3)!);
    final hour = int.parse(match.group(4)!);
    final minute = int.parse(match.group(5)!);
    final second = int.parse(match.group(6)!);
    final offsetHour = match.group(10) == null
        ? 0
        : int.parse(match.group(10)!);
    final offsetMinute = match.group(11) == null
        ? 0
        : int.parse(match.group(11)!);
    if (year == 0 ||
        month < 1 ||
        month > 12 ||
        day < 1 ||
        day > _daysInMonth(year, month) ||
        hour > 23 ||
        minute > 59 ||
        second > 59 ||
        offsetHour > 14 ||
        offsetMinute > 59 ||
        (offsetHour == 14 && offsetMinute != 0)) {
      throw const FormatException('Invalid content grant expiry.');
    }

    final parsed = DateTime.tryParse(value);
    if (parsed == null) {
      throw const FormatException('Invalid content grant expiry.');
    }
    return parsed.toUtc();
  }

  int _daysInMonth(int year, int month) => switch (month) {
    2 => (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)) ? 29 : 28,
    4 || 6 || 9 || 11 => 30,
    _ => 31,
  };

  Map<String, Object?> _requireMap(Object? value) {
    if (value is! Map<String, Object?>) {
      throw const FormatException('Expected JSON object.');
    }
    return value;
  }

  void _requireExactKeys(
    Map<String, Object?> value, {
    required Set<String> required,
    Set<String> optional = const <String>{},
  }) {
    if (!required.every(value.containsKey) ||
        !value.keys.every(
          (key) => required.contains(key) || optional.contains(key),
        )) {
      throw const FormatException('Unexpected JSON shape.');
    }
  }

  String _requireNonEmptyString(Map<String, Object?> value, String key) {
    final result = value[key];
    if (result is! String || result.trim().isEmpty) {
      throw const FormatException('Expected non-empty string.');
    }
    return result;
  }

  String? _optionalNullableString(Map<String, Object?> value, String key) {
    if (!value.containsKey(key) || value[key] == null) {
      return null;
    }
    final result = value[key];
    if (result is! String) {
      throw const FormatException('Expected nullable string.');
    }
    return result;
  }

  String? _optionalNullableSha256(Map<String, Object?> value, String key) {
    final result = _optionalNullableString(value, key);
    if (result != null && !_sha256.hasMatch(result)) {
      throw const FormatException('Invalid SHA-256.');
    }
    return result;
  }

  bool _requireBool(Map<String, Object?> value, String key) {
    final result = value[key];
    if (result is! bool) {
      throw const FormatException('Expected boolean.');
    }
    return result;
  }

  RomdServerInstanceId _requireServerInstanceId(
    Map<String, Object?> value,
    String key,
  ) {
    final text = value[key];
    final result = text is String ? RomdServerInstanceId.tryParse(text) : null;
    if (result == null) {
      throw const FormatException('Invalid server instance id.');
    }
    return result;
  }

  RomdPublicId _requirePublicId(Map<String, Object?> value, String key) {
    final text = value[key];
    final result = text is String ? RomdPublicId.tryParse(text) : null;
    if (result == null) {
      throw const FormatException('Invalid public id.');
    }
    return result;
  }

  int _requireNonNegativeInt64(Map<String, Object?> value, String key) {
    final raw = value[key];
    final result = switch (raw) {
      final int number => number,
      final String text when _canonicalNonNegativeInteger.hasMatch(text) =>
        int.tryParse(text),
      _ => null,
    };
    if (result == null || result < 0 || result > _maxInt64) {
      throw const FormatException('Invalid non-negative int64.');
    }
    return result;
  }

  String _requireSafeRelativePath(Map<String, Object?> value, String key) {
    final path = _requireNonEmptyString(value, key);
    if (path.startsWith('/') ||
        path.startsWith('\\') ||
        path.contains('\\') ||
        path.codeUnits.any((unit) => unit < 32 || unit == 127)) {
      throw const FormatException('Unsafe relative path.');
    }
    final segments = path.split('/');
    if (segments.any(
      (segment) =>
          segment.isEmpty ||
          segment == '.' ||
          segment == '..' ||
          segment.endsWith('.') ||
          segment.endsWith(' ') ||
          _portableUnsafePathCharacter.hasMatch(segment) ||
          _portableReservedPathSegment.hasMatch(segment),
    )) {
      throw const FormatException('Unsafe relative path.');
    }
    return path;
  }

  /// Gate 1b rejects portable case-insensitive collisions without guessing the
  /// destination filesystem's Unicode normalization. Gate 2 acquisition must
  /// additionally normalize candidate destination paths using the actual
  /// target filesystem before any staging write.
  String _portablePathCollisionKey(String path) => path.toLowerCase();

  bool _hasFileDirectoryCollision(Set<String> collisionKeys) {
    for (final key in collisionKeys) {
      final segments = key.split('/');
      for (var length = 1; length < segments.length; length++) {
        if (collisionKeys.contains(segments.take(length).join('/'))) {
          return true;
        }
      }
    }
    return false;
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
