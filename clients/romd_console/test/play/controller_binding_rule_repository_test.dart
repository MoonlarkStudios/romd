import 'package:drift/drift.dart' hide isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_binding_rule_repository.dart';
import 'package:romd_console/src/play/controllers/domain/controller_binding_rules.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

void main() {
  late AppDatabase db;
  late DriftControllerBindingRuleRepository repo;
  final now = DateTime.utc(2026, 7, 6, 12);

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    repo = DriftControllerBindingRuleRepository(database: db, now: () => now);
  });

  tearDown(() async {
    await db.close();
  });

  test('setBinding then bindingsMatching round-trips the rule', () async {
    await repo.setBinding(
      scope: ControllerBindingScope.title,
      scopeValue: 'title-1',
      action: RomdAction.saveState,
      button: GamepadButtonPosition.faceWest,
    );

    final rules = await repo.bindingsMatching(titleId: 'title-1');
    final rule = rules.single;
    expect(rule.scope, ControllerBindingScope.title);
    expect(rule.scopeValue, 'title-1');
    expect(rule.action, RomdAction.saveState);
    expect(rule.button, GamepadButtonPosition.faceWest);
    expect(rule.updatedAt.isAtSameMomentAs(now), isTrue);
  });

  test(
    'setBinding upserts: the newest button wins per (scope, scopeValue, action)',
    () async {
      await repo.setBinding(
        scope: ControllerBindingScope.global,
        scopeValue: '',
        action: RomdAction.screenshot,
        button: GamepadButtonPosition.faceNorth,
      );
      await repo.setBinding(
        scope: ControllerBindingScope.global,
        scopeValue: '',
        action: RomdAction.screenshot,
        button: GamepadButtonPosition.rightTrigger,
      );

      final rules = await repo.bindingsMatching(titleId: 'title-1');
      expect(rules.single.button, GamepadButtonPosition.rightTrigger);
    },
  );

  test(
    'rebinding one action leaves other actions in the scope intact',
    () async {
      await repo.setBinding(
        scope: ControllerBindingScope.global,
        scopeValue: '',
        action: RomdAction.saveState,
        button: GamepadButtonPosition.faceWest,
      );
      await repo.setBinding(
        scope: ControllerBindingScope.global,
        scopeValue: '',
        action: RomdAction.loadState,
        button: GamepadButtonPosition.faceEast,
      );

      final rules = await repo.bindingsMatching(titleId: 'title-1');
      expect(rules, hasLength(2));
    },
  );

  test(
    'bindingsMatching returns global rules plus the given title only',
    () async {
      await repo.setBinding(
        scope: ControllerBindingScope.global,
        scopeValue: '',
        action: RomdAction.menu,
        button: GamepadButtonPosition.guide,
      );
      await repo.setBinding(
        scope: ControllerBindingScope.title,
        scopeValue: 'title-1',
        action: RomdAction.saveState,
        button: GamepadButtonPosition.faceWest,
      );
      await repo.setBinding(
        scope: ControllerBindingScope.title,
        scopeValue: 'title-other',
        action: RomdAction.saveState,
        button: GamepadButtonPosition.faceEast,
      );

      final rules = await repo.bindingsMatching(titleId: 'title-1');
      expect(rules, hasLength(2));
      expect(
        rules.map((rule) => (rule.scope, rule.scopeValue)),
        containsAll(<(ControllerBindingScope, String)>[
          (ControllerBindingScope.global, ''),
          (ControllerBindingScope.title, 'title-1'),
        ]),
      );
    },
  );

  test('global scope values are normalized to empty on write', () async {
    await repo.setBinding(
      scope: ControllerBindingScope.global,
      scopeValue: 'junk-caller-value',
      action: RomdAction.menu,
      button: GamepadButtonPosition.guide,
    );

    final rules = await repo.bindingsMatching(titleId: 'title-1');
    expect(rules.single.scopeValue, '');
  });

  test('a null button round-trips as an explicit unbind', () async {
    await repo.setBinding(
      scope: ControllerBindingScope.title,
      scopeValue: 'title-1',
      action: RomdAction.screenshot,
      button: null,
    );

    final rules = await repo.bindingsMatching(titleId: 'title-1');
    final rule = rules.single;
    expect(rule.action, RomdAction.screenshot);
    expect(rule.button, isNull);
  });

  test(
    'global rows with a malformed scope value are ignored on read',
    () async {
      await db
          .into(db.controllerBindingRules)
          .insert(
            ControllerBindingRulesCompanion(
              scope: const Value('global'),
              scopeValue: const Value('legacy-junk'),
              action: const Value('menu'),
              button: const Value('guide'),
              updatedAt: Value(now),
            ),
          );

      expect(await repo.bindingsMatching(titleId: 'title-1'), isEmpty);
    },
  );

  test('rows with unparseable enum names are skipped, not surfaced', () async {
    await db
        .into(db.controllerBindingRules)
        .insert(
          ControllerBindingRulesCompanion(
            scope: const Value('global'),
            scopeValue: const Value(''),
            action: const Value('retiredAction'),
            button: const Value('faceSouth'),
            updatedAt: Value(now),
          ),
        );
    // A stale button name must be skipped, never misread as an unbind.
    await db
        .into(db.controllerBindingRules)
        .insert(
          ControllerBindingRulesCompanion(
            scope: const Value('global'),
            scopeValue: const Value(''),
            action: const Value('menu'),
            button: const Value('retiredButton'),
            updatedAt: Value(now),
          ),
        );
    await repo.setBinding(
      scope: ControllerBindingScope.global,
      scopeValue: '',
      action: RomdAction.saveState,
      button: GamepadButtonPosition.faceWest,
    );

    final rules = await repo.bindingsMatching(titleId: 'title-1');
    expect(rules.single.action, RomdAction.saveState);
  });
}
