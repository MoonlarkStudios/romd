# Batocera Config Generators

Batocera is a practical reference for translating one normalized controller
model into emulator-specific configuration. It is a reference for config
writers only. ROMD's identity and persistence contract is defined in
[controller identity](../decisions/controller-identity.md) and
[controller mapping ownership](../decisions/controller-mapping-ownership.md).

## What Batocera does

- **Positional mapping.** Controllers are mapped by physical or cardinal button
  position, not by vendor button label.
- **Resolve first, then generate.** Player controllers are resolved before
  launch and passed to the selected emulator's config generator.
- **RetroArch:** generates `input_playerN_*` bindings and
  `input_playerN_joypad_index` from the resolved player order.
- **DuckStation:** enables SDL input and writes `PadN` bindings using
  `SDL-<index>/...` names.
- **Dolphin and MAME:** write emulator-native configuration rather than forcing
  a RetroArch-shaped model through every runtime.

## Sources

- [Configure a controller](https://wiki.batocera.org/configure_a_controller)
- [Remapping controls per emulator](https://wiki.batocera.org/remapping_controls_per_emulator)
- [`controller.py`](https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/controller.py)
- [`libretroControllers.py`](https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/generators/libretro/libretroControllers.py)
- [`duckstationGenerator.py`](https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/generators/duckstation/duckstationGenerator.py)
- [`dolphinControllers.py`](https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/generators/dolphin/dolphinControllers.py)
- [`mameControllers.py`](https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/generators/mame/mameControllers.py)
