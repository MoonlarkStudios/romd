import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';

const Key consoleGoldenSurfaceKey = ValueKey<String>('console-golden-surface');

void configureConsoleGoldenTests() {
  setUpAll(ConsoleGoldenHarness.loadProductionFonts);
}

/// Deterministic rendering contract for console screenshot baselines.
abstract final class ConsoleGoldenHarness {
  static const Size canvasSize = Size(1280, 720);
  static final DateTime fixedNow = DateTime.utc(2026, 7, 19, 20, 14);

  static Future<void> loadProductionFonts() async {
    final archivo = FontLoader('Archivo')
      ..addFont(rootBundle.load('assets/fonts/Archivo-Variable.ttf'));
    final plexMono = FontLoader('IBM Plex Mono')
      ..addFont(rootBundle.load('assets/fonts/IBMPlexMono-Regular.ttf'))
      ..addFont(rootBundle.load('assets/fonts/IBMPlexMono-Medium.ttf'))
      ..addFont(rootBundle.load('assets/fonts/IBMPlexMono-SemiBold.ttf'));
    final materialIcons = FontLoader('MaterialIcons')
      ..addFont(rootBundle.load('fonts/MaterialIcons-Regular.otf'));
    await Future.wait(<Future<void>>[
      archivo.load(),
      plexMono.load(),
      materialIcons.load(),
    ]);
  }

  static Future<void> pump(
    WidgetTester tester, {
    required Widget child,
    Size size = canvasSize,
    ThemeData? theme,
  }) async {
    final activeTheme = theme ?? RomdSkins.baselineDark();
    tester.view
      ..devicePixelRatio = 1
      ..physicalSize = size;
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);

    final previousHighlightStrategy = FocusManager.instance.highlightStrategy;
    FocusManager.instance.highlightStrategy =
        FocusHighlightStrategy.alwaysTraditional;
    addTearDown(
      () => FocusManager.instance.highlightStrategy = previousHighlightStrategy,
    );

    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        locale: const Locale('en', 'US'),
        theme: activeTheme,
        darkTheme: activeTheme,
        themeMode: ThemeMode.dark,
        themeAnimationDuration: Duration.zero,
        builder: (context, appChild) => MediaQuery(
          data: MediaQuery.of(context).copyWith(
            size: size,
            devicePixelRatio: 1,
            textScaler: TextScaler.noScaling,
            platformBrightness: activeTheme.brightness,
            disableAnimations: true,
            accessibleNavigation: false,
            boldText: false,
            highContrast: false,
            alwaysUse24HourFormat: false,
          ),
          child: appChild!,
        ),
        home: RepaintBoundary(
          key: consoleGoldenSurfaceKey,
          child: SizedBox.fromSize(size: size, child: child),
        ),
      ),
    );
    // Never wait for the graph to become globally idle: focus cursors and
    // intentionally repeating artwork can keep scheduling frames. Advance to
    // one explicit, reproducible frame after finite entrance/focus animations.
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    await tester.pump();
  }

  /// Creates cover art without HTTP, decoding, cache, or server variability.
  static Future<MemoryImage> createCoverImage() async {
    const size = Size(192, 256);
    final recorder = ui.PictureRecorder();
    final canvas = Canvas(recorder);
    final bounds = Offset.zero & size;
    canvas.drawRect(
      bounds,
      Paint()
        ..shader = ui.Gradient.linear(
          Offset.zero,
          size.bottomRight(Offset.zero),
          const <Color>[
            Color(0xff153e5c),
            Color(0xff4fe3b0),
            Color(0xffe6c074),
          ],
          const <double>[0, 0.62, 1],
        ),
    );
    canvas.drawCircle(
      const Offset(96, 104),
      54,
      Paint()
        ..color = const Color(0x99ffffff)
        ..style = PaintingStyle.stroke
        ..strokeWidth = 8,
    );
    canvas.drawRect(
      const Rect.fromLTWH(32, 184, 128, 18),
      Paint()..color = const Color(0xcc071016),
    );
    canvas.drawRect(
      const Rect.fromLTWH(52, 214, 88, 8),
      Paint()..color = const Color(0x99ffffff),
    );

    final picture = recorder.endRecording();
    final image = await picture.toImage(
      size.width.toInt(),
      size.height.toInt(),
    );
    final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
    picture.dispose();
    image.dispose();
    if (bytes == null) {
      throw StateError('Could not encode the deterministic cover fixture.');
    }
    return MemoryImage(bytes.buffer.asUint8List());
  }
}
