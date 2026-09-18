import 'dart:async';
import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../domain/romd_server_instance_id.dart';

/// Persists ROMD refresh tokens per local profile and discovered ROMD server
/// instance. Access tokens stay in memory.
abstract interface class RefreshTokenStore {
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  });

  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  });

  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  });

  /// Deletes the version-10 origin-keyed secret without ever reading it.
  ///
  /// This is intentionally the only legacy operation. A successful cleanup is
  /// followed by discovery and fresh instance-scoped credential selection; the
  /// old secret is never returned, sent, or rekeyed.
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  });
}

/// Linearizes every credential-store operation in synchronous invocation order.
///
/// The queue is global by design: legacy cleanup, instance reads, writes, and
/// deletes all observe one order, and a failed operation completes its caller
/// with that error without poisoning later queued work. Token values never
/// enter queue metadata, logs, or SQLite.
final class SerializedRefreshTokenStore implements RefreshTokenStore {
  SerializedRefreshTokenStore(this._inner);

  final RefreshTokenStore _inner;
  Future<void> _tail = Future<void>.value();

  Future<T> _enqueue<T>(Future<T> Function() operation) {
    final completion = Completer<T>();
    _tail = _tail.then((_) async {
      try {
        completion.complete(await operation());
      } on Object catch (error, stackTrace) {
        completion.completeError(error, stackTrace);
      }
    });
    return completion.future;
  }

  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) => _enqueue(
    () => _inner.read(profileId: profileId, serverInstanceId: serverInstanceId),
  );

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) => _enqueue(
    () => _inner.write(
      profileId: profileId,
      serverInstanceId: serverInstanceId,
      refreshToken: refreshToken,
    ),
  );

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) => _enqueue(
    () =>
        _inner.delete(profileId: profileId, serverInstanceId: serverInstanceId),
  );

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) => _enqueue(
    () => _inner.deleteLegacyOriginToken(
      profileId: profileId,
      serverOrigin: serverOrigin,
    ),
  );
}

/// Minimal secure-storage seam used to verify exact key traffic without
/// exposing token values to logs or SQLite.
abstract interface class RefreshTokenSecureStorage {
  Future<String?> read({required String key});

  Future<void> write({required String key, required String value});

  Future<void> delete({required String key});
}

final class FlutterRefreshTokenSecureStorage
    implements RefreshTokenSecureStorage {
  FlutterRefreshTokenSecureStorage({FlutterSecureStorage? storage})
    : _storage =
          storage ??
          // useDataProtectionKeyChain: false uses the legacy macOS keychain,
          // which avoids the data-protection entitlement requirement
          // (errSecMissingEntitlement / -34018) for locally-signed builds.
          const FlutterSecureStorage(
            mOptions: MacOsOptions(useDataProtectionKeyChain: false),
          );

  final FlutterSecureStorage _storage;

  @override
  Future<String?> read({required String key}) => _storage.read(key: key);

  @override
  Future<void> write({required String key, required String value}) =>
      _storage.write(key: key, value: value);

  @override
  Future<void> delete({required String key}) => _storage.delete(key: key);
}

final class SecureRefreshTokenStore implements RefreshTokenStore {
  SecureRefreshTokenStore({RefreshTokenSecureStorage? storage})
    : _storage = storage ?? FlutterRefreshTokenSecureStorage();

  final RefreshTokenSecureStorage _storage;

  static String _profileComponent(String profileId) =>
      base64Url.encode(utf8.encode(profileId)).replaceAll('=', '');

  static String _instanceKey(
    String profileId,
    RomdServerInstanceId serverInstanceId,
  ) =>
      'romd_refresh_token::v2::profile:${_profileComponent(profileId)}'
      '::instance:${serverInstanceId.value}';

  // Preserve the exact shipped version-10 key algorithm only for deletion.
  static String _legacyOriginKey(String profileId, Uri serverOrigin) =>
      'romd_refresh_token::${serverOrigin.toString()}::$profileId';

  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) => _storage.read(key: _instanceKey(profileId, serverInstanceId));

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) => _storage.write(
    key: _instanceKey(profileId, serverInstanceId),
    value: refreshToken,
  );

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) => _storage.delete(key: _instanceKey(profileId, serverInstanceId));

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) => _storage.delete(key: _legacyOriginKey(profileId, serverOrigin));
}
