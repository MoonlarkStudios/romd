import 'dart:async';

import 'package:flutter/material.dart';
import 'package:path_provider/path_provider.dart';
import 'package:window_manager/window_manager.dart';

import 'src/app/romd_console_app.dart';
import 'src/config/romd_environment.dart';
import 'src/presentation/widgets/ottercade_brand.dart';

/// Opt into a fullscreen "console" presentation with
/// `--dart-define=ROMD_FULLSCREEN=true`. Defaults to windowed for development.
const bool _startFullscreen = bool.fromEnvironment('ROMD_FULLSCREEN');

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await windowManager.ensureInitialized();
  final contentBaseDir = await getApplicationSupportDirectory();
  await Future.wait(<Future<void>>[
    _warmAssetImage(ottercadeRompAsset),
    _warmAssetImage(ottercadeWordmarkCreamAsset),
  ]);

  const windowOptions = WindowOptions(
    title: 'Ottercade',
    size: Size(1280, 720),
    center: true,
    fullScreen: _startFullscreen,
  );
  final firstFlutterFrame = Completer<void>();
  final readyToShow = windowManager.waitUntilReadyToShow(
    windowOptions,
    () async {
      await firstFlutterFrame.future;
      await windowManager.show();
      await windowManager.focus();
    },
  );
  WidgetsBinding.instance.addPostFrameCallback((_) {
    if (!firstFlutterFrame.isCompleted) {
      firstFlutterFrame.complete();
    }
  });
  runApp(
    RomdConsoleApp(
      environment: RomdEnvironment.fromDefines(),
      contentBaseDir: contentBaseDir,
    ),
  );
  await readyToShow;
}

Future<void> _warmAssetImage(String assetName) {
  final ready = Completer<void>();
  final stream = AssetImage(assetName).resolve(ImageConfiguration.empty);
  late final ImageStreamListener listener;
  listener = ImageStreamListener(
    (_, _) {
      stream.removeListener(listener);
      ready.complete();
    },
    onError: (error, stackTrace) {
      stream.removeListener(listener);
      ready.completeError(error, stackTrace);
    },
  );
  stream.addListener(listener);
  return ready.future;
}
