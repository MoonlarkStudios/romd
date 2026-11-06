import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/cover_art.dart';

void main() {
  Future<ImageProvider> providerAt(WidgetTester tester, double width) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Center(
          child: SizedBox(
            width: width,
            child: AspectRatio(
              aspectRatio: kCoverAspectRatio,
              child: CoverArt(
                coverUrl: Uri.parse('https://covers.local/ct.png'),
                platformId: 'snes',
                platformName: 'SNES',
                title: 'Chrono Trigger',
              ),
            ),
          ),
        ),
      ),
    );
    return tester.widget<Image>(find.byType(Image)).image;
  }

  test('poster slots use the standard 2:3 shape', () {
    expect(kCoverAspectRatio, 2 / 3);
  });

  test('installed artwork resolves through a local file image', () {
    final uri = File('/tmp/romd-artwork.image').uri;
    expect(artworkImageProvider(uri), isA<FileImage>());
    expect((artworkImageProvider(uri) as FileImage).file.uri, uri);
  });

  testWidgets('covers share one decode size across display contexts', (
    tester,
  ) async {
    // The rail tile (150) and the detail Hero (208) must resolve to the SAME
    // image-cache entry — cacheWidth is part of the cache key, so a
    // size-dependent decode would re-load the cover on navigation and flash
    // the placeholder plate over an image the rail already holds.
    final railProvider = await providerAt(tester, 150);
    expect(tester.widget<Image>(find.byType(Image)).fit, BoxFit.contain);
    final detailProvider = await providerAt(tester, 208);

    final railResize = railProvider as ResizeImage;
    final detailResize = detailProvider as ResizeImage;
    expect(railResize.width, detailResize.width);
    expect(railResize.height, detailResize.height);
    expect(railResize.imageProvider, detailResize.imageProvider);
  });
}
