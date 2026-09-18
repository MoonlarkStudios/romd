import 'dart:ffi';

import 'package:ffi/ffi.dart';

import 'package:romd_console/src/play/controllers/data/sdl/sdl_native_dependency.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';

const int _sdlInitGamepad = 0x00002000;
const int _sdlInitJoystick = 0x00000200;
const int _sdlGuidStringLength = 33;
const int _sdlEventGamepadButtonDown = 0x651;
const int _sdlEventGamepadButtonUp = 0x652;
const int _sdlEventGamepadAdded = 0x653;
const int _sdlEventGamepadRemoved = 0x654;
const int _sdlEventJoystickAxisMotion = 0x600;
const int _sdlEventJoystickHatMotion = 0x602;
const int _sdlEventJoystickButtonDown = 0x603;
const int _sdlEventJoystickButtonUp = 0x604;
const int _sdlEventJoystickAdded = 0x605;
const int _sdlEventJoystickRemoved = 0x606;

final class SdlGamepadEventTypes {
  const SdlGamepadEventTypes._();

  static const int gamepadButtonDown = _sdlEventGamepadButtonDown;
  static const int gamepadButtonUp = _sdlEventGamepadButtonUp;
  static const int gamepadAdded = _sdlEventGamepadAdded;
  static const int gamepadRemoved = _sdlEventGamepadRemoved;
  static const int joystickAxisMotion = _sdlEventJoystickAxisMotion;
  static const int joystickHatMotion = _sdlEventJoystickHatMotion;
  static const int joystickButtonDown = _sdlEventJoystickButtonDown;
  static const int joystickButtonUp = _sdlEventJoystickButtonUp;
  static const int joystickAdded = _sdlEventJoystickAdded;
  static const int joystickRemoved = _sdlEventJoystickRemoved;
}

final class SdlGamepadException implements Exception {
  const SdlGamepadException(this.message);

  final String message;
}

class SdlGamepadHandle {
  const SdlGamepadHandle();
}

final class NativeSdlGamepadHandle extends SdlGamepadHandle {
  const NativeSdlGamepadHandle(this.pointer);

  final Pointer<Void> pointer;
}

abstract interface class SdlGamepadApi {
  bool initGamepad();
  void quitGamepad();
  void setGamepadEventsEnabled(bool enabled);
  List<int> gamepadInstanceIds();
  String? gamepadNameForId(int instanceId);
  String? gamepadGuidForId(int instanceId);
  SdlGamepadHandle? openGamepad(int instanceId);
  String? gamepadSerial(SdlGamepadHandle handle);
  void closeGamepad(SdlGamepadHandle handle);
  SdlPolledEvent? pollEvent();
}

class SdlJoystickHandle {
  const SdlJoystickHandle();
}

final class NativeSdlJoystickHandle extends SdlJoystickHandle {
  const NativeSdlJoystickHandle(this.pointer);

  final Pointer<Void> pointer;
}

/// Optional raw joystick surface. Tests and older adapters that only model
/// normalized gamepads can continue implementing [SdlGamepadApi].
abstract interface class SdlRawInputApi implements SdlGamepadApi {
  String platform();
  void setJoystickEventsEnabled(bool enabled);
  List<int> joystickInstanceIds();
  String? joystickNameForId(int instanceId);
  String? joystickGuidForId(int instanceId);
  bool isGamepad(int instanceId);
  String? gamepadMappingForId(int instanceId);
  SdlJoystickHandle? openJoystick(int instanceId);
  String? joystickSerial(SdlJoystickHandle handle);
  int joystickAxisCount(SdlJoystickHandle handle);
  int joystickButtonCount(SdlJoystickHandle handle);
  int joystickHatCount(SdlJoystickHandle handle);
  void closeJoystick(SdlJoystickHandle handle);
}

final class SdlGamepadSubsystem {
  SdlGamepadSubsystem(this._api);

  final SdlGamepadApi _api;
  var _leases = 0;

  bool acquire() {
    if (_leases > 0) {
      _leases++;
      return true;
    }
    if (!_api.initGamepad()) {
      return false;
    }
    _leases = 1;
    return true;
  }

  void release() {
    if (_leases == 0) {
      return;
    }
    _leases--;
    if (_leases == 0) {
      _api.quitGamepad();
    }
  }
}

final class SdlGuid extends Struct {
  @Array(16)
  external Array<Uint8> data;
}

