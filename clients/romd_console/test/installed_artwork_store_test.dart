import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_artwork.dart';
import 'package:romd_console/src/play/content/data/installed_artwork_store.dart';

void main() {
  late Directory directory;
  late HttpServer server;
  late Uri origin;
  var requests = 0;
  setUp(() async {
    directory = await Directory.systemTemp.createTemp('romd-artwork-test-');
    server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    origin = Uri.parse('http://127.0.0.1:${server.port}');
    requests = 0;
    server.listen((request) async {
      requests++;
      if (request.uri.path == '/redirect') {
        request.response.statusCode = HttpStatus.found;
        request.response.headers.set(HttpHeaders.locationHeader, '/image');
      } else {
        request.response.headers.contentType = ContentType('image', 'png');
        request.response.add([1, 2, 3, 4]);
      }
      await request.response.close();
    });
  });
  tearDown(() async {
    await server.close(force: true);
    await directory.delete(recursive: true);
  });
  ConsoleArtwork image({
    String version = 'v1',
    String path = '/image',
    String role = 'Poster',
  }) => ConsoleArtwork(
    role: role,
    url: origin.resolve(path),
    fit: 'Contain',
    contentVersion: version,
    assetId: 'asset',
    width: 600,
    height: 900,
    originalWidth: 600,
    originalHeight: 900,
    fallbackReason: 'None',
    variants: const [],
  );

  test(
    'retains only selected delivery images and survives restart without server',
    () async {
      final store = InstalledArtworkStore(directory);
      final result = await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [
          image(),
          image(role: 'Hero', path: '/hero'),
        ],
      );
      expect(result.length, 2);
      expect(requests, 2);
      await server.close(force: true);
      final restarted = InstalledArtworkStore(
        directory,
      ).read('server-a', 'release');
      expect(restarted.length, 2);
      expect(restarted.first.contain, isTrue);
      expect(await File.fromUri(restarted.first.url!).readAsBytes(), [
        1,
        2,
        3,
        4,
      ]);
      expect(store.read('server-b', 'release'), isEmpty);
      expect(store.read('server-a', 'other-release'), isEmpty);
    },
  );
  test(
    'deduplicates immutable versions and replaces old retained versions',
    () async {
      final store = InstalledArtworkStore(directory);
      final first = await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [image()],
      );
      await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [image()],
      );
      expect(requests, 1);
      final updated = await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [image(version: 'v2')],
      );
      expect(requests, 2);
      expect(updated.single.url, isNot(first.single.url));
      expect(File.fromUri(first.single.url!).existsSync(), isFalse);
    },
  );
  test(
    'redirect failure preserves the previously usable local selection',
    () async {
      final store = InstalledArtworkStore(directory);
      final first = await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [image()],
      );
      final failed = await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [image(path: '/redirect')],
      );
      expect(requests, 2);
      expect(failed.single.url, first.single.url);
    },
  );
  test('removal clears only the matching server and release', () async {
    final store = InstalledArtworkStore(directory);
    await store.retain(
      server: 'server-a',
      release: 'release',
      origin: origin,
      artwork: [image()],
    );
    await store.retain(
      server: 'server-b',
      release: 'release',
      origin: origin,
      artwork: [image()],
    );
    expect(await store.remove('server-a', 'release'), isTrue);
    expect(store.read('server-a', 'release'), isEmpty);
    expect(store.read('server-b', 'release'), hasLength(1));
  });
  test(
    'mixed replacement success preserves failed role and explicit null removes it',
    () async {
      final store = InstalledArtworkStore(directory);
      final first = await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [
          image(),
          image(role: 'Hero', path: '/hero'),
        ],
      );
      final mixed = await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [
          image(version: 'v2'),
          image(role: 'Hero', version: 'v2', path: '/redirect'),
        ],
      );
      expect(mixed.forRole('Poster')?.contentVersion, 'v2');
      expect(mixed.forRole('Hero')?.url, first.forRole('Hero')?.url);
      expect(File.fromUri(first.forRole('Hero')!.url!).existsSync(), isTrue);
      final removed = await store.retain(
        server: 'server-a',
        release: 'release',
        origin: origin,
        artwork: [
          image(version: 'v2'),
          const ConsoleArtwork(
            role: 'Hero',
            url: null,
            fit: 'Cover',
            contentVersion: null,
            assetId: null,
            width: null,
            height: null,
            originalWidth: null,
            originalHeight: null,
            fallbackReason: 'NoArtwork',
            variants: [],
          ),
        ],
      );
      expect(removed.forRole('Hero'), isNull);
      expect(File.fromUri(first.forRole('Hero')!.url!).existsSync(), isFalse);
    },
  );
}
