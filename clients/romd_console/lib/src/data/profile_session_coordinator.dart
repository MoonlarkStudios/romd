import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';

enum AuthenticatedReplayPolicy { never, onceAfterInvalidToken }

enum SessionRefreshReason { restore, expiry, resume, invalidToken }

enum SessionRefreshFailureKind {
  missingCredential,
  invalidGrant,
  transient,
  protocol,
  credentialStorage,
  staleAuthority,
  staleCredential,
  closed,
  remoteSignOut,
}

sealed class SessionRefreshResult {
  const SessionRefreshResult();
}

final class SessionRefreshSuccess extends SessionRefreshResult {
  const SessionRefreshSuccess(this.session);

  final ConsumerLoginSession session;
}

final class SessionRefreshFailure extends SessionRefreshResult {
  const SessionRefreshFailure(this.kind);

  final SessionRefreshFailureKind kind;
}

final class SessionUnavailableException implements Exception {
  const SessionUnavailableException(this.failure);

  final SessionRefreshFailureKind failure;

  @override
  String toString() => 'Authenticated session unavailable: ${failure.name}';
}

typedef InvalidTokenErrorTest = bool Function(Object error);
typedef SessionTelemetrySink = void Function(SessionTelemetryEvent event);

abstract interface class AuthenticatedRequestExecutor {
  ConsumerLoginSession? get session;
  int get credentialGeneration;

  Future<T> execute<T>({
    required Future<T> Function(String accessToken) request,
    AuthenticatedReplayPolicy replayPolicy = AuthenticatedReplayPolicy.never,
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  });
}

final class SessionTelemetryEvent {
  const SessionTelemetryEvent({
    required this.reason,
    required this.outcome,
    required this.authorityGeneration,
    required this.elapsed,
  });

  final SessionRefreshReason reason;
  final String outcome;
  final int authorityGeneration;
  final Duration elapsed;
}

