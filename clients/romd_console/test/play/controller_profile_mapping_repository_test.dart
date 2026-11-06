import 'package:drift/drift.dart' hide isNotNull, isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_profile_mapping_repository.dart';
import 'package:romd_console/src/play/controllers/domain/controller_binding_rules.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

void main() {
  late AppDatabase db;
  late DriftControllerProfileMappingRepository repo;
  final now = DateTime.utc(2026, 7, 9, 12);

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    repo = DriftControllerProfileMappingRepository(
      database: db,
      now: () => now,
    );
    await _insertProfile(db, 'jan', 'Jan');
    await _insertProfile(db, 'andy', 'Andy');
  });

  tearDown(() async {
    await db.close();
  });

  test(
    'binding rules are isolated by local profile for the same GUID',
    () async {
      await repo.setBinding(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
        scope: ControllerBindingScope.global,
        scopeValue: '',
        action: RomdAction.saveState,
        button: GamepadButtonPosition.faceWest,
      );
      await repo.setBinding(
        localProfileId: 'andy',
        sdlGuid: 'guid-1',
        scope: ControllerBindingScope.global,
        scopeValue: '',
        action: RomdAction.saveState,
        button: GamepadButtonPosition.faceEast,
      );

      final janRules = await repo.bindingsMatching(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
        titleId: 'title-1',
      );
      final andyRules = await repo.bindingsMatching(
        localProfileId: 'andy',
        sdlGuid: 'guid-1',
        titleId: 'title-1',
      );

      expect(janRules.single.button, GamepadButtonPosition.faceWest);
      expect(andyRules.single.button, GamepadButtonPosition.faceEast);
    },
  );

  test('binding rules are isolated by SDL GUID', () async {
    await repo.setBinding(
      localProfileId: 'jan',
      sdlGuid: 'guid-1',
      scope: ControllerBindingScope.global,
      scopeValue: '',
      action: RomdAction.menu,
      button: GamepadButtonPosition.guide,
    );

    expect(
      await repo.bindingsMatching(
        localProfileId: 'jan',
        sdlGuid: 'guid-2',
        titleId: 'title-1',
      ),
      isEmpty,
    );
  });

  test(
    'global scope value is normalized and title matching is selective',
    () async {
      await repo.setBinding(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
        scope: ControllerBindingScope.global,
        scopeValue: 'ignored',
        action: RomdAction.menu,
        button: GamepadButtonPosition.guide,
      );
      await repo.setBinding(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
        scope: ControllerBindingScope.title,
        scopeValue: 'title-1',
        action: RomdAction.saveState,
        button: GamepadButtonPosition.faceWest,
      );
      await repo.setBinding(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
        scope: ControllerBindingScope.title,
        scopeValue: 'title-2',
        action: RomdAction.loadState,
        button: GamepadButtonPosition.faceEast,
      );

      final rules = await repo.bindingsMatching(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
        titleId: 'title-1',
      );

      expect(
        rules.map((rule) => (rule.scope, rule.scopeValue, rule.action)),
        containsAll(<(ControllerBindingScope, String, RomdAction)>[
          (ControllerBindingScope.global, '', RomdAction.menu),
          (ControllerBindingScope.title, 'title-1', RomdAction.saveState),
        ]),
      );
      expect(
        rules.map((rule) => rule.action),
        isNot(contains(RomdAction.loadState)),
      );
    },
  );

  test('a null button round-trips as an explicit unbind', () async {
    await repo.setBinding(
      localProfileId: 'jan',
      sdlGuid: 'guid-1',
      scope: ControllerBindingScope.title,
      scopeValue: 'title-1',
      action: RomdAction.screenshot,
      button: null,
    );

    final rules = await repo.bindingsMatching(
      localProfileId: 'jan',
      sdlGuid: 'guid-1',
      titleId: 'title-1',
    );

    expect(rules.single.action, RomdAction.screenshot);
    expect(rules.single.button, isNull);
  });

  test('rows with stale enum names are skipped, not deleted', () async {
    await db
        .into(db.controllerProfileBindingRules)
        .insert(
          ControllerProfileBindingRulesCompanion(
            localProfileId: const Value('jan'),
            sdlGuid: const Value('guid-1'),
            scope: const Value('global'),
            scopeValue: const Value(''),
            action: const Value('retiredAction'),
            button: const Value('faceSouth'),
            updatedAt: Value(now),
          ),
        );
    await db
        .into(db.controllerProfileBindingRules)
        .insert(
          ControllerProfileBindingRulesCompanion(
            localProfileId: const Value('jan'),
            sdlGuid: const Value('guid-1'),
            scope: const Value('global'),
            scopeValue: const Value(''),
            action: const Value('menu'),
            button: const Value('retiredButton'),
            updatedAt: Value(now),
          ),
        );
    await repo.setBinding(
      localProfileId: 'jan',
      sdlGuid: 'guid-1',
      scope: ControllerBindingScope.global,
      scopeValue: '',
      action: RomdAction.saveState,
      button: GamepadButtonPosition.faceWest,
    );

    final rules = await repo.bindingsMatching(
      localProfileId: 'jan',
      sdlGuid: 'guid-1',
      titleId: 'title-1',
    );

    expect(rules.single.action, RomdAction.saveState);
    expect(
      await (db.select(db.controllerProfileBindingRules)).get(),
      hasLength(3),
    );
  });

  test(
    'mapping profile upsert preserves createdAt and updates metadata',
    () async {
      await repo.saveProfile(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
        displayName: '8BitDo Pro 2',
        templateId: const ControllerTemplateId('xbox'),
      );
      final later = DateTime.utc(2026, 7, 9, 13);
      repo = DriftControllerProfileMappingRepository(
        database: db,
        now: () => later,
      );

      await repo.saveProfile(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
        displayName: '8BitDo Pro 2 Wired',
        templateId: const ControllerTemplateId('nintendo'),
      );

      final profile = await repo.findProfile(
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
      );
      expect(profile, isNotNull);
      expect(profile!.displayName, '8BitDo Pro 2 Wired');
      expect(profile.templateId, const ControllerTemplateId('nintendo'));
      expect(profile.createdAt.isAtSameMomentAs(now), isTrue);
      expect(profile.updatedAt.isAtSameMomentAs(later), isTrue);
    },
  );

  test('empty GUIDs do not create profile or binding rows', () async {
    await repo.saveProfile(
      localProfileId: 'jan',
      sdlGuid: '  ',
      displayName: 'Fallback Controller',
      templateId: const ControllerTemplateId('generic'),
    );
    await repo.setBinding(
      localProfileId: 'jan',
      sdlGuid: '',
      scope: ControllerBindingScope.global,
      scopeValue: '',
      action: RomdAction.menu,
      button: GamepadButtonPosition.guide,
    );

    expect(await db.select(db.controllerMappingProfiles).get(), isEmpty);
    expect(await db.select(db.controllerProfileBindingRules).get(), isEmpty);
  });
}

Future<void> _insertProfile(
  AppDatabase db,
  String id,
  String displayName,
) async {
  final now = DateTime.utc(2026, 7, 9);
  await db
      .into(db.localProfiles)
      .insert(
        LocalProfilesCompanion.insert(
          id: id,
          displayName: displayName,
          avatarKey: 'default',
          accentColor: 0xff336699,
          entryMode: 'open',
          createdAt: now,
          updatedAt: now,
        ),
      );
}
