import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/profile_credential_bootstrap.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/data/server_discovery_api_client.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

void main() {
  final originA = Uri.parse('https://a.example');
  final instanceA = RomdServerInstanceId.tryParse(
    '11111111-1111-4111-8111-111111111111',
  )!;
  final instanceB = RomdServerInstanceId.tryParse(
    '22222222-2222-4222-8222-222222222222',
  )!;

  test('orders cleanup then discovery and defers exact token read', () async {
    final events = <String>[];
    final store = _FakeRefreshTokenStore(events: events);
    final bootstrap = ProfileCredentialBootstrap(
      refreshTokenStore: store,
      createServerDiscoveryApiClient: (origin) => _FakeDiscoveryClient(
        events: events,
        origin: origin,
        result: ServerDiscoverySuccess(instanceA),
      ),
    );

    final result = await bootstrap.run(
      profileId: 'profile-1',
      serverOrigin: originA,
    );

    expect(
      result,
      isA<ProfileCredentialBootstrapReady>().having(
        (ready) => ready.serverInstanceId,
        'instance',
        instanceA,
      ),
    );
    expect(events, <String>[
      'cleanup:profile-1:$originA',
      'discover:$originA',
      'close:$originA',
    ]);
    expect(store.readCount, 0);
  });

  test(
    'cleanup failure blocks all later work and retries after restart',
    () async {
      final events = <String>[];
      final store = _FakeRefreshTokenStore(
        events: events,
        cleanupError: StateError('secure storage unavailable'),
      );
      var discoveryCreations = 0;

      ProfileCredentialBootstrap createBootstrap() =>
          ProfileCredentialBootstrap(
            refreshTokenStore: store,
            createServerDiscoveryApiClient: (_) {
              discoveryCreations++;
              return _FakeDiscoveryClient(
                events: events,
                origin: originA,
                result: ServerDiscoverySuccess(instanceA),
              );
            },
          );

      expect(
        await createBootstrap().run(
          profileId: 'profile-1',
          serverOrigin: originA,
        ),
        isA<ProfileCredentialLegacyCleanupFailed>(),
      );
      expect(
        await createBootstrap().run(
          profileId: 'profile-1',
          serverOrigin: originA,
        ),
        isA<ProfileCredentialLegacyCleanupFailed>(),
      );

      expect(discoveryCreations, 0);
      expect(store.readCount, 0);
      expect(events, <String>[
        'cleanup:profile-1:$originA',
        'cleanup:profile-1:$originA',
      ]);
    },
  );

  test('discovery failure blocks instance token reads', () async {
    final events = <String>[];
    final store = _FakeRefreshTokenStore(events: events);
    final bootstrap = ProfileCredentialBootstrap(
      refreshTokenStore: store,
      createServerDiscoveryApiClient: (origin) => _FakeDiscoveryClient(
        events: events,
        origin: origin,
        result: const ServerDiscoveryFailure(
          ServerDiscoveryFailureKind.transport,
        ),
      ),
    );

    expect(
      await bootstrap.run(profileId: 'profile-1', serverOrigin: originA),
      isA<ProfileCredentialDiscoveryFailed>(),
    );
    expect(store.readCount, 0);
  });

  test('known reconnect rejects replacement before any token read', () async {
    final events = <String>[];
    final store = _FakeRefreshTokenStore(events: events)
      ..tokens[_tokenKey('profile-1', instanceA)] = 'former-token'
      ..tokens[_tokenKey('profile-1', instanceB)] = 'replacement-token';
    final bootstrap = ProfileCredentialBootstrap(
      refreshTokenStore: store,
      createServerDiscoveryApiClient: (origin) => _FakeDiscoveryClient(
        events: events,
        origin: origin,
        result: ServerDiscoverySuccess(instanceB),
      ),
    );

    final result = await bootstrap.run(
      profileId: 'profile-1',
      serverOrigin: originA,
      expectedServerInstanceId: instanceA,
    );

    expect(
      result,
      isA<ProfileCredentialServerReplaced>().having(
        (replaced) => replaced.discoveredServerInstanceId,
        'replacement',
        instanceB,
      ),
    );
    expect(store.readCount, 0);
    expect(events, <String>[
      'cleanup:profile-1:$originA',
      'discover:$originA',
      'close:$originA',
    ]);
  });
}

String _tokenKey(String profileId, RomdServerInstanceId serverInstanceId) =>
    '$profileId::${serverInstanceId.value}';

final class _FakeRefreshTokenStore implements RefreshTokenStore {
  _FakeRefreshTokenStore({required this.events, this.cleanupError});

  final List<String> events;
  final Object? cleanupError;
  final Map<String, String> tokens = <String, String>{};
  int readCount = 0;

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) async {
    events.add('cleanup:$profileId:$serverOrigin');
    if (cleanupError case final error?) {
      throw error;
    }
  }

  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    readCount++;
    events.add('read:$profileId:${serverInstanceId.value}');
    return tokens[_tokenKey(profileId, serverInstanceId)];
  }

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) async {
    tokens[_tokenKey(profileId, serverInstanceId)] = refreshToken;
  }

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    tokens.remove(_tokenKey(profileId, serverInstanceId));
  }
}

final class _FakeDiscoveryClient implements ServerDiscoveryApiClient {
  _FakeDiscoveryClient({
    required this.events,
    required this.origin,
    required this.result,
  });

  final List<String> events;
  final Uri origin;
  final ServerDiscoveryResult result;

  @override
  Future<ServerDiscoveryResult> discover() async {
    events.add('discover:$origin');
    return result;
  }

  @override
  void close() {
    events.add('close:$origin');
  }
}