final class SdlGamepadButtonEventStruct extends Struct {
  @Uint32()
  external int type;

  @Uint32()
  external int reserved;

  @Uint64()
  external int timestamp;

  @Uint32()
  external int which;

  @Uint8()
  external int button;

  @Bool()
  external bool down;

  @Uint8()
  external int padding1;

  @Uint8()
  external int padding2;
}

final class SdlGamepadDeviceEventStruct extends Struct {
  @Uint32()
  external int type;

  @Uint32()
  external int reserved;

  @Uint64()
  external int timestamp;

  @Uint32()
  external int which;
}

final class SdlJoystickAxisEventStruct extends Struct {
  @Uint32()
  external int type;
  @Uint32()
  external int reserved;
  @Uint64()
  external int timestamp;
  @Uint32()
  external int which;
  @Uint8()
  external int axis;
  @Uint8()
  external int padding1;
  @Uint8()
  external int padding2;
  @Uint8()
  external int padding3;
  @Int16()
  external int value;
  @Uint16()
  external int padding4;
}

final class SdlJoystickButtonEventStruct extends Struct {
  @Uint32()
  external int type;
  @Uint32()
  external int reserved;
  @Uint64()
  external int timestamp;
  @Uint32()
  external int which;
  @Uint8()
  external int button;
  @Bool()
  external bool down;
  @Uint8()
  external int padding1;
  @Uint8()
  external int padding2;
}

final class SdlJoystickHatEventStruct extends Struct {
  @Uint32()
  external int type;
  @Uint32()
  external int reserved;
  @Uint64()
  external int timestamp;
  @Uint32()
  external int which;
  @Uint8()
  external int hat;
  @Uint8()
  external int value;
  @Uint8()
  external int padding1;
  @Uint8()
  external int padding2;
}

final class SdlEvent extends Union {
  @Uint32()
  external int type;

  external SdlGamepadButtonEventStruct gbutton;

  external SdlGamepadDeviceEventStruct gdevice;

  external SdlJoystickAxisEventStruct jaxis;

  external SdlJoystickButtonEventStruct jbutton;

  external SdlJoystickHatEventStruct jhat;

  external SdlGamepadDeviceEventStruct jdevice;

  @Array(128)
  external Array<Uint8> padding;
}

sealed class SdlPolledEvent {
  const SdlPolledEvent();
}

final class SdlIgnoredEvent extends SdlPolledEvent {
  const SdlIgnoredEvent();
}

final class SdlGamepadButtonInputEvent extends SdlPolledEvent {
  const SdlGamepadButtonInputEvent({
    required this.instanceId,
    required this.button,
    required this.pressed,
    required this.timestampMillis,
  });

  final int instanceId;
  final int button;
  final bool pressed;
  final int timestampMillis;

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is SdlGamepadButtonInputEvent &&
          other.instanceId == instanceId &&
          other.button == button &&
          other.pressed == pressed &&
          other.timestampMillis == timestampMillis;

  @override
  int get hashCode => Object.hash(instanceId, button, pressed, timestampMillis);
}

enum SdlGamepadDeviceChange { added, removed }

final class SdlGamepadDeviceInputEvent extends SdlPolledEvent {
  const SdlGamepadDeviceInputEvent({
    required this.instanceId,
    required this.change,
    required this.timestampMillis,
  });

  final int instanceId;
  final SdlGamepadDeviceChange change;
  final int timestampMillis;

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is SdlGamepadDeviceInputEvent &&
          other.instanceId == instanceId &&
          other.change == change &&
          other.timestampMillis == timestampMillis;

  @override
  int get hashCode => Object.hash(instanceId, change, timestampMillis);
}

final class SdlJoystickAxisInputEvent extends SdlPolledEvent {
  const SdlJoystickAxisInputEvent({
    required this.instanceId,
    required this.axis,
    required this.value,
    required this.timestampMillis,
  });

  final int instanceId;
  final int axis;
  final int value;
  final int timestampMillis;
}

final class SdlJoystickButtonInputEvent extends SdlPolledEvent {
  const SdlJoystickButtonInputEvent({
    required this.instanceId,
    required this.button,
    required this.pressed,
    required this.timestampMillis,
  });

  final int instanceId;
  final int button;
  final bool pressed;
  final int timestampMillis;
}

final class SdlJoystickHatInputEvent extends SdlPolledEvent {
  const SdlJoystickHatInputEvent({
    required this.instanceId,
    required this.hat,
    required this.value,
    required this.timestampMillis,
  });

