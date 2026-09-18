import 'dart:collection';

/// Stable gameplay controls on ROMD's vendor-neutral gamepad.
///
/// These values describe positions and axes, never printed vendor labels or
/// emulator-specific controls. Runtime adapters own every translation from
/// this model to their native configuration format.
enum CanonicalGamepadControl {
  faceSouth,
  faceEast,
  faceWest,
  faceNorth,
  dpadUp,
  dpadDown,
  dpadLeft,
  dpadRight,
  leftShoulder,
  rightShoulder,
  leftTrigger,
  rightTrigger,
  select,
  start,
  guide,
  leftStickPress,
  rightStickPress,
  leftStickX,
  leftStickY,
  rightStickX,
  rightStickY,
}

enum RawAxisDirection { full, negative, positive }

sealed class RawGamepadInput {
  const RawGamepadInput();

  bool conflictsWith(RawGamepadInput other);
}

final class RawButtonInput extends RawGamepadInput {
  const RawButtonInput(this.button);

  final int button;

  @override
  bool conflictsWith(RawGamepadInput other) =>
      other is RawButtonInput && other.button == button;

  @override
  bool operator ==(Object other) =>
      other is RawButtonInput && other.button == button;

  @override
  int get hashCode => Object.hash(RawButtonInput, button);
}

final class RawAxisInput extends RawGamepadInput {
  const RawAxisInput(
    this.axis, {
    this.direction = RawAxisDirection.full,
    this.inverted = false,
  });

  final int axis;
  final RawAxisDirection direction;
  final bool inverted;

  @override
  bool conflictsWith(RawGamepadInput other) {
    if (other is! RawAxisInput || other.axis != axis) {
      return false;
    }
    return direction == RawAxisDirection.full ||
        other.direction == RawAxisDirection.full ||
        direction == other.direction;
  }

  @override
  bool operator ==(Object other) =>
      other is RawAxisInput &&
      other.axis == axis &&
      other.direction == direction &&
      other.inverted == inverted;

  @override
  int get hashCode => Object.hash(RawAxisInput, axis, direction, inverted);
}

final class RawHatInput extends RawGamepadInput {
  const RawHatInput(this.hat, this.mask);

  final int hat;
  final int mask;

  @override
  bool conflictsWith(RawGamepadInput other) =>
      other is RawHatInput && other.hat == hat && other.mask & mask != 0;

  @override
  bool operator ==(Object other) =>
      other is RawHatInput && other.hat == hat && other.mask == mask;

  @override
  int get hashCode => Object.hash(RawHatInput, hat, mask);
}

/// One physical-input to canonical-pad definition.
final class CanonicalControllerMapping {
  CanonicalControllerMapping(
    Map<CanonicalGamepadControl, RawGamepadInput> bindings,
  ) : _bindings = Map<CanonicalGamepadControl, RawGamepadInput>.unmodifiable(
        bindings,
      ) {
    _validate(_bindings);
  }

  final Map<CanonicalGamepadControl, RawGamepadInput> _bindings;

  Map<CanonicalGamepadControl, RawGamepadInput> get bindings =>
      UnmodifiableMapView<CanonicalGamepadControl, RawGamepadInput>(_bindings);

  RawGamepadInput? inputFor(CanonicalGamepadControl control) =>
      _bindings[control];

  static void _validate(
    Map<CanonicalGamepadControl, RawGamepadInput> bindings,
  ) {
    final entries = bindings.entries.toList(growable: false);
    for (final entry in entries) {
      _validateInput(entry.value);
    }
    for (var i = 0; i < entries.length; i++) {
      for (var j = i + 1; j < entries.length; j++) {
        if (entries[i].value.conflictsWith(entries[j].value)) {
          throw ArgumentError(
            'Raw input collision between ${entries[i].key.name} and '
            '${entries[j].key.name}',
          );
        }
      }
    }
  }

  static void _validateInput(RawGamepadInput input) {
    switch (input) {
      case RawButtonInput(:final button):
        if (button < 0 || button > 65535) {
          throw ArgumentError.value(button, 'button', 'must be 0..65535');
        }
      case RawAxisInput(:final axis):
        if (axis < 0 || axis > 255) {
          throw ArgumentError.value(axis, 'axis', 'must be 0..255');
        }
      case RawHatInput(:final hat, :final mask):
        if (hat < 0 || hat > 255) {
          throw ArgumentError.value(hat, 'hat', 'must be 0..255');
        }
        if (mask != 1 && mask != 2 && mask != 4 && mask != 8) {
          throw ArgumentError.value(
            mask,
            'mask',
            'must be one cardinal SDL hat value: 1, 2, 4, or 8',
          );
        }
    }
  }
}

/// A persisted device-global custom setup. Provider/session ids are omitted on
/// purpose: identical units in one SDL platform/GUID mode share this mapping.
final class ControllerHardwareMapping {
  const ControllerHardwareMapping({
    required this.sdlPlatform,
    required this.sdlGuid,
    required this.displayName,
    required this.mapping,
    required this.createdAt,
    required this.updatedAt,
  });

  final String sdlPlatform;
  final String sdlGuid;
  final String displayName;
  final CanonicalControllerMapping mapping;
  final DateTime createdAt;
  final DateTime updatedAt;
}

abstract interface class ControllerHardwareMappingRepository {
  /// Pure read. Missing, future-format, and malformed rows return null without
  /// repairing or deleting stored data.
  Future<ControllerHardwareMapping?> find({
    required String sdlPlatform,
    required String sdlGuid,
  });

  /// Atomically replaces one explicit platform/GUID custom setup.
  Future<void> save({
    required String sdlPlatform,
    required String sdlGuid,
    required String displayName,
    required CanonicalControllerMapping mapping,
  });

  /// Removes only this custom hardware override. Profile shortcuts and label
  /// preferences are separate stores and are never affected.
  Future<void> reset({required String sdlPlatform, required String sdlGuid});
}
