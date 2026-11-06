import 'controller_assignments.dart';

/// Immutable controller roster approved for one launch attempt.
///
/// [claimRevision] detects session seating mutations. [claims] retains the
/// exact seating and guest-profile inputs that produced [slotResolution].
/// [devices] captures the provider's complete ordered inventory, including
/// ephemeral ids, so a disconnect, reconnect, provider-id replacement, or
/// reorder invalidates the review before an emulator is started.
final class ReviewedLaunchSnapshot {
  ReviewedLaunchSnapshot({
    required this.claimRevision,
    required List<ControllerSlotClaim?> claims,
    required List<ConnectedGamepad> devices,
    required this.slotResolution,
    this.inventoryAvailable = true,
  }) : claims = List<ControllerSlotClaim?>.unmodifiable(claims),
       devices = List<ConnectedGamepad>.unmodifiable(devices);

  factory ReviewedLaunchSnapshot.resolve({
    required int claimRevision,
    required List<ControllerSlotClaim?> claims,
    required List<ConnectedGamepad> devices,
    bool inventoryAvailable = true,
  }) => ReviewedLaunchSnapshot(
    claimRevision: claimRevision,
    claims: claims,
    devices: devices,
    inventoryAvailable: inventoryAvailable,
    slotResolution: ControllerAssignments.resolveSlotResolution(
      claims: claims,
      devices: devices,
    ),
  );

  final int claimRevision;
  final List<ControllerSlotClaim?> claims;
  final List<ConnectedGamepad> devices;
  final ControllerSlotResolution slotResolution;
  final bool inventoryAvailable;

  bool matches({
    required int currentClaimRevision,
    required List<ConnectedGamepad> currentDevices,
  }) {
    if (currentClaimRevision != claimRevision ||
        currentDevices.length != devices.length) {
      return false;
    }
    for (var index = 0; index < devices.length; index++) {
      if (currentDevices[index] != devices[index]) {
        return false;
      }
    }
    return true;
  }
}

Future<ReviewedLaunchSnapshot> captureReviewedLaunchSnapshot({
  required SessionControllerSlotClaims slotClaims,
  required GamepadLister gamepadLister,
}) async {
  while (true) {
    final claimRevision = slotClaims.revision;
    final claims = slotClaims.claimDetails;
    try {
      final devices = await gamepadLister();
      if (slotClaims.revision != claimRevision) {
        continue;
      }
      return ReviewedLaunchSnapshot.resolve(
        claimRevision: claimRevision,
        claims: claims,
        devices: devices,
      );
    } on Object {
      return ReviewedLaunchSnapshot.resolve(
        claimRevision: slotClaims.revision,
        claims: slotClaims.claimDetails,
        devices: const <ConnectedGamepad>[],
        inventoryAvailable: false,
      );
    }
  }
}