  final int instanceId;
  final int hat;
  final int value;
  final int timestampMillis;
}

enum SdlJoystickDeviceChange { added, removed }

final class SdlJoystickDeviceInputEvent extends SdlPolledEvent {
  const SdlJoystickDeviceInputEvent({
    required this.instanceId,
    required this.change,
    required this.timestampMillis,
  });

  final int instanceId;
  final SdlJoystickDeviceChange change;
  final int timestampMillis;
}

final class SdlGamepadButtonIds {
  const SdlGamepadButtonIds._();

  static const int south = 0;
  static const int east = 1;
  static const int west = 2;
  static const int north = 3;
  static const int back = 4;
  static const int guide = 5;
  static const int start = 6;
  static const int leftStick = 7;
  static const int rightStick = 8;
  static const int leftShoulder = 9;
  static const int rightShoulder = 10;
  static const int dpadUp = 11;
  static const int dpadDown = 12;
  static const int dpadLeft = 13;
  static const int dpadRight = 14;
  static const int touchpad = 20;
}

final class SdlGamepadEventParser {
  const SdlGamepadEventParser._();

  static SdlPolledEvent parse(SdlEvent event) {
    switch (event.type) {
      case _sdlEventGamepadButtonDown:
      case _sdlEventGamepadButtonUp:
        final button = event.gbutton;
        return SdlGamepadButtonInputEvent(
          instanceId: button.which,
          button: button.button,
          pressed: button.down,
          timestampMillis: _timestampMillis(button.timestamp),
        );
      case _sdlEventGamepadAdded:
      case _sdlEventGamepadRemoved:
        final device = event.gdevice;
        return SdlGamepadDeviceInputEvent(
          instanceId: device.which,
          change: event.type == _sdlEventGamepadAdded
              ? SdlGamepadDeviceChange.added
              : SdlGamepadDeviceChange.removed,
          timestampMillis: _timestampMillis(device.timestamp),
        );
      case _sdlEventJoystickAxisMotion:
        final axis = event.jaxis;
        return SdlJoystickAxisInputEvent(
          instanceId: axis.which,
          axis: axis.axis,
          value: axis.value,
          timestampMillis: _timestampMillis(axis.timestamp),
        );
      case _sdlEventJoystickButtonDown:
      case _sdlEventJoystickButtonUp:
        final button = event.jbutton;
        return SdlJoystickButtonInputEvent(
          instanceId: button.which,
          button: button.button,
          pressed: button.down,
          timestampMillis: _timestampMillis(button.timestamp),
        );
      case _sdlEventJoystickHatMotion:
        final hat = event.jhat;
        return SdlJoystickHatInputEvent(
          instanceId: hat.which,
          hat: hat.hat,
          value: hat.value,
          timestampMillis: _timestampMillis(hat.timestamp),
        );
      case _sdlEventJoystickAdded:
      case _sdlEventJoystickRemoved:
        final device = event.jdevice;
        return SdlJoystickDeviceInputEvent(
          instanceId: device.which,
          change: event.type == _sdlEventJoystickAdded
              ? SdlJoystickDeviceChange.added
              : SdlJoystickDeviceChange.removed,
          timestampMillis: _timestampMillis(device.timestamp),
        );
      default:
        return const SdlIgnoredEvent();
    }
  }

  static const int _nanosecondsPerMillisecond = 1000000;

  static int _timestampMillis(int timestampNanos) =>
      timestampNanos ~/ _nanosecondsPerMillisecond;
}

