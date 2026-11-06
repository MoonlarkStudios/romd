import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';

/// Fetches the per-platform BIOS listing from the ROMD consumer API. The
/// endpoint requires a signed-in user, so production calls use the shared
/// authenticated-session boundary. The provider remains only as a narrow test
/// seam for isolated resolver tests.
final class RomdBiosCatalog implements BiosCatalog {
  RomdBiosCatalog({
    required ConsumerApiClient apiClient,
    AuthenticatedRequestExecutor? authenticatedSession,
    String? Function()? accessTokenProvider,
  }) : _api = apiClient,
       _authenticatedSession = authenticatedSession,
       _accessToken = accessTokenProvider;

  final ConsumerApiClient _api;
  final AuthenticatedRequestExecutor? _authenticatedSession;
  final String? Function()? _accessToken;

  @override
  Future<List<BiosFileListing>> biosForPlatform(String platformShortName) {
    final authenticatedSession = _authenticatedSession;
    if (authenticatedSession != null) {
      return authenticatedSession.execute<List<BiosFileListing>>(
        request: (accessToken) => _api.listPlatformBios(
          accessToken: accessToken,
          platformShortName: platformShortName,
        ),
        replayPolicy: AuthenticatedReplayPolicy.onceAfterInvalidToken,
        isInvalidTokenError: (error) =>
            error is ConsumerApiException && error.isInvalidToken,
      );
    }
    final accessToken = _accessToken?.call();
    if (accessToken == null || accessToken.isEmpty) {
      throw const ConsumerApiException(
        'No active session for the BIOS listing.',
      );
    }

    return _api.listPlatformBios(
      accessToken: accessToken,
      platformShortName: platformShortName,
    );
  }
}
