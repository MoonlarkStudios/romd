import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/players_projection.dart';

const _xbox = ConnectedGamepad(
  id: 'xbox',
  order: 0,
  identity: ControllerIdentity(displayName: 'Xbox Controller'),
);

const _dualSenseExact = ConnectedGamepad(
  id: 'dual-sense',
  order: 1,
  identity: ControllerIdentity(
    displayName: 'DualSense',
    sdlGuid: '030000004c050000e60c000000006800',
    serial: 'serial-a',
  ),
);

void main() {
  test('unclaimed controllers stay available outside four open slots', () {
    final projection = PlayersProjection.resolve(
      claims: const <ControllerSlotClaim?>[],
      devices: const <ConnectedGamepad>[_xbox],
    );

    expect(projection.slots, hasLength(ControllerAssignments.maxSlots));
    expect(projection.slots.first.state, PlayersSlotState.open);
    expect(projection.slots.first.controller, isNull);
    expect(projection.availableControllers, const <ConnectedGamepad>[_xbox]);
    expect(
      projection.slots.skip(1).map((slot) => slot.state),
      everyElement(PlayersSlotState.open),
    );
  });

  test('unmatched fallback claim leaves the device available', () {
    final claim = ControllerSlotClaim.fallback('Stadia Controller');
    final projection = PlayersProjection.resolve(
      claims: <ControllerSlotClaim?>[claim],
      devices: const <ConnectedGamepad>[_xbox],
    );

    final playerOne = projection.slots.first;
    expect(playerOne.state, PlayersSlotState.open);
    expect(playerOne.controller, isNull);
    expect(playerOne.claim, claim);
    expect(playerOne.assignmentSource, isNull);
    expect(projection.availableControllers, const <ConnectedGamepad>[_xbox]);
  });

  test('offline exact identity remains a reserved hole', () {
    final claim = ControllerSlotClaim.fromGamepad(_dualSenseExact);
    final projection = PlayersProjection.resolve(
      claims: <ControllerSlotClaim?>[claim],
      devices: const <ConnectedGamepad>[_xbox],
    );

    expect(projection.slots[0].state, PlayersSlotState.reservedExact);
    expect(projection.slots[0].claim, claim);
    expect(
      projection.slots[0].assignmentSource,
      PlayersAssignmentSource.exactIdentity,
    );
    expect(projection.slots[1].state, PlayersSlotState.open);
    expect(projection.availableControllers, const <ConnectedGamepad>[_xbox]);
  });

  test('fallback claim cannot consume exact same-name controller', () {
    final claim = ControllerSlotClaim.fallback('DualSense');
    final projection = PlayersProjection.resolve(
      claims: <ControllerSlotClaim?>[claim],
      devices: const <ConnectedGamepad>[_dualSenseExact],
    );

    expect(projection.slots[0].state, PlayersSlotState.blockedAmbiguous);
    expect(projection.slots[0].claim, claim);
    expect(
      projection.slots[0].assignmentSource,
      PlayersAssignmentSource.fallbackName,
    );
    expect(projection.slots[1].state, PlayersSlotState.open);
    expect(projection.availableControllers, isEmpty);
    expect(projection.attentionControllers, const <ConnectedGamepad>[
      _dualSenseExact,
    ]);
  });

  test('source metadata distinguishes provider and fallback matching', () {
    const providerClaim = ControllerSlotClaim(
      displayName: 'Xbox Controller',
      providerId: 'xbox',
    );
    final providerProjection = PlayersProjection.resolve(
      claims: const <ControllerSlotClaim?>[providerClaim],
      devices: const <ConnectedGamepad>[_xbox],
    );
    final fallbackProjection = PlayersProjection.resolve(
      claims: <ControllerSlotClaim?>[
        ControllerSlotClaim.fallback('Xbox Controller'),
      ],
      devices: const <ConnectedGamepad>[_xbox],
    );

    expect(
      providerProjection.slots.first.assignmentSource,
      PlayersAssignmentSource.providerId,
    );
    expect(
      fallbackProjection.slots.first.assignmentSource,
      PlayersAssignmentSource.fallbackName,
    );
  });

  test('later exact ownership removes earlier fallback ambiguity', () {
    final projection = PlayersProjection.resolve(
      claims: <ControllerSlotClaim?>[
        ControllerSlotClaim.fallback('DualSense'),
        ControllerSlotClaim.fromGamepad(_dualSenseExact),
      ],
      devices: const <ConnectedGamepad>[_dualSenseExact],
    );

    expect(projection.slots[0].state, PlayersSlotState.open);
    expect(projection.slots[0].claim?.displayName, 'DualSense');
    expect(projection.slots[1].state, PlayersSlotState.connected);
    expect(
      projection.slots[1].assignmentSource,
      PlayersAssignmentSource.exactIdentity,
    );
    expect(projection.availableControllers, isEmpty);
    expect(projection.attentionControllers, isEmpty);
  });
}
