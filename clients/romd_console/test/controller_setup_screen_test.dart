import 'dart:async';
import 'dart:ui' show Tristate;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/controller_setup_screen.dart';

const String _guid = '030000004c050000e60c000000006800';

final class _Repository implements ControllerHardwareMappingRepository {
  ControllerHardwareMapping? value;
  int finds = 0;
  int saves = 0;
  int resets = 0;
  CanonicalControllerMapping? saved;

  @override
  Future<ControllerHardwareMapping?> find({
    required String sdlPlatform,
    required String sdlGuid,
  }) async {
    finds++;
    return value;
  }

  @override
  Future<void> reset({
    required String sdlPlatform,
    required String sdlGuid,
  }) async {
    resets++;
  }

  @override
  Future<void> save({
    required String sdlPlatform,
    required String sdlGuid,
    required String displayName,
    required CanonicalControllerMapping mapping,
  }) async {
    saves++;
    saved = mapping;
  }
}

final class _Lease implements ControllerInputCaptureLease {
  _Lease({
    required this.controllerId,
    required this.stream,
    required this.done,
  });

  @override
  final String controllerId;
  final Stream<RawControllerEvent> stream;
  final VoidCallback done;
  bool disposed = false;

  @override
  Stream<RawControllerEvent> get events => stream;

  @override
  void dispose() {
    if (disposed) return;
    disposed = true;
    done();
  }
}

final class _Provider
    implements
        ControllerInputProvider,
        RawControllerInputProvider,
        ControllerInputCaptureGate {
  final StreamController<RawControllerEvent> raw =
      StreamController<RawControllerEvent>.broadcast(sync: true);
  bool suppressed = false;
  int captures = 0;

  @override
  Future<ControllerCaptureResult> acquireCapture(String controllerId) async {
    if (suppressed) return const ControllerCaptureBusy();
    suppressed = true;
    captures++;
    return ControllerCaptureStarted(
      _Lease(
        controllerId: controllerId,
        stream: raw.stream,
        done: () => suppressed = false,
      ),
    );
  }

  @override
  void addCaptureStateListener(VoidCallback listener) {}

  @override
  Stream<NormalizedGamepadEvent> events() => const Stream.empty();

  @override
  bool get isNavigationSuppressed => suppressed;

  @override
  Future<List<ConnectedGamepad>> listGamepads() async => const [];

  @override
  Future<List<RawControllerDescriptor>> listRawControllers() async => const [];

  @override
  Stream<RawControllerEvent> rawEvents() => raw.stream;

  @override
  void removeCaptureStateListener(VoidCallback listener) {}

  Future<void> close() => raw.close();
}

RawControllerDescriptor _descriptor({
  String? guid = _guid,
  String? detectedMapping,
}) => RawControllerDescriptor(
  controllerId: 'target',
  displayName: 'Neutral Test Controller',
  sdlPlatform: 'Mac OS X',
  sdlGuid: guid,
  isMappedGamepad: detectedMapping != null,
  capabilities: const RawControllerCapabilities(
    axisCount: 6,
    buttonCount: 16,
    hatCount: 1,
  ),
  sdlMapping: detectedMapping,
);

ControllerHardwareMapping _custom(
  Map<CanonicalGamepadControl, RawGamepadInput> bindings,
) => ControllerHardwareMapping(
  sdlPlatform: 'Mac OS X',
  sdlGuid: _guid,
  displayName: 'Neutral Test Controller',
  mapping: CanonicalControllerMapping(bindings),
  createdAt: DateTime.utc(2026),
  updatedAt: DateTime.utc(2026),
);

void _button(
  _Provider provider,
  int button, {
  String controllerId = 'target',
  bool release = true,
}) {
  provider.raw.add(
    RawControllerButtonEvent(
      controllerId: controllerId,
      timestampMillis: button * 2,
      button: button,
      pressed: true,
    ),
  );
  if (release) {
    provider.raw.add(
      RawControllerButtonEvent(
        controllerId: controllerId,
        timestampMillis: button * 2 + 1,
        button: button,
        pressed: false,
      ),
    );
  }
}

void _axis(
  _Provider provider,
  int axis,
  int value, {
  String controllerId = 'target',
  int timestampMillis = 0,
}) {
  provider.raw.add(
    RawControllerAxisEvent(
      controllerId: controllerId,
      timestampMillis: timestampMillis,
      axis: axis,
      value: value,
    ),
  );
}

