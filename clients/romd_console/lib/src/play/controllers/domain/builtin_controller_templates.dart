import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

/// Built-in controller templates and the default chord set. Template ids are
/// immutable once shipped — persisted device assignments will reference them.
final class BuiltinControllerTemplates {
  const BuiltinControllerTemplates._();

  /// The shipped in-game chord scheme (see `RetroArchConfigWriter` and
  /// `ControllerHelpScreen`): hold **Select**, then Start → menu, R → save,
  /// L → load, ◂ ▸ → state slot, north face button → screenshot.
  /// quit / fastForward / pause are deliberately unbound: Quit lives in the
  /// emulator menu so an accidental chord can never end a session.
  static const ControllerMapping defaultMapping =
      ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.hotkeyEnable: GamepadButtonPosition.select,
        RomdAction.menu: GamepadButtonPosition.start,
        RomdAction.saveState: GamepadButtonPosition.rightBumper,
        RomdAction.loadState: GamepadButtonPosition.leftBumper,
        RomdAction.stateSlotNext: GamepadButtonPosition.dpadRight,
        RomdAction.stateSlotPrevious: GamepadButtonPosition.dpadLeft,
        RomdAction.screenshot: GamepadButtonPosition.faceNorth,
      });

  static const ControllerTemplate xboxStyle = ControllerTemplate(
    id: ControllerTemplateId('xbox'),
    displayName: 'Xbox-style',
    glyphFamily: GlyphFamily.xbox,
  );

  static const ControllerTemplate playstationStyle = ControllerTemplate(
    id: ControllerTemplateId('playstation'),
    displayName: 'PlayStation-style',
    glyphFamily: GlyphFamily.playstation,
  );

  static const ControllerTemplate nintendoStyle = ControllerTemplate(
    id: ControllerTemplateId('nintendo'),
    displayName: 'Nintendo-style',
    glyphFamily: GlyphFamily.nintendo,
  );

  static const ControllerTemplate generic = ControllerTemplate(
    id: ControllerTemplateId('generic'),
    displayName: 'Generic / Steam Deck',
    glyphFamily: GlyphFamily.generic,
  );

  static const ControllerTemplate keyboard = ControllerTemplate(
    id: ControllerTemplateId('keyboard'),
    displayName: 'Keyboard',
    glyphFamily: GlyphFamily.keyboard,
  );

  static const List<ControllerTemplate> all = <ControllerTemplate>[
    xboxStyle,
    playstationStyle,
    nintendoStyle,
    generic,
    keyboard,
  ];

  /// The template with [id], or null for unknown/stale ids — callers fall
  /// back to [generic].
  static ControllerTemplate? byId(ControllerTemplateId id) {
    for (final template in all) {
      if (template.id == id) {
        return template;
      }
    }
    return null;
  }
}
