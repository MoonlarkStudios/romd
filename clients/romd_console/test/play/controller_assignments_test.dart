import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';

final ConnectedGamepad _xbox = ConnectedGamepad.fallback(
  id: '0',
  name: 'Xbox Controller',
  order: 0,
);
final ConnectedGamepad _dualSense = ConnectedGamepad.fallback(
  id: '1',
  name: 'DualSense',
  order: 1,
);
final ConnectedGamepad _pro2A = ConnectedGamepad.fallback(
  id: '2',
  name: '8BitDo Pro 2',
  order: 2,
);
final ConnectedGamepad _pro2B = ConnectedGamepad.fallback(
  id: '3',
  name: '8BitDo Pro 2',
  order: 3,
);
const ConnectedGamepad _dualSenseExactA = ConnectedGamepad(
  id: '7',
  order: 0,
  identity: ControllerIdentity(
    displayName: 'DualSense',
    sdlGuid: '030000004c050000e60c000000006800',
    serial: 'serial-a',
  ),
);
const ConnectedGamepad _dualSenseExactB = ConnectedGamepad(
  id: '9',
  order: 1,
  identity: ControllerIdentity(
    displayName: 'DualSense',
    sdlGuid: '030000004c050000e60c000000006800',
    serial: 'serial-b',
  ),
);

List<String?> _claims([String? p1, String? p2, String? p3, String? p4]) =>
    <String?>[p1, p2, p3, p4];

List<ControllerSlotClaim?> _slotClaims([
  ControllerSlotClaim? p1,
  ControllerSlotClaim? p2,
  ControllerSlotClaim? p3,
  ControllerSlotClaim? p4,
]) => <ControllerSlotClaim?>[p1, p2, p3, p4];

