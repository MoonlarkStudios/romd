import 'controller_assignments.dart';

/// The effective state of one session player slot.
///
/// This is a read-only view of the canonical assignment resolver. It does not
/// persist seating or inspect controller mapping/profile state.
enum PlayersSlotState { connected, reservedExact, blockedAmbiguous, open }

/// Player-facing name for the provenance emitted by the canonical resolver.
typedef PlayersAssignmentSource = ControllerAssignmentSource;

final class PlayerSlotProjection {
  const PlayerSlotProjection({
    required this.playerSlot,
    required this.state,
    this.controller,
    this.claim,
    this.assignmentSource,
  });

  final int playerSlot;
  final PlayersSlotState state;
  final ConnectedGamepad? controller;
  final ControllerSlotClaim? claim;
  final PlayersAssignmentSource? assignmentSource;
}

/// Canonical read-only player seating used by controller-facing UI.
///
/// Future launch and status-row consumers should build from this projection
/// instead of independently interpreting claims. [resolve] deliberately calls
/// [ControllerAssignments.resolveSlotResolution], preserving no-claim and
/// fallback behavior in one place.
final class PlayersProjection {
  PlayersProjection._(
    List<PlayerSlotProjection> slots,
    List<ConnectedGamepad> availableControllers,
    List<ConnectedGamepad> attentionControllers,
  ) : slots = List<PlayerSlotProjection>.unmodifiable(slots),
      availableControllers = List<ConnectedGamepad>.unmodifiable(
        availableControllers,
      ),
      attentionControllers = List<ConnectedGamepad>.unmodifiable(
        attentionControllers,
      );

  final List<PlayerSlotProjection> slots;
  final List<ConnectedGamepad> availableControllers;

  /// Connected controllers that remain visible as physical inventory but are
  /// not joinable because canonical identity correlation needs attention.
  final List<ConnectedGamepad> attentionControllers;

  factory PlayersProjection.resolve({
    required List<ControllerSlotClaim?> claims,
    required List<ConnectedGamepad> devices,
  }) {
    final resolution = ControllerAssignments.resolveSlotResolution(
      claims: claims,
      devices: devices,
    );
    final connectedBySlot = <int, ResolvedControllerSlot>{
      for (final slot in resolution.controllerSlots) slot.playerSlot: slot,
    };

    return PlayersProjection._(
      <PlayerSlotProjection>[
        for (
          var playerSlot = 0;
          playerSlot < ControllerAssignments.maxSlots;
          playerSlot++
        )
          _projectSlot(
            playerSlot: playerSlot,
            claim: playerSlot < claims.length ? claims[playerSlot] : null,
            resolved: connectedBySlot[playerSlot],
            reservedExact: resolution.isPlayerSlotReserved(playerSlot),
            blockedAmbiguous: resolution.blockedPlayerSlots.contains(
              playerSlot,
            ),
          ),
      ],
      resolution.availableControllers,
      resolution.attentionControllers,
    );
  }

  static PlayerSlotProjection _projectSlot({
    required int playerSlot,
    required ControllerSlotClaim? claim,
    required ResolvedControllerSlot? resolved,
    required bool reservedExact,
    required bool blockedAmbiguous,
  }) {
    if (resolved != null) {
      return PlayerSlotProjection(
        playerSlot: playerSlot,
        state: PlayersSlotState.connected,
        controller: resolved.controller,
        claim: claim,
        assignmentSource: resolved.assignmentSource,
      );
    }
    if (reservedExact) {
      return PlayerSlotProjection(
        playerSlot: playerSlot,
        state: PlayersSlotState.reservedExact,
        claim: claim,
        assignmentSource: PlayersAssignmentSource.exactIdentity,
      );
    }
    if (blockedAmbiguous) {
      return PlayerSlotProjection(
        playerSlot: playerSlot,
        state: PlayersSlotState.blockedAmbiguous,
        claim: claim,
        assignmentSource: PlayersAssignmentSource.fallbackName,
      );
    }
    return PlayerSlotProjection(
      playerSlot: playerSlot,
      state: PlayersSlotState.open,
      claim: claim,
    );
  }
}
