import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';

void main() {
  final p1 = ConnectedGamepad.fallback(
    id: 'pad-1',
    name: 'DualSense',
    order: 0,
  );
  final p2 = ConnectedGamepad.fallback(id: 'pad-2', name: 'Xbox', order: 1);

  test('matches the exact claim revision and ordered provider inventory', () {
    final snapshot = ReviewedLaunchSnapshot.resolve(
      claimRevision: 3,
      claims: const <ControllerSlotClaim?>[],
      devices: <ConnectedGamepad>[p1, p2],
    );

    expect(
      snapshot.matches(
        currentClaimRevision: 3,
        currentDevices: <ConnectedGamepad>[p1, p2],
      ),
      isTrue,
    );
    expect(
      snapshot.matches(
        currentClaimRevision: 4,
        currentDevices: <ConnectedGamepad>[p1, p2],
      ),
      isFalse,
    );
  });

  test(
    'disconnect, reorder, and provider-id replacement invalidate review',
    () {
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: 0,
        claims: const <ControllerSlotClaim?>[],
        devices: <ConnectedGamepad>[p1, p2],
      );
      final reconnectedP1 = ConnectedGamepad.fallback(
        id: 'pad-1-new',
        name: 'DualSense',
        order: 0,
      );

      for (final devices in <List<ConnectedGamepad>>[
        <ConnectedGamepad>[p1],
        <ConnectedGamepad>[p2, p1],
        <ConnectedGamepad>[reconnectedP1, p2],
      ]) {
        expect(
          snapshot.matches(currentClaimRevision: 0, currentDevices: devices),
          isFalse,
        );
      }
    },
  );

  test('snapshot owns immutable device and resolution collections', () {
    final devices = <ConnectedGamepad>[p1];
    final snapshot = ReviewedLaunchSnapshot.resolve(
      claimRevision: 0,
      claims: const <ControllerSlotClaim?>[],
      devices: devices,
    );
    devices.clear();

    expect(snapshot.devices, <ConnectedGamepad>[p1]);
    expect(() => snapshot.devices.add(p2), throwsUnsupportedError);
    expect(snapshot.slotResolution.controllerSlots, isEmpty);
    expect(snapshot.slotResolution.availableControllers.single, p1);
  });

  test(
    'ambiguous candidate stays inventoried but is not joinable available',
    () {
      const exact = ConnectedGamepad(
        id: 'exact',
        order: 0,
        identity: ControllerIdentity(
          displayName: 'DualSense',
          sdlGuid: 'guid',
          serial: 'serial',
        ),
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: 0,
        claims: <ControllerSlotClaim?>[
          ControllerSlotClaim.fallback('DualSense'),
        ],
        devices: const <ConnectedGamepad>[exact],
      );

      expect(snapshot.devices, const <ConnectedGamepad>[exact]);
      expect(snapshot.slotResolution.blockedPlayerSlots, <int>{0});
      expect(snapshot.slotResolution.availableControllers, isEmpty);
    },
  );
}
