import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

void main() {
  final instanceA = RomdServerInstanceId.tryParse(
    '11111111-1111-4111-8111-111111111111',
  )!;
  final instanceB = RomdServerInstanceId.tryParse(
    '22222222-2222-4222-8222-222222222222',
  )!;

  test('uses the exact versioned profile and instance key', () async {
    final storage = _RecordingSecureStorage();
    final store = SecureRefreshTokenStore(storage: storage);
    const expectedKey =
        'romd_refresh_token::v2::profile:cHJvZmlsZS0x'
        '::instance:11111111-1111-4111-8111-111111111111';

    await store.write(
      profileId: 'profile-1',
      serverInstanceId: instanceA,
      refreshToken: 'secret-a',
    );
    expect(
      await store.read(profileId: 'profile-1', serverInstanceId: instanceA),
      'secret-a',
    );
    await store.delete(profileId: 'profile-1', serverInstanceId: instanceA);

    expect(storage.calls, <String>[
      'write:$expectedKey',
      'read:$expectedKey',
      'delete:$expectedKey',
    ]);
    expect(storage.values, isEmpty);
  });

  test('profile and server instance partitions are independent', () async {
    final storage = _RecordingSecureStorage();
    final store = SecureRefreshTokenStore(storage: storage);

    await store.write(
      profileId: 'profile-1',
      serverInstanceId: instanceA,
      refreshToken: 'profile-1-a',
    );
    await store.write(
      profileId: 'profile-1',
      serverInstanceId: instanceB,
      refreshToken: 'profile-1-b',
    );
    await store.write(
      profileId: 'profile-2',
      serverInstanceId: instanceA,
      refreshToken: 'profile-2-a',
    );

    expect(
      await store.read(profileId: 'profile-1', serverInstanceId: instanceA),
      'profile-1-a',
    );
    expect(
      await store.read(profileId: 'profile-1', serverInstanceId: instanceB),
      'profile-1-b',
    );
    expect(
      await store.read(profileId: 'profile-2', serverInstanceId: instanceA),
      'profile-2-a',
    );
  });

  test('legacy origin secret is delete-only and never rekeyed', () async {
    final storage = _RecordingSecureStorage();
    final store = SecureRefreshTokenStore(storage: storage);
    final origin = Uri.parse('https://library.example:8443');
    const legacyKey =
        'romd_refresh_token::https://library.example:8443::profile-1';
    const instanceKey =
        'romd_refresh_token::v2::profile:cHJvZmlsZS0x'
        '::instance:11111111-1111-4111-8111-111111111111';
    storage.values[legacyKey] = 'legacy-secret';

    await store.deleteLegacyOriginToken(
      profileId: 'profile-1',
      serverOrigin: origin,
    );
    expect(
      await store.read(profileId: 'profile-1', serverInstanceId: instanceA),
      isNull,
    );

    expect(storage.calls, <String>['delete:$legacyKey', 'read:$instanceKey']);
    expect(storage.values, isEmpty);
    expect(storage.calls.where((call) => call == 'read:$legacyKey'), isEmpty);
    expect(storage.calls.where((call) => call.startsWith('write:')), isEmpty);
  });

  test('serializes an older delete before a newer write', () async {
    final inner = _ControllableRefreshTokenStore();
    final store = SerializedRefreshTokenStore(inner);
    inner.values[_partition('profile-1', instanceA)] = 'old-token';
    inner.deleteGate = Completer<void>();

    final delete = store.delete(
      profileId: 'profile-1',
      serverInstanceId: instanceA,
    );
    await inner.deleteStarted.future;
    final write = store.write(
      profileId: 'profile-1',
      serverInstanceId: instanceA,
      refreshToken: 'new-token',
    );

    expect(inner.calls, <String>['delete:profile-1:$instanceA']);
    inner.deleteGate!.complete();
    await Future.wait(<Future<void>>[delete, write]);

    expect(inner.values[_partition('profile-1', instanceA)], 'new-token');
    expect(inner.calls, <String>[
      'delete:profile-1:$instanceA',
      'write:profile-1:$instanceA',
    ]);
  });

  test('serializes two writes in synchronous invocation order', () async {
    final inner = _ControllableRefreshTokenStore();
    final store = SerializedRefreshTokenStore(inner);
    inner.writeGate = Completer<void>();

    final oldWrite = store.write(
      profileId: 'profile-1',
      serverInstanceId: instanceA,
      refreshToken: 'old-token',
    );
    await inner.writeStarted.future;
    final newWrite = store.write(
      profileId: 'profile-1',
      serverInstanceId: instanceA,
      refreshToken: 'new-token',
    );

    expect(inner.calls, <String>['write:profile-1:$instanceA']);
    inner.writeGate!.complete();
    await Future.wait(<Future<void>>[oldWrite, newWrite]);

    expect(inner.values[_partition('profile-1', instanceA)], 'new-token');
    expect(inner.calls, <String>[
      'write:profile-1:$instanceA',
      'write:profile-1:$instanceA',
    ]);
  });

  test('a failed operation does not poison later queued work', () async {
    final inner = _ControllableRefreshTokenStore()..failNextDelete = true;
    final store = SerializedRefreshTokenStore(inner);

    final failedDelete = store.delete(
      profileId: 'profile-1',
      serverInstanceId: instanceA,
    );
    final laterWrite = store.write(
      profileId: 'profile-1',
      serverInstanceId: instanceA,
      refreshToken: 'surviving-token',
    );

    await expectLater(failedDelete, throwsStateError);
    await laterWrite;
    expect(inner.values[_partition('profile-1', instanceA)], 'surviving-token');
  });

  test(
    'queued mutations leave unrelated authority partitions intact',
    () async {
      final inner = _ControllableRefreshTokenStore();
      final store = SerializedRefreshTokenStore(inner);
      inner.values[_partition('profile-2', instanceB)] = 'other-token';

      await store.delete(profileId: 'profile-1', serverInstanceId: instanceA);
      await store.write(
        profileId: 'profile-1',
        serverInstanceId: instanceA,
        refreshToken: 'new-token',
      );

      expect(inner.values[_partition('profile-2', instanceB)], 'other-token');
      expect(inner.values[_partition('profile-1', instanceA)], 'new-token');
    },
  );
}

