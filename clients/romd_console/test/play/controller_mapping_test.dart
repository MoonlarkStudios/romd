import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

void main() {
  group('ControllerMapping', () {
    test('overlaidWith lets the override win per action', () {
      const base = ControllerMapping.fixed(<RomdAction, GamepadButtonPosition>{
        RomdAction.saveState: GamepadButtonPosition.rightBumper,
        RomdAction.loadState: GamepadButtonPosition.leftBumper,
      });
      const overrides = ControllerMapping.fixed(
        <RomdAction, GamepadButtonPosition>{
          RomdAction.saveState: GamepadButtonPosition.faceWest,
        },
      );

      final merged = base.overlaidWith(overrides);
      expect(
        merged.bindingFor(RomdAction.saveState),
        GamepadButtonPosition.faceWest,
      );
      expect(
        merged.bindingFor(RomdAction.loadState),
        GamepadButtonPosition.leftBumper,
      );
    });

    test('overlaidWith with an empty override returns the same mapping', () {
      const base = ControllerMapping.fixed(<RomdAction, GamepadButtonPosition>{
        RomdAction.menu: GamepadButtonPosition.start,
      });
      expect(
        identical(base.overlaidWith(ControllerMapping.empty), base),
        isTrue,
      );
    });

    test('actions absent from every layer stay unbound', () {
      final merged = ControllerMapping.empty.overlaidWith(
        BuiltinControllerTemplates.defaultMapping,
      );
      expect(merged.bindingFor(RomdAction.quit), isNull);
    });

    test('an explicit-unbound override shadows an inherited binding', () {
      const overrides = ControllerMapping.fixed(
        <RomdAction, GamepadButtonPosition?>{RomdAction.saveState: null},
      );

      final merged = BuiltinControllerTemplates.defaultMapping.overlaidWith(
        overrides,
      );
      expect(merged.bindingFor(RomdAction.saveState), isNull);
      expect(merged.bindings.containsKey(RomdAction.saveState), isTrue);
      expect(
        merged.bindingFor(RomdAction.loadState),
        GamepadButtonPosition.leftBumper,
      );
    });

    test('the default constructor copies: retained maps cannot mutate it', () {
      final source = <RomdAction, GamepadButtonPosition?>{
        RomdAction.menu: GamepadButtonPosition.start,
      };
      final mapping = ControllerMapping(source);

      source[RomdAction.menu] = GamepadButtonPosition.guide;
      expect(mapping.bindingFor(RomdAction.menu), GamepadButtonPosition.start);
    });

    test('bindings exposes a read-only view', () {
      const mapping = ControllerMapping.fixed(
        <RomdAction, GamepadButtonPosition?>{
          RomdAction.menu: GamepadButtonPosition.start,
        },
      );
      expect(
        () => mapping.bindings[RomdAction.menu] = GamepadButtonPosition.guide,
        throwsUnsupportedError,
      );
    });

    test(
      'filteredFor keeps explicit unbinds and drops unavailable buttons',
      () {
        const capabilities = ControllerCapabilities.fixed(
          <GamepadButtonPosition>{
            GamepadButtonPosition.select,
            GamepadButtonPosition.start,
          },
        );
        const mapping =
            ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
              RomdAction.hotkeyEnable: GamepadButtonPosition.select,
              RomdAction.menu: GamepadButtonPosition.start,
              RomdAction.saveState: GamepadButtonPosition.rightBumper,
              RomdAction.screenshot: null,
            });

        final filtered = mapping.filteredFor(capabilities);

        expect(
          filtered.bindingFor(RomdAction.hotkeyEnable),
          GamepadButtonPosition.select,
        );
        expect(
          filtered.bindingFor(RomdAction.menu),
          GamepadButtonPosition.start,
        );
        expect(filtered.bindings.containsKey(RomdAction.saveState), isFalse);
        expect(filtered.bindings.containsKey(RomdAction.screenshot), isTrue);
        expect(filtered.bindingFor(RomdAction.screenshot), isNull);
      },
    );
  });

  group('BuiltinControllerTemplates', () {
    test('default mapping matches the shipped chord scheme', () {
      const mapping = BuiltinControllerTemplates.defaultMapping;
      expect(mapping.bindings, <RomdAction, GamepadButtonPosition>{
        RomdAction.hotkeyEnable: GamepadButtonPosition.select,
        RomdAction.menu: GamepadButtonPosition.start,
        RomdAction.saveState: GamepadButtonPosition.rightBumper,
        RomdAction.loadState: GamepadButtonPosition.leftBumper,
        RomdAction.stateSlotNext: GamepadButtonPosition.dpadRight,
        RomdAction.stateSlotPrevious: GamepadButtonPosition.dpadLeft,
        RomdAction.screenshot: GamepadButtonPosition.faceNorth,
      });
    });

    test('quit, fastForward, and pause are deliberately unbound', () {
      const mapping = BuiltinControllerTemplates.defaultMapping;
      expect(mapping.bindingFor(RomdAction.quit), isNull);
      expect(mapping.bindingFor(RomdAction.fastForward), isNull);
      expect(mapping.bindingFor(RomdAction.pause), isNull);
    });

    test('template ids are unique and carry no binding overrides yet', () {
      final ids = BuiltinControllerTemplates.all
          .map((template) => template.id)
          .toSet();
      expect(ids, hasLength(BuiltinControllerTemplates.all.length));
      for (final template in BuiltinControllerTemplates.all) {
        expect(
          template.overrides.bindings,
          isEmpty,
          reason: '${template.id.value} should not override the default yet',
        );
      }
    });

    test('shipped templates keep glyph family separate from capabilities', () {
      for (final template in BuiltinControllerTemplates.all) {
        for (final position in GamepadButtonPosition.values) {
          expect(
            template.capabilities.supports(position),
            isTrue,
            reason:
                '${template.id.value} should keep current fallback behavior',
          );
        }
      }
    });

    test('byId finds shipped templates and rejects unknown ids', () {
      expect(
        BuiltinControllerTemplates.byId(const ControllerTemplateId('nintendo')),
        same(BuiltinControllerTemplates.nintendoStyle),
      );
      expect(
        BuiltinControllerTemplates.byId(const ControllerTemplateId('retired')),
        isNull,
      );
    });

    test('every glyph family is represented by a template', () {
      expect(
        BuiltinControllerTemplates.all
            .map((template) => template.glyphFamily)
            .toSet(),
        GlyphFamily.values.toSet(),
      );
    });
  });
}
