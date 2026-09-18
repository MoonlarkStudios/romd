import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/reference_catalog.dart';
import 'package:romd_console/src/presentation/reference_catalog_scope.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/platform_corner_mark.dart';
import 'package:romd_console/src/presentation/widgets/platform_logo_resolver.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

final class FakeCatalogClient implements ReferenceCatalogApiClient {
  FakeCatalogClient(this.catalog, this.bytes);
  ReferenceCatalog catalog;
  Uint8List bytes;
  bool offline = false;
  ReferenceCatalog? afterExpiry;
  int catalogReads = 0;
  bool expireAssets = false;
  Completer<ReferenceCatalog?>? pending;
  @override
  Future<ReferenceCatalog?> getReferenceCatalog(
    String token, {
    String? revision,
  }) async {
    catalogReads++;
    if (pending case final wait?) return wait.future;
    if (offline) throw const SocketException('offline');
    return revision == catalog.revision ? null : catalog;
  }

  @override
  Future<Uint8List> getReferenceAsset(String url) async {
    if (expireAssets) {
      if (afterExpiry case final next?) {
        catalog = next;
        afterExpiry = null;
        expireAssets = false;
      }
      throw const ReferenceAssetUnavailable();
    }
    return bytes;
  }
}

ReferenceCatalog snapshot(
  String revision,
  String hash, {
  String label = 'Future system',
}) => ReferenceCatalog({
  'schemaVersion': 1,
  'builtInVersion': 1,
  'revision': revision * 64,
  'systems': [
    <String, dynamic>{
      'key': 'future-system',
      'name': label,
      'compactLabel': 'FUT',
      'icon': {
        'url': '/icon',
        'sha256': hash,
        'contentType': 'image/png',
        'monochrome': false,
      },
    },
  ],
  'ratings': <Object>[],
  'ratingBoards': <Object>[],
});

