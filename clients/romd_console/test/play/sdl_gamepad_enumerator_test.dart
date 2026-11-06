import 'dart:async';
import 'dart:ffi';

import 'package:ffi/ffi.dart' as ffi;
import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_controller_input_provider.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_gamepad_enumerator.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_native_dependency.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';

final class _FakeHandle extends SdlGamepadHandle {
  const _FakeHandle(this.instanceId);

  final int instanceId;
}

class _FakeSdlGamepadApi implements SdlGamepadApi {
  _FakeSdlGamepadApi({
    this.initResult = true,
    this.serials = const <int, String?>{7: 'serial-a', 9: 'serial-b'},
    this.openReturnsNull = false,
    this.serialThrows = false,
    this.polledEvents = const <SdlPolledEvent>[],
    this.requireActiveForPoll = false,
    this.requireOpenForButtonPoll = false,
    this.pollErrorAfterCount,
  });

  final bool initResult;
  final Map<int, String?> serials;
  final bool openReturnsNull;
  final bool serialThrows;
  final List<SdlPolledEvent> polledEvents;
  final bool requireActiveForPoll;
  final bool requireOpenForButtonPoll;
  final int? pollErrorAfterCount;

  var initCount = 0;
  var quitCount = 0;
  var pollCount = 0;
  var pollErrorArmed = false;
  var active = false;
  var eventsEnabled = false;
  final opened = <int>[];
  final closed = <int>[];
  final activeHandleCounts = <int, int>{};

  @override
  bool initGamepad() {
    initCount++;
    active = initResult;
    return initResult;
  }

  @override
  void quitGamepad() {
    quitCount++;
    active = false;
  }

  @override
  void setGamepadEventsEnabled(bool enabled) {
    eventsEnabled = enabled;
  }

  @override
  List<int> gamepadInstanceIds() => const <int>[7, 9];

  @override
  String? gamepadNameForId(int instanceId) => switch (instanceId) {
    7 => 'DualSense',
    9 => 'DualSense',
    _ => null,
  };

  @override
  String? gamepadGuidForId(int instanceId) => switch (instanceId) {
    7 || 9 => '030000004c050000e60c000000006800',
    _ => null,
  };

  @override
  SdlGamepadHandle? openGamepad(int instanceId) {
    if (openReturnsNull) {
      return null;
    }
    opened.add(instanceId);
    activeHandleCounts.update(
      instanceId,
      (count) => count + 1,
      ifAbsent: () => 1,
    );
    return _FakeHandle(instanceId);
  }

  @override
  String? gamepadSerial(SdlGamepadHandle handle) {
    if (serialThrows) {
      throw StateError('serial read failed');
    }
    return serials[(handle as _FakeHandle).instanceId];
  }

  @override
  void closeGamepad(SdlGamepadHandle handle) {
    final instanceId = (handle as _FakeHandle).instanceId;
    closed.add(instanceId);
    final count = activeHandleCounts[instanceId];
    if (count == null || count <= 1) {
      activeHandleCounts.remove(instanceId);
    } else {
      activeHandleCounts[instanceId] = count - 1;
    }
  }

  @override
  SdlPolledEvent? pollEvent() {
    if (requireActiveForPoll && !active) {
      return null;
    }
    if (pollErrorAfterCount case final errorAfter?
        when pollCount >= errorAfter) {
      if (!pollErrorArmed) {
        pollErrorArmed = true;
        return null;
      }
      throw StateError('SDL polling failed');
    }
    if (pollCount >= polledEvents.length) {
      return null;
    }
    final event = polledEvents[pollCount];
    if (requireOpenForButtonPoll &&
        event is SdlGamepadButtonInputEvent &&
        !activeHandleCounts.containsKey(event.instanceId)) {
      return null;
    }
    pollCount++;
    return event;
  }
}

final class _FakeJoystickHandle extends SdlJoystickHandle {
  const _FakeJoystickHandle(this.instanceId);

  final int instanceId;
}

