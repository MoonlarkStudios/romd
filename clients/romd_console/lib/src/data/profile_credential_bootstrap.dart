import '../domain/romd_server_instance_id.dart';
import 'refresh_token_store.dart';
import 'server_discovery_api_client.dart';

typedef ServerDiscoveryApiClientFactory =
    ServerDiscoveryApiClient Function(Uri origin);

sealed class ProfileCredentialBootstrapResult {
  const ProfileCredentialBootstrapResult();
}

final class ProfileCredentialBootstrapReady
    extends ProfileCredentialBootstrapResult {
  const ProfileCredentialBootstrapReady({required this.serverInstanceId});

  final RomdServerInstanceId serverInstanceId;
}

final class ProfileCredentialLegacyCleanupFailed
    extends ProfileCredentialBootstrapResult {
  const ProfileCredentialLegacyCleanupFailed();
}

final class ProfileCredentialDiscoveryFailed
    extends ProfileCredentialBootstrapResult {
  const ProfileCredentialDiscoveryFailed();
}

final class ProfileCredentialServerReplaced
    extends ProfileCredentialBootstrapResult {
  const ProfileCredentialServerReplaced(this.discoveredServerInstanceId);

  final RomdServerInstanceId discoveredServerInstanceId;
}

final class ProfileCredentialBootstrapCancelled
    extends ProfileCredentialBootstrapResult {
  const ProfileCredentialBootstrapCancelled();
}

/// Performs the fail-closed credential bootstrap for one profile and locator.
///
/// Every invocation retries delete-only legacy cleanup. Only successful cleanup
/// may create the anonymous discovery client. The root-owned session
/// coordinator selects the exact profile+instance credential partition only
/// after this discovery result has been activated.
final class ProfileCredentialBootstrap {
  const ProfileCredentialBootstrap({
    required RefreshTokenStore refreshTokenStore,
    required ServerDiscoveryApiClientFactory createServerDiscoveryApiClient,
  }) : _refreshTokenStore = refreshTokenStore,
       _createServerDiscoveryApiClient = createServerDiscoveryApiClient;

  final RefreshTokenStore _refreshTokenStore;
  final ServerDiscoveryApiClientFactory _createServerDiscoveryApiClient;

  Future<ProfileCredentialBootstrapResult> run({
    required String profileId,
    required Uri serverOrigin,
    RomdServerInstanceId? expectedServerInstanceId,
    bool Function()? isCurrent,
  }) async {
    try {
      await _refreshTokenStore.deleteLegacyOriginToken(
        profileId: profileId,
        serverOrigin: serverOrigin,
      );
    } on Object {
      return const ProfileCredentialLegacyCleanupFailed();
    }
    if (isCurrent != null && !isCurrent()) {
      return const ProfileCredentialBootstrapCancelled();
    }

    final ServerDiscoveryResult discoveryResult;
    ServerDiscoveryApiClient? discoveryClient;
    try {
      discoveryClient = _createServerDiscoveryApiClient(serverOrigin);
      discoveryResult = await discoveryClient.discover();
    } on Object {
      return const ProfileCredentialDiscoveryFailed();
    } finally {
      discoveryClient?.close();
    }
    if (isCurrent != null && !isCurrent()) {
      return const ProfileCredentialBootstrapCancelled();
    }

    final RomdServerInstanceId serverInstanceId;
    switch (discoveryResult) {
      case ServerDiscoverySuccess(serverInstanceId: final discoveredId):
        serverInstanceId = discoveredId;
      case ServerDiscoveryFailure():
        return const ProfileCredentialDiscoveryFailed();
    }
    if (expectedServerInstanceId != null &&
        serverInstanceId != expectedServerInstanceId) {
      return ProfileCredentialServerReplaced(serverInstanceId);
    }

    return ProfileCredentialBootstrapReady(serverInstanceId: serverInstanceId);
  }
}