void main() {
  Future<(_Repository, _Provider)> pump(
    WidgetTester tester, {
    _Repository? repository,
    RawControllerDescriptor? descriptor,
    double textScale = 1,
    bool reducedMotion = false,
  }) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final repo = repository ?? _Repository();
    final provider = _Provider();
    addTearDown(provider.close);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context).copyWith(
            textScaler: TextScaler.linear(textScale),
            disableAnimations: reducedMotion,
          ),
          child: child!,
        ),
        home: ControllerSetupScreen(
          repository: repo,
          descriptor: descriptor ?? _descriptor(),
          inputProvider: provider,
          connectedUnits: 1,
        ),
      ),
    );
    await tester.pumpAndSettle();
    return (repo, provider);
  }

  Future<void> openMap(WidgetTester tester) async {
    await tester.tap(find.text('Map controls'));
    await tester.pump();
  }

  testWidgets('shows one neutral positional controller workspace', (
    tester,
  ) async {
    await pump(tester);

    expect(
      find.bySemanticsLabel('Neutral Ottercade controller map'),
      findsOneWidget,
    );
    for (final control in CanonicalGamepadControl.values.where(
      (control) =>
          control != CanonicalGamepadControl.leftStickX &&
          control != CanonicalGamepadControl.leftStickY &&
          control != CanonicalGamepadControl.rightStickX &&
          control != CanonicalGamepadControl.rightStickY,
    )) {
      expect(
        find.byKey(ValueKey<String>('control-${control.name}')),
        findsOneWidget,
      );
    }
    expect(
      find.byKey(const ValueKey<String>('control-leftStick')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('control-rightStick')),
      findsOneWidget,
    );
    expect(find.text('L X'), findsNothing);
    expect(find.text('L Y'), findsNothing);
    expect(find.text('R X'), findsNothing);
    expect(find.text('R Y'), findsNothing);
    expect(find.text('Test controller'), findsOneWidget);
    expect(find.text('Map controls'), findsOneWidget);
    expect(find.text('SOUTH'), findsOneWidget);
    expect(find.text('L TRIGGER'), findsOneWidget);
    expect(find.text('HOME'), findsOneWidget);
  });

  testWidgets('identity-less connection is Test-only with zero repository IO', (
    tester,
  ) async {
    final (repo, provider) = await pump(
      tester,
      descriptor: _descriptor(guid: null),
    );

    expect(repo.finds, 0);
    expect(
      find.textContaining('Mapping unavailable for this connection'),
      findsOneWidget,
    );
    final map = tester.widget<OutlinedButton>(
      find.widgetWithText(OutlinedButton, 'Map controls'),
    );
    expect(map.onPressed, isNull);

    await tester.tap(find.text('Test controller'));
    await tester.pump();
    expect(provider.suppressed, isTrue);
    _button(provider, 7);
    await tester.pump();
    expect(find.textContaining('Unmapped button 8'), findsOneWidget);
    expect(repo.saves, 0);
    expect(repo.resets, 0);
  });

  testWidgets('Test visualizes mapped and honest unmapped target activity', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: RawButtonInput(0),
      });
    final (_, provider) = await pump(tester, repository: repo);
    await tester.tap(find.text('Test controller'));
    await tester.pump();

    _button(provider, 0, controllerId: 'other');
    await tester.pump();
    expect(find.textContaining('Waiting for input'), findsOneWidget);
    _button(provider, 0, release: false);
    await tester.pump();
    expect(find.textContaining('Bottom face button pressed'), findsOneWidget);
    expect(
      find.bySemanticsLabel(RegExp('Bottom face button.*Mapped.*Active')),
      findsOneWidget,
    );
    provider.raw.add(
      const RawControllerButtonEvent(
        controllerId: 'target',
        timestampMillis: 2,
        button: 0,
        pressed: false,
      ),
    );
    await tester.pump();
    expect(find.textContaining('Bottom face button released'), findsOneWidget);
    expect(
      find.bySemanticsLabel(RegExp('Bottom face button.*Active')),
      findsNothing,
    );
    expect(
      tester
          .getSemantics(find.byKey(const ValueKey<String>('control-faceSouth')))
          .flagsCollection
          .isSelected,
      isNot(Tristate.isTrue),
    );
    _button(provider, 8);
    await tester.pump();
    expect(find.textContaining('Unmapped button 9'), findsOneWidget);

    await tester.tap(find.text('Stop testing'));
    await tester.pump();
    expect(provider.suppressed, isFalse);
    expect(repo.saves, 0);
  });

  testWidgets('Test reports paired stick activity without axis vocabulary', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.leftStickX: RawAxisInput(0),
        CanonicalGamepadControl.leftStickY: RawAxisInput(1),
      });
    final (_, provider) = await pump(tester, repository: repo);
    await tester.tap(find.text('Test controller'));
    await tester.pump();

    _axis(provider, 0, 0);
    _axis(provider, 0, 21000);
    await tester.pump();

    expect(find.text('Left stick moved'), findsOneWidget);
    expect(find.textContaining('Axis 1'), findsNothing);
    expect(
      find.bySemanticsLabel(RegExp('Left stick.*Mapped.*Active')),
      findsOneWidget,
    );
    expect(repo.saves, 0);
  });

  testWidgets('target long-hold exits Test without saving', (tester) async {
    final (repo, provider) = await pump(tester);
    await tester.tap(find.text('Test controller'));
    await tester.pump();
    expect(find.text('TEST MODE · NO CHANGES ARE SAVED'), findsOneWidget);

    _button(provider, 4, controllerId: 'other', release: false);
    await tester.pump(const Duration(milliseconds: 1201));
    expect(find.text('TEST MODE · NO CHANGES ARE SAVED'), findsOneWidget);

    _button(provider, 4, release: false);
    await tester.pump(const Duration(milliseconds: 1201));
    expect(find.text('OVERVIEW'), findsOneWidget);
    expect(provider.suppressed, isFalse);
    expect(repo.saves, 0);
  });

  testWidgets('Map changes one selected control only after explicit Change', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    expect(find.text('MAP MODE · EDITING A LOCAL DRAFT'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey<String>('control-faceEast')));
    await tester.pump();

    _button(provider, 4);
    await tester.pump();
    expect(find.textContaining('Not mapped'), findsOneWidget);
    await tester.tap(find.text('Change'));
    await tester.pump();
    expect(provider.suppressed, isTrue);
    _button(provider, 3, controllerId: 'other');
    await tester.pump();
    expect(find.text('Listening…'), findsOneWidget);
    _button(provider, 4);
    await tester.pump();

    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey<String>('selected-binding')))
          .data,
      contains('Button 5'),
    );
    expect(provider.suppressed, isFalse);
    expect(repo.saves, 0);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'controller-change',
    );
  });

  testWidgets('target long-hold cancels Change without mapping or writing', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-faceEast')));
    await tester.tap(find.text('Change'));
    await tester.pump();

    _button(provider, 5, controllerId: 'other', release: false);
    await tester.pump(const Duration(milliseconds: 1201));
    expect(find.text('Listening…'), findsOneWidget);

    _button(provider, 5, release: false);
    await tester.pump(const Duration(milliseconds: 1201));
    expect(find.textContaining('Change canceled'), findsOneWidget);
    expect(
      find.textContaining('Right face button · Not mapped'),
      findsOneWidget,
    );
    expect(provider.suppressed, isFalse);
    expect(repo.saves, 0);

    provider.raw.add(
      const RawControllerButtonEvent(
        controllerId: 'target',
        timestampMillis: 99,
        button: 5,
        pressed: false,
      ),
    );
    await tester.pump();
    expect(
      find.textContaining('Right face button · Not mapped'),
      findsOneWidget,
    );
  });

  testWidgets('visible Cancel stops Change before dismissing the route', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.text('Change'));
    await tester.pump();

    await tester.tap(find.text('Cancel'));
    await tester.pump();

    expect(find.byType(ControllerSetupScreen), findsOneWidget);
    expect(find.textContaining('Change canceled'), findsOneWidget);
    expect(provider.suppressed, isFalse);
    expect(repo.saves, 0);
  });

  testWidgets('conflict is rejected without stealing either binding', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: RawButtonInput(0),
        CanonicalGamepadControl.faceEast: RawButtonInput(1),
      });
    final (_, provider) = await pump(tester, repository: repo);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-faceEast')));
    await tester.tap(find.text('Change'));
    await tester.pump();
    _button(provider, 0);
    await tester.pump();

    expect(
      find.textContaining('Bottom face button already uses'),
      findsOneWidget,
    );
    expect(find.textContaining('Right face button · Button 2'), findsOneWidget);
    expect(provider.suppressed, isFalse);
    expect(repo.saves, 0);
  });

  testWidgets('Clear remains local until explicit atomic Save', (tester) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: RawButtonInput(0),
        CanonicalGamepadControl.start: RawButtonInput(7),
      });
    await pump(tester, repository: repo);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-faceSouth')));
    await tester.tap(find.text('Clear'));
    await tester.pump();
    expect(repo.saves, 0);
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(repo.saves, 1);
    expect(repo.saved!.inputFor(CanonicalGamepadControl.faceSouth), isNull);
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.start),
      const RawButtonInput(7),
    );
  });

  testWidgets('cardinal hat inputs can be mapped', (tester) async {
    final (_, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-dpadUp')));
    await tester.tap(find.text('Change'));
    await tester.pump();
    provider.raw.add(
      const RawControllerHatEvent(
        controllerId: 'target',
        timestampMillis: 0,
        hat: 0,
        value: 3,
      ),
    );
    await tester.pump();
    expect(
      find.text('Move the D-pad in one direction at a time.'),
      findsOneWidget,
    );
    expect(find.text('Listening…'), findsOneWidget);
    provider.raw.add(
      const RawControllerHatEvent(
        controllerId: 'target',
        timestampMillis: 1,
        hat: 0,
        value: 1,
      ),
    );
    await tester.pump();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey<String>('selected-binding')))
          .data,
      contains('Directional input 1 · 1'),
    );
  });

  testWidgets('stick capture maps right and down as one normal pair', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-leftStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();

    expect(
      find.textContaining('stick you want to use for Left stick to the right'),
      findsOneWidget,
    );
    _axis(provider, 0, 0);
    _axis(provider, 1, 0);
    _axis(provider, 0, 20000);
    await tester.pump();
    expect(
      find.text('Keep that stick held right, then move it down.'),
      findsOneWidget,
    );
    expect(find.textContaining('one continuous movement'), findsOneWidget);
    _axis(provider, 1, 22000);
    await tester.pump();

    expect(
      find.text('Left stick · Mapped · directions calibrated'),
      findsOneWidget,
    );
    expect(provider.suppressed, isFalse);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'controller-change',
    );
    expect(repo.saves, 0);
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickX),
      const RawAxisInput(0),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickY),
      const RawAxisInput(1),
    );
  });

  testWidgets('stick capture derives inversion from both directed gestures', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-rightStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();

    _axis(provider, 4, 0);
    _axis(provider, 5, 0);
    _axis(provider, 4, -21000);
    _axis(provider, 5, -23000);
    await tester.pump();
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.rightStickX),
      const RawAxisInput(4, inverted: true),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.rightStickY),
      const RawAxisInput(5, inverted: true),
    );
  });

  testWidgets('detected stick pair rejects a cross-stick vertical axis', (
    tester,
  ) async {
    final (repo, provider) = await pump(
      tester,
      descriptor: _descriptor(
        detectedMapping:
            '$_guid,Pad,leftx:a0,lefty:a1,rightx:a2,righty:a3,crc:1234,',
      ),
    );
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-leftStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();

    _axis(provider, 0, 0);
    _axis(provider, 1, 0);
    _axis(provider, 3, 0);
    _axis(provider, 0, -22000);
    _axis(provider, 3, -22000);
    await tester.pump();
    expect(find.textContaining('different controller stick'), findsOneWidget);
    expect(find.text('Listening…'), findsOneWidget);
    expect(repo.saves, 0);

    _axis(provider, 1, -23000);
    await tester.pump();
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickX),
      const RawAxisInput(0, inverted: true),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickY),
      const RawAxisInput(1, inverted: true),
    );
  });

  testWidgets('unknown stick pair requires horizontal to stay held', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-leftStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();

    _axis(provider, 0, 0);
    _axis(provider, 1, 0);
    _axis(provider, 0, 22000);
    _axis(provider, 0, 0);
    await tester.pump();
    expect(find.textContaining('stick was released'), findsOneWidget);
    _axis(provider, 1, 23000);
    await tester.pump();
    expect(find.textContaining('Return the stick to center'), findsOneWidget);
    expect(find.text('Listening…'), findsOneWidget);
    expect(repo.saves, 0);

    _axis(provider, 1, 0);
    _axis(provider, 0, 22000);
    _axis(provider, 1, 23000);
    await tester.pump();
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickX),
      const RawAxisInput(0),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickY),
      const RawAxisInput(1),
    );
  });

  testWidgets('partial detected map uses continuous-hold fallback', (
    tester,
  ) async {
    final (repo, provider) = await pump(
      tester,
      descriptor: _descriptor(detectedMapping: '$_guid,Pad,leftx:a0,crc:1234,'),
    );
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-leftStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();

    _axis(provider, 2, 0);
    _axis(provider, 3, 0);
    _axis(provider, 2, 22000);
    _axis(provider, 3, 23000);
    await tester.pump();
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickX),
      const RawAxisInput(2),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickY),
      const RawAxisInput(3),
    );
  });

  testWidgets('canceling partial stick capture changes neither axis', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.leftStickX: RawAxisInput(0),
        CanonicalGamepadControl.leftStickY: RawAxisInput(1),
      });
    final (_, provider) = await pump(tester, repository: repo);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-leftStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();
    _axis(provider, 2, 0);
    _axis(provider, 2, 22000);
    await tester.pump();

    await tester.tap(find.text('Cancel'));
    await tester.pump();

    expect(find.textContaining('Change canceled'), findsOneWidget);
    expect(
      find.text('Left stick · Mapped · directions calibrated'),
      findsOneWidget,
    );
    expect(
      tester
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Save'))
          .onPressed,
      isNull,
    );
    expect(repo.saves, 0);
    expect(provider.suppressed, isFalse);
  });

  testWidgets('holding a button cancels partial stick capture atomically', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.leftStickX: RawAxisInput(0),
        CanonicalGamepadControl.leftStickY: RawAxisInput(1),
      });
    final (_, provider) = await pump(tester, repository: repo);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-leftStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();
    _axis(provider, 2, 0);
    _axis(provider, 2, 22000);
    _button(provider, 6, release: false);
    await tester.pump(const Duration(milliseconds: 1201));

    expect(find.textContaining('Change canceled'), findsOneWidget);
    expect(
      find.text('Left stick · Mapped · directions calibrated'),
      findsOneWidget,
    );
    expect(
      tester
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Save'))
          .onPressed,
      isNull,
    );
    expect(repo.saves, 0);
    expect(provider.suppressed, isFalse);
  });

  testWidgets('disconnect discards partial stick capture', (tester) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.rightStickX: RawAxisInput(4),
        CanonicalGamepadControl.rightStickY: RawAxisInput(5),
      });
    final (_, provider) = await pump(tester, repository: repo);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-rightStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();
    _axis(provider, 2, 0);
    _axis(provider, 2, 22000);
    provider.raw.add(
      const RawControllerDeviceEvent(
        controllerId: 'target',
        timestampMillis: 3,
        change: RawControllerDeviceChange.removed,
      ),
    );
    await tester.pump();

    expect(find.textContaining('Controller disconnected'), findsOneWidget);
    expect(
      find.text('Right stick · Mapped · directions calibrated'),
      findsOneWidget,
    );
    expect(
      tester
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Save'))
          .onPressed,
      isNull,
    );
    expect(repo.saves, 0);
    expect(provider.suppressed, isFalse);
  });

  testWidgets('stick conflict rejects the complete pair atomically', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: RawAxisInput(3),
        CanonicalGamepadControl.leftStickX: RawAxisInput(0),
        CanonicalGamepadControl.leftStickY: RawAxisInput(1),
      });
    final (_, provider) = await pump(tester, repository: repo);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-leftStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();
    _axis(provider, 2, 0);
    _axis(provider, 3, 0);
    _axis(provider, 2, 22000);
    _axis(provider, 3, 22000);
    await tester.pump();

    expect(
      find.textContaining('Bottom face button already uses part of that stick'),
      findsOneWidget,
    );
    expect(
      find.text('Left stick · Mapped · directions calibrated'),
      findsOneWidget,
    );
    expect(
      tester
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Save'))
          .onPressed,
      isNull,
    );
    expect(repo.saves, 0);
    expect(provider.suppressed, isFalse);
  });

  testWidgets('only target controller can complete paired stick capture', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-rightStick')));
    await tester.tap(find.text('Change'));
    await tester.pump();

    _axis(provider, 4, 0, controllerId: 'other');
    _axis(provider, 5, 0, controllerId: 'other');
    _axis(provider, 4, 24000, controllerId: 'other');
    _axis(provider, 5, 24000, controllerId: 'other');
    await tester.pump();
    expect(
      find.textContaining('stick you want to use for Right stick to the right'),
      findsOneWidget,
    );

    _axis(provider, 4, 0);
    _axis(provider, 5, 0);
    _axis(provider, 4, 24000);
    _axis(provider, 5, 24000);
    await tester.pump();
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(repo.saves, 1);
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.rightStickX),
      const RawAxisInput(4),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.rightStickY),
      const RawAxisInput(5),
    );
  });

  testWidgets('Test draft returns to Map without writing', (tester) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.byKey(const ValueKey<String>('control-faceSouth')));
    await tester.tap(find.text('Change'));
    await tester.pump();
    _button(provider, 2);
    await tester.pump();
    await tester.tap(find.text('Test draft'));
    await tester.pump();
    _button(provider, 2, release: false);
    await tester.pump();
    expect(find.textContaining('Bottom face button pressed'), findsOneWidget);
    await tester.tap(find.text('Back to mapping'));
    await tester.pump();
    expect(
      find.textContaining('Bottom face button · Button 3'),
      findsOneWidget,
    );
    expect(repo.saves, 0);
  });

  testWidgets('dirty dismissal offers Save, Discard, and Continue', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.text('Change'));
    await tester.pump();
    _button(provider, 1);
    await tester.pump();
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pumpAndSettle();

    expect(find.text('Save controller changes?'), findsOneWidget);
    expect(find.text('Save changes'), findsOneWidget);
    expect(find.text('Discard'), findsOneWidget);
    expect(find.text('Continue editing'), findsOneWidget);
    await tester.tap(find.text('Continue editing'));
    await tester.pumpAndSettle();
    expect(find.text('Save controller changes?'), findsNothing);
    expect(repo.saves, 0);
  });

  testWidgets('dirty dismissal Save atomically writes the draft', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await openMap(tester);
    await tester.tap(find.text('Change'));
    await tester.pump();
    _button(provider, 1);
    await tester.pump();
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Save changes'));
    await tester.pumpAndSettle();
    expect(repo.saves, 1);
  });

  testWidgets('Use detected explicitly saves the exact detected mapping', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: RawButtonInput(3),
      });
    await pump(
      tester,
      repository: repo,
      descriptor: _descriptor(
        detectedMapping: '$_guid,Pad,a:b0,b:b1,crc:1234,',
      ),
    );
    expect(find.text('Use detected map'), findsOneWidget);
    await tester.tap(find.text('Use detected map'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Use detected'));
    await tester.pumpAndSettle();

    expect(repo.saves, 1);
    expect(repo.resets, 0);
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.faceSouth),
      const RawButtonInput(0),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.faceEast),
      const RawButtonInput(1),
    );
    expect(repo.saved!.bindings, hasLength(2));
    expect(find.text('Use detected map'), findsNothing);
    expect(
      find.bySemanticsLabel(RegExp('Bottom face button.*Mapped')),
      findsOneWidget,
    );
    await tester.tap(find.text('Map controls'));
    await tester.pump();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey<String>('selected-binding')))
          .data,
      contains('Button 1'),
    );
    expect(
      tester
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Save'))
          .onPressed,
      isNull,
    );
  });

  testWidgets('Remove custom resets only when no detected mapping exists', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: RawButtonInput(3),
      });
    await pump(tester, repository: repo);

    expect(find.text('Remove custom map'), findsOneWidget);
    await tester.tap(find.text('Remove custom map'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Remove map'));
    await tester.pumpAndSettle();

    expect(repo.resets, 1);
    expect(repo.saves, 0);
    expect(find.text('Remove custom map'), findsNothing);
  });

  testWidgets('detected map with no stored row offers explicit activation', (
    tester,
  ) async {
    final repo = _Repository();
    await pump(
      tester,
      repository: repo,
      descriptor: _descriptor(
        detectedMapping: '$_guid,Pad,a:b0,b:b1,crc:1234,',
      ),
    );

    expect(repo.finds, 1);
    expect(repo.saves, 0);
    expect(repo.resets, 0);
    expect(find.text('Use detected map'), findsOneWidget);
    await tester.tap(find.text('Use detected map'));
    await tester.pumpAndSettle();
    expect(find.text('Cancel'), findsOneWidget);
    await tester.tap(find.text('Use detected'));
    await tester.pumpAndSettle();

    expect(repo.saves, 1);
    expect(repo.resets, 0);
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.faceSouth),
      const RawButtonInput(0),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.faceEast),
      const RawButtonInput(1),
    );
    expect(repo.saved!.bindings, hasLength(2));
    expect(find.text('Use detected map'), findsNothing);
  });

  testWidgets(
    'stored map equal to detected stays persisted without a reset offer or write',
    (tester) async {
      final repo = _Repository()
        ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.faceSouth: RawButtonInput(0),
          CanonicalGamepadControl.faceEast: RawButtonInput(1),
        });
      await pump(
        tester,
        repository: repo,
        descriptor: _descriptor(
          detectedMapping: '$_guid,Pad,a:b0,b:b1,crc:1234,',
        ),
      );

      expect(repo.finds, 1);
      expect(repo.saves, 0);
      expect(repo.resets, 0);
      expect(find.text('Use detected map'), findsNothing);
      expect(repo.value, isNotNull);
    },
  );

  testWidgets('custom map differing from detected offers Use detected', (
    tester,
  ) async {
    final repo = _Repository()
      ..value = _custom(const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: RawButtonInput(3),
      });
    await pump(
      tester,
      repository: repo,
      descriptor: _descriptor(detectedMapping: '$_guid,Pad,a:b0,crc:1234,'),
    );

    expect(find.text('Use detected map'), findsOneWidget);
    expect(repo.saves, 0);
    expect(repo.resets, 0);
  });

  testWidgets('empty map offers same-surface guided bootstrap', (tester) async {
    final (_, provider) = await pump(tester);
    await tester.tap(find.text('Guided setup'));
    await tester.pump();
    expect(find.textContaining('press Bottom face button'), findsOneWidget);
    _button(provider, 0);
    await tester.pump();
    expect(find.textContaining('press Right face button'), findsOneWidget);

    provider.raw.add(
      const RawControllerButtonEvent(
        controllerId: 'target',
        timestampMillis: 8,
        button: 8,
        pressed: true,
      ),
    );
    await tester.pump(const Duration(milliseconds: 1201));
    expect(find.textContaining('press Left face button'), findsOneWidget);
  });

  testWidgets('guided setup captures and skips sticks as atomic pairs', (
    tester,
  ) async {
    final (repo, provider) = await pump(tester);
    await tester.tap(find.text('Guided setup'));
    await tester.pump();

    for (var button = 0; button < 15; button++) {
      _button(provider, button);
      await tester.pump();
    }
    expect(
      find.textContaining('stick you want to use for Left stick to the right'),
      findsOneWidget,
    );

    _axis(provider, 0, 0);
    _axis(provider, 1, 0);
    _axis(provider, 0, 22000);
    _axis(provider, 1, 23000);
    await tester.pump();
    expect(find.textContaining('press Left stick press'), findsOneWidget);

    _button(provider, 15);
    await tester.pump();
    expect(
      find.textContaining('stick you want to use for Right stick to the right'),
      findsOneWidget,
    );

    _axis(provider, 2, 0);
    _axis(provider, 3, 0);
    _axis(provider, 2, 22000);
    _button(provider, 0, release: false);
    await tester.pump(const Duration(milliseconds: 1201));
    expect(find.textContaining('press Right stick press'), findsOneWidget);

    _button(provider, 1, release: false);
    await tester.pump(const Duration(milliseconds: 1201));
    expect(find.textContaining('Guided setup finished'), findsOneWidget);
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickX),
      const RawAxisInput(0),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickY),
      const RawAxisInput(1),
    );
    expect(
      repo.saved!.inputFor(CanonicalGamepadControl.leftStickPress),
      const RawButtonInput(15),
    );
    expect(repo.saved!.inputFor(CanonicalGamepadControl.rightStickX), isNull);
    expect(repo.saved!.inputFor(CanonicalGamepadControl.rightStickY), isNull);
  });

  testWidgets('contains diagram at 1280x720, 2x text, and reduced motion', (
    tester,
  ) async {
    await pump(tester, textScale: 2, reducedMotion: true);
    expect(tester.takeException(), isNull);
    expect(
      find.bySemanticsLabel('Neutral Ottercade controller map'),
      findsOneWidget,
    );
    expect(find.text('Test controller'), findsOneWidget);
  });
}
