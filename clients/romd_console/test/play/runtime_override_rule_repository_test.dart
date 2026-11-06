import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/emulator/data/registry/drift_runtime_override_rule_repository.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

void main() {
  late AppDatabase db;
  late DriftRuntimeOverrideRuleRepository repo;
  final now = DateTime.utc(2026, 7, 5, 12);

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    repo = DriftRuntimeOverrideRuleRepository(database: db, now: () => now);
  });

  tearDown(() async {
    await db.close();
  });

  Future<List<RuntimeOverrideRule>> matchDefault() => repo.rulesMatching(
    platformShortName: 'snes',
    titleId: 'title-1',
    releaseId: 'rel-1',
  );

  test('setRule then rulesMatching round-trips the rule', () async {
    await repo.setRule(
      scope: RuntimeOverrideScope.platform,
      scopeValue: 'snes',
      profileId: const RuntimeProfileId('retroarch:snes:bsnes'),
    );

    final rules = await matchDefault();
    final rule = rules.single;
    expect(rule.scope, RuntimeOverrideScope.platform);
    expect(rule.scopeValue, 'snes');
    expect(rule.profileId, const RuntimeProfileId('retroarch:snes:bsnes'));
    expect(rule.updatedAt.isAtSameMomentAs(now), isTrue);
  });

  test(
    'setRule upserts: the newest profileId wins per (scope, scopeValue)',
    () async {
      await repo.setRule(
        scope: RuntimeOverrideScope.platform,
        scopeValue: 'snes',
        profileId: const RuntimeProfileId('retroarch:snes:bsnes'),
      );
      await repo.setRule(
        scope: RuntimeOverrideScope.platform,
        scopeValue: 'snes',
        profileId: const RuntimeProfileId('retroarch:snes:snes9x'),
      );

      final rules = await matchDefault();
      expect(
        rules.single.profileId,
        const RuntimeProfileId('retroarch:snes:snes9x'),
      );
    },
  );

  test('rulesMatching returns only rules matching the identity', () async {
    await repo.setRule(
      scope: RuntimeOverrideScope.platform,
      scopeValue: 'snes',
      profileId: const RuntimeProfileId('retroarch:snes:bsnes'),
    );
    await repo.setRule(
      scope: RuntimeOverrideScope.title,
      scopeValue: 'title-1',
      profileId: const RuntimeProfileId('retroarch:snes:snes9x'),
    );
    await repo.setRule(
      scope: RuntimeOverrideScope.release,
      scopeValue: 'rel-1',
      profileId: const RuntimeProfileId('retroarch:snes:bsnes'),
    );
    await repo.setRule(
      scope: RuntimeOverrideScope.platform,
      scopeValue: 'nes',
      profileId: const RuntimeProfileId('retroarch:nes:nestopia'),
    );
    await repo.setRule(
      scope: RuntimeOverrideScope.title,
      scopeValue: 'title-other',
      profileId: const RuntimeProfileId('retroarch:snes:bsnes'),
    );
    await repo.setRule(
      scope: RuntimeOverrideScope.release,
      scopeValue: 'rel-other',
      profileId: const RuntimeProfileId('retroarch:snes:bsnes'),
    );

    final rules = await matchDefault();
    expect(rules, hasLength(3));
    expect(
      rules.map((rule) => (rule.scope, rule.scopeValue)),
      containsAll(<(RuntimeOverrideScope, String)>[
        (RuntimeOverrideScope.platform, 'snes'),
        (RuntimeOverrideScope.title, 'title-1'),
        (RuntimeOverrideScope.release, 'rel-1'),
      ]),
    );
  });

  test('platform scope values are normalized on write and read', () async {
    await repo.setRule(
      scope: RuntimeOverrideScope.platform,
      scopeValue: ' SNES ',
      profileId: const RuntimeProfileId('retroarch:snes:bsnes'),
    );

    final stored = await matchDefault();
    expect(stored.single.scopeValue, 'snes');

    final viaMessyQuery = await repo.rulesMatching(
      platformShortName: ' SNES ',
      titleId: 'title-1',
      releaseId: 'rel-1',
    );
    expect(viaMessyQuery.single.scopeValue, 'snes');
  });
}
