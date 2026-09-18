import 'dart:collection';

/// Typed id for controller templates. Extension type so equality and hashing
/// delegate to the underlying [String] with zero runtime cost (mirrors
/// [RuntimeProfileId]).
extension type const ControllerTemplateId(String value) {}

/// A button position on a standard gamepad. Position names are SDL-inspired,
/// but this is ROMD's own model — not SDL's, and not any emulator's.
/// Positions are stable across vendors; labels are not (faceSouth is A on
/// Xbox, B on Nintendo, Cross on PlayStation) — glyph rendering resolves
/// labels via [GlyphFamily].
///
/// Declaration order is presentation order only. Never use `.index` as an
/// SDL, RetroPad, or any other emitter code — runtime emitters own explicit
/// per-format maps.
enum GamepadButtonPosition {
  faceSouth,
  faceEast,
  faceWest,
  faceNorth,
  dpadUp,
  dpadDown,
  dpadLeft,
  dpadRight,
  leftBumper,
  rightBumper,
  leftTrigger,
  rightTrigger,
  select,
  start,
  guide,
  leftStickPress,
  rightStickPress,
}

/// Which label/icon set to render for a device's buttons.
enum GlyphFamily { xbox, playstation, nintendo, generic, keyboard }

/// Physical buttons ROMD believes are available on the current controller
/// layout. This is deliberately separate from [GlyphFamily]: labels describe
/// what to print, capabilities describe what can be pressed.
final class ControllerCapabilities {
  ControllerCapabilities(Iterable<GamepadButtonPosition> availableButtons)
    : _availableButtons = Set<GamepadButtonPosition>.unmodifiable(
        availableButtons,
      );

  /// For compile-time set literals only. Runtime callers should use the
  /// default constructor so retained sets cannot mutate this value.
  const ControllerCapabilities.fixed(this._availableButtons);

  static const ControllerCapabilities standardGamepad =
      ControllerCapabilities.fixed(<GamepadButtonPosition>{
        GamepadButtonPosition.faceSouth,
        GamepadButtonPosition.faceEast,
        GamepadButtonPosition.faceWest,
        GamepadButtonPosition.faceNorth,
        GamepadButtonPosition.dpadUp,
        GamepadButtonPosition.dpadDown,
        GamepadButtonPosition.dpadLeft,
        GamepadButtonPosition.dpadRight,
        GamepadButtonPosition.leftBumper,
        GamepadButtonPosition.rightBumper,
        GamepadButtonPosition.leftTrigger,
        GamepadButtonPosition.rightTrigger,
        GamepadButtonPosition.select,
        GamepadButtonPosition.start,
        GamepadButtonPosition.guide,
        GamepadButtonPosition.leftStickPress,
        GamepadButtonPosition.rightStickPress,
      });

  final Set<GamepadButtonPosition> _availableButtons;

  Set<GamepadButtonPosition> get availableButtons =>
      UnmodifiableSetView<GamepadButtonPosition>(_availableButtons);

  bool supports(GamepadButtonPosition button) =>
      _availableButtons.contains(button);
}

/// Launcher-level in-game actions ROMD can bind. Every action except
/// [hotkeyEnable] is emitted as an emulator hotkey chord — hold the
/// [hotkeyEnable] button, then press the action's button. ROMD never captures
/// input while a game runs; runtime emitters translate these into each
/// emulator's own hotkey config.
enum RomdAction {
  hotkeyEnable,
  menu,
  quit,
  saveState,
  loadState,
  fastForward,
  pause,
  screenshot,
  stateSlotNext,
  stateSlotPrevious,
}

/// A partial [RomdAction] → [GamepadButtonPosition] assignment, used both as an
/// override layer and as the resolved effective mapping. Three states per
/// action:
/// - bound: present with a button,
/// - explicitly unbound: present with `null` — disables the chord, shadowing
///   anything an earlier layer bound,
/// - absent: inherits from earlier layers (unbound if absent from all).
final class ControllerMapping {
  /// Copies [bindings] into an unmodifiable map — a retained reference to the
  /// argument can never mutate this mapping.
  ControllerMapping(Map<RomdAction, GamepadButtonPosition?> bindings)
    : _bindings = Map<RomdAction, GamepadButtonPosition?>.unmodifiable(
        bindings,
      );

  /// For compile-time map literals only, which are deeply immutable. Never
  /// pass a runtime-built map here — use the default constructor, which
  /// copies.
  const ControllerMapping.fixed(this._bindings);

  static const ControllerMapping empty = ControllerMapping.fixed(
    <RomdAction, GamepadButtonPosition?>{},
  );

  final Map<RomdAction, GamepadButtonPosition?> _bindings;

  /// Read-only view; the backing map is never exposed for mutation.
  Map<RomdAction, GamepadButtonPosition?> get bindings =>
      UnmodifiableMapView<RomdAction, GamepadButtonPosition?>(_bindings);

  /// The effective button for [action]; `null` when unbound — explicitly or
  /// by absence. Layer inspection (which scope unbound it) belongs to the
  /// rules, not the resolved mapping.
  GamepadButtonPosition? bindingFor(RomdAction action) => _bindings[action];

  /// Layers [overrides] on top of this mapping: the override wins per action
  /// (including explicit-unbound entries), absent actions inherit.
  ControllerMapping overlaidWith(ControllerMapping overrides) =>
      overrides._bindings.isEmpty
      ? this
      : ControllerMapping(<RomdAction, GamepadButtonPosition?>{
          ..._bindings,
          ...overrides._bindings,
        });

  /// Drops non-null bindings that point at buttons unavailable on
  /// [capabilities]. Explicit unbinds (`null`) remain present so they can
  /// continue shadowing earlier layers.
  ControllerMapping filteredFor(ControllerCapabilities capabilities) {
    if (_bindings.isEmpty) {
      return this;
    }

    final filtered = <RomdAction, GamepadButtonPosition?>{};
    for (final entry in _bindings.entries) {
      final button = entry.value;
      if (button == null || capabilities.supports(button)) {
        filtered[entry.key] = button;
      }
    }
    return filtered.length == _bindings.length
        ? this
        : ControllerMapping(filtered);
  }
}

/// A device template: the glyph family to render plus binding overrides
/// layered over the built-in default mapping. Templates are code-defined;
/// persisted rules reference them by id, so ids are immutable once shipped
/// (same invariant as built-in runtime profiles).
final class ControllerTemplate {
  const ControllerTemplate({
    required this.id,
    required this.displayName,
    required this.glyphFamily,
    this.capabilities = ControllerCapabilities.standardGamepad,
    this.overrides = ControllerMapping.empty,
  });

  final ControllerTemplateId id;

  final String displayName;

  final GlyphFamily glyphFamily;

  final ControllerCapabilities capabilities;

  /// Bindings this template changes relative to the built-in default. Empty
  /// for all shipped templates today — the chord set is positional, so it
  /// holds across vendors.
  final ControllerMapping overrides;
}
