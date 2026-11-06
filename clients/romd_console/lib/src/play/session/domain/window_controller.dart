/// Controls the ROMD window around external emulator launches.
abstract interface class WindowController {
  /// Bring ROMD back to the foreground after an external emulator exits.
  ///
  /// Deliberately does NOT change windowed/fullscreen state: launching an
  /// emulator only takes foreground focus, it doesn't alter ROMD's own window,
  /// so re-focusing restores the exact prior setup (windowed stays windowed,
  /// fullscreen stays fullscreen).
  Future<void> reclaimForeground();
}
