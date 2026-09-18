import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';

void main() {
  final instanceId = RomdServerInstanceId.tryParse(
    '11111111-1111-4111-8111-111111111111',
  )!;
  final authority = InstallAuthorityContext(
    localProfileId: 'profile-1',
    connection: ProfileServerConnection(
      instanceId: instanceId,
      origin: Uri.parse('https://romd.example'),
      firstSeenAt: DateTime.utc(2026),
      lastSeenAt: DateTime.utc(2026),
    ),
    generation: 7,
  );
  late DateTime now;
  late bool authorityCurrent;
  late _Store store;
  late _ConsumerApi api;
  late List<SessionTelemetryEvent> telemetry;
  late ProfileSessionCoordinator coordinator;

  ConsumerLoginSession session(
    String token,
    String refreshToken, {
    Duration lifetime = const Duration(minutes: 10),
  }) => ConsumerLoginSession(
    token: token,
    tokenType: 'Bearer',
    refreshToken: refreshToken,
    expiresAt: now.add(lifetime),
    account: const ConsumerAccount(
      id: 'account-1',
      username: 'player',
      email: 'player@example.com',
    ),
  );

  ProfileSessionCoordinator createCoordinator() => ProfileSessionCoordinator(
    authority: authority,
    consumerApiClient: api,
    refreshTokenStore: store,
    isAuthorityCurrent: () => authorityCurrent,
    now: () => now,
    telemetry: telemetry.add,
  );

  setUp(() {
    now = DateTime.utc(2026, 7, 19, 12);
    authorityCurrent = true;
    store = _Store();
    api = _ConsumerApi();
    telemetry = <SessionTelemetryEvent>[];
    coordinator = createCoordinator();
  });

  tearDown(() => coordinator.dispose());

  test(
    'restore rotates storage before publishing the complete session',
    () async {
      store.tokens[_key('profile-1', instanceId)] = 'refresh-old';
      api.results.add(
        ConsumerLoginSuccess(session('access-new', 'refresh-new')),
      );
      final observedSessionsAtWrite = <ConsumerLoginSession?>[];
      store.beforeWrite = () =>
          observedSessionsAtWrite.add(coordinator.session);

      final result = await coordinator.restore();

      expect(result, isA<SessionRefreshSuccess>());
      expect(api.refreshTokens, <String>['refresh-old']);
      expect(observedSessionsAtWrite, <ConsumerLoginSession?>[null]);
      expect(store.tokens[_key('profile-1', instanceId)], 'refresh-new');
      expect(coordinator.session?.token, 'access-new');
      expect(coordinator.credentialGeneration, 1);
    },
  );

  test('proactively refreshes inside skew and uses the new token', () async {
    await coordinator.acceptAuthenticatedSession(
      session(
        'access-old',
        'refresh-old',
        lifetime: const Duration(seconds: 60),
      ),
    );
    api.results.add(ConsumerLoginSuccess(session('access-new', 'refresh-new')));
    now = now.add(const Duration(seconds: 20));

    final used = await coordinator.execute<String>(
      request: (token) async => token,
    );

    expect(used, 'access-new');
    expect(api.calls, 1);
  });

  test('resume refreshes only when the access token is inside skew', () async {
    await coordinator.acceptAuthenticatedSession(
      session(
        'access-old',
        'refresh-old',
        lifetime: const Duration(seconds: 60),
      ),
    );
    expect(
      await coordinator.refreshIfNeeded(SessionRefreshReason.resume),
      isA<SessionRefreshSuccess>(),
    );
    expect(api.calls, 0);

    now = now.add(const Duration(seconds: 20));
    api.results.add(ConsumerLoginSuccess(session('access-new', 'refresh-new')));
    await coordinator.refreshIfNeeded(SessionRefreshReason.resume);

    expect(api.calls, 1);
    expect(telemetry.single.reason, SessionRefreshReason.resume);
  });

  test('ten concurrent invalid_token responses share one rotation', () async {
    await coordinator.acceptAuthenticatedSession(
      session('access-old', 'refresh-old'),
    );
    final exchange = Completer<ConsumerLoginResult>();
    api.pending = exchange;

    final requests = List<Future<String>>.generate(
      10,
      (_) => coordinator.execute<String>(
        request: (token) async => token == 'access-old' ? 'invalid' : token,
        replayPolicy: AuthenticatedReplayPolicy.onceAfterInvalidToken,
        isInvalidTokenResult: (result) => result == 'invalid',
      ),
    );
    await _until(() => api.calls == 1);
    exchange.complete(
      ConsumerLoginSuccess(session('access-new', 'refresh-new')),
    );

    expect(await Future.wait(requests), everyElement('access-new'));
    expect(api.calls, 1);
    expect(store.tokens[_key('profile-1', instanceId)], 'refresh-new');
  });

  test(
    'safe request is replayed when another call rotates its token',
    () async {
      await coordinator.acceptAuthenticatedSession(
        session('access-old', 'refresh-old'),
      );
      final firstResponse = Completer<String>();
      var requestCalls = 0;
      final pending = coordinator.execute<String>(
        request: (token) {
          requestCalls++;
          return requestCalls == 1 ? firstResponse.future : Future.value(token);
        },
        replayPolicy: AuthenticatedReplayPolicy.onceAfterInvalidToken,
      );
      await _until(() => requestCalls == 1);
      await coordinator.acceptAuthenticatedSession(
        session('access-new', 'refresh-new'),
      );

      firstResponse.complete('response-from-old-generation');

      expect(await pending, 'access-new');
      expect(requestCalls, 2);
    },
  );

  test(
    'non-replayable request rejects a concurrently rotated result',
    () async {
      await coordinator.acceptAuthenticatedSession(
        session('access-old', 'refresh-old'),
      );
      final firstResponse = Completer<String>();
      final pending = coordinator.execute<String>(
        request: (_) => firstResponse.future,
      );
      await Future<void>.delayed(Duration.zero);
      await coordinator.acceptAuthenticatedSession(
        session('access-new', 'refresh-new'),
      );
      firstResponse.complete('response-from-old-generation');

      await expectLater(
        pending,
        throwsA(
          isA<SessionUnavailableException>().having(
            (error) => error.failure,
            'failure',
            SessionRefreshFailureKind.staleCredential,
          ),
        ),
      );
    },
  );

  test('authority change discards an in-flight refresh result', () async {
    store.tokens[_key('profile-1', instanceId)] = 'refresh-old';
    final exchange = Completer<ConsumerLoginResult>();
    api.pending = exchange;
    final pending = coordinator.restore();
    await _until(() => api.calls == 1);
    authorityCurrent = false;
    exchange.complete(
      ConsumerLoginSuccess(session('access-new', 'refresh-new')),
    );

    expect(
      await pending,
      isA<SessionRefreshFailure>().having(
        (failure) => failure.kind,
        'kind',
        SessionRefreshFailureKind.staleAuthority,
      ),
    );
    expect(store.writes, isEmpty);
    expect(coordinator.session, isNull);
  });

  test('sign-out invalidates an in-flight rotation before storage', () async {
    store.tokens[_key('profile-1', instanceId)] = 'refresh-old';
    final exchange = Completer<ConsumerLoginResult>();
    api.pending = exchange;
    final pending = coordinator.restore();
    await _until(() => api.calls == 1);

    await coordinator.signOut();
    expect(api.revokedTokens, ['refresh-old']);
    exchange.complete(
      ConsumerLoginSuccess(session('access-stale', 'refresh-stale')),
    );
    final result = await pending;

    expect(
      result,
      isA<SessionRefreshFailure>().having(
        (failure) => failure.kind,
        'kind',
        SessionRefreshFailureKind.staleAuthority,
      ),
    );
    expect(coordinator.session, isNull);
    expect(store.tokens[_key('profile-1', instanceId)], isNull);
    expect(store.writes, isEmpty);
  });

  test(
    'transient failure retains refresh storage and valid access token',
    () async {
      await coordinator.acceptAuthenticatedSession(
        session('access-old', 'refresh-old'),
      );
      api.results.add(
        const ConsumerLoginFailure(
          'offline',
          kind: ConsumerLoginFailureKind.transient,
        ),
      );

      final result = await coordinator.restore();

      expect(
        result,
        isA<SessionRefreshFailure>().having(
          (failure) => failure.kind,
          'kind',
          SessionRefreshFailureKind.transient,
        ),
      );
      expect(store.deletes, isEmpty);
      expect(store.tokens[_key('profile-1', instanceId)], 'refresh-old');
      expect(coordinator.session?.token, 'access-old');
    },
  );

  test('invalid_grant clears only the exact authority credential', () async {
    await coordinator.acceptAuthenticatedSession(
      session('access-old', 'refresh-old'),
    );
    store.tokens['another-profile::${instanceId.value}'] = 'keep-me';
    api.results.add(const ConsumerLoginFailure('expired'));

    final result = await coordinator.restore();

    expect(
      result,
      isA<SessionRefreshFailure>().having(
        (failure) => failure.kind,
        'kind',
        SessionRefreshFailureKind.invalidGrant,
      ),
    );
    expect(coordinator.session, isNull);
    expect(store.tokens[_key('profile-1', instanceId)], isNull);
    expect(store.tokens['another-profile::${instanceId.value}'], 'keep-me');
  });

  test(
    'rotated-token storage failure does not publish new access state',
    () async {
      store.tokens[_key('profile-1', instanceId)] = 'refresh-old';
      store.writeError = StateError('secure storage unavailable');
      api.results.add(
        ConsumerLoginSuccess(session('access-new', 'refresh-new')),
      );

      final result = await coordinator.restore();

      expect(
        result,
        isA<SessionRefreshFailure>().having(
          (failure) => failure.kind,
          'kind',
          SessionRefreshFailureKind.credentialStorage,
        ),
      );
      expect(coordinator.session, isNull);
      expect(store.tokens[_key('profile-1', instanceId)], 'refresh-old');
    },
  );

  test('secure-storage read failure has an explicit failure kind', () async {
    store.readError = StateError('secure storage unavailable');

    final result = await coordinator.restore();

    expect(
      result,
      isA<SessionRefreshFailure>().having(
        (failure) => failure.kind,
        'kind',
        SessionRefreshFailureKind.credentialStorage,
      ),
    );
    expect(api.calls, 0);
  });

  test('a second invalid_token is returned without another retry', () async {
    await coordinator.acceptAuthenticatedSession(
      session('access-old', 'refresh-old'),
    );
    api.results.add(ConsumerLoginSuccess(session('access-new', 'refresh-new')));
    var requestCalls = 0;

    final result = await coordinator.execute<String>(
      request: (_) async {
        requestCalls++;
        return 'invalid';
      },
      replayPolicy: AuthenticatedReplayPolicy.onceAfterInvalidToken,
      isInvalidTokenResult: (result) => result == 'invalid',
    );

    expect(result, 'invalid');
    expect(requestCalls, 2);
    expect(api.calls, 1);
  });

  test('non-replayable request never refreshes after invalid_token', () async {
    await coordinator.acceptAuthenticatedSession(
      session('access-old', 'refresh-old'),
    );

    final result = await coordinator.execute<String>(
      request: (_) async => 'invalid',
      isInvalidTokenResult: (result) => result == 'invalid',
    );

    expect(result, 'invalid');
    expect(api.calls, 0);
  });

  test('telemetry reports metadata without exposing either token', () async {
    store.tokens[_key('profile-1', instanceId)] = 'refresh-secret';
    api.results.add(
      ConsumerLoginSuccess(session('access-secret', 'rotated-secret')),
    );

    await coordinator.restore();

    expect(telemetry, hasLength(1));
    expect(telemetry.single.reason, SessionRefreshReason.restore);
    expect(telemetry.single.outcome, 'success');
    expect(telemetry.single.authorityGeneration, 7);
    expect(telemetry.single.elapsed, Duration.zero);
    expect(telemetry.single.toString(), isNot(contains('secret')));
  });
}

