import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/controller_binding_rules.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping_resolver.dart';
import 'package:romd_console/src/play/controllers/domain/controller_profile_mappings.dart';

final class _FakeRuleRepository implements ControllerBindingRuleRepository {
  _FakeRuleRepository(this._rules);

  final List<ControllerBindingRule> _rules;

  @override
  Future<void> setBinding({
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => throw UnimplementedError('resolver never writes');

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String titleId,
  }) async => _rules
      .where(
        (rule) =>
            rule.scope == ControllerBindingScope.global ||
            rule.scopeValue == titleId,
      )
      .toList(growable: false);
}

final class _FakeProfileRuleRepository
    implements ControllerProfileMappingRepository {
  _FakeProfileRuleRepository(this._rules);

  final Map<(String, String), List<ControllerBindingRule>> _rules;

  @override
  Future<void> saveProfile({
    required String localProfileId,
    required String sdlGuid,
    required String displayName,
    required ControllerTemplateId? templateId,
  }) => throw UnimplementedError('resolver never writes');

  @override
  Future<ControllerMappingProfile?> findProfile({
    required String localProfileId,
    required String sdlGuid,
  }) => throw UnimplementedError('resolver never reads profiles');

  @override
  Future<void> setBinding({
    required String localProfileId,
    required String sdlGuid,
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => throw UnimplementedError('resolver never writes');

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String localProfileId,
    required String sdlGuid,
    required String titleId,
  }) async =>
      (_rules[(localProfileId, sdlGuid)] ?? const <ControllerBindingRule>[])
          .where(
            (rule) =>
                rule.scope == ControllerBindingScope.global ||
                rule.scopeValue == titleId,
          )
          .toList(growable: false);
}

ControllerBindingRule _rule(
  ControllerBindingScope scope,
  String scopeValue,
  RomdAction action,
  GamepadButtonPosition? button,
) => ControllerBindingRule(
  scope: scope,
  scopeValue: scopeValue,
  action: action,
  button: button,
  updatedAt: DateTime.utc(2026, 7, 6),
);

