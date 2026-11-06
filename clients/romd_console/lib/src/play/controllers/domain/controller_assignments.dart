import 'dart:async';

/// Stable controller identity when a provider can supply it. SDL identity is an
/// exact-match key only when both [sdlGuid] and [serial] are present; otherwise
/// callers must preserve the display-name/order fallback behavior.
final class ControllerIdentity {
  const ControllerIdentity({
    required this.displayName,
    this.sdlGuid,
    this.serial,
  });

  final String displayName;
  final String? sdlGuid;
  final String? serial;

  bool get hasExactIdentity =>
      sdlGuid?.isNotEmpty == true && serial?.isNotEmpty == true;

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is ControllerIdentity &&
          other.displayName == displayName &&
          other.sdlGuid == sdlGuid &&
          other.serial == serial;

  @override
  int get hashCode => Object.hash(displayName, sdlGuid, serial);
}

/// A connected pad as ROMD sees it — decoupled from any platform plugin so
/// domain logic and tests never need a provider implementation.
final class ConnectedGamepad {
  const ConnectedGamepad({
    required this.id,
    required this.order,
    required this.identity,
  });

  factory ConnectedGamepad.fallback({
    required String id,
    required String name,
    required int order,
  }) => ConnectedGamepad(
    id: id,
    order: order,
    identity: ControllerIdentity(displayName: name),
  );

  /// Ephemeral provider id for correlating live events with the listed device.
  final String id;

  /// Ephemeral provider enumeration order for the current connection session.
  final int order;

  final ControllerIdentity identity;

  String get name => identity.displayName;

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is ConnectedGamepad &&
          other.id == id &&
          other.order == order &&
          other.identity == identity;

  @override
  int get hashCode => Object.hash(id, order, identity);
}

typedef GamepadLister = Future<List<ConnectedGamepad>> Function();

/// Session-scoped claim for the controller seated in a player slot.
///
/// [providerId] is an ephemeral event/listing correlation id for the current
/// app session. It is never a durable hardware identity. Exact identity is only
/// meaningful when both [sdlGuid] and [serial] are present. Claims without
/// exact identity intentionally retain the current display-name fallback path.
/// A guest [localProfileId] is session-only metadata on the same claim.
final class ControllerSlotClaim {
  const ControllerSlotClaim({
    required this.displayName,
    this.providerId,
    this.sdlGuid,
    this.serial,
    this.localProfileId,
  });

  factory ControllerSlotClaim.fallback(String displayName) =>
      ControllerSlotClaim(displayName: displayName);

  factory ControllerSlotClaim.fromGamepad(
    ConnectedGamepad gamepad, {
    String? localProfileId,
  }) => ControllerSlotClaim(
    displayName: gamepad.name,
    providerId: gamepad.id,
    sdlGuid: gamepad.identity.sdlGuid,
    serial: gamepad.identity.serial,
    localProfileId: localProfileId,
  );

  final String displayName;
  final String? providerId;
  final String? sdlGuid;
  final String? serial;

  /// Optional local profile attached to this controller for the current couch
  /// session. P1 ignores this field and remains owned by the active profile.
  /// This value is never persisted as controller affinity.
  final String? localProfileId;

  bool get hasProviderId => providerId?.isNotEmpty == true;

  bool get hasExactIdentity =>
      sdlGuid?.isNotEmpty == true && serial?.isNotEmpty == true;

  bool matchesProvider(ConnectedGamepad device) =>
      hasProviderId && device.id == providerId;

  bool matchesExact(ControllerIdentity identity) =>
      hasExactIdentity &&
      identity.hasExactIdentity &&
      identity.sdlGuid == sdlGuid &&
      identity.serial == serial;

  ControllerSlotClaim withLocalProfileId(String? value) => ControllerSlotClaim(
    displayName: displayName,
    providerId: providerId,
    sdlGuid: sdlGuid,
    serial: serial,
    localProfileId: value,
  );

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is ControllerSlotClaim &&
          other.displayName == displayName &&
          other.providerId == providerId &&
          other.sdlGuid == sdlGuid &&
          other.serial == serial &&
          other.localProfileId == localProfileId;

  @override
  int get hashCode =>
      Object.hash(displayName, providerId, sdlGuid, serial, localProfileId);
}