final class FfiSdlGamepadApi implements SdlRawInputApi {
  FfiSdlGamepadApi._(DynamicLibrary library)
    : _initSubSystem = library
          .lookupFunction<Bool Function(Uint32), bool Function(int)>(
            'SDL_InitSubSystem',
          ),
      _quitSubSystem = library
          .lookupFunction<Void Function(Uint32), void Function(int)>(
            'SDL_QuitSubSystem',
          ),
      _setGamepadEventsEnabled = library
          .lookupFunction<Void Function(Bool), void Function(bool)>(
            'SDL_SetGamepadEventsEnabled',
          ),
      _setJoystickEventsEnabled = library
          .lookupFunction<Void Function(Bool), void Function(bool)>(
            'SDL_SetJoystickEventsEnabled',
          ),
      _getPlatform = library
          .lookupFunction<Pointer<Utf8> Function(), Pointer<Utf8> Function()>(
            'SDL_GetPlatform',
          ),
      _getGamepads = library
          .lookupFunction<
            Pointer<Uint32> Function(Pointer<Int32>),
            Pointer<Uint32> Function(Pointer<Int32>)
          >('SDL_GetGamepads'),
      _getJoysticks = library
          .lookupFunction<
            Pointer<Uint32> Function(Pointer<Int32>),
            Pointer<Uint32> Function(Pointer<Int32>)
          >('SDL_GetJoysticks'),
      _getGamepadNameForId = library
          .lookupFunction<
            Pointer<Utf8> Function(Uint32),
            Pointer<Utf8> Function(int)
          >('SDL_GetGamepadNameForID'),
      _getGamepadGuidForId = library
          .lookupFunction<SdlGuid Function(Uint32), SdlGuid Function(int)>(
            'SDL_GetGamepadGUIDForID',
          ),
      _getJoystickNameForId = library
          .lookupFunction<
            Pointer<Utf8> Function(Uint32),
            Pointer<Utf8> Function(int)
          >('SDL_GetJoystickNameForID'),
      _getJoystickGuidForId = library
          .lookupFunction<SdlGuid Function(Uint32), SdlGuid Function(int)>(
            'SDL_GetJoystickGUIDForID',
          ),
      _isGamepad = library
          .lookupFunction<Bool Function(Uint32), bool Function(int)>(
            'SDL_IsGamepad',
          ),
      _getGamepadMappingForId = library
          .lookupFunction<
            Pointer<Utf8> Function(Uint32),
            Pointer<Utf8> Function(int)
          >('SDL_GetGamepadMappingForID'),
      _guidToString = library
          .lookupFunction<
            Void Function(SdlGuid, Pointer<Utf8>, Int32),
            void Function(SdlGuid, Pointer<Utf8>, int)
          >('SDL_GUIDToString'),
      _openGamepad = library
          .lookupFunction<
            Pointer<Void> Function(Uint32),
            Pointer<Void> Function(int)
          >('SDL_OpenGamepad'),
      _openJoystick = library
          .lookupFunction<
            Pointer<Void> Function(Uint32),
            Pointer<Void> Function(int)
          >('SDL_OpenJoystick'),
      _getGamepadSerial = library
          .lookupFunction<
            Pointer<Utf8> Function(Pointer<Void>),
            Pointer<Utf8> Function(Pointer<Void>)
          >('SDL_GetGamepadSerial'),
      _getJoystickSerial = library
          .lookupFunction<
            Pointer<Utf8> Function(Pointer<Void>),
            Pointer<Utf8> Function(Pointer<Void>)
          >('SDL_GetJoystickSerial'),
      _getNumJoystickAxes = library
          .lookupFunction<
            Int32 Function(Pointer<Void>),
            int Function(Pointer<Void>)
          >('SDL_GetNumJoystickAxes'),
      _getNumJoystickButtons = library
          .lookupFunction<
            Int32 Function(Pointer<Void>),
            int Function(Pointer<Void>)
          >('SDL_GetNumJoystickButtons'),
      _getNumJoystickHats = library
          .lookupFunction<
            Int32 Function(Pointer<Void>),
            int Function(Pointer<Void>)
          >('SDL_GetNumJoystickHats'),
      _closeGamepad = library
          .lookupFunction<
            Void Function(Pointer<Void>),
            void Function(Pointer<Void>)
          >('SDL_CloseGamepad'),
      _closeJoystick = library
          .lookupFunction<
            Void Function(Pointer<Void>),
            void Function(Pointer<Void>)
          >('SDL_CloseJoystick'),
      _pollEvent = library
          .lookupFunction<
            Bool Function(Pointer<SdlEvent>),
            bool Function(Pointer<SdlEvent>)
          >('SDL_PollEvent'),
      _free = library
          .lookupFunction<
            Void Function(Pointer<Void>),
            void Function(Pointer<Void>)
          >('SDL_free');

  factory FfiSdlGamepadApi.open(String libraryPath) =>
      FfiSdlGamepadApi._(DynamicLibrary.open(libraryPath));