void main() {
  const template = BuiltinControllerTemplates.xboxStyle;
  const reducedTemplate = ControllerTemplate(
    id: ControllerTemplateId('reduced'),
    displayName: 'Reduced',
    glyphFamily: GlyphFamily.generic,
    capabilities: ControllerCapabilities.fixed(<GamepadButtonPosition>{
      GamepadButtonPosition.faceSouth,
      GamepadButtonPosition.faceEast,
      GamepadButtonPosition.dpadUp,
      GamepadButtonPosition.dpadDown,
      GamepadButtonPosition.dpadLeft,
      GamepadButtonPosition.dpadRight,
      GamepadButtonPosition.select,
      GamepadButtonPosition.start,
    }),
  );

  test('without a rules repository the resolver yields the defaults', () async {
    const resolver = ControllerMappingResolver();

    final mapping = await resolver.resolve(template: template, titleId: 't-1');
    expect(
      mapping.bindings,
      BuiltinControllerTemplates.defaultMapping.bindings,
    );
  });

  test('template overrides beat the built-in default', () async {
    const custom = ControllerTemplate(
      id: ControllerTemplateId('test-template'),
      displayName: 'Test',
      glyphFamily: GlyphFamily.generic,
      overrides: ControllerMapping.fixed(<RomdAction, GamepadButtonPosition>{
        RomdAction.screenshot: GamepadButtonPosition.rightTrigger,
      }),
    );
    const resolver = ControllerMappingResolver();

    final mapping = await resolver.resolve(template: custom, titleId: 't-1');
    expect(
      mapping.bindingFor(RomdAction.screenshot),
      GamepadButtonPosition.rightTrigger,
    );
    expect(mapping.bindingFor(RomdAction.menu), GamepadButtonPosition.start);
  });

  test('user-global rules beat template overrides', () async {
    const custom = ControllerTemplate(
      id: ControllerTemplateId('test-template'),
      displayName: 'Test',
      glyphFamily: GlyphFamily.generic,
      overrides: ControllerMapping.fixed(<RomdAction, GamepadButtonPosition>{
        RomdAction.screenshot: GamepadButtonPosition.rightTrigger,
      }),
    );
    final resolver = ControllerMappingResolver(
      rules: _FakeRuleRepository(<ControllerBindingRule>[
        _rule(
          ControllerBindingScope.global,
          '',
          RomdAction.screenshot,
          GamepadButtonPosition.faceWest,
        ),
      ]),
    );

    final mapping = await resolver.resolve(template: custom, titleId: 't-1');
    expect(
      mapping.bindingFor(RomdAction.screenshot),
      GamepadButtonPosition.faceWest,
    );
  });

  test('per-title rules beat user-global rules', () async {
    final resolver = ControllerMappingResolver(
      rules: _FakeRuleRepository(<ControllerBindingRule>[
        _rule(
          ControllerBindingScope.global,
          '',
          RomdAction.saveState,
          GamepadButtonPosition.faceWest,
        ),
        _rule(
          ControllerBindingScope.title,
          't-1',
          RomdAction.saveState,
          GamepadButtonPosition.faceEast,
        ),
      ]),
    );

    final mapping = await resolver.resolve(template: template, titleId: 't-1');
    expect(
      mapping.bindingFor(RomdAction.saveState),
      GamepadButtonPosition.faceEast,
    );
  });

  test('a per-title explicit unbind disables an inherited chord', () async {
    final resolver = ControllerMappingResolver(
      rules: _FakeRuleRepository(<ControllerBindingRule>[
        _rule(ControllerBindingScope.title, 't-1', RomdAction.saveState, null),
      ]),
    );

    final mapping = await resolver.resolve(template: template, titleId: 't-1');
    expect(mapping.bindingFor(RomdAction.saveState), isNull);
    expect(
      mapping.bindingFor(RomdAction.loadState),
      GamepadButtonPosition.leftBumper,
    );
  });

  test('rules can bind actions the default leaves unbound', () async {
    final resolver = ControllerMappingResolver(
      rules: _FakeRuleRepository(<ControllerBindingRule>[
        _rule(
          ControllerBindingScope.global,
          '',
          RomdAction.fastForward,
          GamepadButtonPosition.rightTrigger,
        ),
      ]),
    );

    final mapping = await resolver.resolve(template: template, titleId: 't-1');
    expect(
      mapping.bindingFor(RomdAction.fastForward),
      GamepadButtonPosition.rightTrigger,
    );
  });

  test('a null titleId applies global rules but no title layer', () async {
    final resolver = ControllerMappingResolver(
      rules: _FakeRuleRepository(<ControllerBindingRule>[
        _rule(
          ControllerBindingScope.global,
          '',
          RomdAction.menu,
          GamepadButtonPosition.guide,
        ),
        _rule(
          ControllerBindingScope.title,
          '',
          RomdAction.menu,
          GamepadButtonPosition.faceSouth,
        ),
      ]),
    );

    final mapping = await resolver.resolve(template: template);
    expect(mapping.bindingFor(RomdAction.menu), GamepadButtonPosition.guide);
  });

  test('profile/GUID rules beat legacy device-wide rules', () async {
    final resolver = ControllerMappingResolver(
      rules: _FakeRuleRepository(<ControllerBindingRule>[
        _rule(
          ControllerBindingScope.title,
          't-1',
          RomdAction.saveState,
          GamepadButtonPosition.faceEast,
        ),
      ]),
      profileRules: _FakeProfileRuleRepository(
        <(String, String), List<ControllerBindingRule>>{
          ('jan', 'guid-1'): <ControllerBindingRule>[
            _rule(
              ControllerBindingScope.global,
              '',
              RomdAction.saveState,
              GamepadButtonPosition.faceWest,
            ),
          ],
        },
      ),
    );

    final mapping = await resolver.resolve(
      template: template,
      titleId: 't-1',
      localProfileId: 'jan',
      sdlGuid: 'guid-1',
    );

    expect(
      mapping.bindingFor(RomdAction.saveState),
      GamepadButtonPosition.faceWest,
    );
  });

  test('profile title rules beat profile global rules', () async {
    final resolver = ControllerMappingResolver(
      profileRules: _FakeProfileRuleRepository(
        <(String, String), List<ControllerBindingRule>>{
          ('jan', 'guid-1'): <ControllerBindingRule>[
            _rule(
              ControllerBindingScope.global,
              '',
              RomdAction.menu,
              GamepadButtonPosition.guide,
            ),
            _rule(
              ControllerBindingScope.title,
              't-1',
              RomdAction.menu,
              GamepadButtonPosition.faceSouth,
            ),
          ],
        },
      ),
    );

    final mapping = await resolver.resolve(
      template: template,
      titleId: 't-1',
      localProfileId: 'jan',
      sdlGuid: 'guid-1',
    );

    expect(
      mapping.bindingFor(RomdAction.menu),
      GamepadButtonPosition.faceSouth,
    );
  });

  test('assigned profile overlays the active profile per action', () async {
    final resolver = ControllerMappingResolver(
      profileRules: _FakeProfileRuleRepository(
        <(String, String), List<ControllerBindingRule>>{
          ('active', 'guid-1'): <ControllerBindingRule>[
            _rule(
              ControllerBindingScope.global,
              '',
              RomdAction.saveState,
              GamepadButtonPosition.faceWest,
            ),
            _rule(
              ControllerBindingScope.global,
              '',
              RomdAction.loadState,
              GamepadButtonPosition.faceEast,
            ),
          ],
          ('guest', 'guid-1'): <ControllerBindingRule>[
            _rule(
              ControllerBindingScope.global,
              '',
              RomdAction.saveState,
              GamepadButtonPosition.faceNorth,
            ),
          ],
        },
      ),
    );

    final mapping = await resolver.resolve(
      template: template,
      localProfileId: 'guest',
      fallbackLocalProfileId: 'active',
      sdlGuid: 'guid-1',
    );

    expect(
      mapping.bindingFor(RomdAction.saveState),
      GamepadButtonPosition.faceNorth,
    );
    expect(
      mapping.bindingFor(RomdAction.loadState),
      GamepadButtonPosition.faceEast,
    );
  });

  test('profile rules are inert without an SDL GUID', () async {
    final resolver = ControllerMappingResolver(
      rules: _FakeRuleRepository(<ControllerBindingRule>[
        _rule(
          ControllerBindingScope.global,
          '',
          RomdAction.menu,
          GamepadButtonPosition.guide,
        ),
      ]),
      profileRules: _FakeProfileRuleRepository(
        <(String, String), List<ControllerBindingRule>>{
          ('jan', 'guid-1'): <ControllerBindingRule>[
            _rule(
              ControllerBindingScope.global,
              '',
              RomdAction.menu,
              GamepadButtonPosition.faceSouth,
            ),
          ],
        },
      ),
    );

    final mapping = await resolver.resolve(
      template: template,
      titleId: 't-1',
      localProfileId: 'jan',
    );

    expect(mapping.bindingFor(RomdAction.menu), GamepadButtonPosition.guide);
  });

  test(
    'unavailable default bindings are dropped for reduced layouts',
    () async {
      const resolver = ControllerMappingResolver();

      final mapping = await resolver.resolve(
        template: reducedTemplate,
        titleId: 't-1',
      );

      expect(mapping.bindingFor(RomdAction.menu), GamepadButtonPosition.start);
      expect(mapping.bindings.containsKey(RomdAction.saveState), isFalse);
      expect(mapping.bindings.containsKey(RomdAction.loadState), isFalse);
    },
  );

  test(
    'unsupported stored bindings do not shadow supported inherited ones',
    () async {
      final resolver = ControllerMappingResolver(
        rules: _FakeRuleRepository(<ControllerBindingRule>[
          _rule(
            ControllerBindingScope.global,
            '',
            RomdAction.menu,
            GamepadButtonPosition.rightStickPress,
          ),
        ]),
        profileRules: _FakeProfileRuleRepository(
          <(String, String), List<ControllerBindingRule>>{
            ('jan', 'guid-1'): <ControllerBindingRule>[
              _rule(
                ControllerBindingScope.global,
                '',
                RomdAction.menu,
                GamepadButtonPosition.leftStickPress,
              ),
            ],
          },
        ),
      );

      final mapping = await resolver.resolve(
        template: reducedTemplate,
        titleId: 't-1',
        localProfileId: 'jan',
        sdlGuid: 'guid-1',
      );

      expect(mapping.bindingFor(RomdAction.menu), GamepadButtonPosition.start);
    },
  );

  test('explicit unbinds still shadow available inherited bindings', () async {
    final resolver = ControllerMappingResolver(
      profileRules: _FakeProfileRuleRepository(
        <(String, String), List<ControllerBindingRule>>{
          ('jan', 'guid-1'): <ControllerBindingRule>[
            _rule(ControllerBindingScope.global, '', RomdAction.menu, null),
          ],
        },
      ),
    );

    final mapping = await resolver.resolve(
      template: reducedTemplate,
      titleId: 't-1',
      localProfileId: 'jan',
      sdlGuid: 'guid-1',
    );

    expect(mapping.bindings.containsKey(RomdAction.menu), isTrue);
    expect(mapping.bindingFor(RomdAction.menu), isNull);
  });
}