/// Canonical provenance for a resolved controller assignment.
///
/// This is recorded by the matching algorithm at the point where it chooses a
/// device, so downstream UI and launch projections never have to repeat
/// identity/provider/name precedence rules.
enum ControllerAssignmentSource {
  exactIdentity,
  providerId,
  fallbackName,

  /// Legacy-only provenance emitted by [resolveDeviceOrder].
  automatic,
}

/// Resolved player-slot assignment for one launch. [runtimeIndex] is the
/// input-stack index an emulator config writer should emit for [controller].
final class ResolvedControllerSlot {
  const ResolvedControllerSlot({
    required this.playerSlot,
    required this.controller,
    required this.runtimeIndex,
    this.assignmentSource = ControllerAssignmentSource.automatic,
  });

  final int playerSlot;
  final ConnectedGamepad controller;
  final int runtimeIndex;
  final ControllerAssignmentSource assignmentSource;

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is ResolvedControllerSlot &&
          other.playerSlot == playerSlot &&
          other.controller == controller &&
          other.runtimeIndex == runtimeIndex &&
          other.assignmentSource == assignmentSource;

  @override
  int get hashCode =>
      Object.hash(playerSlot, controller, runtimeIndex, assignmentSource);
}

/// Full slot-resolution result for one launch or UI projection. Connected
/// slots stay keyed by their player slot; exact offline claims remain reserved
/// holes instead of being compacted away. Fallback claims can also block their
/// slot when assigning it would steal an exact same-name device.
final class ControllerSlotResolution {
  ControllerSlotResolution({
    List<ResolvedControllerSlot> controllerSlots =
        const <ResolvedControllerSlot>[],
    Set<int> reservedPlayerSlots = const <int>{},
    Set<int> blockedPlayerSlots = const <int>{},
    List<ConnectedGamepad> availableControllers = const <ConnectedGamepad>[],
    List<ConnectedGamepad> attentionControllers = const <ConnectedGamepad>[],
  }) : controllerSlots = List<ResolvedControllerSlot>.unmodifiable(
         controllerSlots,
       ),
       reservedPlayerSlots = Set<int>.unmodifiable(reservedPlayerSlots),
       blockedPlayerSlots = Set<int>.unmodifiable(blockedPlayerSlots),
       availableControllers = List<ConnectedGamepad>.unmodifiable(
         availableControllers,
       ),
       attentionControllers = List<ConnectedGamepad>.unmodifiable(
         attentionControllers,
       );

  const ControllerSlotResolution.empty()
    : controllerSlots = const <ResolvedControllerSlot>[],
      reservedPlayerSlots = const <int>{},
      blockedPlayerSlots = const <int>{},
      availableControllers = const <ConnectedGamepad>[],
      attentionControllers = const <ConnectedGamepad>[];

  final List<ResolvedControllerSlot> controllerSlots;
  final Set<int> reservedPlayerSlots;
  final Set<int> blockedPlayerSlots;
  final List<ConnectedGamepad> availableControllers;

  /// Connected hardware withheld from both seating and joining because the
  /// canonical resolver cannot safely correlate it to a fallback claim.
  final List<ConnectedGamepad> attentionControllers;

  bool isPlayerSlotReserved(int playerSlot) =>
      reservedPlayerSlots.contains(playerSlot);

  bool isPlayerSlotBlocked(int playerSlot) =>
      reservedPlayerSlots.contains(playerSlot) ||
      blockedPlayerSlots.contains(playerSlot);
}

/// Switch-style player-slot assignment. Users claim slots by chord (L + R on
/// the pad that wants the slot); session claims resolve exact SDL identity first
/// when available, then preserve the display-name/order fallback behavior.
/// Unclaimed devices remain available. Only an explicit session claim creates
/// a joined player. Exact claims whose controller is offline reserve their
/// player slot instead of letting another connected controller promote into it.
final class ControllerAssignments {
  const ControllerAssignments._();

  /// Slots shown and seated. Only P1/P2 flow into a runtime today
  /// (DuckStation Pad1/Pad2); higher slots are claimable ahead of runtimes
  /// that use them.
  static const int maxSlots = 4;

