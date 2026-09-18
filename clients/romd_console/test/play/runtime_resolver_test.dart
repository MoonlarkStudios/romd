import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';

PlayIdentity _identity(String platformShortName) => PlayIdentity(
  platformShortName: platformShortName,
  titleId: 'title-1',
  releaseId: 'rel-1',
);

/// In-memory rule store for resolver tests, seeded at construction. Resolver
/// tests never write rules, so [setRule] is deliberately unsupported.
final class _FakeRuleRepository implements RuntimeOverrideRuleRepository {
  _FakeRuleRepository(this._rules);

  final List<RuntimeOverrideRule> _rules;

  @override
  Future<void> setRule({
    required RuntimeOverrideScope scope,
    required String scopeValue,
    required RuntimeProfileId profileId,
  }) => throw UnsupportedError('resolver tests never write rules');

  @override
  Future<List<RuntimeOverrideRule>> rulesMatching({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) async {
    final platform = platformShortName.trim().toLowerCase();
    return _rules
        .where(
          (rule) => switch (rule.scope) {
            RuntimeOverrideScope.platform => rule.scopeValue == platform,
            RuntimeOverrideScope.title => rule.scopeValue == titleId,
            RuntimeOverrideScope.release => rule.scopeValue == releaseId,
          },
        )
        .toList(growable: false);
  }
}

RuntimeOverrideRule _rule(
  RuntimeOverrideScope scope,
  String value,
  String id,
) => RuntimeOverrideRule(
  scope: scope,
  scopeValue: value,
  profileId: RuntimeProfileId(id),
  updatedAt: DateTime.utc(2026, 7, 5),
);

LocalRuntimeResolver _resolverWithRules(List<RuntimeOverrideRule> rules) =>
    LocalRuntimeResolver(rules: _FakeRuleRepository(rules));

void main() {
  const resolver = LocalRuntimeResolver();

  group('LocalRuntimeResolver without rules', () {
    test(
      'snes preferred stays the built-in default snes9x, bsnes second',
      () async {
        final result = await resolver.resolve(_identity('snes'));
        expect(result.candidates.map((profile) => profile.id.value), <String>[
          'retroarch:snes:snes9x',
          'retroarch:snes:bsnes',
        ]);
        expect(result.preferred!.id.value, 'retroarch:snes:snes9x');
      },
    );

    test('normalizes platform input conservatively', () async {
      final result = await resolver.resolve(_identity(' N64 '));
      expect(result.preferred?.id.value, 'retroarch:n64:mupen64plus_next');
    });

    test(
      'returns empty candidates and null preferred when unsupported',
      () async {
        final result = await resolver.resolve(_identity('saturn'));
        expect(result.candidates, isEmpty);
        expect(result.preferred, isNull);
      },
    );

    test(
      'gb and gbc resolve to distinct profiles sharing the sameboy core',
      () async {
        final gb = await resolver.resolve(_identity('gb'));
        final gbc = await resolver.resolve(_identity('gbc'));
        expect(gb.preferred!.id, isNot(gbc.preferred!.id));
        expect(gb.preferred!.coreRequirement?.coreId, 'sameboy');
        expect(gbc.preferred!.coreRequirement?.coreId, 'sameboy');
      },
    );

    test('psx resolves three candidates with swanstation preferred', () async {
      final result = await resolver.resolve(_identity('psx'));
      expect(result.candidates.map((profile) => profile.id.value), <String>[
        'retroarch:psx:swanstation',
        'duckstation:psx:standalone',
        'retroarch:psx:mednafen_psx_hw',
      ]);
      expect(result.preferred!.id.value, 'retroarch:psx:swanstation');
    });

    test('ps2 resolves to PCSX2', () async {
      final result = await resolver.resolve(_identity('ps2'));
      expect(result.candidates.map((profile) => profile.id.value), <String>[
        'pcsx2:ps2:standalone',
      ]);
      expect(result.preferred!.id.value, 'pcsx2:ps2:standalone');
    });

    test('gc resolves to Dolphin while wii remains unsupported', () async {
      final gameCube = await resolver.resolve(_identity('gc'));
      expect(gameCube.candidates.map((profile) => profile.id.value), <String>[
        'dolphin:gc:standalone',
      ]);
      expect(gameCube.preferred!.id.value, 'dolphin:gc:standalone');

      final wii = await resolver.resolve(_identity('wii'));
      expect(wii.candidates, isEmpty);
      expect(wii.preferred, isNull);
    });
  });

  group('LocalRuntimeResolver with rules', () {
    test('platform rule flips the snes preferred profile to bsnes', () async {
      final ruled = _resolverWithRules(<RuntimeOverrideRule>[
        _rule(RuntimeOverrideScope.platform, 'snes', 'retroarch:snes:bsnes'),
      ]);
      final result = await ruled.resolve(_identity('snes'));
      expect(result.preferred!.id.value, 'retroarch:snes:bsnes');
    });

    test('title rule beats platform rule', () async {
      final ruled = _resolverWithRules(<RuntimeOverrideRule>[
        _rule(RuntimeOverrideScope.platform, 'snes', 'retroarch:snes:bsnes'),
        _rule(RuntimeOverrideScope.title, 'title-1', 'retroarch:snes:snes9x'),
      ]);
      final result = await ruled.resolve(_identity('snes'));
      expect(result.preferred!.id.value, 'retroarch:snes:snes9x');
    });

    test('release rule beats title rule', () async {
      final ruled = _resolverWithRules(<RuntimeOverrideRule>[
        _rule(RuntimeOverrideScope.title, 'title-1', 'retroarch:snes:bsnes'),
        _rule(RuntimeOverrideScope.release, 'rel-1', 'retroarch:snes:snes9x'),
      ]);
      final result = await ruled.resolve(_identity('snes'));
      expect(result.preferred!.id.value, 'retroarch:snes:snes9x');
    });

    test(
      'stale rule with an unknown profileId falls through to the next tier',
      () async {
        final ruled = _resolverWithRules(<RuntimeOverrideRule>[
          _rule(RuntimeOverrideScope.release, 'rel-1', 'retroarch:snes:gone'),
          _rule(RuntimeOverrideScope.platform, 'snes', 'retroarch:snes:bsnes'),
        ]);
        final result = await ruled.resolve(_identity('snes'));
        expect(result.preferred!.id.value, 'retroarch:snes:bsnes');
      },
    );

    test(
      'rule naming a profile from another platform is skipped as stale',
      () async {
        final ruled = _resolverWithRules(<RuntimeOverrideRule>[
          _rule(RuntimeOverrideScope.title, 'title-1', 'retroarch:nes:mesen'),
        ]);
        final result = await ruled.resolve(_identity('snes'));
        expect(result.preferred!.id.value, 'retroarch:snes:snes9x');
      },
    );

    test(
      'rules choose the preferred profile but never filter candidates',
      () async {
        final ruled = _resolverWithRules(<RuntimeOverrideRule>[
          _rule(RuntimeOverrideScope.platform, 'snes', 'retroarch:snes:bsnes'),
        ]);
        final result = await ruled.resolve(_identity('snes'));
        expect(result.candidates.map((profile) => profile.id.value), <String>[
          'retroarch:snes:snes9x',
          'retroarch:snes:bsnes',
        ]);
      },
    );
  });

  group('PlayIdentity', () {
    test('ofTarget carries the resolved target identity fields', () {
      const target = ResolvedPlayTarget(
        releaseId: 'rel-9',
        titleId: 'title-9',
        platformShortName: 'snes',
        displayName: 'Test Game',
        localProfileId: 'profile-1',
        contentRoot: '/content',
        launchAbsolutePath: '/content/game.bin',
        saveRoot: '/saves',
        stateRoot: '/states',
        configRoot: '/config',
      );
      final identity = PlayIdentity.ofTarget(target);
      expect(identity.platformShortName, 'snes');
      expect(identity.titleId, 'title-9');
      expect(identity.releaseId, 'rel-9');
    });

    test('ofInstall carries the install identity fields', () {
      final install = LocalInstall(
        serverInstanceId: '11111111-1111-4111-8111-111111111111',
        releaseId: 'rel-9',
        titleId: 'title-9',
        titleName: 'Test Game',
        platformId: 'p1',
        platformName: 'SNES',
        platformShortName: 'snes',
        coverUrl: null,
        releaseName: 'USA',
        releaseRevision: null,
        contentRoot: '/content',
        launchRelativePath: 'game.bin',
        sizeBytes: 5,
        primarySha256: null,
        manifestFingerprint: 'fp',
        state: InstallState.installed,
        installMode: 'permanent',
        items: const <InstalledItem>[],
        installedAt: DateTime.utc(2026, 7, 5),
        lastPlayedAt: null,
      );
      final identity = PlayIdentity.ofInstall(install);
      expect(identity.platformShortName, 'snes');
      expect(identity.titleId, 'title-9');
      expect(identity.releaseId, 'rel-9');
    });
  });
}