void main() {
  group('ControllerIdentity', () {
    test('requires both SDL GUID and serial for exact identity', () {
      const exact = ControllerIdentity(
        displayName: 'DualSense',
        sdlGuid: '030000004c050000e60c000000006800',
        serial: 'serial-a',
      );

      expect(exact.hasExactIdentity, isTrue);
      expect(
        const ControllerIdentity(
          displayName: 'DualSense',
          sdlGuid: '030000004c050000e60c000000006800',
        ).hasExactIdentity,
        isFalse,
      );
      expect(
        const ControllerIdentity(
          displayName: 'DualSense',
          serial: 'serial-a',
        ).hasExactIdentity,
        isFalse,
      );
    });

    test('fallback connected gamepad carries display name only', () {
      expect(_dualSense.name, 'DualSense');
      expect(
        _dualSense.identity,
        const ControllerIdentity(displayName: 'DualSense'),
      );
      expect(_dualSense.identity.sdlGuid, isNull);
      expect(_dualSense.identity.serial, isNull);
    });

    test('slot claims retain exact identity when available', () {
      final claim = ControllerSlotClaim.fromGamepad(_dualSenseExactA);

      expect(claim.displayName, 'DualSense');
      expect(claim.providerId, _dualSenseExactA.id);
      expect(claim.sdlGuid, _dualSenseExactA.identity.sdlGuid);
      expect(claim.serial, 'serial-a');
      expect(claim.matchesProvider(_dualSenseExactA), isTrue);
      expect(claim.hasExactIdentity, isTrue);
      expect(claim.matchesExact(_dualSenseExactA.identity), isTrue);
      expect(claim.matchesExact(_dualSenseExactB.identity), isFalse);
    });
  });

  group('fallback slot resolution', () {
    test('no claims resolves to connection order', () {
      expect(
        ControllerAssignments.resolveDeviceOrder(
          claims: _claims(),
          devices: <ConnectedGamepad>[_xbox, _dualSense],
        ),
        <int>[0, 1],
      );
    });

    test('a name claim reorders and the rest fill by connection order', () {
      expect(
        ControllerAssignments.resolveDeviceOrder(
          claims: _claims('DualSense'),
          devices: <ConnectedGamepad>[_xbox, _dualSense],
        ),
        <int>[1, 0],
      );
    });

    test('duplicate names claim distinct devices in claim order', () {
      expect(
        ControllerAssignments.resolveDeviceOrder(
          claims: _claims('8BitDo Pro 2', '8BitDo Pro 2'),
          devices: <ConnectedGamepad>[_xbox, _pro2A, _pro2B],
        ),
        <int>[1, 2, 0],
      );
    });

    test('a claim for a disconnected pad is skipped, not a hole', () {
      expect(
        ControllerAssignments.resolveDeviceOrder(
          claims: _claims('Stadia Controller', 'DualSense'),
          devices: <ConnectedGamepad>[_xbox, _dualSense],
        ),
        // P1 falls back to the first unused device; P2 keeps its claim.
        <int>[0, 1],
      );
    });

    test('claimed middle slot leaves earlier slots order-filled', () {
      expect(
        ControllerAssignments.resolveDeviceOrder(
          claims: _claims(null, 'Xbox Controller'),
          devices: <ConnectedGamepad>[_xbox, _dualSense],
        ),
        <int>[1, 0],
      );
    });

    test('never exceeds the device count or maxSlots', () {
      expect(
        ControllerAssignments.resolveDeviceOrder(
          claims: _claims(),
          devices: <ConnectedGamepad>[_xbox],
        ),
        <int>[0],
      );
      expect(
        ControllerAssignments.resolveDeviceOrder(
          claims: _claims(),
          devices: const <ConnectedGamepad>[],
        ),
        isEmpty,
      );
    });
  });

  group('identity-aware slot resolution', () {
    test('resolved slots retain canonical matching provenance', () {
      final exact = ControllerAssignments.resolveSlots(
        claims: _slotClaims(
          const ControllerSlotClaim(
            displayName: 'DualSense',
            providerId: '7',
            sdlGuid: '030000004c050000e60c000000006800',
            serial: 'serial-b',
          ),
        ),
        devices: <ConnectedGamepad>[_dualSenseExactA, _dualSenseExactB],
      );
      final provider = ControllerAssignments.resolveSlots(
        claims: _slotClaims(
          const ControllerSlotClaim(
            displayName: '8BitDo Pro 2',
            providerId: '3',
          ),
        ),
        devices: <ConnectedGamepad>[_pro2A, _pro2B],
      );
      final fallback = ControllerAssignments.resolveSlots(
        claims: _slotClaims(ControllerSlotClaim.fallback('Xbox Controller')),
        devices: <ConnectedGamepad>[_xbox],
      );
      final available = ControllerAssignments.resolveSlotResolution(
        claims: _slotClaims(ControllerSlotClaim.fallback('Stadia Controller')),
        devices: <ConnectedGamepad>[_xbox],
      );

      expect(exact.first.controller, _dualSenseExactB);
      expect(
        exact.first.assignmentSource,
        ControllerAssignmentSource.exactIdentity,
      );
      expect(provider.first.controller, _pro2B);
      expect(
        provider.first.assignmentSource,
        ControllerAssignmentSource.providerId,
      );
      expect(
        fallback.first.assignmentSource,
        ControllerAssignmentSource.fallbackName,
      );
      expect(available.controllerSlots, isEmpty);
      expect(available.availableControllers, <ConnectedGamepad>[_xbox]);
    });

    test('no claims keep controllers available in connection order', () {
      final resolution = ControllerAssignments.resolveSlotResolution(
        claims: _slotClaims(),
        devices: <ConnectedGamepad>[_xbox, _dualSense],
      );

      expect(resolution.reservedPlayerSlots, isEmpty);
      expect(resolution.blockedPlayerSlots, isEmpty);
      expect(resolution.controllerSlots, isEmpty);
      expect(resolution.availableControllers, <ConnectedGamepad>[
        _xbox,
        _dualSense,
      ]);
    });

    test('provider id claims distinguish same-name controllers in-session', () {
      final resolved = ControllerAssignments.resolveSlots(
        claims: _slotClaims(
          const ControllerSlotClaim(
            displayName: '8BitDo Pro 2',
            providerId: '3',
          ),
        ),
        devices: <ConnectedGamepad>[_pro2A, _pro2B],
      );

      expect(resolved.map((slot) => slot.controller.id), <String>['3']);
      expect(resolved.map((slot) => slot.runtimeIndex), <int>[3]);
    });

    test('exact identity wins over a reused provider id', () {
      final resolved = ControllerAssignments.resolveSlots(
        claims: _slotClaims(ControllerSlotClaim.fromGamepad(_dualSenseExactA)),
        devices: const <ConnectedGamepad>[
          ConnectedGamepad(
            id: '7',
            order: 0,
            identity: ControllerIdentity(
              displayName: 'DualSense',
              sdlGuid: '030000004c050000e60c000000006800',
              serial: 'serial-b',
            ),
          ),
          ConnectedGamepad(
            id: '9',
            order: 1,
            identity: ControllerIdentity(
              displayName: 'DualSense',
              sdlGuid: '030000004c050000e60c000000006800',
              serial: 'serial-a',
            ),
          ),
        ],
      );

      expect(resolved.map((slot) => slot.controller.id), <String>['9']);
    });

    test('exact identity claims reorder identical controllers', () {
      final resolved = ControllerAssignments.resolveSlots(
        claims: _slotClaims(ControllerSlotClaim.fromGamepad(_dualSenseExactB)),
        devices: <ConnectedGamepad>[_dualSenseExactA, _dualSenseExactB],
      );

      expect(resolved.map((slot) => slot.controller.identity.serial), <String?>[
        'serial-b',
      ]);
      expect(resolved.map((slot) => slot.runtimeIndex), <int>[1]);
    });

    test('fallback disconnected claims leave unclaimed devices available', () {
      final resolution = ControllerAssignments.resolveSlotResolution(
        claims: _slotClaims(
          ControllerSlotClaim.fallback('Stadia Controller'),
          ControllerSlotClaim.fallback('DualSense'),
        ),
        devices: <ConnectedGamepad>[_xbox, _dualSense],
      );

      expect(resolution.reservedPlayerSlots, isEmpty);
      expect(resolution.controllerSlots.map((slot) => slot.playerSlot), <int>[
        1,
      ]);
      expect(
        resolution.controllerSlots.map((slot) => slot.controller.name),
        <String>['DualSense'],
      );
      expect(resolution.availableControllers, <ConnectedGamepad>[_xbox]);
    });

    test('disconnected exact claim reserves its player-slot hole', () {
      const xbox = ConnectedGamepad(
        id: '11',
        order: 2,
        identity: ControllerIdentity(displayName: 'Xbox Controller'),
      );
      final resolution = ControllerAssignments.resolveSlotResolution(
        claims: _slotClaims(
          ControllerSlotClaim.fromGamepad(_dualSenseExactA),
          ControllerSlotClaim.fromGamepad(_dualSenseExactB),
        ),
        devices: <ConnectedGamepad>[_dualSenseExactB, xbox],
      );

      expect(resolution.reservedPlayerSlots, <int>{0});
      expect(resolution.controllerSlots.map((slot) => slot.playerSlot), <int>[
        1,
      ]);
      expect(
        resolution.controllerSlots.map((slot) => slot.controller.name),
        <String>['DualSense'],
      );
      expect(resolution.controllerSlots.map((slot) => slot.runtimeIndex), <int>[
        1,
      ]);
      expect(resolution.availableControllers, <ConnectedGamepad>[xbox]);
    });

    test('fallback claims do not consume exact same-name devices', () {
      final resolution = ControllerAssignments.resolveSlotResolution(
        claims: _slotClaims(ControllerSlotClaim.fallback('DualSense')),
        devices: const <ConnectedGamepad>[_dualSenseExactA],
      );

      expect(resolution.reservedPlayerSlots, isEmpty);
      expect(resolution.blockedPlayerSlots, <int>{0});
      expect(resolution.controllerSlots, isEmpty);
      expect(resolution.availableControllers, isEmpty);
      expect(resolution.attentionControllers, const <ConnectedGamepad>[
        _dualSenseExactA,
      ]);
    });

    test(
      'later exact identity globally outranks an earlier fallback claim',
      () {
        final resolution = ControllerAssignments.resolveSlotResolution(
          claims: _slotClaims(
            ControllerSlotClaim.fallback('DualSense'),
            ControllerSlotClaim.fromGamepad(_dualSenseExactA),
          ),
          devices: <ConnectedGamepad>[_dualSenseExactA, _xbox],
        );

        expect(resolution.blockedPlayerSlots, isEmpty);
        expect(resolution.reservedPlayerSlots, isEmpty);
        expect(resolution.controllerSlots, hasLength(1));
        expect(resolution.controllerSlots.single.playerSlot, 1);
        expect(
          resolution.controllerSlots.single.assignmentSource,
          ControllerAssignmentSource.exactIdentity,
        );
        expect(resolution.availableControllers, <ConnectedGamepad>[_xbox]);
        expect(resolution.attentionControllers, isEmpty);
      },
    );

    test('later provider ownership globally outranks fallback ambiguity', () {
      const providerClaim = ControllerSlotClaim(
        displayName: 'DualSense',
        providerId: '7',
      );
      final resolution = ControllerAssignments.resolveSlotResolution(
        claims: _slotClaims(
          ControllerSlotClaim.fallback('DualSense'),
          providerClaim,
        ),
        devices: const <ConnectedGamepad>[_dualSenseExactA],
      );

      expect(resolution.blockedPlayerSlots, isEmpty);
      expect(resolution.controllerSlots.single.playerSlot, 1);
      expect(
        resolution.controllerSlots.single.assignmentSource,
        ControllerAssignmentSource.providerId,
      );
      expect(resolution.availableControllers, isEmpty);
      expect(resolution.attentionControllers, isEmpty);
    });

    test('connected classifications are pairwise disjoint and exhaustive', () {
      final devices = <ConnectedGamepad>[
        _dualSenseExactA,
        _dualSenseExactB,
        _xbox,
      ];
      final resolution = ControllerAssignments.resolveSlotResolution(
        claims: _slotClaims(
          ControllerSlotClaim.fromGamepad(_dualSenseExactA),
          ControllerSlotClaim.fallback('DualSense'),
        ),
        devices: devices,
      );
      final classifiedIds = <String>[
        for (final slot in resolution.controllerSlots) slot.controller.id,
        for (final controller in resolution.availableControllers) controller.id,
        for (final controller in resolution.attentionControllers) controller.id,
      ];

      expect(classifiedIds, hasLength(devices.length));
      expect(classifiedIds.toSet(), hasLength(devices.length));
      expect(classifiedIds.toSet(), devices.map((device) => device.id).toSet());
      expect(resolution.controllerSlots.single.controller, _dualSenseExactA);
      expect(resolution.availableControllers, <ConnectedGamepad>[_xbox]);
      expect(resolution.attentionControllers, const <ConnectedGamepad>[
        _dualSenseExactB,
      ]);
    });

    test(
      'exact offline claim does not promote a fallback same-name device',
      () {
        final resolution = ControllerAssignments.resolveSlotResolution(
          claims: _slotClaims(
            ControllerSlotClaim.fromGamepad(_dualSenseExactA),
          ),
          devices: <ConnectedGamepad>[
            ConnectedGamepad.fallback(
              id: 'fallback',
              name: 'DualSense',
              order: 3,
            ),
            _dualSenseExactB,
          ],
        );

        expect(resolution.reservedPlayerSlots, <int>{0});
        expect(resolution.controllerSlots, isEmpty);
        expect(resolution.availableControllers.map((pad) => pad.id), <String>[
          'fallback',
          '9',
        ]);
      },
    );

    test('claims without serial preserve provider-id matching', () {
      const guidOnlyClaim = ControllerSlotClaim(
        displayName: 'DualSense',
        providerId: '9',
        sdlGuid: '030000004c050000e60c000000006800',
      );

      final resolved = ControllerAssignments.resolveSlots(
        claims: _slotClaims(guidOnlyClaim),
        devices: <ConnectedGamepad>[_dualSenseExactA, _dualSenseExactB],
      );

      expect(resolved.map((slot) => slot.controller.identity.serial), <String?>[
        'serial-b',
      ]);
    });

    test('claims without serial preserve non-exact display-name fallback', () {
      const guidOnlyClaim = ControllerSlotClaim(
        displayName: 'DualSense',
        sdlGuid: '030000004c050000e60c000000006800',
      );

      final resolved = ControllerAssignments.resolveSlots(
        claims: _slotClaims(guidOnlyClaim),
        devices: <ConnectedGamepad>[
          ConnectedGamepad.fallback(
            id: 'fallback',
            name: 'DualSense',
            order: 3,
          ),
          _dualSenseExactB,
        ],
      );

      expect(resolved.map((slot) => slot.controller.id), <String>['fallback']);
    });
  });

  group('session slot claims', () {
    test('legacy display-name view stays available', () async {
      final claims = SessionControllerSlotClaims.fromClaims(
        <ControllerSlotClaim?>[
          ControllerSlotClaim.fromGamepad(_dualSenseExactA),
        ],
      );

      expect(claims.claims, <String?>['DualSense', null, null, null]);
      expect(await claims.load(), <String?>['DualSense', null, null, null]);
      expect(await claims.loadClaims(), <ControllerSlotClaim?>[
        ControllerSlotClaim.fromGamepad(_dualSenseExactA),
        null,
        null,
        null,
      ]);
    });

    test('first-use P1 claim does not overwrite an existing claim', () async {
      final claims = SessionControllerSlotClaims();

      expect(
        await claims.claimPlayerOneIfEmpty(
          ControllerSlotClaim.fromGamepad(_dualSenseExactA),
        ),
        isTrue,
      );
      expect(
        await claims.claimPlayerOneIfEmpty(
          ControllerSlotClaim.fromGamepad(_dualSenseExactB),
        ),
        isFalse,
      );

      expect(await claims.loadClaims(), <ControllerSlotClaim?>[
        ControllerSlotClaim.fromGamepad(_dualSenseExactA),
        null,
        null,
        null,
      ]);
    });

    test('guest profile follows an exact controller through reorder', () async {
      final claims =
          SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
            ControllerSlotClaim.fromGamepad(_dualSenseExactA),
            ControllerSlotClaim.fromGamepad(
              _dualSenseExactB,
              localProfileId: 'guest-profile',
            ),
          ]);

      await claims.replaceAllClaims(<ControllerSlotClaim?>[
        ControllerSlotClaim.fromGamepad(_dualSenseExactA),
        null,
        ControllerSlotClaim.fromGamepad(_dualSenseExactB),
      ]);

      final reordered = await claims.loadClaims();
      expect(reordered[0]!.localProfileId, isNull);
      expect(reordered[1], isNull);
      expect(reordered[2]!.localProfileId, 'guest-profile');
    });

    test(
      'guest assignment survives provider-id replacement and clears',
      () async {
        final claims = SessionControllerSlotClaims.fromClaims(
          <ControllerSlotClaim?>[
            ControllerSlotClaim.fromGamepad(_dualSenseExactA),
            null,
          ],
        );
        final initialRevision = claims.revision;

        expect(
          await claims.assignGuestProfile(
            playerSlot: 1,
            claim: ControllerSlotClaim.fromGamepad(_dualSenseExactB),
            localProfileId: ' guest-profile ',
          ),
          isTrue,
        );
        expect(claims.revision, initialRevision + 1);

        const reconnected = ConnectedGamepad(
          id: 'replacement-id',
          order: 0,
          identity: ControllerIdentity(
            displayName: 'DualSense',
            sdlGuid: '030000004c050000e60c000000006800',
            serial: 'serial-b',
          ),
        );
        final resolved = ControllerAssignments.resolveSlots(
          claims: claims.claimDetails,
          devices: const <ConnectedGamepad>[_dualSenseExactA, reconnected],
        );
        expect(resolved[1].controller.id, 'replacement-id');
        expect(claims.claimDetails[1]!.localProfileId, 'guest-profile');

        await claims.clearAll();
        expect(claims.claimDetails.every((claim) => claim == null), isTrue);
      },
    );

    test('P1 cannot receive a guest profile assignment', () async {
      final claims = SessionControllerSlotClaims();

      await expectLater(
        claims.assignGuestProfile(
          playerSlot: 0,
          claim: ControllerSlotClaim.fromGamepad(_dualSenseExactA),
          localProfileId: 'guest-profile',
        ),
        throwsRangeError,
      );
    });

    test(
      'join atomically uses next open seat and keeps Guest repeatable',
      () async {
        final claims = SessionControllerSlotClaims();

        final p1 = await claims.join(
          claim: ControllerSlotClaim.fromGamepad(_xbox),
          localProfileId: 'ignored-for-p1',
        );
        final p2 = await claims.join(
          claim: ControllerSlotClaim.fromGamepad(_dualSense),
        );

        expect(p1.disposition, ControllerJoinDisposition.joined);
        expect(p1.playerSlot, 0);
        expect(p2.playerSlot, 1);
        expect(claims.claimDetails[0]!.localProfileId, isNull);
        expect(claims.claimDetails[1]!.localProfileId, isNull);
        expect(claims.revision, 2);
      },
    );

    test(
      'join rejects duplicate controller and named profile atomically',
      () async {
        final claims = SessionControllerSlotClaims.fromClaims(
          <ControllerSlotClaim?>[
            ControllerSlotClaim.fromGamepad(_xbox),
            ControllerSlotClaim.fromGamepad(_dualSense, localProfileId: 'sam'),
          ],
        );
        final revision = claims.revision;

        expect(
          (await claims.join(
            claim: ControllerSlotClaim.fromGamepad(_dualSense),
          )).disposition,
          ControllerJoinDisposition.alreadyJoined,
        );
        expect(
          (await claims.join(
            claim: ControllerSlotClaim.fromGamepad(_pro2A),
            localProfileId: 'sam',
          )).disposition,
          ControllerJoinDisposition.profileAlreadyJoined,
        );
        expect(claims.revision, revision);
      },
    );

    test('join reports a full roster without mutation', () async {
      final claims =
          SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
            ControllerSlotClaim.fromGamepad(_xbox),
            ControllerSlotClaim.fromGamepad(_dualSense),
            ControllerSlotClaim.fromGamepad(_pro2A),
            ControllerSlotClaim.fromGamepad(_pro2B),
          ]);
      final revision = claims.revision;

      final result = await claims.join(
        claim: const ControllerSlotClaim(
          displayName: 'Fifth Pad',
          providerId: 'fifth',
        ),
      );

      expect(result.disposition, ControllerJoinDisposition.rosterFull);
      expect(claims.revision, revision);
    });
  });
}