  static List<ResolvedControllerSlot> resolveSlots({
    required List<ControllerSlotClaim?> claims,
    required List<ConnectedGamepad> devices,
  }) => resolveSlotResolution(claims: claims, devices: devices).controllerSlots;

  static ControllerSlotResolution resolveSlotResolution({
    required List<ControllerSlotClaim?> claims,
    required List<ConnectedGamepad> devices,
  }) {
    final resolution = _resolveDeviceIndicesBySlot(
      claims: claims,
      devices: devices,
    );
    final availableControllers = <ConnectedGamepad>[
      for (final (index, device) in devices.indexed)
        if (!resolution.usedDeviceIndices.contains(index) &&
            !resolution.quarantinedDeviceIndices.contains(index))
          device,
    ];
    final attentionControllers = <ConnectedGamepad>[
      for (final (index, device) in devices.indexed)
        if (resolution.quarantinedDeviceIndices.contains(index)) device,
    ];
    assert(
      resolution.usedDeviceIndices
          .intersection(resolution.quarantinedDeviceIndices)
          .isEmpty,
      'A connected controller cannot be both joined and attention-classified.',
    );
    assert(
      resolution.usedDeviceIndices.length +
              availableControllers.length +
              attentionControllers.length ==
          devices.length,
      'Connected controller classifications must be exhaustive and disjoint.',
    );
    return ControllerSlotResolution(
      controllerSlots: <ResolvedControllerSlot>[
        for (final (slot, deviceIndex) in resolution.bySlot.indexed)
          if (deviceIndex case final int index)
            ResolvedControllerSlot(
              playerSlot: slot,
              controller: devices[index],
              runtimeIndex: devices[index].order,
              assignmentSource: resolution.sourcesBySlot[slot]!,
            ),
      ],
      reservedPlayerSlots: resolution.reservedPlayerSlots,
      blockedPlayerSlots: resolution.blockedPlayerSlots,
      availableControllers: availableControllers,
      attentionControllers: attentionControllers,
    );
  }

  /// Resolves claims to concrete device indices, one per player slot.
  ///
  /// Element `k` of the result is the index into [devices] for player `k+1`.
  /// Each claimed name takes the first not-yet-used device with that name
  /// (duplicate pad models therefore claim distinct devices in claim order);
  /// claims whose name is not connected are skipped; remaining slots fill
  /// with unused devices in connection order. The result never exceeds
  /// [maxSlots] or the device count.
  static List<int> resolveDeviceOrder({
    required List<String?> claims,
    required List<ConnectedGamepad> devices,
  }) {
    final resolution = _resolveDeviceIndicesBySlot(
      claims: <ControllerSlotClaim?>[
        for (final claim in claims)
          if (claim == null) null else ControllerSlotClaim.fallback(claim),
      ],
      devices: devices,
      fillUnclaimed: true,
    );
    return <int>[
      for (final deviceIndex in resolution.bySlot)
        if (deviceIndex case final int index) index,
    ];
  }