String _key(String profileId, RomdServerInstanceId instanceId) =>
    '$profileId::${instanceId.value}';

Future<void> _until(bool Function() condition) async {
  for (var attempt = 0; attempt < 20; attempt++) {
    if (condition()) return;
    await Future<void>.delayed(Duration.zero);
  }
  fail('Condition did not become true.');
}

final class _Store implements RefreshTokenStore {
  final Map<String, String> tokens = <String, String>{};
  final List<String> writes = <String>[];
  final List<String> deletes = <String>[];
  Object? readError;
  Object? writeError;
  Object? deleteError;
  void Function()? beforeWrite;

  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    if (readError case final error?) throw error;
    return tokens[_key(profileId, serverInstanceId)];
  }

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) async {
    beforeWrite?.call();
    if (writeError case final error?) throw error;
    writes.add(_key(profileId, serverInstanceId));
    tokens[_key(profileId, serverInstanceId)] = refreshToken;
  }

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    if (deleteError case final error?) throw error;
    deletes.add(_key(profileId, serverInstanceId));
    tokens.remove(_key(profileId, serverInstanceId));
  }

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) async {}
}

final class _ConsumerApi implements ConsumerApiClient {
  final List<ConsumerLoginResult> results = <ConsumerLoginResult>[];
  final List<String> refreshTokens = <String>[];
  final List<String> revokedTokens = <String>[];

  @override
  Future<void> revokeSession({required String refreshToken}) async {
    revokedTokens.add(refreshToken);
  }

  Completer<ConsumerLoginResult>? pending;
  int calls = 0;

  @override
  Future<ConsumerLoginResult> refreshSession({
    required String refreshToken,
  }) async {
    calls++;
    refreshTokens.add(refreshToken);
    final exchange = pending;
    if (exchange != null) {
      pending = null;
      return exchange.future;
    }
    return results.removeAt(0);
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