final class _FakeRawSdlApi extends _FakeSdlGamepadApi
    implements SdlRawInputApi {
  _FakeRawSdlApi({super.polledEvents});

  var joystickEventsEnabled = false;
  final openedJoysticks = <int>[];
  final closedJoysticks = <int>[];

  @override
  String platform() => 'macOS';

  @override
  void setJoystickEventsEnabled(bool enabled) {
    joystickEventsEnabled = enabled;
  }

  @override
  List<int> joystickInstanceIds() => const <int>[7, 11];

  @override
  String? joystickNameForId(int instanceId) => switch (instanceId) {
    7 => 'Mapped pad',
    11 => 'Unknown joystick',
    _ => null,
  };

  @override
  String? joystickGuidForId(int instanceId) => switch (instanceId) {
    7 => 'mapped-guid',
    11 => 'unknown-guid',
    _ => null,
  };

  @override
  bool isGamepad(int instanceId) => instanceId == 7;

  @override
  String? gamepadMappingForId(int instanceId) =>
      instanceId == 7 ? 'mapped-guid,Mapped pad,a:b0,platform:macOS,' : null;

  @override
  SdlJoystickHandle? openJoystick(int instanceId) {
    openedJoysticks.add(instanceId);
    return _FakeJoystickHandle(instanceId);
  }

  @override
  String? joystickSerial(SdlJoystickHandle handle) => null;

  @override
  int joystickAxisCount(SdlJoystickHandle handle) =>
      (handle as _FakeJoystickHandle).instanceId == 7 ? 6 : 2;

  @override
  int joystickButtonCount(SdlJoystickHandle handle) =>
      (handle as _FakeJoystickHandle).instanceId == 7 ? 15 : 4;

  @override
  int joystickHatCount(SdlJoystickHandle handle) => 1;

  @override
  void closeJoystick(SdlJoystickHandle handle) {
    closedJoysticks.add((handle as _FakeJoystickHandle).instanceId);
  }
}