  static ({
    List<int?> bySlot,
    List<ControllerAssignmentSource?> sourcesBySlot,
    Set<int> reservedPlayerSlots,
    Set<int> blockedPlayerSlots,
    Set<int> usedDeviceIndices,
    Set<int> quarantinedDeviceIndices,
  })
  _resolveDeviceIndicesBySlot({
    required List<ControllerSlotClaim?> claims,
    required List<ConnectedGamepad> devices,
    bool fillUnclaimed = false,
  }) {
    final used = <int>{};
    final bySlot = List<int?>.filled(maxSlots, null);
    final sourcesBySlot = List<ControllerAssignmentSource?>.filled(
      maxSlots,
      null,
    );
    final reservedPlayerSlots = <int>{};
    final blockedPlayerSlots = <int>{};
    final quarantinedDeviceIndices = <int>{};

    final slotLimit = claims.length < maxSlots ? claims.length : maxSlots;

    // Exact identity owns connected hardware globally, regardless of whether
    // a weaker fallback claim appears in an earlier player slot.
    for (var slot = 0; slot < slotLimit; slot++) {
      final claim = claims[slot];
      if (claim == null || !claim.hasExactIdentity) {
        continue;
      }
      final exactMatch = _firstUnusedIndex(
        used: used,
        devices: devices,
        matches: (device) => claim.matchesExact(device.identity),
      );
      if (exactMatch != null) {
        bySlot[slot] = exactMatch;
        sourcesBySlot[slot] = ControllerAssignmentSource.exactIdentity;
        used.add(exactMatch);
      } else {
        reservedPlayerSlots.add(slot);
      }
    }

    // Ephemeral provider correlation is the next-strongest ownership signal.
    for (var slot = 0; slot < slotLimit; slot++) {
      final claim = claims[slot];
      if (claim == null || claim.hasExactIdentity || !claim.hasProviderId) {
        continue;
      }
      final providerMatch = _firstUnusedIndex(
        used: used,
        devices: devices,
        matches: claim.matchesProvider,
      );
      if (providerMatch != null) {
        bySlot[slot] = providerMatch;
        sourcesBySlot[slot] = ControllerAssignmentSource.providerId;
        used.add(providerMatch);
      }
    }

    // Only after all strong claims are consumed can fallback ambiguity and its
    // quarantine be finalized truthfully.
    for (var slot = 0; slot < slotLimit; slot++) {
      final claim = claims[slot];
      if (claim == null || claim.hasExactIdentity || bySlot[slot] != null) {
        continue;
      }
      final fallbackMatch = _firstUnusedIndex(
        used: used,
        devices: devices,
        matches: (device) =>
            !device.identity.hasExactIdentity &&
            device.name == claim.displayName,
      );
      if (fallbackMatch != null) {
        bySlot[slot] = fallbackMatch;
        sourcesBySlot[slot] = ControllerAssignmentSource.fallbackName;
        used.add(fallbackMatch);
      } else if (_unusedExactSameNameIndices(
            used: used,
            devices: devices,
            displayName: claim.displayName,
          )
          case final candidates when candidates.isNotEmpty) {
        blockedPlayerSlots.add(slot);
        quarantinedDeviceIndices.addAll(candidates);
      }
    }

    if (fillUnclaimed) {
      var next = 0;
      for (var slot = 0; slot < maxSlots; slot++) {
        if (bySlot[slot] != null) {
          continue;
        }
        if (reservedPlayerSlots.contains(slot) ||
            blockedPlayerSlots.contains(slot)) {
          continue;
        }
        while (next < devices.length &&
            (used.contains(next) || quarantinedDeviceIndices.contains(next))) {
          next++;
        }
        if (next >= devices.length) {
          break;
        }
        bySlot[slot] = next;
        sourcesBySlot[slot] = ControllerAssignmentSource.automatic;
        used.add(next);
      }
    }

    return (
      bySlot: bySlot,
      sourcesBySlot: sourcesBySlot,
      reservedPlayerSlots: reservedPlayerSlots,
      blockedPlayerSlots: blockedPlayerSlots,
      usedDeviceIndices: used,
      quarantinedDeviceIndices: quarantinedDeviceIndices,
    );
  }

  static int? _firstUnusedIndex({
    required Set<int> used,
    required List<ConnectedGamepad> devices,
    required bool Function(ConnectedGamepad device) matches,
  }) {
    for (var i = 0; i < devices.length; i++) {
      if (!used.contains(i) && matches(devices[i])) {
        return i;
      }
    }
    return null;
  }

  static List<int> _unusedExactSameNameIndices({
    required Set<int> used,
    required List<ConnectedGamepad> devices,
    required String displayName,
  }) => <int>[
    for (final (index, device) in devices.indexed)
      if (!used.contains(index) &&
          device.identity.hasExactIdentity &&
          device.name == displayName)
        index,
  ];
}

/// Session-scoped seating store — the console model. Player order is joined or
/// renegotiated explicitly per session and never persisted. Within a session a
/// seated-but-disconnected pad
/// keeps its claim and snaps back on reconnect.
///
/// Durable per-controller state (glyph family, calibration) belongs to a
/// future registry keyed on real hardware identity — not to seats. The API
/// stays async so a durable variant could slot in without touching callers.
final class SessionControllerSlotClaims {
  SessionControllerSlotClaims([List<String?>? claims])
    : _claims = List<ControllerSlotClaim?>.generate(
        ControllerAssignments.maxSlots,
        (slot) => claims != null && slot < claims.length
            ? _fromDisplayName(claims[slot])
            : null,
      );