  final bool Function(int flags) _initSubSystem;
  final void Function(int flags) _quitSubSystem;
  final void Function(bool enabled) _setGamepadEventsEnabled;
  final void Function(bool enabled) _setJoystickEventsEnabled;
  final Pointer<Utf8> Function() _getPlatform;
  final Pointer<Uint32> Function(Pointer<Int32> count) _getGamepads;
  final Pointer<Uint32> Function(Pointer<Int32> count) _getJoysticks;
  final Pointer<Utf8> Function(int instanceId) _getGamepadNameForId;
  final SdlGuid Function(int instanceId) _getGamepadGuidForId;
  final Pointer<Utf8> Function(int instanceId) _getJoystickNameForId;
  final SdlGuid Function(int instanceId) _getJoystickGuidForId;
  final bool Function(int instanceId) _isGamepad;
  final Pointer<Utf8> Function(int instanceId) _getGamepadMappingForId;
  final void Function(SdlGuid guid, Pointer<Utf8> buffer, int bufferLength)
  _guidToString;
  final Pointer<Void> Function(int instanceId) _openGamepad;
  final Pointer<Void> Function(int instanceId) _openJoystick;
  final Pointer<Utf8> Function(Pointer<Void> gamepad) _getGamepadSerial;
  final Pointer<Utf8> Function(Pointer<Void> joystick) _getJoystickSerial;
  final int Function(Pointer<Void> joystick) _getNumJoystickAxes;
  final int Function(Pointer<Void> joystick) _getNumJoystickButtons;
  final int Function(Pointer<Void> joystick) _getNumJoystickHats;
  final void Function(Pointer<Void> gamepad) _closeGamepad;
  final void Function(Pointer<Void> joystick) _closeJoystick;
  final bool Function(Pointer<SdlEvent> event) _pollEvent;
  final void Function(Pointer<Void> memory) _free;

  @override
  bool initGamepad() => _initSubSystem(_sdlInitGamepad | _sdlInitJoystick);

  @override
  void quitGamepad() => _quitSubSystem(_sdlInitGamepad | _sdlInitJoystick);

  @override
  void setGamepadEventsEnabled(bool enabled) =>
      _setGamepadEventsEnabled(enabled);

  @override
  void setJoystickEventsEnabled(bool enabled) =>
      _setJoystickEventsEnabled(enabled);

  @override
  String platform() => _optionalUtf8(_getPlatform()) ?? 'unknown';

  @override
  List<int> gamepadInstanceIds() {
    return _readInstanceIds(_getGamepads);
  }

  @override
  List<int> joystickInstanceIds() => _readInstanceIds(_getJoysticks);

  List<int> _readInstanceIds(Pointer<Uint32> Function(Pointer<Int32>) getter) {
    final count = calloc<Int32>();
    try {
      final ids = getter(count);
      if (ids == nullptr || count.value <= 0) {
        return const <int>[];
      }
      try {
        return <int>[for (var i = 0; i < count.value; i++) ids[i]];
      } finally {
        _free(ids.cast<Void>());
      }
    } finally {
      calloc.free(count);
    }
  }

  @override
  String? gamepadNameForId(int instanceId) =>
      _optionalUtf8(_getGamepadNameForId(instanceId));

  @override
  String? joystickNameForId(int instanceId) =>
      _optionalUtf8(_getJoystickNameForId(instanceId));

  @override
  String? gamepadGuidForId(int instanceId) {
    return _guidString(_getGamepadGuidForId(instanceId));
  }

  @override
  String? joystickGuidForId(int instanceId) =>
      _guidString(_getJoystickGuidForId(instanceId));

  String? _guidString(SdlGuid guid) {
    final buffer = calloc<Uint8>(_sdlGuidStringLength);
    try {
      _guidToString(guid, buffer.cast<Utf8>(), _sdlGuidStringLength);
      final value = _normalizeOptionalString(
        buffer.cast<Utf8>().toDartString(),
      );
      return value == null || _isZeroGuid(value) ? null : value;
    } finally {
      calloc.free(buffer);
    }
  }

  @override
  bool isGamepad(int instanceId) => _isGamepad(instanceId);

  @override
  String? gamepadMappingForId(int instanceId) {
    final mapping = _getGamepadMappingForId(instanceId);
    if (mapping == nullptr) {
      return null;
    }
    try {
      return _normalizeOptionalString(mapping.toDartString());
    } finally {
      _free(mapping.cast<Void>());
    }
  }

  @override
  SdlGamepadHandle? openGamepad(int instanceId) {
    final gamepad = _openGamepad(instanceId);
    return gamepad == nullptr ? null : NativeSdlGamepadHandle(gamepad);
  }

