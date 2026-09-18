import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

sealed class LaunchAuthorizationResult {
  const LaunchAuthorizationResult();
}

final class LaunchAuthorized extends LaunchAuthorizationResult {
  const LaunchAuthorized({
    required this.authority,
    required this.releaseId,
    required this.titleId,
  });

  final InstallAuthorityContext authority;
  final RomdPublicId releaseId;
  final RomdPublicId titleId;
}

/// The exact profile has never acquired this release, or its local authority
/// projection is no longer coherent. No server metadata may be requested.
final class AuthorizationNotGranted extends LaunchAuthorizationResult {
  const AuthorizationNotGranted();
}

/// The exact profile has a cached or newly learned explicit revoke.
final class AuthorizationRevoked extends LaunchAuthorizationResult {
  const AuthorizationRevoked();
}

/// The launch could not establish a safe, current local authority snapshot.
final class LaunchAuthorizationUnavailable extends LaunchAuthorizationResult {
  const LaunchAuthorizationUnavailable();
}

abstract interface class LaunchAuthorizer {
  InstallAuthorityContext? get authority;

  Future<LaunchAuthorizationResult> authorize(PlayRequest request);

  void close();
}
