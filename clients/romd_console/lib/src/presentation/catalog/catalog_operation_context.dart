import 'package:flutter/material.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';

/// Immutable identity captured for one Catalog route or request.
///
/// The root owns [_isAuthorityCurrent]. Presentation code may retain a route
/// while that exact profile/server authority remains active, but may render a
/// response only while [isRequestCurrent] still has a live session. The
/// coordinator itself invalidates or safely replays work that crosses an
/// access-token generation.
final class CatalogOperationContext implements InstallOperationLease {
  CatalogOperationContext({
    required this.authority,
    required this.epoch,
    AuthenticatedRequestExecutor? authenticatedSession,
    bool Function()? isAuthorityCurrent,
    String? accessToken,
    bool Function()? isCurrent,
    this.changes,
  }) : assert(authenticatedSession != null || accessToken != null),
       assert(isAuthorityCurrent != null || isCurrent != null),
       authenticatedSession =
           authenticatedSession ??
           _FixedAuthenticatedRequestExecutor(accessToken!),
       _isAuthorityCurrent = isAuthorityCurrent ?? isCurrent!;

  final InstallAuthorityContext authority;
  final AuthenticatedRequestExecutor authenticatedSession;
  final int epoch;
  final bool Function() _isAuthorityCurrent;
  final Listenable? changes;

  bool get isAuthorityCurrent => _isAuthorityCurrent();
  @override
  bool get isCurrent => isAuthorityCurrent;
  bool get isRequestCurrent =>
      isAuthorityCurrent && authenticatedSession.session != null;

  Future<T> execute<T>({
    required Future<T> Function(String accessToken) request,
    AuthenticatedReplayPolicy replayPolicy = AuthenticatedReplayPolicy.never,
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  }) => authenticatedSession.execute<T>(
    request: request,
    replayPolicy: replayPolicy,
    isInvalidTokenError: isInvalidTokenError,
    isInvalidTokenResult: isInvalidTokenResult,
  );

  Future<T> executeConsumer<T>(
    Future<T> Function(String accessToken) request,
  ) => execute<T>(
    request: request,
    replayPolicy: AuthenticatedReplayPolicy.onceAfterInvalidToken,
    isInvalidTokenError: (error) =>
        error is ConsumerApiException && error.isInvalidToken,
  );

  bool hasSameOperation(CatalogOperationContext other) =>
      authority.hasSameAuthority(other.authority) &&
      epoch == other.epoch &&
      identical(authenticatedSession, other.authenticatedSession);
}

/// Backward-compatible isolated-test seam. Production always supplies the
/// root-owned coordinator.
final class _FixedAuthenticatedRequestExecutor
    implements AuthenticatedRequestExecutor {
  const _FixedAuthenticatedRequestExecutor(this.accessToken);

  final String accessToken;

  @override
  int get credentialGeneration => 0;

  @override
  ConsumerLoginSession get session => ConsumerLoginSession(
    token: accessToken,
    tokenType: 'Bearer',
    refreshToken: 'isolated-test-session',
    expiresAt: DateTime.utc(9999),
    account: const ConsumerAccount(
      id: 'isolated-test-account',
      username: 'test',
      email: 'test@localhost',
    ),
  );

  @override
  Future<T> execute<T>({
    required Future<T> Function(String accessToken) request,
    AuthenticatedReplayPolicy replayPolicy = AuthenticatedReplayPolicy.never,
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  }) => request(accessToken);
}

/// Opaque boundary used by Catalog-owned routes.
///
/// Invalidating an operation replaces the complete subtree before route
/// teardown animations can expose names, artwork, counts, or descriptions
/// from the former profile/server authority.
final class CatalogPrivacyBoundary extends StatelessWidget {
  const CatalogPrivacyBoundary({
    required this.operation,
    required this.child,
    super.key,
  });

  final CatalogOperationContext? operation;
  final Widget child;

  static CatalogOperationContext? maybeOperationOf(BuildContext context) =>
      context
          .dependOnInheritedWidgetOfExactType<_CatalogOperationScope>()
          ?.operation;

  @override
  Widget build(BuildContext context) {
    final current = operation;
    if (current == null) return child;
    final changes = current.changes;
    if (changes == null) {
      return current.isRequestCurrent
          ? _CatalogOperationScope(operation: current, child: child)
          : const CatalogPrivacyShield();
    }
    return ListenableBuilder(
      listenable: changes,
      builder: (context, _) => current.isRequestCurrent
          ? _CatalogOperationScope(operation: current, child: child)
          : const CatalogPrivacyShield(),
    );
  }
}

final class _CatalogOperationScope extends InheritedWidget {
  const _CatalogOperationScope({required this.operation, required super.child});

  final CatalogOperationContext operation;

  @override
  bool updateShouldNotify(_CatalogOperationScope oldWidget) =>
      !oldWidget.operation.hasSameOperation(operation);
}

/// Full-subtree replacement shown the instant an operation is invalidated,
/// styled as a standard status panel (§10). The live region announces the
/// change to assistive tech; the opaque backdrop keeps prior-operation
/// content from ever being exposed.
final class CatalogPrivacyShield extends StatelessWidget {
  const CatalogPrivacyShield({super.key});

  @override
  Widget build(BuildContext context) => ColoredBox(
    key: const ValueKey<String>('catalog-privacy-shield'),
    color: context.theme.scaffoldBackgroundColor,
    child: Semantics(
      liveRegion: true,
      child: const LauncherStatusPanel(
        icon: Icons.sync_lock_outlined,
        title: 'Connection changed',
        message: 'Reconnect to browse this server’s Catalog.',
      ),
    ),
  );
}
