/// Closed wire formats. Only exact lowercase ids are accepted at the boundary.
enum RomDeliveryFormat {
  original,
  rvz,
  chd;

  static RomDeliveryFormat? tryParse(String value) => switch (value) {
    'original' => RomDeliveryFormat.original,
    'rvz' => RomDeliveryFormat.rvz,
    'chd' => RomDeliveryFormat.chd,
    _ => null,
  };
}

enum RomDeliveryState {
  available,
  preparing,
  unsupported;

  static RomDeliveryState? tryParse(String value) => switch (value) {
    'available' => RomDeliveryState.available,
    'preparing' => RomDeliveryState.preparing,
    'unsupported' => RomDeliveryState.unsupported,
    _ => null,
  };
}
