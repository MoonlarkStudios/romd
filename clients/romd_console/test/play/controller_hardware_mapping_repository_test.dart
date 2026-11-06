import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_hardware_mapping_repository.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/sdl_gamepad_mapping_codec.dart';

void main() {
  const guidA = '03000000c82d00000061000000010000';
  const guidB = '030000005e0400008e02000000010000';
  final mapping = CanonicalControllerMapping(
    const <CanonicalGamepadControl, RawGamepadInput>{
      CanonicalGamepadControl.faceSouth: RawButtonInput(0),
      CanonicalGamepadControl.dpadUp: RawHatInput(0, 1),
      CanonicalGamepadControl.leftStickX: RawAxisInput(0),
    },
  );
  late AppDatabase db;
  late DriftControllerHardwareMappingRepository repository;
  var now = DateTime.utc(2026, 7, 12, 12);

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    repository = DriftControllerHardwareMappingRepository(
      database: db,
      now: () => now,
    );
  });

  tearDown(() => db.close());

  test('explicit save round-trips and preserves creation timestamp', () async {
    await repository.save(
      sdlPlatform: 'Mac OS X',
      sdlGuid: guidA.toUpperCase(),
      displayName: '8BitDo Pro 2',
      mapping: mapping,
    );
    final createdAt = now;
    now = now.add(const Duration(hours: 1));
    await repository.save(
      sdlPlatform: 'Mac OS X',
      sdlGuid: guidA,
      displayName: '8BitDo Pro 2 Wired',
      mapping: mapping,
    );

    final saved = await repository.find(
      sdlPlatform: 'Mac OS X',
      sdlGuid: guidA,
    );
    expect(saved, isNotNull);
    expect(saved!.displayName, '8BitDo Pro 2 Wired');
    expect(saved.mapping.bindings, mapping.bindings);
    expect(saved.createdAt.isAtSameMomentAs(createdAt), isTrue);
    expect(saved.updatedAt.isAtSameMomentAs(now), isTrue);
    expect(await db.select(db.controllerHardwareMappings).get(), hasLength(1));
  });

  test('platform and GUID keys are isolated', () async {
    Future<void> save(String platform, String guid, int button) =>
        repository.save(
          sdlPlatform: platform,
          sdlGuid: guid,
          displayName: 'Shared Model',
          mapping: CanonicalControllerMapping(
            <CanonicalGamepadControl, RawGamepadInput>{
              CanonicalGamepadControl.faceSouth: RawButtonInput(button),
            },
          ),
        );

    await save('Mac OS X', guidA, 0);
    await save('Windows', guidA, 1);
    await save('Mac OS X', guidB, 2);

    expect(
      (await repository.find(
        sdlPlatform: 'Mac OS X',
        sdlGuid: guidA,
      ))?.mapping.inputFor(CanonicalGamepadControl.faceSouth),
      const RawButtonInput(0),
    );
    expect(
      (await repository.find(
        sdlPlatform: 'Windows',
        sdlGuid: guidA,
      ))?.mapping.inputFor(CanonicalGamepadControl.faceSouth),
      const RawButtonInput(1),
    );
    expect(await db.select(db.controllerHardwareMappings).get(), hasLength(3));
  });

  test('passive missing and invalid reads perform zero writes', () async {
    expect(
      await repository.find(sdlPlatform: 'Mac OS X', sdlGuid: guidA),
      isNull,
    );
    expect(await repository.find(sdlPlatform: '', sdlGuid: 'fallback'), isNull);
    expect(await db.select(db.controllerHardwareMappings).get(), isEmpty);
  });

  test('future and malformed rows fail closed and are retained', () async {
    final timestamp = DateTime.utc(2026, 7, 12);
    for (final entry in <({String guid, int version, String data})>[
      (
        guid: guidA,
        version: SdlGamepadMappingCodec.currentFormatVersion + 1,
        data: 'future-data',
      ),
      (guid: guidB, version: 1, data: 'malformed'),
    ]) {
      await db
          .into(db.controllerHardwareMappings)
          .insert(
            ControllerHardwareMappingsCompanion.insert(
              sdlPlatform: 'Mac OS X',
              sdlGuid: entry.guid,
              displayName: 'Pad',
              mappingFormatVersion: entry.version,
              mappingData: entry.data,
              createdAt: timestamp,
              updatedAt: timestamp,
            ),
          );
      expect(
        await repository.find(sdlPlatform: 'Mac OS X', sdlGuid: entry.guid),
        isNull,
      );
    }
    expect(await db.select(db.controllerHardwareMappings).get(), hasLength(2));
  });

  test('failed replacement rolls back and retains the prior mapping', () async {
    await repository.save(
      sdlPlatform: 'Mac OS X',
      sdlGuid: guidA,
      displayName: 'Original',
      mapping: mapping,
    );
    await db.customStatement('''
CREATE TRIGGER reject_hardware_mapping_update
BEFORE UPDATE ON controller_hardware_mappings
BEGIN
  SELECT RAISE(ABORT, 'injected save failure');
END
''');

    await expectLater(
      repository.save(
        sdlPlatform: 'Mac OS X',
        sdlGuid: guidA,
        displayName: 'Replacement',
        mapping: CanonicalControllerMapping(
          const <CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: RawButtonInput(7),
          },
        ),
      ),
      throwsA(anything),
    );

    final retained = await repository.find(
      sdlPlatform: 'Mac OS X',
      sdlGuid: guidA,
    );
    expect(retained?.displayName, 'Original');
    expect(
      retained?.mapping.inputFor(CanonicalGamepadControl.faceSouth),
      const RawButtonInput(0),
    );
  });

  test('reset removes only the selected hardware override', () async {
    await repository.save(
      sdlPlatform: 'Mac OS X',
      sdlGuid: guidA,
      displayName: 'Pad A',
      mapping: mapping,
    );
    await repository.save(
      sdlPlatform: 'Mac OS X',
      sdlGuid: guidB,
      displayName: 'Pad B',
      mapping: mapping,
    );

    await repository.reset(sdlPlatform: 'Mac OS X', sdlGuid: guidA);

    expect(
      await repository.find(sdlPlatform: 'Mac OS X', sdlGuid: guidA),
      isNull,
    );
    expect(
      await repository.find(sdlPlatform: 'Mac OS X', sdlGuid: guidB),
      isNotNull,
    );
  });
}
