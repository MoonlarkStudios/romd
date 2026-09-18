import 'local_profile.dart';
import 'romd_server_instance_id.dart';

final class ProfileServerConnection {
  const ProfileServerConnection({
    required this.instanceId,
    required this.origin,
    required this.firstSeenAt,
    required this.lastSeenAt,
  });

  final RomdServerInstanceId instanceId;
  final Uri origin;
  final DateTime firstSeenAt;
  final DateTime lastSeenAt;
}

final class PendingProfileServerLocator {
  const PendingProfileServerLocator({
    required this.origin,
    required this.createdAt,
    this.lastAttemptAt,
  });

  final Uri origin;
  final DateTime createdAt;
  final DateTime? lastAttemptAt;
}

final class ProfileServerConnectionState {
  const ProfileServerConnectionState({
    required this.localProfileId,
    required this.generation,
    required this.selectedConnection,
    required this.pendingLocator,
  });

  final String localProfileId;
  final int generation;
  final ProfileServerConnection? selectedConnection;
  final PendingProfileServerLocator? pendingLocator;
}

sealed class ProfileServerConnectionReadResult {
  const ProfileServerConnectionReadResult();
}

final class ProfileServerConnectionFound
    extends ProfileServerConnectionReadResult {
  const ProfileServerConnectionFound(this.state);

  final ProfileServerConnectionState state;
}

final class ProfileServerConnectionProfileNotFound
    extends ProfileServerConnectionReadResult {
  const ProfileServerConnectionProfileNotFound();
}

final class ProfileServerConnectionProjectionInconsistent
    extends ProfileServerConnectionReadResult {
  const ProfileServerConnectionProjectionInconsistent();
}

sealed class ProfileServerMutationResult {
  const ProfileServerMutationResult();
}

final class ProfileServerMutationApplied extends ProfileServerMutationResult {
  const ProfileServerMutationApplied(this.generation);

  final int generation;
}

final class ProfileServerMutationStale extends ProfileServerMutationResult {
  const ProfileServerMutationStale();
}

final class ProfileServerMutationProfileNotFound
    extends ProfileServerMutationResult {
  const ProfileServerMutationProfileNotFound();
}

final class ProfileServerMutationProjectionInconsistent
    extends ProfileServerMutationResult {
  const ProfileServerMutationProjectionInconsistent();
}

Uri normalizeRomdServerOrigin(Uri origin) =>
    RomdServerOrigins.parse(origin.toString());