  @override
  SdlJoystickHandle? openJoystick(int instanceId) {
    final joystick = _openJoystick(instanceId);
    return joystick == nullptr ? null : NativeSdlJoystickHandle(joystick);
  }

  @override
  String? gamepadSerial(SdlGamepadHandle handle) {
    final nativeHandle = handle as NativeSdlGamepadHandle;
    return _optionalUtf8(_getGamepadSerial(nativeHandle.pointer));
  }

  @override
  String? joystickSerial(SdlJoystickHandle handle) => _optionalUtf8(
    _getJoystickSerial((handle as NativeSdlJoystickHandle).pointer),
  );

  @override
  int joystickAxisCount(SdlJoystickHandle handle) =>
      _getNumJoystickAxes((handle as NativeSdlJoystickHandle).pointer);

  @override
  int joystickButtonCount(SdlJoystickHandle handle) =>
      _getNumJoystickButtons((handle as NativeSdlJoystickHandle).pointer);

  @override
  int joystickHatCount(SdlJoystickHandle handle) =>
      _getNumJoystickHats((handle as NativeSdlJoystickHandle).pointer);

  @override
  void closeGamepad(SdlGamepadHandle handle) {
    final nativeHandle = handle as NativeSdlGamepadHandle;
    _closeGamepad(nativeHandle.pointer);
  }

  @override
  void closeJoystick(SdlJoystickHandle handle) =>
      _closeJoystick((handle as NativeSdlJoystickHandle).pointer);

  @override
  SdlPolledEvent? pollEvent() {
    final event = calloc<SdlEvent>();
    try {
      if (!_pollEvent(event)) {
        return null;
      }
      return SdlGamepadEventParser.parse(event.ref);
    } finally {
      calloc.free(event);
    }
  }

  String? _optionalUtf8(Pointer<Utf8> pointer) => pointer == nullptr
      ? null
      : _normalizeOptionalString(pointer.toDartString());

  String? _normalizeOptionalString(String value) {
    final trimmed = value.trim();
    return trimmed.isEmpty ? null : trimmed;
  }

  bool _isZeroGuid(String value) => RegExp(r'^0+$').hasMatch(value);
}

final class SdlGamepadEnumerator {
  SdlGamepadEnumerator(this._api, {SdlGamepadSubsystem? subsystem})
    : _subsystem = subsystem ?? SdlGamepadSubsystem(_api);

  final SdlGamepadApi _api;
  final SdlGamepadSubsystem _subsystem;

  Future<List<ConnectedGamepad>> listGamepads() async {
    if (!_subsystem.acquire()) {
      throw const SdlGamepadException('Could not initialize SDL gamepads.');
    }
    try {
      final rawApi = _api is SdlRawInputApi ? _api : null;
      final ids = rawApi?.joystickInstanceIds() ?? _api.gamepadInstanceIds();
      return <ConnectedGamepad>[
        for (final (order, instanceId) in ids.indexed)
          ConnectedGamepad(
            id: instanceId.toString(),
            order: order,
            identity: ControllerIdentity(
              displayName:
                  rawApi?.joystickNameForId(instanceId) ??
                  _api.gamepadNameForId(instanceId) ??
                  'Controller',
              sdlGuid:
                  rawApi?.joystickGuidForId(instanceId) ??
                  _api.gamepadGuidForId(instanceId),
              serial: rawApi == null
                  ? _readSerial(instanceId)
                  : _readJoystickSerial(rawApi, instanceId),
            ),
          ),
      ];
    } finally {
      _subsystem.release();
    }
  }

  String? _readSerial(int instanceId) {
    final handle = _api.openGamepad(instanceId);
    if (handle == null) {
      return null;
    }
    try {
      final serial = _api.gamepadSerial(handle)?.trim();
      return serial == null || serial.isEmpty ? null : serial;
    } finally {
      _api.closeGamepad(handle);
    }
  }

  String? _readJoystickSerial(SdlRawInputApi api, int instanceId) {
    final handle = api.openJoystick(instanceId);
    if (handle == null) {
      return null;
    }
    try {
      final serial = api.joystickSerial(handle)?.trim();
      return serial == null || serial.isEmpty ? null : serial;
    } finally {
      api.closeJoystick(handle);
    }
  }
}

final class SdlRawControllerEnumerator {
  SdlRawControllerEnumerator(this._api, {SdlGamepadSubsystem? subsystem})
    : _subsystem = subsystem ?? SdlGamepadSubsystem(_api);

