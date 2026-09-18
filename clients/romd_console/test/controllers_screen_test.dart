import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';
import 'package:romd_console/src/presentation/glyph_family_scope.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/controllers_screen.dart';

import 'helpers/in_memory_controller_preferences.dart';

final class _HardwareRepository implements ControllerHardwareMappingRepository {
  int finds = 0;

  @override
  Future<ControllerHardwareMapping?> find({
    required String sdlPlatform,
    required String sdlGuid,
  }) async {
    finds++;
    return null;
  }

  @override
  Future<void> reset({
    required String sdlPlatform,
    required String sdlGuid,
  }) async {}

  @override
  Future<void> save({
    required String sdlPlatform,
    required String sdlGuid,
    required String displayName,
    required CanonicalControllerMapping mapping,
  }) async {}
}

final class _RawProvider
    implements ControllerInputProvider, RawControllerInputProvider {
  const _RawProvider(this.descriptors);

  final List<RawControllerDescriptor> descriptors;

  @override
  Future<ControllerCaptureResult> acquireCapture(String controllerId) async =>
      const ControllerCaptureUnsupported();

  @override
  Stream<NormalizedGamepadEvent> events() => const Stream.empty();

  @override
  Future<List<ConnectedGamepad>> listGamepads() async => const [];

  @override
  Future<List<RawControllerDescriptor>> listRawControllers() async =>
      descriptors;

  @override
  Stream<RawControllerEvent> rawEvents() => const Stream.empty();
}

RawControllerDescriptor _rawDescriptor({
  String controllerId = 'raw-pad',
  String? guid = 'exact-guid',
}) => RawControllerDescriptor(
  controllerId: controllerId,
  displayName: 'Living Room Controller',
  sdlPlatform: 'Mac OS X',
  sdlGuid: guid,
  isMappedGamepad: guid != null,
  capabilities: const RawControllerCapabilities(
    axisCount: 6,
    buttonCount: 16,
    hatCount: 1,
  ),
  sdlMapping: guid == null
      ? null
      : '$guid,Living Room Controller,a:b0,platform:Mac OS X,',
);