void main() {
  late Directory root;
  setUp(() async {
    root = await Directory.systemTemp.createTemp('romd-reference-');
  });
  tearDown(() async {
    await root.delete(recursive: true);
  });

  test(
    'expired artwork retries current once and commits a complete revision',
    () async {
      final bytes = Uint8List.fromList([1, 2, 3]);
      final hash = sha256.convert(bytes).toString();
      final client = FakeCatalogClient(snapshot('a', hash), bytes)
        ..expireAssets = true
        ..afterExpiry = snapshot('b', hash, label: 'Updated');
      final controller = ReferenceCatalogController(root)
        ..activate('server', client);
      await controller.refresh('token');
      expect(client.catalogReads, 2);
      expect(controller.catalog!.revision, 'b' * 64);
      expect(
        (await ReferenceCatalogCache(root, 'server').load())!.revision,
        'b' * 64,
      );
      controller.dispose();
    },
  );

  test(
    'unavailable current artwork retains offline cache without retry loop',
    () async {
      final bytes = Uint8List.fromList([1, 2, 3]);
      final hash = sha256.convert(bytes).toString();
      final client = FakeCatalogClient(snapshot('a', hash), bytes);
      final controller = ReferenceCatalogController(root)
        ..activate('server', client);
      await controller.refresh('token');
      client.catalog = snapshot('b', 'c' * 64);
      client.expireAssets = true;
      await controller.refresh('token');
      expect(client.catalogReads, 3);
      expect(controller.catalog!.revision, 'a' * 64);
      expect(
        (await ReferenceCatalogCache(root, 'server').load())!.revision,
        'a' * 64,
      );
      controller.dispose();
    },
  );

  test(
    'unfamiliar identities and artwork survive a fresh offline client',
    () async {
      final bytes = Uint8List.fromList([1, 2, 3]);
      final hash = sha256.convert(bytes).toString();
      final client = FakeCatalogClient(snapshot('a', hash), bytes);
      final online = ReferenceCatalogController(root)
        ..activate('server-a', client);
      await online.refresh('memory-only-token');
      expect(online.catalog!.system('future-system')!['compactLabel'], 'FUT');
      online.dispose();
      client.offline = true;
      final offline = ReferenceCatalogController(root)
        ..activate('server-a', client);
      await offline.refresh('memory-only-token');
      expect(
        offline.catalog!.system('future-system')!['name'],
        'Future system',
      );
      expect(
        offline
            .resolve(
              platformId: 'future-system',
              platformName: '',
              shortCode: '',
            )!
            .cacheKey,
        hash,
      );
      offline.dispose();
      final other = ReferenceCatalogCache(root, 'server-b');
      expect(await other.load(), isNull);
    },
  );

  testWidgets(
    'cached unknown-system labels and artwork render after an offline restart',
    (tester) async {
      late ReferenceCatalogController offline;
      await tester.runAsync(() async {
        final bytes = await File(
          '../../reference-data/assets/presentation/platforms/snes.png',
        ).readAsBytes();
        final hash = sha256.convert(bytes).toString();
        final client = FakeCatalogClient(
          snapshot('a', hash, label: 'Unfamiliar system'),
          bytes,
        );
        final online = ReferenceCatalogController(root)
          ..activate('server', client);
        await online.refresh('token');
        online.dispose();
        client.offline = true;
        offline = ReferenceCatalogController(root)..activate('server', client);
        await offline.refresh('token');
      });
      addTearDown(offline.dispose);
      await tester.pumpWidget(const MaterialApp(home: SizedBox()));
      await tester.runAsync(
        () => precacheImage(
          offline
              .resolve(
                platformId: 'future-system',
                platformName: '',
                shortCode: '',
              )!
              .image,
          tester.element(find.byType(SizedBox).first),
        ),
      );
      await tester.pumpWidget(
        ReferenceCatalogScope(
          controller: offline,
          child: PlatformLogoResolverScope(
            resolver: offline,
            child: MaterialApp(
              theme: RomdSkins.baselineDark(),
              home: Builder(
                builder: (context) {
                  final presentation = platformPresentationFor(
                    context,
                    platformId: 'future-system',
                    platformName: 'Fallback',
                  );
                  return Column(
                    children: [
                      Text(presentation.label),
                      Text(presentation.shortCode),
                      PlatformCornerMark(
                        platformId: 'future-system',
                        platformName: presentation.label,
                        shortCode: presentation.shortCode,
                      ),
                    ],
                  );
                },
              ),
            ),
          ),
        ),
      );
      await tester.pump();
      expect(find.text('Unfamiliar system'), findsOneWidget);
      expect(find.text('FUT'), findsOneWidget);
      expect(tester.widget<Image>(find.byType(Image)).image, isA<FileImage>());
      expect(tester.takeException(), isNull);
    },
  );

  test(
    'hidden artwork remains hidden offline even when its old file is cached',
    () async {
      final bytes = Uint8List.fromList([1]);
      final hash = sha256.convert(bytes).toString();
      final client = FakeCatalogClient(snapshot('a', hash), bytes);
      final controller = ReferenceCatalogController(root)
        ..activate('server', client);
      await controller.refresh('token');
      client.catalog = snapshot('b', hash)..systems.single['icon'] = null;
      await controller.refresh('token');
      controller.dispose();
      client.offline = true;
      final offline = ReferenceCatalogController(root)
        ..activate('server', client);
      await offline.refresh('token');
      expect(offline.catalog!.revision, 'b' * 64);
      expect(
        offline.resolve(
          platformId: 'future-system',
          platformName: 'Super Nintendo',
          shortCode: 'SNES',
        ),
        isNull,
      );
      expect(
        ReferenceCatalogCache(root, 'server').asset(hash).existsSync(),
        isTrue,
      );
      offline.dispose();
    },
  );

  test('server switching does not wait for an old in-flight request', () async {
    final bytes = Uint8List.fromList([1]);
    final hash = sha256.convert(bytes).toString();
    final old = FakeCatalogClient(snapshot('a', hash), bytes)
      ..pending = Completer<ReferenceCatalog?>();
    final next = FakeCatalogClient(
      snapshot('b', hash, label: 'Other server'),
      bytes,
    );
    final controller = ReferenceCatalogController(root)..activate('old', old);
    final oldRefresh = controller.refresh('old-token');
    await Future<void>.delayed(Duration.zero);
    controller.activate('next', next);
    await controller.refresh('next-token');
    expect(
      controller.catalog!.system('future-system')!['name'],
      'Other server',
    );
    old.pending!.complete(old.catalog);
    await oldRefresh;
    expect(controller.catalog!.revision, 'b' * 64);
    controller.dispose();
  });

  test('malformed runtime entries fail with a recoverable format error', () {
    expect(
      () => ReferenceCatalog({'schemaVersion': 1, 'revision': 42}),
      throwsFormatException,
    );
    expect(
      () => ReferenceCatalog({
        'schemaVersion': 1,
        'revision': 'a' * 64,
        'systems': [false],
        'ratings': <Object>[],
        'ratingBoards': <Object>[],
      }),
      throwsFormatException,
    );
  });

  test('bad asset never replaces the last complete snapshot', () async {
    final bytes = Uint8List.fromList([1]);
    final hash = sha256.convert(bytes).toString();
    final cache = ReferenceCatalogCache(root, 'server');
    final client = FakeCatalogClient(snapshot('a', hash), bytes);
    await cache.save(client.catalog, client);
    client.catalog = snapshot('b', 'c' * 64);
    await expectLater(
      cache.save(client.catalog, client),
      throwsFormatException,
    );
    expect((await cache.load())!.revision, 'a' * 64);
  });

  test(
    'changed server facts use the same renderer and retain immutable snapshots',
    () async {
      final bytes = Uint8List.fromList([1]);
      final hash = sha256.convert(bytes).toString();
      final client = FakeCatalogClient(snapshot('a', hash), bytes);
      final controller = ReferenceCatalogController(root)
        ..activate('server', client);
      await controller.refresh('token');
      client.catalog = snapshot('b', hash, label: 'New server label');
      await controller.refresh('token');
      expect(
        controller.catalog!.system('future-system')!['name'],
        'New server label',
      );
      final cache = ReferenceCatalogCache(root, 'server');
      expect(
        File('${cache.directory.path}/${'a' * 64}.json').existsSync(),
        isTrue,
      );
      controller.dispose();
    },
  );
}