void main() {
  test(
    'raw enumeration includes unknown joysticks and mapping metadata',
    () async {
      final api = _FakeRawSdlApi();

      final controllers = await SdlRawControllerEnumerator(
        api,
      ).listControllers();

      expect(controllers, hasLength(2));
      expect(controllers.first.controllerId, '7');
      expect(controllers.first.sdlPlatform, 'macOS');
      expect(controllers.first.isMappedGamepad, isTrue);
      expect(controllers.first.capabilities.axisCount, 6);
      expect(controllers.first.capabilities.buttonCount, 15);
      expect(controllers.first.sdlMapping, contains('a:b0'));
      expect(controllers.last.displayName, 'Unknown joystick');
      expect(controllers.last.isMappedGamepad, isFalse);
      expect(controllers.last.sdlMapping, isNull);
      expect(api.openedJoysticks, <int>[7, 11]);
      expect(api.closedJoysticks, <int>[7, 11]);
    },
  );

  test('parses raw joystick button axis hat and device events', () {
    final button = ffi.calloc.allocate<SdlEvent>(sizeOf<SdlEvent>());
    final axis = ffi.calloc.allocate<SdlEvent>(sizeOf<SdlEvent>());
    final hat = ffi.calloc.allocate<SdlEvent>(sizeOf<SdlEvent>());
    final device = ffi.calloc.allocate<SdlEvent>(sizeOf<SdlEvent>());
    addTearDown(() {
      ffi.calloc.free(button);
      ffi.calloc.free(axis);
      ffi.calloc.free(hat);
      ffi.calloc.free(device);
    });

    button.ref.jbutton.type = SdlGamepadEventTypes.joystickButtonDown;
    button.ref.jbutton.timestamp = 1000000;
    button.ref.jbutton.which = 11;
    button.ref.jbutton.button = 3;
    button.ref.jbutton.down = true;
    axis.ref.jaxis.type = SdlGamepadEventTypes.joystickAxisMotion;
    axis.ref.jaxis.timestamp = 2000000;
    axis.ref.jaxis.which = 11;
    axis.ref.jaxis.axis = 2;
    axis.ref.jaxis.value = -12345;
    hat.ref.jhat.type = SdlGamepadEventTypes.joystickHatMotion;
    hat.ref.jhat.timestamp = 3000000;
    hat.ref.jhat.which = 11;
    hat.ref.jhat.hat = 0;
    hat.ref.jhat.value = 4;
    device.ref.jdevice.type = SdlGamepadEventTypes.joystickAdded;
    device.ref.jdevice.timestamp = 4000000;
    device.ref.jdevice.which = 11;

    expect(
      SdlGamepadEventParser.parse(button.ref),
      isA<SdlJoystickButtonInputEvent>()
          .having((event) => event.instanceId, 'instanceId', 11)
          .having((event) => event.button, 'button', 3)
          .having((event) => event.pressed, 'pressed', isTrue),
    );
    expect(
      SdlGamepadEventParser.parse(axis.ref),
      isA<SdlJoystickAxisInputEvent>()
          .having((event) => event.axis, 'axis', 2)
          .having((event) => event.value, 'value', -12345),
    );
    expect(
      SdlGamepadEventParser.parse(hat.ref),
      isA<SdlJoystickHatInputEvent>()
          .having((event) => event.hat, 'hat', 0)
          .having((event) => event.value, 'value', 4),
    );
    expect(
      SdlGamepadEventParser.parse(device.ref),
      isA<SdlJoystickDeviceInputEvent>().having(
        (event) => event.change,
        'change',
        SdlJoystickDeviceChange.added,
      ),
    );
  });

  test('one SDL pump fans out normalized and raw events', () async {
    final pending = <SdlPolledEvent>[];
    final api = _FakeRawSdlApi(polledEvents: pending);
    final provider = SdlControllerInputProvider(
      api: api,
      pollInterval: const Duration(milliseconds: 1),
    );
    final normalized = <NormalizedGamepadEvent>[];
    final raw = <RawControllerEvent>[];
    final normalizedSubscription = provider.events().listen(normalized.add);
    final rawSubscription = provider.rawEvents().listen(raw.add);
    addTearDown(normalizedSubscription.cancel);
    addTearDown(rawSubscription.cancel);

    pending.addAll(const <SdlPolledEvent>[
      SdlGamepadButtonInputEvent(
        instanceId: 7,
        button: SdlGamepadButtonIds.south,
        pressed: true,
        timestampMillis: 10,
      ),
      SdlJoystickButtonInputEvent(
        instanceId: 7,
        button: 4,
        pressed: true,
        timestampMillis: 11,
      ),
      SdlJoystickAxisInputEvent(
        instanceId: 11,
        axis: 1,
        value: 16000,
        timestampMillis: 12,
      ),
    ]);
    for (var attempt = 0; attempt < 20 && raw.length < 2; attempt++) {
      await Future<void>.delayed(const Duration(milliseconds: 2));
    }

    expect(normalized.single.button, GamepadButton.a);
    expect(raw, hasLength(2));
    expect(raw.first.controllerId, '7');
    expect(raw.last, isA<RawControllerAxisEvent>());
    expect(api.initCount, 1);
  });

  test('capture is exclusive, target-filtered, and disposable', () async {
    final pending = <SdlPolledEvent>[];
    final provider = SdlControllerInputProvider(
      api: _FakeRawSdlApi(polledEvents: pending),
      pollInterval: const Duration(milliseconds: 1),
    );
    var policyChanges = 0;
    provider.addCaptureStateListener(() => policyChanges++);

    final result = await provider.acquireCapture('7');
    expect(result, isA<ControllerCaptureStarted>());
    expect(provider.isNavigationSuppressed, isTrue);
    expect(await provider.acquireCapture('11'), isA<ControllerCaptureBusy>());
    final lease = (result as ControllerCaptureStarted).lease;
    final captured = <RawControllerEvent>[];
    final subscription = lease.events.listen(captured.add);
    addTearDown(subscription.cancel);

    pending.addAll(const <SdlPolledEvent>[
      SdlJoystickButtonInputEvent(
        instanceId: 11,
        button: 1,
        pressed: true,
        timestampMillis: 1,
      ),
      SdlJoystickButtonInputEvent(
        instanceId: 7,
        button: 2,
        pressed: true,
        timestampMillis: 2,
      ),
    ]);
    for (var attempt = 0; attempt < 20 && captured.isEmpty; attempt++) {
      await Future<void>.delayed(const Duration(milliseconds: 2));
    }
    expect(captured.single.controllerId, '7');

    lease.dispose();
    lease.dispose();
    expect(provider.isNavigationSuppressed, isFalse);
    expect(policyChanges, 2);
    expect(
      await acquireControllerCapture(
        const GamepadsControllerInputProvider(),
        'fallback',
      ),
      isA<ControllerCaptureUnsupported>(),
    );
  });

  test('lists SDL identities with provider id and enumeration order', () async {
    final api = _FakeSdlGamepadApi();

    final pads = await SdlGamepadEnumerator(api).listGamepads();

    expect(api.initCount, 1);
    expect(api.quitCount, 1);
    expect(api.opened, <int>[7, 9]);
    expect(api.closed, <int>[7, 9]);
    expect(pads, hasLength(2));
    expect(pads[0].id, '7');
    expect(pads[0].order, 0);
    expect(
      pads[0].identity,
      const ControllerIdentity(
        displayName: 'DualSense',
        sdlGuid: '030000004c050000e60c000000006800',
        serial: 'serial-a',
      ),
    );
    expect(pads[1].id, '9');
    expect(pads[1].order, 1);
    expect(pads[1].identity.serial, 'serial-b');
  });

  test('null serial still closes the opened gamepad handle', () async {
    final api = _FakeSdlGamepadApi(serials: const <int, String?>{7: null});

    final pads = await SdlGamepadEnumerator(api).listGamepads();

    expect(api.opened, <int>[7, 9]);
    expect(api.closed, <int>[7, 9]);
    expect(pads[0].identity.serial, isNull);
  });

  test('open failure leaves serial null without a close call', () async {
    final api = _FakeSdlGamepadApi(openReturnsNull: true);

    final pads = await SdlGamepadEnumerator(api).listGamepads();

    expect(api.opened, isEmpty);
    expect(api.closed, isEmpty);
    expect(pads.map((pad) => pad.identity.serial), everyElement(isNull));
  });

  test('thrown serial read still closes the opened handle', () async {
    final api = _FakeSdlGamepadApi(serialThrows: true);

    await expectLater(
      SdlGamepadEnumerator(api).listGamepads(),
      throwsA(isA<StateError>()),
    );

    expect(api.opened, <int>[7]);
    expect(api.closed, <int>[7]);
    expect(api.quitCount, 1);
  });

  test('init failure throws before listing', () async {
    final api = _FakeSdlGamepadApi(initResult: false);

    await expectLater(
      SdlGamepadEnumerator(api).listGamepads(),
      throwsA(isA<SdlGamepadException>()),
    );

    expect(api.opened, isEmpty);
    expect(api.quitCount, 0);
  });

  test('SDL-backed lister falls back when the SDL path fails', () async {
    final lister = SdlBackedGamepadLister(
      apiFactory: () async => throw StateError('no SDL'),
      fallback: () async => <ConnectedGamepad>[
        ConnectedGamepad.fallback(id: 'fallback', name: 'Fallback', order: 0),
      ],
    );

    final pads = await lister();

    expect(
      pads.single.identity,
      const ControllerIdentity(displayName: 'Fallback'),
    );
    expect(pads.single.identity.hasExactIdentity, isFalse);
  });

  test('provisioned lister uses SDL identities when SDL is ready', () async {
    var provisionCount = 0;
    String? openedLibraryPath;
    final api = _FakeSdlGamepadApi();
    final lister = ProvisionedSdlGamepadLister(
      ensureSdl3: () async {
        provisionCount++;
        return const SdlNativeReady(libraryPath: '/native/SDL3');
      },
      apiFactory: (libraryPath) {
        openedLibraryPath = libraryPath;
        return api;
      },
      fallback: () async => <ConnectedGamepad>[
        ConnectedGamepad.fallback(id: 'event-a', name: 'DualSense', order: 0),
        ConnectedGamepad.fallback(id: 'event-b', name: 'DualSense', order: 1),
      ],
    );

    final pads = await lister();

    expect(provisionCount, 1);
    expect(openedLibraryPath, '/native/SDL3');
    expect(pads.map((pad) => pad.id), <String>['event-a', 'event-b']);
    expect(pads.map((pad) => pad.identity.serial), <String>[
      'serial-a',
      'serial-b',
    ]);
  });

  test(
    'provisioned lister preserves provider ids for event matching',
    () async {
      final lister = ProvisionedSdlGamepadLister(
        ensureSdl3: () async =>
            const SdlNativeReady(libraryPath: '/native/SDL3'),
        apiFactory: (_) => _FakeSdlGamepadApi(),
        fallback: () async => <ConnectedGamepad>[
          ConnectedGamepad.fallback(
            id: 'gamepads-event-id',
            name: 'Provider Name',
            order: 0,
          ),
        ],
      );

      final pads = await lister();

      expect(pads.single.id, 'gamepads-event-id');
      expect(pads.single.name, 'Provider Name');
      expect(
        pads.single.identity,
        const ControllerIdentity(
          displayName: 'Provider Name',
          sdlGuid: '030000004c050000e60c000000006800',
          serial: 'serial-a',
        ),
      );
    },
  );

  test('provisioned lister caches unavailable SDL and falls back', () async {
    var provisionCount = 0;
    var fallbackCount = 0;
    final lister = ProvisionedSdlGamepadLister(
      ensureSdl3: () async {
        provisionCount++;
        return const SdlNativeUnavailable('not available');
      },
      apiFactory: (_) => throw StateError('should not open SDL'),
      fallback: () async {
        fallbackCount++;
        return <ConnectedGamepad>[
          ConnectedGamepad.fallback(id: 'fallback', name: 'Fallback', order: 0),
        ];
      },
    );

    final first = await lister();
    final second = await lister();

    expect(provisionCount, 1);
    expect(fallbackCount, 2);
    expect(first.single.id, 'fallback');
    expect(second.single.id, 'fallback');
  });

  test('parses SDL gamepad button down and up events', () {
    final down = ffi.calloc.allocate<SdlEvent>(sizeOf<SdlEvent>());
    final up = ffi.calloc.allocate<SdlEvent>(sizeOf<SdlEvent>());
    addTearDown(() {
      ffi.calloc.free(down);
      ffi.calloc.free(up);
    });

    down.ref.gbutton.type = SdlGamepadEventTypes.gamepadButtonDown;
    down.ref.gbutton.timestamp = 1234000000;
    down.ref.gbutton.which = 7;
    down.ref.gbutton.button = SdlGamepadButtonIds.leftShoulder;
    down.ref.gbutton.down = true;
    up.ref.gbutton.type = SdlGamepadEventTypes.gamepadButtonUp;
    up.ref.gbutton.timestamp = 2234000000;
    up.ref.gbutton.which = 7;
    up.ref.gbutton.button = SdlGamepadButtonIds.leftShoulder;
    up.ref.gbutton.down = false;

    expect(
      SdlGamepadEventParser.parse(down.ref),
      const SdlGamepadButtonInputEvent(
        instanceId: 7,
        button: SdlGamepadButtonIds.leftShoulder,
        pressed: true,
        timestampMillis: 1234,
      ),
    );
    expect(
      SdlGamepadEventParser.parse(up.ref),
      const SdlGamepadButtonInputEvent(
        instanceId: 7,
        button: SdlGamepadButtonIds.leftShoulder,
        pressed: false,
        timestampMillis: 2234,
      ),
    );
  });

  test('parses SDL gamepad add and remove events', () {
    final added = ffi.calloc.allocate<SdlEvent>(sizeOf<SdlEvent>());
    final removed = ffi.calloc.allocate<SdlEvent>(sizeOf<SdlEvent>());
    addTearDown(() {
      ffi.calloc.free(added);
      ffi.calloc.free(removed);
    });

    added.ref.gdevice.type = SdlGamepadEventTypes.gamepadAdded;
    added.ref.gdevice.timestamp = 1000000;
    added.ref.gdevice.which = 7;
    removed.ref.gdevice.type = SdlGamepadEventTypes.gamepadRemoved;
    removed.ref.gdevice.timestamp = 2000000;
    removed.ref.gdevice.which = 9;

    expect(
      SdlGamepadEventParser.parse(added.ref),
      const SdlGamepadDeviceInputEvent(
        instanceId: 7,
        change: SdlGamepadDeviceChange.added,
        timestampMillis: 1,
      ),
    );
    expect(
      SdlGamepadEventParser.parse(removed.ref),
      const SdlGamepadDeviceInputEvent(
        instanceId: 9,
        change: SdlGamepadDeviceChange.removed,
        timestampMillis: 2,
      ),
    );
  });

  test('SDL event poller normalizes gamepad button events', () {
    final api = _FakeSdlGamepadApi(
      polledEvents: const <SdlPolledEvent>[
        SdlGamepadButtonInputEvent(
          instanceId: 7,
          button: SdlGamepadButtonIds.leftShoulder,
          pressed: true,
          timestampMillis: 10,
        ),
        SdlGamepadDeviceInputEvent(
          instanceId: 9,
          change: SdlGamepadDeviceChange.added,
          timestampMillis: 11,
        ),
        SdlGamepadButtonInputEvent(
          instanceId: 7,
          button: SdlGamepadButtonIds.rightShoulder,
          pressed: false,
          timestampMillis: 12,
        ),
      ],
    );

    final events = SdlGamepadEventPoller(api: api).pollPendingEvents();

    expect(events.map((event) => event.gamepadId), <String>['7', '7']);
    expect(events.map((event) => event.button), <GamepadButton?>[
      GamepadButton.leftBumper,
      GamepadButton.rightBumper,
    ]);
    expect(events.map((event) => event.value), <double>[1.0, 0.0]);
  });

  test('provisioned provider lists and emits events from SDL ids', () async {
    final api = _FakeSdlGamepadApi(
      polledEvents: const <SdlPolledEvent>[
        SdlGamepadButtonInputEvent(
          instanceId: 7,
          button: SdlGamepadButtonIds.leftShoulder,
          pressed: true,
          timestampMillis: 10,
        ),
      ],
    );
    final provider = ProvisionedSdlControllerInputProvider(
      ensureSdl3: () async => const SdlNativeReady(libraryPath: '/native/SDL3'),
      apiFactory: (_) => api,
      fallback: const GamepadsControllerInputProvider(),
      pollInterval: const Duration(milliseconds: 1),
    );

    final pads = await provider.listGamepads();
    final expectation = expectLater(
      provider.events(),
      emits(
        isA<NormalizedGamepadEvent>()
            .having((event) => event.gamepadId, 'gamepadId', '7')
            .having(
              (event) => event.button,
              'button',
              GamepadButton.leftBumper,
            ),
      ),
    );

    await expectation;
    expect(pads.first.id, '7');
  });

  test('provider listing does not stop active SDL event polling', () async {
    final polledEvents = <SdlPolledEvent>[];
    final api = _FakeSdlGamepadApi(
      polledEvents: polledEvents,
      requireActiveForPoll: true,
      requireOpenForButtonPoll: true,
    );
    final provider = SdlControllerInputProvider(
      api: api,
      pollInterval: const Duration(milliseconds: 1),
    );
    final seen = <NormalizedGamepadEvent>[];
    final subscription = provider.events().listen(seen.add);

    await Future<void>.delayed(const Duration(milliseconds: 2));
    expect(api.initCount, 1);
    expect(api.eventsEnabled, isTrue);
    expect(api.opened, <int>[7, 9]);

    await provider.listGamepads();

    expect(api.initCount, 1);
    expect(api.quitCount, 0);

    polledEvents.add(
      const SdlGamepadButtonInputEvent(
        instanceId: 7,
        button: SdlGamepadButtonIds.leftShoulder,
        pressed: true,
        timestampMillis: 10,
      ),
    );

    for (var attempt = 0; attempt < 20 && seen.isEmpty; attempt++) {
      await Future<void>.delayed(const Duration(milliseconds: 2));
    }

    expect(seen, hasLength(1));
    expect(seen.single.gamepadId, '7');
    expect(seen.single.button, GamepadButton.leftBumper);

    await subscription.cancel();
    expect(api.quitCount, 1);
  });

  test(
    'runtime SDL polling failure switches listing and events to fallback ids',
    () async {
      final api = _FakeSdlGamepadApi(
        polledEvents: const <SdlPolledEvent>[
          SdlGamepadButtonInputEvent(
            instanceId: 7,
            button: SdlGamepadButtonIds.leftShoulder,
            pressed: true,
            timestampMillis: 10,
          ),
        ],
        pollErrorAfterCount: 1,
      );
      final fallbackPad = ConnectedGamepad.fallback(
        id: 'fallback-pad',
        name: 'Fallback pad',
        order: 0,
      );
      final fallbackEvent = NormalizedGamepadEvent(
        gamepadId: fallbackPad.id,
        timestamp: 20,
        value: 1.0,
        button: GamepadButton.rightBumper,
        rawEvent: GamepadEvent(
          gamepadId: fallbackPad.id,
          timestamp: 20,
          type: KeyType.button,
          key: 'fallback:right-bumper',
          value: 1.0,
        ),
      );
      final fallbackEvents =
          StreamController<NormalizedGamepadEvent>.broadcast();
      addTearDown(fallbackEvents.close);
      final provider = ProvisionedSdlControllerInputProvider(
        ensureSdl3: () async =>
            const SdlNativeReady(libraryPath: '/native/SDL3'),
        apiFactory: (_) => api,
        fallback: CallbackControllerInputProvider(
          listGamepads: () async => <ConnectedGamepad>[fallbackPad],
          events: () => fallbackEvents.stream,
        ),
        pollInterval: const Duration(milliseconds: 1),
      );

      expect((await provider.listGamepads()).map((pad) => pad.id), <String>[
        '7',
        '9',
      ]);

      final seen = <NormalizedGamepadEvent>[];
      final subscription = provider.events().listen(seen.add);
      addTearDown(subscription.cancel);

      for (
        var attempt = 0;
        attempt < 20 && !fallbackEvents.hasListener;
        attempt++
      ) {
        await Future<void>.delayed(const Duration(milliseconds: 2));
      }
      expect(fallbackEvents.hasListener, isTrue);
      expect(seen.map((event) => event.gamepadId), contains('7'));

      expect(await provider.listGamepads(), <ConnectedGamepad>[fallbackPad]);

      fallbackEvents.add(fallbackEvent);
      for (
        var attempt = 0;
        attempt < 20 && !seen.contains(fallbackEvent);
        attempt++
      ) {
        await Future<void>.delayed(const Duration(milliseconds: 2));
      }

      expect(seen, contains(fallbackEvent));
      expect(
        seen.where((event) => event == fallbackEvent).single.gamepadId,
        fallbackPad.id,
      );
    },
  );

  test(
    'in-flight SDL listing waits for failover and discards stale SDL ids',
    () async {
      final sdlListing = Completer<List<ConnectedGamepad>>();
      final sdlListingStarted = Completer<void>();
      final cancellationStarted = Completer<void>();
      final allowCancellation = Completer<void>();
      addTearDown(() {
        if (!allowCancellation.isCompleted) {
          allowCancellation.complete();
        }
      });
      final sdlEvents = StreamController<NormalizedGamepadEvent>(
        onCancel: () {
          cancellationStarted.complete();
          return allowCancellation.future;
        },
      );
      addTearDown(sdlEvents.close);
      final fallbackEvents =
          StreamController<NormalizedGamepadEvent>.broadcast();
      addTearDown(fallbackEvents.close);
      final fallbackPad = ConnectedGamepad.fallback(
        id: 'fallback-pad',
        name: 'Fallback pad',
        order: 0,
      );
      final provider = ProvisionedSdlControllerInputProvider(
        ensureSdl3: () async =>
            const SdlNativeReady(libraryPath: '/native/SDL3'),
        apiFactory: (_) => _FakeSdlGamepadApi(),
        fallback: CallbackControllerInputProvider(
          listGamepads: () async => <ConnectedGamepad>[fallbackPad],
          events: () => fallbackEvents.stream,
        ),
        sdlProviderFactory: (_, _) => CallbackControllerInputProvider(
          listGamepads: () {
            sdlListingStarted.complete();
            return sdlListing.future;
          },
          events: () => sdlEvents.stream,
        ),
      );

      final subscription = provider.events().listen((_) {});
      addTearDown(subscription.cancel);
      for (var attempt = 0; attempt < 20 && !sdlEvents.hasListener; attempt++) {
        await Future<void>.delayed(const Duration(milliseconds: 1));
      }
      expect(sdlEvents.hasListener, isTrue);

      final listingResult = provider.listGamepads();
      await sdlListingStarted.future;
      sdlEvents.addError(StateError('SDL polling failed'));
      await cancellationStarted.future;
      sdlListing.complete(<ConnectedGamepad>[
        const ConnectedGamepad(
          id: '7',
          order: 0,
          identity: ControllerIdentity(
            displayName: 'SDL pad',
            sdlGuid: 'guid',
            serial: 'serial',
          ),
        ),
      ]);

      var listingCompleted = false;
      unawaited(listingResult.then((_) => listingCompleted = true));
      await Future<void>.delayed(Duration.zero);
      expect(listingCompleted, isFalse);
      expect(fallbackEvents.hasListener, isFalse);

      allowCancellation.complete();

      expect(await listingResult, <ConnectedGamepad>[fallbackPad]);
      expect(fallbackEvents.hasListener, isTrue);
    },
  );

  test(
    'listing started during failover waits and never enumerates SDL',
    () async {
      final cancellationStarted = Completer<void>();
      final allowCancellation = Completer<void>();
      addTearDown(() {
        if (!allowCancellation.isCompleted) {
          allowCancellation.complete();
        }
      });
      final sdlEvents = StreamController<NormalizedGamepadEvent>(
        onCancel: () {
          cancellationStarted.complete();
          return allowCancellation.future;
        },
      );
      addTearDown(sdlEvents.close);
      final fallbackEvents =
          StreamController<NormalizedGamepadEvent>.broadcast();
      addTearDown(fallbackEvents.close);
      final fallbackPad = ConnectedGamepad.fallback(
        id: 'fallback-pad',
        name: 'Fallback pad',
        order: 0,
      );
      var sdlListingCount = 0;
      final provider = ProvisionedSdlControllerInputProvider(
        ensureSdl3: () async =>
            const SdlNativeReady(libraryPath: '/native/SDL3'),
        apiFactory: (_) => _FakeSdlGamepadApi(),
        fallback: CallbackControllerInputProvider(
          listGamepads: () async => <ConnectedGamepad>[fallbackPad],
          events: () => fallbackEvents.stream,
        ),
        sdlProviderFactory: (_, _) => CallbackControllerInputProvider(
          listGamepads: () async {
            sdlListingCount++;
            return const <ConnectedGamepad>[];
          },
          events: () => sdlEvents.stream,
        ),
      );

      final subscription = provider.events().listen((_) {});
      addTearDown(subscription.cancel);
      for (var attempt = 0; attempt < 20 && !sdlEvents.hasListener; attempt++) {
        await Future<void>.delayed(const Duration(milliseconds: 1));
      }
      expect(sdlEvents.hasListener, isTrue);

      sdlEvents.addError(StateError('SDL polling failed'));
      await cancellationStarted.future;

      final listingResult = provider.listGamepads();
      var listingCompleted = false;
      unawaited(listingResult.then((_) => listingCompleted = true));
      await Future<void>.delayed(Duration.zero);
      expect(listingCompleted, isFalse);
      expect(sdlListingCount, 0);
      expect(fallbackEvents.hasListener, isFalse);

      allowCancellation.complete();

      expect(await listingResult, <ConnectedGamepad>[fallbackPad]);
      expect(sdlListingCount, 0);
      expect(fallbackEvents.hasListener, isTrue);
    },
  );

  test('provisioned provider falls back when SDL is unavailable', () async {
    final fallbackPad = ConnectedGamepad.fallback(
      id: 'fallback',
      name: 'Fallback',
      order: 0,
    );
    final fallbackEvent = NormalizedGamepadEvent(
      gamepadId: 'fallback',
      timestamp: 0,
      value: 1.0,
      button: GamepadButton.leftBumper,
      rawEvent: GamepadEvent(
        gamepadId: 'fallback',
        timestamp: 0,
        type: KeyType.button,
        key: 'raw',
        value: 1.0,
      ),
    );
    final provider = ProvisionedSdlControllerInputProvider(
      ensureSdl3: () async => const SdlNativeUnavailable('not available'),
      apiFactory: (_) => throw StateError('should not open SDL'),
      fallback: CallbackControllerInputProvider(
        listGamepads: () async => <ConnectedGamepad>[fallbackPad],
        events: () => Stream<NormalizedGamepadEvent>.value(fallbackEvent),
      ),
    );

    expect(await provider.listGamepads(), <ConnectedGamepad>[fallbackPad]);

    await expectLater(provider.events(), emits(fallbackEvent));
  });
}