void main() {
  Future<void> pumpControllers(
    WidgetTester tester, {
    required InMemoryControllerPreferences preferences,
    ControllerInputProvider controllerInputProvider = const _RawProvider([]),
    ControllerHardwareMappingRepository? hardwareMappings,
    double textScale = 1,
  }) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: MediaQueryData(textScaler: TextScaler.linear(textScale)),
          child: ControllersScreen(
            preferences: preferences,
            controllerInputProvider: controllerInputProvider,
            hardwareMappings: hardwareMappings,
          ),
        ),
      ),
    );
    await tester.pump();
    await tester.pump();
  }

  Future<void> tapVisible(WidgetTester tester, Finder finder) async {
    await tester.ensureVisible(finder);
    await tester.pump();
    await tester.tap(finder);
    await tester.pump();
  }

  testWidgets('contains device settings but no session-only Players controls', (
    tester,
  ) async {
    await pumpControllers(tester, preferences: InMemoryControllerPreferences());

    expect(find.text('Controllers'), findsOneWidget);
    expect(find.text('Connected controllers'), findsOneWidget);
    expect(find.text('Button prompts'), findsOneWidget);
    expect(find.text('CONTROLLER HARDWARE'), findsOneWidget);
    expect(find.text('Players'), findsNothing);
    expect(find.text('Change player order'), findsNothing);
    expect(find.text('Reset player order'), findsNothing);
    expect(find.text('Generic / Steam Deck'), findsNothing);
  });

  testWidgets('connected model opens the unified Test and Map workspace', (
    tester,
  ) async {
    final repository = _HardwareRepository();
    await pumpControllers(
      tester,
      preferences: InMemoryControllerPreferences(),
      controllerInputProvider: _RawProvider(<RawControllerDescriptor>[
        _rawDescriptor(),
      ]),
      hardwareMappings: repository,
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      contains('exact-guid'),
    );
    await tapVisible(tester, find.text('Living Room Controller'));
    await tester.pumpAndSettle();

    expect(find.text('CONTROLLER SETUP'), findsOneWidget);
    expect(find.text('Test controller'), findsOneWidget);
    expect(find.text('Map controls'), findsOneWidget);
    expect(repository.finds, 2);
  });

  testWidgets('identity-less controller opens honestly in Test-only mode', (
    tester,
  ) async {
    final repository = _HardwareRepository();
    await pumpControllers(
      tester,
      preferences: InMemoryControllerPreferences(),
      controllerInputProvider: _RawProvider(<RawControllerDescriptor>[
        _rawDescriptor(guid: null),
      ]),
      hardwareMappings: repository,
    );

    expect(find.textContaining('Test only'), findsOneWidget);
    await tapVisible(tester, find.text('Living Room Controller'));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Mapping unavailable for this connection'),
      findsOneWidget,
    );
    final map = tester.widget<OutlinedButton>(
      find.widgetWithText(OutlinedButton, 'Map controls'),
    );
    expect(map.onPressed, isNull);
    expect(repository.finds, 0);
  });

  testWidgets('duplicate model explains why shared setup is unavailable', (
    tester,
  ) async {
    await pumpControllers(
      tester,
      preferences: InMemoryControllerPreferences(),
      controllerInputProvider: _RawProvider(<RawControllerDescriptor>[
        _rawDescriptor(controllerId: 'left'),
        _rawDescriptor(controllerId: 'right'),
      ]),
      hardwareMappings: _HardwareRepository(),
    );

    expect(find.text('Living Room Controller'), findsOneWidget);
    expect(
      find.textContaining('2 connected units share this setup'),
      findsOneWidget,
    );
    expect(
      find.textContaining('Disconnect all but one identical unit'),
      findsOneWidget,
    );
  });

  testWidgets('activating a button prompt preference persists it', (
    tester,
  ) async {
    final preferences = InMemoryControllerPreferences();
    await pumpControllers(tester, preferences: preferences);

    await tapVisible(tester, find.text('Button prompts'));
    await tapVisible(tester, find.text('PlayStation-style'));

    expect(
      preferences.preferences.templateId,
      BuiltinControllerTemplates.playstationStyle.id,
    );
  });

  testWidgets('selecting prompts updates the shared glyph family', (
    tester,
  ) async {
    final family = ValueNotifier<GlyphFamily>(GlyphFamily.generic);
    addTearDown(family.dispose);
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: GlyphFamilyScope(
          notifier: family,
          child: ControllersScreen(
            preferences: InMemoryControllerPreferences(),
            controllerInputProvider: const _RawProvider([]),
          ),
        ),
      ),
    );
    await tester.pump();

    await tapVisible(tester, find.text('Button prompts'));
    await tapVisible(tester, find.text('Nintendo-style'));
    expect(family.value, GlyphFamily.nintendo);
  });

  testWidgets('keyboard traversal reaches and activates prompt choices', (
    tester,
  ) async {
    final preferences = InMemoryControllerPreferences();
    await pumpControllers(tester, preferences: preferences);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      contains('settings-row-Xbox-style'),
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(
      preferences.preferences.templateId,
      BuiltinControllerTemplates.playstationStyle.id,
    );
  });

  testWidgets('1280x720 at 2x text remains functional', (tester) async {
    await pumpControllers(
      tester,
      preferences: InMemoryControllerPreferences(),
      textScale: 2,
    );

    await tapVisible(tester, find.text('Button prompts'));
    await tester.ensureVisible(find.text('Keyboard'));
    await tester.pump();
    expect(tester.takeException(), isNull);
    expect(find.text('PROMPT STYLE'), findsOneWidget);
  });

  testWidgets('DismissIntent backs out of the controllers route', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => ControllersScreen(
                  preferences: InMemoryControllerPreferences(),
                  controllerInputProvider: const _RawProvider([]),
                ),
              ),
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    expect(find.byType(ControllersScreen), findsOneWidget);

    final context = tester.binding.focusManager.primaryFocus?.context;
    expect(context, isNotNull);
    Actions.maybeInvoke(context!, const DismissIntent());
    await tester.pumpAndSettle();

    expect(find.byType(ControllersScreen), findsNothing);
  });
}
