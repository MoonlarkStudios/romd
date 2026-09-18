import 'consumer_account.dart';

/// The pending device authorization the user must approve in a browser.
final class DeviceAuthorization {
  const DeviceAuthorization({
    required this.deviceCode,
    required this.userCode,
    required this.verificationUri,
    required this.interval,
    required this.expiresAt,
  });

  final String deviceCode;
  final String userCode;
  final Uri verificationUri;
  final Duration interval;
  final DateTime expiresAt;
}

sealed class DeviceAuthorizationResult {
  const DeviceAuthorizationResult();
}

final class DeviceAuthorizationSuccess extends DeviceAuthorizationResult {
  const DeviceAuthorizationSuccess(this.authorization);

  final DeviceAuthorization authorization;
}

final class DeviceAuthorizationFailure extends DeviceAuthorizationResult {
  const DeviceAuthorizationFailure(this.message);

  final String message;
}

/// The result of one poll of the token endpoint while waiting for device approval.
sealed class DeviceTokenResult {
  const DeviceTokenResult();
}

/// The user has not yet approved the device; keep polling at the current interval.
final class DeviceTokenPending extends DeviceTokenResult {
  const DeviceTokenPending();
}

/// The server asked the client to poll less frequently.
final class DeviceTokenSlowDown extends DeviceTokenResult {
  const DeviceTokenSlowDown();
}

final class DeviceTokenSuccess extends DeviceTokenResult {
  const DeviceTokenSuccess(this.session);

  final ConsumerLoginSession session;
}

/// A terminal failure (expired, denied, or transport error). Restart authorization to retry.
final class DeviceTokenFailure extends DeviceTokenResult {
  const DeviceTokenFailure(this.message);

  final String message;
}
