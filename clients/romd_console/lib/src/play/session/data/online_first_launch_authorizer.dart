import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/data/release_access_api_client.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/session/domain/launch_authorization.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

import 'launch_authorization_store.dart';

typedef LaunchAccessTokenProvider = String? Function();

/// Online-first one-release authorization with exact cached fallback.
/// Server failures and authentication restoration are never decisions.
final class OnlineFirstLaunchAuthorizer implements LaunchAuthorizer {
  OnlineFirstLaunchAuthorizer({
    required this.authority,
    required DriftLaunchAuthorizationStore store,
    required ReleaseAccessApiClient accessApiClient,
    AuthenticatedRequestExecutor? authenticatedSession,
    LaunchAccessTokenProvider? accessTokenProvider,
    DateTime Function()? now,
  }) : _store = store,
       _access = accessApiClient,
       _authenticatedSession = authenticatedSession,
       _accessTokenProvider = accessTokenProvider,
       _now = now ?? DateTime.now;

  @override
  final InstallAuthorityContext? authority;
  final DriftLaunchAuthorizationStore _store;
  final ReleaseAccessApiClient _access;
  final AuthenticatedRequestExecutor? _authenticatedSession;
  final LaunchAccessTokenProvider? _accessTokenProvider;
  final DateTime Function() _now;
  bool _closed = false;

  @override
  Future<LaunchAuthorizationResult> authorize(PlayRequest request) async {
    final activeAuthority = authority;
    final releaseId = RomdPublicId.tryParse(request.releaseId);
    final titleId = RomdPublicId.tryParse(request.titleId);
    if (_closed ||
        activeAuthority == null ||
        releaseId == null ||
        titleId == null) {
      return const AuthorizationNotGranted();
    }

    final read = await _safeRead(
      authority: activeAuthority,
      releaseId: releaseId,
      titleId: titleId,
    );
    if (_closed) return const LaunchAuthorizationUnavailable();
    final LaunchAuthorizationSnapshot snapshot;
    switch (read) {
      case LaunchAuthorizationFound(snapshot: final found):
        snapshot = found;
      case LaunchAuthorizationNotFound():
        return const AuthorizationNotGranted();
      case LaunchAuthorizationInconsistent():
        return const LaunchAuthorizationUnavailable();
    }

    final cachedAuthorized =
        snapshot.authorization == ProfileGameAuthorization.authorized;
    final ReleaseAccessResult result;
    try {
      final authenticatedSession = _authenticatedSession;
      if (authenticatedSession != null) {
        result = await authenticatedSession.execute<ReleaseAccessResult>(
          request: (token) => _getAccess(
            token: token,
            authority: activeAuthority,
            releaseId: releaseId,
            titleId: titleId,
          ),
          // The access-check POST is read-like, and an invalid_token challenge
          // rejects it in authentication before endpoint execution.
          replayPolicy: AuthenticatedReplayPolicy.onceAfterInvalidToken,
          isInvalidTokenResult: (result) =>
              result is ReleaseAccessFailure && result.isInvalidToken,
        );
      } else {
        final token = _accessTokenProvider?.call();
        if (token == null || token.trim().isEmpty) {
          return _cachedResult(
            cachedAuthorized,
            activeAuthority,
            releaseId,
            titleId,
          );
        }
        result = await _getAccess(
          token: token,
          authority: activeAuthority,
          releaseId: releaseId,
          titleId: titleId,
        );
      }
    } on Object {
      return _cachedFallbackAfterAwait(
        authority: activeAuthority,
        releaseId: releaseId,
        titleId: titleId,
        expected: snapshot,
      );
    }
    if (_closed) return const LaunchAuthorizationUnavailable();

    switch (result) {
      case ReleaseAccessSuccess(decision: ReleaseAccessRevoked()):
        // A successful explicit revoke blocks this attempt even if persistence
        // loses a race or storage becomes unavailable.
        await _safeWrite(
          authority: activeAuthority,
          releaseId: releaseId,
          titleId: titleId,
          expected: snapshot,
          authorization: ProfileGameAuthorization.revoked,
        );
        return const AuthorizationRevoked();
      case ReleaseAccessSuccess(decision: ReleaseAccessAllowed()):
        final write = await _safeWrite(
          authority: activeAuthority,
          releaseId: releaseId,
          titleId: titleId,
          expected: snapshot,
          authorization: ProfileGameAuthorization.authorized,
        );
        return write == LaunchAuthorizationWriteResult.updated && !_closed
            ? LaunchAuthorized(
                authority: activeAuthority,
                releaseId: releaseId,
                titleId: titleId,
              )
            : const LaunchAuthorizationUnavailable();
      case ReleaseAccessFailure():
        return _cachedFallbackAfterAwait(
          authority: activeAuthority,
          releaseId: releaseId,
          titleId: titleId,
          expected: snapshot,
        );
    }
  }