/// The single authenticated-request and refresh authority for one immutable
/// local-profile + ROMD-server selection generation.
///
/// Refresh-token values only cross this boundary while being read, exchanged,
/// and rotated. Resource callers receive an access token only inside the
/// request callback, and concurrent refreshes share one exchange.
final class ProfileSessionCoordinator extends ChangeNotifier
    implements AuthenticatedRequestExecutor {
  ProfileSessionCoordinator({
    required this.authority,
    required ConsumerApiClient consumerApiClient,
    required RefreshTokenStore refreshTokenStore,
    required bool Function() isAuthorityCurrent,
    DateTime Function()? now,
    this.refreshSkew = const Duration(seconds: 45),
    SessionTelemetrySink? telemetry,
  }) : _consumerApiClient = consumerApiClient,
       _refreshTokenStore = refreshTokenStore,
       _isAuthorityCurrent = isAuthorityCurrent,
       _now = now ?? DateTime.now,
       _telemetry = telemetry;

  final InstallAuthorityContext authority;
  final ConsumerApiClient _consumerApiClient;
  final RefreshTokenStore _refreshTokenStore;
  final bool Function() _isAuthorityCurrent;
  final DateTime Function() _now;
  final SessionTelemetrySink? _telemetry;
  final Duration refreshSkew;

  ConsumerLoginSession? _session;
  Future<SessionRefreshResult>? _refreshInFlight;
  Timer? _proactiveRefreshTimer;
  int _credentialGeneration = 0;
  int _mutationEpoch = 0;
  bool _closed = false;

  ConsumerLoginSession? get session => _session;
  int get credentialGeneration => _credentialGeneration;
  bool get isAuthorityCurrent => !_closed && _isAuthorityCurrent();

  Future<SessionRefreshResult> restore() =>
      _refresh(SessionRefreshReason.restore);

  Future<SessionRefreshResult> acceptAuthenticatedSession(
    ConsumerLoginSession session,
  ) async {
    _mutationEpoch++;
    final acceptEpoch = _mutationEpoch;
    _refreshInFlight = null;
    if (!_isRefreshCurrent(acceptEpoch)) {
      return const SessionRefreshFailure(
        SessionRefreshFailureKind.staleAuthority,
      );
    }
    try {
      await _refreshTokenStore.write(
        profileId: authority.localProfileId,
        serverInstanceId: authority.connection.instanceId,
        refreshToken: session.refreshToken,
      );
    } on Object {
      return const SessionRefreshFailure(
        SessionRefreshFailureKind.credentialStorage,
      );
    }
    if (!_isRefreshCurrent(acceptEpoch)) {
      return const SessionRefreshFailure(
        SessionRefreshFailureKind.staleAuthority,
      );
    }
    _publish(session);
    return SessionRefreshSuccess(session);
  }

  Future<SessionRefreshResult> refreshIfNeeded(SessionRefreshReason reason) {
    if (!isAuthorityCurrent) {
      return Future<SessionRefreshResult>.value(
        const SessionRefreshFailure(SessionRefreshFailureKind.staleAuthority),
      );
    }
    final current = _session;
    if (current == null || _needsRefresh(current)) {
      return _refresh(reason);
    }
    return Future<SessionRefreshResult>.value(SessionRefreshSuccess(current));
  }

  Future<T> execute<T>({
    required Future<T> Function(String accessToken) request,
    AuthenticatedReplayPolicy replayPolicy = AuthenticatedReplayPolicy.never,
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  }) async {
    final initial = await refreshIfNeeded(SessionRefreshReason.expiry);
    final initialSession = switch (initial) {
      SessionRefreshSuccess(:final session) => session,
      SessionRefreshFailure(kind: SessionRefreshFailureKind.transient) =>
        _currentSessionWhileValidOrThrow(),
      SessionRefreshFailure(:final kind) => throw SessionUnavailableException(
        kind,
      ),
    };
    final observedGeneration = _credentialGeneration;

    try {
      final result = await request(initialSession.token);
      if (!isAuthorityCurrent) {
        throw const SessionUnavailableException(
          SessionRefreshFailureKind.staleAuthority,
        );
      }
      final rejected = isInvalidTokenResult?.call(result) ?? false;
      if (!rejected || replayPolicy == AuthenticatedReplayPolicy.never) {
        if (_credentialGeneration != observedGeneration) {
          if (replayPolicy == AuthenticatedReplayPolicy.never) {
            throw const SessionUnavailableException(
              SessionRefreshFailureKind.staleCredential,
            );
          }
          return _replayWithCurrentSession(request);
        }
        return result;
      }
    } on Object catch (error) {
      final rejected = isInvalidTokenError?.call(error) ?? false;
      if (!rejected || replayPolicy == AuthenticatedReplayPolicy.never) {
        rethrow;
      }
    }

    final SessionRefreshResult refreshed;
    if (_credentialGeneration == observedGeneration) {
      refreshed = await _refresh(SessionRefreshReason.invalidToken);
    } else if (_session case final current?) {
      refreshed = SessionRefreshSuccess(current);
    } else {
      refreshed = const SessionRefreshFailure(
        SessionRefreshFailureKind.missingCredential,
      );
    }
    final retrySession = switch (refreshed) {
      SessionRefreshSuccess(:final session) => session,
      SessionRefreshFailure(:final kind) => throw SessionUnavailableException(
        kind,
      ),
    };
    final result = await request(retrySession.token);
    if (!isAuthorityCurrent) {
      throw const SessionUnavailableException(
        SessionRefreshFailureKind.staleAuthority,
      );
    }
    return result;
  }

  Future<T> _replayWithCurrentSession<T>(
    Future<T> Function(String accessToken) request,
  ) async {
    final current = _session;
    if (current == null) {
      throw const SessionUnavailableException(
        SessionRefreshFailureKind.missingCredential,
      );
    }
    final replayGeneration = _credentialGeneration;
    final result = await request(current.token);
    if (!isAuthorityCurrent) {
      throw const SessionUnavailableException(
        SessionRefreshFailureKind.staleAuthority,
      );
    }
    if (_credentialGeneration != replayGeneration) {
      throw const SessionUnavailableException(
        SessionRefreshFailureKind.staleCredential,
      );
    }
    return result;
  }

  Future<SessionRefreshResult> _refresh(SessionRefreshReason reason) {
    if (_closed) {
      return Future<SessionRefreshResult>.value(
        const SessionRefreshFailure(SessionRefreshFailureKind.closed),
      );
    }
    final existing = _refreshInFlight;
    if (existing != null) return existing;

    final pending = _performRefresh(reason);
    _refreshInFlight = pending;
    return pending.whenComplete(() {
      if (identical(_refreshInFlight, pending)) {
        _refreshInFlight = null;
      }
    });
  }

  Future<SessionRefreshResult> _performRefresh(
    SessionRefreshReason reason,
  ) async {
    final refreshEpoch = _mutationEpoch;
    final started = _now();
    SessionRefreshResult finish(SessionRefreshResult result) {
      final outcome = switch (result) {
        SessionRefreshSuccess() => 'success',
        SessionRefreshFailure(:final kind) => kind.name,
      };
      try {
        _telemetry?.call(
          SessionTelemetryEvent(
            reason: reason,
            outcome: outcome,
            authorityGeneration: authority.generation,
            elapsed: _now().difference(started),
          ),
        );
      } on Object {
        // Observability must never become part of authentication correctness.
      }
      return result;
    }

    if (!_isRefreshCurrent(refreshEpoch)) {
      return finish(
        const SessionRefreshFailure(SessionRefreshFailureKind.staleAuthority),
      );
    }

    final String? refreshToken;
    try {
      refreshToken = await _refreshTokenStore.read(
        profileId: authority.localProfileId,
        serverInstanceId: authority.connection.instanceId,
      );
    } on Object {
      return finish(
        const SessionRefreshFailure(
          SessionRefreshFailureKind.credentialStorage,
        ),
      );
    }
    if (!_isRefreshCurrent(refreshEpoch)) {
      return finish(
        const SessionRefreshFailure(SessionRefreshFailureKind.staleAuthority),
      );
    }
    if (refreshToken == null || refreshToken.isEmpty) {
      return finish(
        const SessionRefreshFailure(
          SessionRefreshFailureKind.missingCredential,
        ),
      );
    }

    final ConsumerLoginResult result;
    try {
      result = await _consumerApiClient.refreshSession(
        refreshToken: refreshToken,
      );
    } on Object {
      return finish(
        const SessionRefreshFailure(SessionRefreshFailureKind.transient),
      );
    }
    if (!_isRefreshCurrent(refreshEpoch)) {
      return finish(
        const SessionRefreshFailure(SessionRefreshFailureKind.staleAuthority),
      );
    }

    switch (result) {
      case ConsumerLoginSuccess(:final session):
        try {
          await _refreshTokenStore.write(
            profileId: authority.localProfileId,
            serverInstanceId: authority.connection.instanceId,
            refreshToken: session.refreshToken,
          );
        } on Object {
          return finish(
            const SessionRefreshFailure(
              SessionRefreshFailureKind.credentialStorage,
            ),
          );
        }
        if (!_isRefreshCurrent(refreshEpoch)) {
          return finish(
            const SessionRefreshFailure(
              SessionRefreshFailureKind.staleAuthority,
            ),
          );
        }
        _publish(session);
        return finish(SessionRefreshSuccess(session));
      case ConsumerLoginFailure(:final kind):
        final failureKind = switch (kind) {
          ConsumerLoginFailureKind.invalidGrant =>
            SessionRefreshFailureKind.invalidGrant,
          ConsumerLoginFailureKind.transient =>
            SessionRefreshFailureKind.transient,
          ConsumerLoginFailureKind.protocol =>
            SessionRefreshFailureKind.protocol,
        };
        if (failureKind == SessionRefreshFailureKind.invalidGrant) {
          _clearPublishedSession();
          try {
            await _refreshTokenStore.delete(
              profileId: authority.localProfileId,
              serverInstanceId: authority.connection.instanceId,
            );
          } on Object {
            return finish(
              const SessionRefreshFailure(
                SessionRefreshFailureKind.credentialStorage,
              ),
            );
          }
        }
        return finish(SessionRefreshFailure(failureKind));
    }
  }

  bool _needsRefresh(ConsumerLoginSession session) =>
      !session.expiresAt.isAfter(_now().add(refreshSkew));

  bool _isRefreshCurrent(int refreshEpoch) =>
      refreshEpoch == _mutationEpoch && isAuthorityCurrent;

  ConsumerLoginSession _currentSessionWhileValidOrThrow() {
    final current = _session;
    if (current != null && current.expiresAt.isAfter(_now())) return current;
    throw const SessionUnavailableException(
      SessionRefreshFailureKind.transient,
    );
  }

  void _publish(ConsumerLoginSession session) {
    _session = session;
    _credentialGeneration++;
    _scheduleProactiveRefresh(session);
    notifyListeners();
  }

  void _scheduleProactiveRefresh(ConsumerLoginSession session) {
    _proactiveRefreshTimer?.cancel();
    final delay = session.expiresAt.difference(_now()) - refreshSkew;
    _proactiveRefreshTimer = Timer(
      delay.isNegative ? Duration.zero : delay,
      () => unawaited(_refresh(SessionRefreshReason.expiry)),
    );
  }

  void clearSession() => _clearPublishedSession();

  Future<SessionRefreshResult> signOut() async {
    _mutationEpoch++;
    _refreshInFlight = null;
    final refreshToken = _session?.refreshToken;
    _clearPublishedSession();
    var remoteFailed = false;
    try {
      final token =
          refreshToken ??
          await _refreshTokenStore.read(
            profileId: authority.localProfileId,
            serverInstanceId: authority.connection.instanceId,
          );
      if (token != null) {
        await _consumerApiClient
            .revokeSession(refreshToken: token)
            .timeout(const Duration(seconds: 5));
      }
    } on Object {
      remoteFailed = true;
    }
    try {
      await _refreshTokenStore.delete(
        profileId: authority.localProfileId,
        serverInstanceId: authority.connection.instanceId,
      );
    } on Object {
      return const SessionRefreshFailure(
        SessionRefreshFailureKind.credentialStorage,
      );
    }
    return SessionRefreshFailure(
      remoteFailed
          ? SessionRefreshFailureKind.remoteSignOut
          : SessionRefreshFailureKind.missingCredential,
    );
  }

  void _clearPublishedSession() {
    _proactiveRefreshTimer?.cancel();
    _proactiveRefreshTimer = null;
    if (_session == null) return;
    _session = null;
    _credentialGeneration++;
    notifyListeners();
  }

  @override
  void dispose() {
    _mutationEpoch++;
    _closed = true;
    _proactiveRefreshTimer?.cancel();
    _clearPublishedSession();
    super.dispose();
  }
}
