sealed class ConsumerLoginResult {
  const ConsumerLoginResult();
}

final class ConsumerLoginSuccess extends ConsumerLoginResult {
  const ConsumerLoginSuccess(this.session);

  final ConsumerLoginSession session;
}

final class ConsumerLoginFailure extends ConsumerLoginResult {
  const ConsumerLoginFailure(
    this.message, {
    this.kind = ConsumerLoginFailureKind.invalidGrant,
    this.rotatedRefreshToken,
  });

  final String message;
  final ConsumerLoginFailureKind kind;

  /// Set when the token endpoint accepted the exchange but the session could
  /// not be completed. The server has already redeemed the presented refresh
  /// token, so this replacement must be persisted to keep the sign-in.
  final String? rotatedRefreshToken;
}

enum ConsumerLoginFailureKind { invalidGrant, transient, protocol }

final class ConsumerLoginSession {
  const ConsumerLoginSession({
    required this.token,
    required this.tokenType,
    required this.refreshToken,
    required this.expiresAt,
    required this.account,
  });

  final String token;
  final String tokenType;
  final String refreshToken;
  final DateTime expiresAt;
  final ConsumerAccount account;
}

final class ConsumerAccount {
  const ConsumerAccount({
    required this.id,
    required this.username,
    required this.email,
  });

  final String id;
  final String username;
  final String email;
}