  Future<LaunchAuthorizationResult> _cachedFallbackAfterAwait({
    required InstallAuthorityContext authority,
    required RomdPublicId releaseId,
    required RomdPublicId titleId,
    required LaunchAuthorizationSnapshot expected,
  }) async {
    final current = await _safeRead(
      authority: authority,
      releaseId: releaseId,
      titleId: titleId,
    );
    if (_closed) return const LaunchAuthorizationUnavailable();
    switch (current) {
      case LaunchAuthorizationNotFound():
        return const AuthorizationNotGranted();
      case LaunchAuthorizationInconsistent():
        return const LaunchAuthorizationUnavailable();
      case LaunchAuthorizationFound(snapshot: final snapshot):
        if (!snapshot.hasSameState(expected)) {
          return const LaunchAuthorizationUnavailable();
        }
        return _cachedResult(
          snapshot.authorization == ProfileGameAuthorization.authorized,
          authority,
          releaseId,
          titleId,
        );
    }
  }

  Future<LaunchAuthorizationReadResult> _safeRead({
    required InstallAuthorityContext authority,
    required RomdPublicId releaseId,
    required RomdPublicId titleId,
  }) async {
    try {
      return await _store.read(
        authority: authority,
        releaseId: releaseId,
        titleId: titleId,
      );
    } on Object {
      return const LaunchAuthorizationInconsistent();
    }
  }

  Future<ReleaseAccessResult> _getAccess({
    required String token,
    required InstallAuthorityContext authority,
    required RomdPublicId releaseId,
    required RomdPublicId titleId,
  }) async {
    try {
      return await _access.getReleaseAccess(
        accessToken: token,
        expectedServerInstanceId: authority.connection.instanceId,
        releaseId: releaseId,
        expectedTitleId: titleId,
      );
    } on Object {
      return const ReleaseAccessFailure(ReleaseAccessFailureKind.transport);
    }
  }

  Future<LaunchAuthorizationWriteResult> _safeWrite({
    required InstallAuthorityContext authority,
    required RomdPublicId releaseId,
    required RomdPublicId titleId,
    required LaunchAuthorizationSnapshot expected,
    required ProfileGameAuthorization authorization,
  }) async {
    try {
      return await _store.compareAndSet(
        authority: authority,
        releaseId: releaseId,
        titleId: titleId,
        expected: expected,
        authorization: authorization,
        checkedAt: _now(),
      );
    } on Object {
      return LaunchAuthorizationWriteResult.inconsistent;
    }
  }

  LaunchAuthorizationResult _cachedResult(
    bool cachedAuthorized,
    InstallAuthorityContext authority,
    RomdPublicId releaseId,
    RomdPublicId titleId,
  ) => cachedAuthorized
      ? LaunchAuthorized(
          authority: authority,
          releaseId: releaseId,
          titleId: titleId,
        )
      : const AuthorizationRevoked();

  @override
  void close() {
    if (_closed) return;
    _closed = true;
    _access.close();
  }
}
