import 'dart:async';

/// Physical controls reported by a controller before any SDL gamepad mapping
/// is applied.
sealed class RawControllerEvent {
  const RawControllerEvent({
    required this.controllerId,
    required this.timestampMillis,
  });

  final String controllerId;
  final int timestampMillis;
}

final class RawControllerButtonEvent extends RawControllerEvent {
  const RawControllerButtonEvent({
    required super.controllerId,
    required super.timestampMillis,
    required this.button,
    required this.pressed,
  });

  final int button;
  final bool pressed;
}

final class RawControllerAxisEvent extends RawControllerEvent {
  const RawControllerAxisEvent({
    required super.controllerId,
    required super.timestampMillis,
    required this.axis,
    required this.value,
  });

  final int axis;
  final int value;
}

final class RawControllerHatEvent extends RawControllerEvent {
  const RawControllerHatEvent({
    required super.controllerId,
    required super.timestampMillis,
    required this.hat,
    required this.value,
  });

  final int hat;
  final int value;
}

enum RawControllerDeviceChange { added, removed }

final class RawControllerDeviceEvent extends RawControllerEvent {
  const RawControllerDeviceEvent({
    required super.controllerId,
    required super.timestampMillis,
    required this.change,
  });

  final RawControllerDeviceChange change;
}

final class RawControllerCapabilities {
  const RawControllerCapabilities({
    required this.axisCount,
    required this.buttonCount,
    required this.hatCount,
  });

  final int axisCount;
  final int buttonCount;
  final int hatCount;
}

/// SDL's raw joystick view and its optional normalized gamepad mapping share
/// one provider id with navigation events.
final class RawControllerDescriptor {
  const RawControllerDescriptor({
    required this.controllerId,
    required this.displayName,
    required this.sdlPlatform,
    required this.sdlGuid,
    required this.isMappedGamepad,
    required this.capabilities,
    this.sdlMapping,
  });

  final String controllerId;
  final String displayName;
  final String sdlPlatform;
  final String? sdlGuid;
  final bool isMappedGamepad;
  final RawControllerCapabilities capabilities;
  final String? sdlMapping;
}

sealed class ControllerCaptureResult {
  const ControllerCaptureResult();
}

final class ControllerCaptureStarted extends ControllerCaptureResult {
  const ControllerCaptureStarted(this.lease);

  final ControllerInputCaptureLease lease;
}

final class ControllerCaptureUnsupported extends ControllerCaptureResult {
  const ControllerCaptureUnsupported();
}

final class ControllerCaptureBusy extends ControllerCaptureResult {
  const ControllerCaptureBusy();
}

abstract interface class ControllerInputCaptureLease {
  String get controllerId;
  Stream<RawControllerEvent> get events;
  void dispose();
}

/// Optional capability implemented by providers that can attribute physical
/// controls to one exact controller.
abstract interface class RawControllerInputProvider {
  Future<List<RawControllerDescriptor>> listRawControllers();
  Stream<RawControllerEvent> rawEvents();
  Future<ControllerCaptureResult> acquireCapture(String controllerId);
}

/// Navigator-facing capture policy. A capture owner suppresses all normalized
/// navigation until its disposable lease is released.
abstract interface class ControllerInputCaptureGate {
  bool get isNavigationSuppressed;
  void addCaptureStateListener(void Function() listener);
  void removeCaptureStateListener(void Function() listener);
}

Future<ControllerCaptureResult> acquireControllerCapture(
  Object provider,
  String controllerId,
) async {
  if (provider case final RawControllerInputProvider rawProvider) {
    return await rawProvider.acquireCapture(controllerId);
  }
  return const ControllerCaptureUnsupported();
}