String _partition(String profileId, RomdServerInstanceId serverInstanceId) =>
    '$profileId:${serverInstanceId.value}';

final class _RecordingSecureStorage implements RefreshTokenSecureStorage {
  final Map<String, String> values = <String, String>{};
  final List<String> calls = <String>[];

  @override
  Future<String?> read({required String key}) async {
    calls.add('read:$key');
    return values[key];
  }

  @override
  Future<void> write({required String key, required String value}) async {
    calls.add('write:$key');
    values[key] = value;
  }

  @override
  Future<void> delete({required String key}) async {
    calls.add('delete:$key');
    values.remove(key);
  }
}

final class _ControllableRefreshTokenStore implements RefreshTokenStore {
  final Map<String, String> values = <String, String>{};
  final List<String> calls = <String>[];
  final Completer<void> deleteStarted = Completer<void>();
  final Completer<void> writeStarted = Completer<void>();
  Completer<void>? deleteGate;
  Completer<void>? writeGate;
  bool failNextDelete = false;
  bool _hasGatedWrite = false;

  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    calls.add('read:$profileId:$serverInstanceId');
    return values[_partition(profileId, serverInstanceId)];
  }

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) async {
    calls.add('write:$profileId:$serverInstanceId');
    if (!writeStarted.isCompleted) {
      writeStarted.complete();
    }
    if (!_hasGatedWrite && writeGate != null) {
      _hasGatedWrite = true;
      await writeGate!.future;
    }
    values[_partition(profileId, serverInstanceId)] = refreshToken;
  }

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    calls.add('delete:$profileId:$serverInstanceId');
    if (!deleteStarted.isCompleted) {
      deleteStarted.complete();
    }
    if (deleteGate != null) {
      await deleteGate!.future;
    }
    if (failNextDelete) {
      failNextDelete = false;
      throw StateError('delete failed');
    }
    values.remove(_partition(profileId, serverInstanceId));
  }

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) async {
    calls.add('legacy-delete:$profileId:$serverOrigin');
  }
}
