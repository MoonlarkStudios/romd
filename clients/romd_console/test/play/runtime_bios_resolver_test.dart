import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/runtime_bios_resolver.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

final class _FakeBiosCatalog implements BiosCatalog {
  _FakeBiosCatalog([this.listings = const <BiosFileListing>[]]);

  final List<BiosFileListing> listings;
  final List<String> requestedPlatforms = <String>[];

  @override
  Future<List<BiosFileListing>> biosForPlatform(
    String platformShortName,
  ) async {
    requestedPlatforms.add(platformShortName);
    return listings;
  }
}

final class _FakeDownloadClient implements DownloadClient {
  _FakeDownloadClient(this.bytesByUrl);

  final Map<Uri, List<int>> bytesByUrl;
  final List<Uri> requested = <Uri>[];

  @override
  Stream<int> download({required Uri url, required File destination}) async* {
    requested.add(url);
    final bytes = bytesByUrl[url];
    if (bytes == null) {
      throw const DownloadHttpError(404);
    }
    await destination.parent.create(recursive: true);
    await destination.writeAsBytes(bytes);
    yield bytes.length;
  }
}

const _anyPs2Bios = BiosSetRequirement(
  id: 'bios',
  displayName: 'PlayStation 2 BIOS',
  files: <BiosFileSpec>[],
  satisfaction: BiosSatisfaction.anyOne,
  acceptsAnyPlatformBios: true,
);

void main() {
  late Directory tmp;
  late Directory biosDir;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_bios_resolver_test');
    biosDir = Directory(p.join(tmp.path, 'bios'));
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test('any-platform BIOS is satisfied by an existing local file', () async {
    biosDir.createSync(recursive: true);
    File(
      p.join(biosDir.path, 'ps2-0100j-20000117.bin'),
    ).writeAsStringSync('bios');
    final catalog = _FakeBiosCatalog();
    final resolver = RuntimeBiosResolver(
      biosCatalog: catalog,
      downloadClient: _FakeDownloadClient(const <Uri, List<int>>{}),
    );

    final events = await resolver
        .resolve(
          requirement: _anyPs2Bios,
          platformShortName: 'ps2',
          biosDir: biosDir,
        )
        .toList();

    expect(events, isEmpty);
    expect(catalog.requestedPlatforms, isEmpty);
  });

  test(
    'any-platform BIOS downloads the first available platform listing',
    () async {
      final grant = Uri.parse('https://romd.example/grants/ps2-bios');
      final bytes = <int>[1, 2, 3, 4];
      final catalog = _FakeBiosCatalog(<BiosFileListing>[
        const BiosFileListing(
          biosId: 'missing',
          name: 'Unavailable PS2 BIOS',
          fileName: 'missing.bin',
          sizeBytes: 4,
          isAvailable: false,
        ),
        BiosFileListing(
          biosId: 'ps2-0100j',
          name: 'PS2 BIOS 0100J',
          fileName: 'ps2-0100j-20000117.bin',
          sizeBytes: bytes.length,
          sha1: 'aea061e6e263fdcc1c4fdbd68553ef78dae74263',
          isAvailable: true,
          downloadUrl: grant,
        ),
      ]);
      final downloads = _FakeDownloadClient(<Uri, List<int>>{grant: bytes});
      final resolver = RuntimeBiosResolver(
        biosCatalog: catalog,
        downloadClient: downloads,
      );

      final events = await resolver
          .resolve(
            requirement: _anyPs2Bios,
            platformShortName: 'ps2',
            biosDir: biosDir,
          )
          .toList();

      expect(catalog.requestedPlatforms, <String>['ps2']);
      expect(downloads.requested, <Uri>[grant]);
      expect(
        File(p.join(biosDir.path, 'ps2-0100j-20000117.bin')).readAsBytesSync(),
        bytes,
      );
      expect(
        File(
          p.join(biosDir.path, 'ps2-0100j-20000117.bin.download'),
        ).existsSync(),
        isFalse,
      );
      expect(events.single, isA<DependencyDownloading>());
      expect(
        (events.single as DependencyDownloading).label,
        'PlayStation 2 BIOS',
      );
    },
  );
}