  final SdlRawInputApi _api;
  final SdlGamepadSubsystem _subsystem;

  Future<List<RawControllerDescriptor>> listControllers() async {
    if (!_subsystem.acquire()) {
      throw const SdlGamepadException('Could not initialize SDL joysticks.');
    }
    try {
      return <RawControllerDescriptor>[
        for (final instanceId in _api.joystickInstanceIds())
          _describe(instanceId),
      ];
    } finally {
      _subsystem.release();
    }
  }

  RawControllerDescriptor _describe(int instanceId) {
    final handle = _api.openJoystick(instanceId);
    try {
      return RawControllerDescriptor(
        controllerId: instanceId.toString(),
        displayName: _api.joystickNameForId(instanceId) ?? 'Controller',
        sdlPlatform: _api.platform().trim(),
        sdlGuid: _api.joystickGuidForId(instanceId),
        isMappedGamepad: _api.isGamepad(instanceId),
        capabilities: RawControllerCapabilities(
          axisCount: handle == null ? 0 : _api.joystickAxisCount(handle),
          buttonCount: handle == null ? 0 : _api.joystickButtonCount(handle),
          hatCount: handle == null ? 0 : _api.joystickHatCount(handle),
        ),
        sdlMapping: _api.gamepadMappingForId(instanceId),
      );
    } finally {
      if (handle != null) {
        _api.closeJoystick(handle);
      }
    }
  }
}

final class SdlBackedGamepadLister {
  const SdlBackedGamepadLister({
    required this.apiFactory,
    required this.fallback,
  });

  final Future<SdlGamepadApi> Function() apiFactory;
  final GamepadLister fallback;

  Future<List<ConnectedGamepad>> call() async {
    try {
      return await SdlGamepadEnumerator(await apiFactory()).listGamepads();
    } on Object {
      return await fallback();
    }
  }
}

typedef SdlGamepadApiFactory = SdlGamepadApi Function(String libraryPath);
typedef Sdl3Provision = Future<SdlNativeProvisionResult> Function();

final class ProvisionedSdlGamepadLister {
  ProvisionedSdlGamepadLister({
    required Sdl3Provision ensureSdl3,
    required SdlGamepadApiFactory apiFactory,
    required GamepadLister fallback,
  }) : _ensureSdl3 = ensureSdl3,
       _apiFactory = apiFactory,
       _fallback = fallback;

  final Sdl3Provision _ensureSdl3;
  final SdlGamepadApiFactory _apiFactory;
  final GamepadLister _fallback;
  Future<SdlNativeProvisionResult>? _sdl3Provision;

  /// Returns gamepads with provider ids from [_fallback], enriched with SDL
  /// identity when available. The id source matters because controller claim
  /// events are still emitted by the `gamepads` package.
  Future<List<ConnectedGamepad>> call() async {
    final providerDevices = await _tryFallback();
    try {
      final sdlDevices = await SdlGamepadEnumerator(
        await _openProvisionedApi(),
      ).listGamepads();
      if (providerDevices == null || providerDevices.isEmpty) {
        return sdlDevices;
      }
      return _withProviderIds(
        providerDevices: providerDevices,
        identityDevices: sdlDevices,
      );
    } on Object {
      return providerDevices ?? const <ConnectedGamepad>[];
    }
  }

  Future<SdlGamepadApi> _openProvisionedApi() async {
    final result = await (_sdl3Provision ??= _ensureSdl3());
    if (result case SdlNativeReady(:final libraryPath)) {
      return _apiFactory(libraryPath);
    }
    throw const SdlGamepadException('SDL gamepad identity is unavailable.');
  }

  Future<List<ConnectedGamepad>?> _tryFallback() async {
    try {
      return await _fallback();
    } on Object {
      return null;
    }
  }

  List<ConnectedGamepad> _withProviderIds({
    required List<ConnectedGamepad> providerDevices,
    required List<ConnectedGamepad> identityDevices,
  }) => <ConnectedGamepad>[
    for (final (index, provider) in providerDevices.indexed)
      if (index < identityDevices.length)
        ConnectedGamepad(
          id: provider.id,
          order: provider.order,
          identity: ControllerIdentity(
            displayName: provider.name,
            sdlGuid: identityDevices[index].identity.sdlGuid,
            serial: identityDevices[index].identity.serial,
          ),
        )
      else
        provider,
  ];
}