  SessionControllerSlotClaims.fromClaims(List<ControllerSlotClaim?> claims)
    : _claims = _normalizeClaims(claims);

  List<ControllerSlotClaim?> _claims;
  final StreamController<int> _changes = StreamController<int>.broadcast(
    sync: true,
  );
  int _revision = 0;

  /// Emits after a session seating mutation commits. This is an in-memory
  /// observation seam only; it does not make player order durable.
  Stream<int> get changes => _changes.stream;

  int get revision => _revision;

  /// Snapshot of the current claims — always
  /// [ControllerAssignments.maxSlots] long, `null` for unclaimed. This legacy
  /// view exposes only display names for existing UI/test call sites.
  List<String?> get claims => <String?>[
    for (final claim in _claims) claim?.displayName,
  ];

  List<ControllerSlotClaim?> get claimDetails =>
      List<ControllerSlotClaim?>.unmodifiable(_claims);

  /// Claims per slot, `null` for unclaimed — always
  /// [ControllerAssignments.maxSlots] long.
  Future<List<String?>> load() async => List<String?>.of(claims);

  Future<List<ControllerSlotClaim?>> loadClaims() async =>
      List<ControllerSlotClaim?>.of(_claims);

  /// Seats a controller as P1 only when P1 has no session claim yet. First-use
  /// startup handoff uses this so it cannot overwrite a Change Order
  /// assignment that already exists in the current app session.
  Future<bool> claimPlayerOneIfEmpty(ControllerSlotClaim claim) async {
    if (_claims[0] != null) {
      return false;
    }
    _claims = _normalizeClaims(<ControllerSlotClaim?>[
      claim.withLocalProfileId(null),
      for (var slot = 1; slot < _claims.length; slot++) _claims[slot],
    ]);
    _notifyChanged();
    return true;
  }

  /// Atomically joins one available controller to the next open player seat.
  /// The caller may gather identity first, but cancellation never calls this
  /// mutation and therefore writes no session intent.
  Future<ControllerJoinResult> join({
    required ControllerSlotClaim claim,
    String? localProfileId,
  }) async {
    if (_matchesExistingClaim(claim)) {
      return const ControllerJoinResult.alreadyJoined();
    }
    final playerSlot = _claims.indexOf(null);
    if (playerSlot < 0) {
      return const ControllerJoinResult.rosterFull();
    }
    final normalizedProfileId = playerSlot == 0
        ? null
        : _normalizeProfileId(localProfileId);
    if (normalizedProfileId != null &&
        _claims.any(
          (existing) => existing?.localProfileId == normalizedProfileId,
        )) {
      return const ControllerJoinResult.profileAlreadyJoined();
    }
    final next = List<ControllerSlotClaim?>.of(_claims);
    next[playerSlot] = claim.withLocalProfileId(normalizedProfileId);
    _claims = next;
    _notifyChanged();
    return ControllerJoinResult.joined(playerSlot: playerSlot);
  }

  /// Atomically replaces every claim: element `k` is the device name owning
  /// slot `k`, `null` for unclaimed. Entries beyond
  /// [ControllerAssignments.maxSlots] are ignored. This is the ceremony's
  /// commit — chord order in, whole assignment out.
  Future<void> replaceAll(List<String?> claims) async {
    await replaceAllClaims(<ControllerSlotClaim?>[
      for (final claim in claims) _fromDisplayName(claim),
    ]);
  }

  Future<void> replaceAllClaims(List<ControllerSlotClaim?> claims) async {
    final next = _normalizeClaims(claims);
    for (var slot = 0; slot < next.length; slot++) {
      final claim = next[slot];
      if (claim == null) {
        continue;
      }
      if (slot == 0) {
        next[slot] = claim.withLocalProfileId(null);
        continue;
      }
      if (claim.localProfileId?.isNotEmpty == true) {
        continue;
      }
      final previousProfileId = _profileForMatchingClaim(claim);
      if (previousProfileId != null) {
        next[slot] = claim.withLocalProfileId(previousProfileId);
      }
    }
    _claims = next;
    _notifyChanged();
  }

