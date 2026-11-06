import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/sdl_gamepad_mapping_codec.dart';

void main() {
  const codec = SdlGamepadMappingCodec();
  const guid = '03000000c82d00000061000000010000';

  test('SDL3 mapping encoding is deterministic and round-trips raw inputs', () {
    final mapping = CanonicalControllerMapping(
      const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.rightTrigger: RawAxisInput(
          5,
          direction: RawAxisDirection.positive,
        ),
        CanonicalGamepadControl.faceSouth: RawButtonInput(0),
        CanonicalGamepadControl.leftStickY: RawAxisInput(1, inverted: true),
        CanonicalGamepadControl.dpadUp: RawHatInput(0, 1),
      },
    );

    final encoded = codec.encode(
      SdlGamepadMappingDocument(
        sdlGuid: guid.toUpperCase(),
        displayName: 'Test Pad',
        sdlPlatform: 'Mac OS X',
        mapping: mapping,
      ),
    );

    expect(
      encoded,
      '$guid,Test Pad,a:b0,dpup:h0.1,righttrigger:+a5,lefty:a1~,platform:Mac OS X,',
    );
    final decoded = codec.decode(encoded);
    expect(decoded.sdlGuid, guid);
    expect(decoded.displayName, 'Test Pad');
    expect(decoded.sdlPlatform, 'Mac OS X');
    expect(decoded.mapping.bindings, mapping.bindings);
  });

  test('rejects diagonal hat bindings and retains cardinal values', () {
    expect(
      () => codec.decode('$guid,Test Pad,dpup:h0.3,platform:Mac OS X,'),
      throwsFormatException,
    );
    expect(
      () => CanonicalControllerMapping(
        const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.dpadUp: RawHatInput(0, 3),
        },
      ),
      throwsArgumentError,
    );
    expect(
      codec
          .decode('$guid,Test Pad,dpup:h0.1,platform:Mac OS X,')
          .mapping
          .inputFor(CanonicalGamepadControl.dpadUp),
      const RawHatInput(0, 1),
    );
  });

  test(
    'codec rejects malformed, unknown, duplicate, and unterminated data',
    () {
      for (final value in <String>[
        '$guid,Pad,a:b0,platform:Mac OS X',
        '$guid,Pad,paddle1:b0,platform:Mac OS X,',
        '$guid,Pad,a:b0,a:b1,platform:Mac OS X,',
        '$guid,Pad,a:nope,platform:Mac OS X,',
        '$guid,Pad,a:b0,',
      ]) {
        expect(() => codec.decode(value), throwsFormatException, reason: value);
      }
    },
  );

  test('detected import tolerates SDL metadata and extra controls', () {
    final detected = codec.decodeDetected(
      '$guid,Xbox Wireless Controller,'
      'crc:6d4a,a:b0,b:b1,x:b2,y:b3,leftx:a0,lefty:a1~,'
      'paddle1:b11,misc1:b15,touchpad:b16,'
      'hint:SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,platform:macOS,',
    );

    expect(detected.sdlGuid, guid);
    expect(detected.displayName, 'Xbox Wireless Controller');
    expect(detected.sdlPlatform, 'macOS');
    expect(detected.mapping.bindings, <
      CanonicalGamepadControl,
      RawGamepadInput
    >{
      CanonicalGamepadControl.faceSouth: const RawButtonInput(0),
      CanonicalGamepadControl.faceEast: const RawButtonInput(1),
      CanonicalGamepadControl.faceWest: const RawButtonInput(2),
      CanonicalGamepadControl.faceNorth: const RawButtonInput(3),
      CanonicalGamepadControl.leftStickX: const RawAxisInput(0),
      CanonicalGamepadControl.leftStickY: const RawAxisInput(1, inverted: true),
    });
    expect(
      () => codec.decode(
        '$guid,Xbox Wireless Controller,a:b0,crc:6d4a,platform:macOS,',
      ),
      throwsFormatException,
      reason: 'persisted decode must remain strict',
    );
  });

  test('detected import skips only unsafe individual source bindings', () {
    final detected = codec.decodeDetected(
      '$guid,Extension Pad,a:b0,b:b0,x:garbage,y:b70000,dpup:h0.3,'
      'lefttrigger:+a5,extension_without_separator,',
      fallbackPlatform: 'macOS',
    );

    expect(detected.sdlPlatform, 'macOS');
    expect(
      detected.mapping.bindings,
      <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: const RawButtonInput(0),
        CanonicalGamepadControl.leftTrigger: const RawAxisInput(
          5,
          direction: RawAxisDirection.positive,
        ),
      },
    );
  });

  test('detected import still validates controller identity', () {
    expect(
      () => codec.decodeDetected('not-a-guid,Pad,a:b0,platform:macOS,'),
      throwsFormatException,
    );
    expect(
      () => codec.decodeDetected('$guid,,a:b0,platform:macOS,'),
      throwsFormatException,
    );
    expect(
      () => codec.decodeDetected('$guid,Pad,a:b0,'),
      throwsFormatException,
    );
  });

  test('mapping validation rejects conflicting physical inputs', () {
    expect(
      () => CanonicalControllerMapping(
        const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.faceSouth: RawButtonInput(0),
          CanonicalGamepadControl.faceEast: RawButtonInput(0),
        },
      ),
      throwsArgumentError,
    );
    expect(
      () => CanonicalControllerMapping(
        const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.leftStickX: RawAxisInput(0),
          CanonicalGamepadControl.dpadLeft: RawAxisInput(
            0,
            direction: RawAxisDirection.negative,
          ),
        },
      ),
      throwsArgumentError,
    );
  });

  test('opposite half-axes remain distinct raw inputs', () {
    final mapping = CanonicalControllerMapping(
      const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.dpadLeft: RawAxisInput(
          0,
          direction: RawAxisDirection.negative,
        ),
        CanonicalGamepadControl.dpadRight: RawAxisInput(
          0,
          direction: RawAxisDirection.positive,
        ),
      },
    );

    expect(mapping.bindings, hasLength(2));
  });
}
