import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/widgets/ottercade_brand.dart';

final _kit = Directory('../../brand/ottercade');
const _android = 'android/app/src/main/res';
const _macIcons = 'macos/Runner/Assets.xcassets/AppIcon.appiconset';

Uint8List _bytes(String path) => File(path).readAsBytesSync();

void _same(String placed, String canonical) {
  expect(_bytes(placed), orderedEquals(_bytes(canonical)), reason: placed);
}

void _pngSize(String path, int width) {
  final bytes = ByteData.sublistView(_bytes(path));
  expect(bytes.getUint32(0), 0x89504e47, reason: path);
  expect(bytes.getUint32(16), width, reason: path);
  expect(bytes.getUint32(20), width, reason: path);
}

Future<Uint8List> _pixels(String path) async {
  final codec = await ui.instantiateImageCodec(_bytes(path));
  final frame = await codec.getNextFrame();
  final data = await frame.image.toByteData();
  frame.image.dispose();
  codec.dispose();
  return data!.buffer.asUint8List();
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'lockup selects the supplied wordmark for the active brightness',
    (tester) async {
      for (final brightness in Brightness.values) {
        await tester.pumpWidget(
          MaterialApp(
            theme: ThemeData(brightness: brightness),
            themeAnimationDuration: Duration.zero,
            home: const OttercadeStackedLockup(
              rompSize: 96,
              wordmarkWidth: 280,
              spacing: 16,
            ),
          ),
        );
        await tester.pump();
        final image = tester.widget<Image>(
          find.descendant(
            of: find.byType(OttercadeWordmark),
            matching: find.byType(Image),
          ),
        );
        expect(
          (image.image as AssetImage).assetName,
          brightness == Brightness.dark
              ? ottercadeWordmarkCreamAsset
              : ottercadeWordmarkInkAsset,
        );
      }
    },
  );

  test('placed artwork and body fonts retain their canonical bytes', () {
    for (final pair in <String, String>{
      'ottercade_romp.png': 'mascot-soft/logo.png',
      'ottercade_wordmark_ink.png': 'wordmark-primary/logo.png',
      'ottercade_wordmark_cream.png': 'wordmark-reversed/logo.png',
    }.entries) {
      _same(
        'assets/brand/${pair.key}',
        '${_kit.path}/assets/logos/${pair.value}',
      );
    }
    for (final family in ['archivo', 'ibm-plex-mono']) {
      for (final file in Directory(
        '../../brand/romd/fonts/$family',
      ).listSync().whereType<File>()) {
        _same('assets/fonts/${file.uri.pathSegments.last}', file.path);
      }
    }
    _same(
      '$_macIcons/app_icon_1024.png',
      '${_kit.path}/assets/icons/square/icon.png',
    );
    _pngSize('linux/runner/resources/ottercade.png', 512);
  });

  test('native icons retain platform sizes and Windows size frames', () {
    for (final size in [16, 32, 64, 128, 256, 512, 1024]) {
      _pngSize('$_macIcons/app_icon_$size.png', size);
    }
    for (final pair in <String, int>{
      'mdpi': 48,
      'hdpi': 72,
      'xhdpi': 96,
      'xxhdpi': 144,
      'xxxhdpi': 192,
    }.entries) {
      _pngSize('$_android/mipmap-${pair.key}/ic_launcher.png', pair.value);
    }
    _pngSize('$_android/drawable-xxxhdpi/ottercade_launch_mark.png', 880);
    final ico = ByteData.sublistView(
      _bytes('windows/runner/resources/app_icon.ico'),
    );
    expect(ico.getUint16(0, Endian.little), 0);
    expect(ico.getUint16(2, Endian.little), 1);
    const sizes = [16, 24, 32, 48, 64, 128, 256];
    expect(ico.getUint16(4, Endian.little), sizes.length);
    for (final (index, size) in sizes.indexed) {
      final entry = 6 + index * 16;
      expect(ico.getUint8(entry), size % 256);
      expect(ico.getUint8(entry + 1), size % 256);
      final length = ico.getUint32(entry + 8, Endian.little);
      final offset = ico.getUint32(entry + 12, Endian.little);
      expect(length, greaterThan(0));
      expect(offset, greaterThanOrEqualTo(6 + sizes.length * 16));
      expect(offset + length, lessThanOrEqualTo(ico.lengthInBytes));
    }
  });

  test('adaptive art stays inside its safe area; macOS stays opaque', () async {
    const path = '$_android/drawable-xxxhdpi/ottercade_launcher_foreground.png';
    _pngSize(path, 432);
    final foreground = await _pixels(path);
    var visible = 0;
    for (var y = 0; y < 432; y++) {
      for (var x = 0; x < 432; x++) {
        if (foreground[(y * 432 + x) * 4 + 3] == 0) continue;
        visible++;
        expect(x, inInclusiveRange(84, 347));
        expect(y, inInclusiveRange(84, 347));
      }
    }
    expect(
      visible,
      greaterThan(20000),
      reason: 'The foreground must not be empty.',
    );
    for (final size in [16, 32, 64, 128, 256, 512, 1024]) {
      final square = await _pixels('$_macIcons/app_icon_$size.png');
      expect(
        Iterable<int>.generate(
          square.length ~/ 4,
        ).every((pixel) => square[pixel * 4 + 3] == 255),
        isTrue,
        reason: 'macOS owns masking ($size px).',
      );
    }
  });

  test('adaptive icon uses the approved tile independently of the splash', () {
    final tokens =
        jsonDecode(File('${_kit.path}/tokens.json').readAsStringSync())
            as Map<String, Object?>;
    final palette = tokens['palette']! as Map<String, Object?>;
    final tile = palette['tile']! as Map<String, Object?>;
    final value = tile[r'$value']! as Map<String, Object?>;
    final components = (value['components']! as List).cast<num>();
    final hex = components
        .map(
          (channel) =>
              (channel * 255).round().toRadixString(16).padLeft(2, '0'),
        )
        .join()
        .toUpperCase();
    expect(
      File('$_android/values/ottercade_brand.xml').readAsStringSync(),
      contains('<color name="ottercade_icon_background">#$hex</color>'),
    );
    expect(
      File('$_android/mipmap-anydpi-v26/ic_launcher.xml').readAsStringSync(),
      contains('@color/ottercade_icon_background'),
    );
    for (final directory in ['drawable', 'drawable-v21']) {
      expect(
        File('$_android/$directory/launch_background.xml').readAsStringSync(),
        contains('@color/ottercade_midnight'),
      );
    }
  });
}
