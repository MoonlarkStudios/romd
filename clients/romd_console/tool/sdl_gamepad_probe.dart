import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_gamepad_enumerator.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_native_dependency.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';

Future<void> main(List<String> args) async {
  final options = _ProbeOptions.parse(args);
  if (options.help) {
    _printUsage();
    return;
  }

  try {
    final resolved = await _openSdlApi(options);
    final pads = await SdlGamepadEnumerator(resolved.api).listGamepads();

    stdout.writeln('SDL3 library: ${resolved.libraryPath}');
    if (options.transportLabel != null) {
      stdout.writeln('Transport label: ${options.transportLabel}');
    }
    _printPads(pads);
    if (resolved.api case final SdlRawInputApi rawApi) {
      _printRawControllers(
        await SdlRawControllerEnumerator(rawApi).listControllers(),
      );
    }
  } on Object catch (e) {
    stderr.writeln('SDL probe failed: $e');
    exitCode = 1;
  }
}

void _printRawControllers(List<RawControllerDescriptor> controllers) {
  stdout.writeln('Raw controllers detected: ${controllers.length}');
  for (final controller in controllers) {
    final capabilities = controller.capabilities;
    stdout.writeln(
      'raw controllerId=${controller.controllerId} '
      'name="${controller.displayName}" '
      'platform="${controller.sdlPlatform}" '
      'sdlGuid=${controller.sdlGuid ?? '<none>'} '
      'mapped=${controller.isMappedGamepad} '
      'axes=${capabilities.axisCount} '
      'buttons=${capabilities.buttonCount} '
      'hats=${capabilities.hatCount}',
    );
    stdout.writeln('SDL mapping: ${controller.sdlMapping ?? '<none>'}');
  }
}

Future<({SdlGamepadApi api, String libraryPath})> _openSdlApi(
  _ProbeOptions options,
) async {
  final overridePath =
      options.sdl3Path ?? Platform.environment['ROMD_SDL3_PATH'];
  if (overridePath != null && overridePath.isNotEmpty) {
    return (
      api: FfiSdlGamepadApi.open(overridePath),
      libraryPath: overridePath,
    );
  }

  const environment = SystemSdlNativeEnvironment();
  final downloads = HttpDownloadClient();
  try {
    final result = await SdlNativeDependencyProvisioner(
      artifactResolver: const SdlNativeCatalog().sdl3,
      installer: const MacosSdlNativeInstaller(
        commandRunner: SystemSdlNativeCommandRunner(),
      ),
      downloadClient: downloads,
      nativeRoot: options.nativeRoot,
      operatingSystem: environment.operatingSystem,
      architecture: environment.architecture,
    ).ensureSdl3();
    return switch (result) {
      SdlNativeReady(:final libraryPath) => (
        api: FfiSdlGamepadApi.open(libraryPath),
        libraryPath: libraryPath,
      ),
      SdlNativeUnavailable(:final message) => throw StateError(message),
      SdlNativeInstallFailed(:final message) => throw StateError(message),
    };
  } finally {
    downloads.close();
  }
}

void _printPads(List<ConnectedGamepad> pads) {
  stdout.writeln('Controllers detected: ${pads.length}');
  if (pads.isEmpty) {
    stdout.writeln('No controllers detected by SDL3.');
    return;
  }

  for (final pad in pads) {
    final serial = pad.identity.serial;
    stdout.writeln(
      'controller order=${pad.order} providerId=${pad.id} '
      'name="${pad.identity.displayName}" '
      'sdlGuid=${pad.identity.sdlGuid ?? '<none>'} '
      'serial=${_redactedSerial(serial)} '
      'exactIdentity=${pad.identity.hasExactIdentity}',
    );
  }

  final dualSense = pads
      .where(
        (pad) => pad.identity.displayName.toLowerCase().contains('dualsense'),
      )
      .toList();
  stdout.writeln('DualSense controllers detected: ${dualSense.length}');
  if (dualSense.isNotEmpty) {
    final guids = {
      for (final pad in dualSense)
        if (pad.identity.sdlGuid case final guid?) guid,
    };
    final serialHashes = {
      for (final pad in dualSense)
        if (pad.identity.serial case final serial?)
          sha256.convert(utf8.encode(serial)).toString(),
    };
    stdout.writeln('DualSense unique GUIDs: ${guids.length}');
    stdout.writeln(
      'DualSense unique serial fingerprints: ${serialHashes.length}',
    );
  }
}

String _redactedSerial(String? serial) {
  if (serial == null || serial.isEmpty) {
    return '<none>';
  }
  final hash = sha256.convert(utf8.encode(serial)).toString().substring(0, 12);
  return 'sha256:$hash length=${serial.length}';
}

void _printUsage() {
  stdout.writeln('Usage: dart run tool/sdl_gamepad_probe.dart [options]');
  stdout.writeln();
  stdout.writeln('Options:');
  stdout.writeln('  --native-root <path>  Managed SDL3 install root.');
  stdout.writeln(
    '  --sdl3-path <path>    Load SDL3 from an explicit library path.',
  );
  stdout.writeln(
    '  --transport <label>   Label this run, e.g. usb or bluetooth.',
  );
  stdout.writeln('  --help                Show this help.');
}

final class _ProbeOptions {
  const _ProbeOptions({
    required this.nativeRoot,
    this.sdl3Path,
    this.transportLabel,
    this.help = false,
  });

  final Directory nativeRoot;
  final String? sdl3Path;
  final String? transportLabel;
  final bool help;

  static _ProbeOptions parse(List<String> args) {
    var nativeRoot = Directory(
      p.join(Directory.current.path, '.data', 'romd', 'native'),
    );
    String? sdl3Path;
    String? transportLabel;
    var help = false;

    for (var i = 0; i < args.length; i++) {
      switch (args[i]) {
        case '--help' || '-h':
          help = true;
        case '--native-root':
          i++;
          if (i >= args.length) {
            throw const FormatException('--native-root requires a path');
          }
          nativeRoot = Directory(args[i]);
        case '--sdl3-path':
          i++;
          if (i >= args.length) {
            throw const FormatException('--sdl3-path requires a path');
          }
          sdl3Path = args[i];
        case '--transport':
          i++;
          if (i >= args.length) {
            throw const FormatException('--transport requires a label');
          }
          transportLabel = args[i];
        default:
          throw FormatException('Unknown argument: ${args[i]}');
      }
    }

    return _ProbeOptions(
      nativeRoot: nativeRoot,
      sdl3Path: sdl3Path,
      transportLabel: transportLabel,
      help: help,
    );
  }
}
