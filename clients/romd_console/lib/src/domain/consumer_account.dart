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
  });

  final String message;
  final ConsumerLoginFailureKind kind;
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