  /// Attaches or clears a local profile for P2-P4. The profile stays with the
  /// claimed controller across provider-id replacement and reordering.
  Future<bool> assignGuestProfile({
    required int playerSlot,
    required ControllerSlotClaim claim,
    required String? localProfileId,
  }) async {
    if (playerSlot <= 0 || playerSlot >= ControllerAssignments.maxSlots) {
      throw RangeError.range(
        playerSlot,
        1,
        ControllerAssignments.maxSlots - 1,
        'playerSlot',
      );
    }
    final trimmed = localProfileId?.trim();
    final normalizedProfileId = trimmed == null || trimmed.isEmpty
        ? null
        : trimmed;
    if (normalizedProfileId != null) {
      for (var slot = 1; slot < _claims.length; slot++) {
        if (slot != playerSlot &&
            _claims[slot]?.localProfileId == normalizedProfileId) {
          return false;
        }
      }
    }
    final updated = claim.withLocalProfileId(normalizedProfileId);
    if (_claims[playerSlot] == updated) {
      return false;
    }
    final next = List<ControllerSlotClaim?>.of(_claims);
    next[playerSlot] = updated;
    _claims = next;
    _notifyChanged();
    return true;
  }

  /// Clears every joined claim; connected devices become available again.
  Future<void> clearAll() async {
    _claims = List<ControllerSlotClaim?>.filled(
      ControllerAssignments.maxSlots,
      null,
    );
    _notifyChanged();
  }

  void _notifyChanged() {
    _revision++;
    _changes.add(_revision);
  }

  String? _profileForMatchingClaim(ControllerSlotClaim claim) {
    for (final previous in _claims) {
      final profileId = previous?.localProfileId;
      if (previous == null || profileId?.isNotEmpty != true) {
        continue;
      }
      if (claim.hasExactIdentity &&
          previous.hasExactIdentity &&
          claim.sdlGuid == previous.sdlGuid &&
          claim.serial == previous.serial) {
        return profileId;
      }
      if (claim.hasProviderId &&
          previous.hasProviderId &&
          claim.providerId == previous.providerId) {
        return profileId;
      }
    }
    return null;
  }

  bool _matchesExistingClaim(ControllerSlotClaim claim) {
    for (final existing in _claims) {
      if (existing == null) {
        continue;
      }
      if (claim.hasExactIdentity &&
          existing.hasExactIdentity &&
          claim.sdlGuid == existing.sdlGuid &&
          claim.serial == existing.serial) {
        return true;
      }
      if (claim.hasProviderId &&
          existing.hasProviderId &&
          claim.providerId == existing.providerId) {
        return true;
      }
    }
    return false;
  }

  static String? _normalizeProfileId(String? value) {
    final trimmed = value?.trim();
    return trimmed == null || trimmed.isEmpty ? null : trimmed;
  }

  static ControllerSlotClaim? _fromDisplayName(String? displayName) =>
      displayName == null ? null : ControllerSlotClaim.fallback(displayName);

  static List<ControllerSlotClaim?> _normalizeClaims(
    List<ControllerSlotClaim?> claims,
  ) => List<ControllerSlotClaim?>.generate(ControllerAssignments.maxSlots, (
    slot,
  ) {
    final claim = slot < claims.length ? claims[slot] : null;
    return slot == 0 ? claim?.withLocalProfileId(null) : claim;
  });
}

enum ControllerJoinDisposition {
  joined,
  alreadyJoined,
  rosterFull,
  profileAlreadyJoined,
}

final class ControllerJoinResult {
  const ControllerJoinResult.joined({required this.playerSlot})
    : disposition = ControllerJoinDisposition.joined;

  const ControllerJoinResult.alreadyJoined()
    : disposition = ControllerJoinDisposition.alreadyJoined,
      playerSlot = null;

  const ControllerJoinResult.rosterFull()
    : disposition = ControllerJoinDisposition.rosterFull,
      playerSlot = null;

  const ControllerJoinResult.profileAlreadyJoined()
    : disposition = ControllerJoinDisposition.profileAlreadyJoined,
      playerSlot = null;

  final ControllerJoinDisposition disposition;
  final int? playerSlot;
}
