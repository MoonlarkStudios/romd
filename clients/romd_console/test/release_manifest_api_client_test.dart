import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/release_manifest_api_client.dart';
import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/domain/rom_delivery_format.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/domain/server_bound_release_manifest.dart';
import 'package:romd_console/src/play/content/domain/release_install_plan.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';

import 'support/release_manifest_wire.dart';

void main() {
  test(
    'unavailable Original and available held CHD remain a coherent server manifest',
    () async {
      final wire = _wirePs2();
      wire['isDeliveryComplete'] = false;
      final item = _item(wire);
      item
        ..['isAvailable'] = false
        ..['sha256'] = null
        ..['contentGrant'] = null;
      final original = (item['formats']! as List<Object?>)
          .cast<Map<String, Object?>>()
          .singleWhere((format) => format['format'] == 'original');
      original
        ..['state'] = 'unsupported'
        ..['runtime'] = null
        ..['contentGrant'] = null
        ..remove('filename')
        ..remove('sizeBytes')
        ..remove('sha256')
        ..remove('md5')
        ..remove('sha1');
      final parsed = _expectSuccess(
        await _resultFor(responseBody: wire, expectedSystemKey: 'ps2'),
      );
      expect(parsed.isComplete, isTrue);
      expect(parsed.isDeliveryComplete, isFalse);
      expect(parsed.items.single.isAvailable, isFalse);
      expect(parsed.items.single.sha256, isNull);
      expect(parsed.items.single.contentGrant, isNull);
      final held = parsed.items.single.formats.singleWhere(
        (format) => format.format == RomDeliveryFormat.chd,
      );
      expect(held.state, RomDeliveryState.available);
      expect(held.contentGrant, isNotNull);
      expect(held.sha256, 'b' * 64);
    },
  );

  test(
    'derived CUE completes delivery with its own bytes and no original claim',
    () async {
      final manifest = _expectSuccess(
        await _resultFor(responseBody: _derivedWire()),
      );
      expect(manifest.isComplete, isFalse);
      expect(manifest.isDeliveryComplete, isTrue);
      final cue = manifest.items.singleWhere(
        (item) => item.provenance == ContentFileProvenance.derived,
      );
      expect(cue.relativePath, 'chrono/disc.cue');
      expect(cue.sizeBytes, 9);
      expect(cue.sha256, 'b' * 64);
      expect(cue.formats, isEmpty);
      expect(
        cue.contentGrant!.downloadUrl.path,
        '/delivery/derived-descriptor/generated.signature',
      );
      expect(manifest.runtime.minimumInstallBytes, 21);
    },
  );
  for (final defect in [
    'missing-delivery',
    'missing-provenance',
    'unknown-provenance',
    'wrong-case',
    'formats',
    'wrong-purpose',
    'preserved',
    'incomplete',
    'wrong-extension',
  ]) {
    test(
      'derived manifest rejects $defect without compatibility defaults',
      () async {
        final wire = _derivedWire();
        final cue =
            (wire['items']! as List<Object?>).last! as Map<String, Object?>;
        switch (defect) {
          case 'missing-delivery':
            wire.remove('isDeliveryComplete');
          case 'missing-provenance':
            cue.remove('provenance');
          case 'unknown-provenance':
            cue['provenance'] = 'Unknown';
          case 'wrong-case':
            cue['provenance'] = 'derived';
          case 'formats':
            cue['formats'] = [<String, Object?>{}];
          case 'wrong-purpose':
            (cue['contentGrant']! as Map<String, Object?>)['downloadUrl'] =
                '/delivery/content/generated.signature';
          case 'preserved':
            wire['isComplete'] = true;
          case 'incomplete':
            wire['isDeliveryComplete'] = false;
          case 'wrong-extension':
            cue['relativePath'] = 'chrono/disc.iso';
            (wire['runtime']! as Map<String, Object?>)['launch'] = {
              'type': 'file',
              'relativePath': 'chrono/disc.iso',
            };
        }
        final result = await _resultFor(responseBody: wire);
        expect(result, isA<ReleaseManifestFailure>());
        expect(
          (result as ReleaseManifestFailure).kind,
          ReleaseManifestFailureKind.malformedResponse,
        );
      },
    );
  }

  test('accepts the server wire shape for a single-disc PS2 release', () async {
    final manifest = _expectSuccess(
      await _resultFor(
        rawResponseBody: _serverWireSingleDiscPs2,
        expectedSystemKey: 'ps2',
      ),
    );
    final item = manifest.items.single;
    expect(item.relativePath, 'disc/game.iso');
    expect(item.sizeBytes, 12);
    expect(item.sha256, _sha256);
    expect(item.contentGrant?.downloadUrl.path, '/delivery/content/item.sig');
    expect(manifest.revision, isNull);
    expect(manifest.runtime.launch?.relativePath, 'disc/game.iso');
    expect(manifest.runtime.minimumInstallBytes, 12);

    final formats = item.formats;
    expect(formats.map((format) => format.format), <RomDeliveryFormat>[
      RomDeliveryFormat.original,
      RomDeliveryFormat.rvz,
      RomDeliveryFormat.chd,
    ]);
    final original = formats[0];
    expect(original.state, RomDeliveryState.available);
    expect(original.runtime?.launch?.relativePath, 'disc/game.iso');
    expect(original.runtime?.minimumInstallBytes, 12);
    expect(
      original.contentGrant?.downloadUrl.path,
      '/delivery/content/original.sig',
    );
    final rvz = formats[1];
    expect(rvz.state, RomDeliveryState.unsupported);
    expect(rvz.runtime, isNull);
    expect(rvz.contentGrant, isNull);
    expect(rvz.sizeBytes, isNull);
    final chd = formats[2];
    expect(chd.state, RomDeliveryState.available);
    expect(chd.relativePath, 'disc/game.chd');
    expect(chd.filename, 'game.chd');
    expect(chd.sizeBytes, 5);
    expect(chd.sha256, 'b' * 64);
    expect(chd.md5, 'c' * 32);
    expect(chd.sha1, 'd' * 40);
    expect(chd.profileVersion, 'chd-observed/v1');
    expect(chd.runtimeApplicability, <String>['pcsx2']);
    expect(chd.runtime?.contentType, 'single_rom');
    expect(chd.runtime?.packaging, 'direct_files');
    expect(chd.runtime?.launch?.type, 'file');
    expect(chd.runtime?.launch?.relativePath, 'disc/game.chd');
    expect(chd.runtime?.minimumInstallBytes, 5);
    expect(chd.contentGrant?.downloadUrl.path, '/delivery/content/chd.sig');
    expect(() => formats.clear(), throwsUnsupportedError);
    expect(() => chd.runtimeApplicability.clear(), throwsUnsupportedError);
  });

  test(
    'accepts the server wire shape when every format runtime is null',
    () async {
      final manifest = _expectSuccess(
        await _resultFor(rawResponseBody: _serverWireSnesWithFormats),
      );
      expect(manifest.items.single.relativePath, 'chrono/game.sfc');
      expect(manifest.items.single.sha256, _sha256);
      expect(manifest.runtime.launch?.relativePath, 'chrono/game.sfc');
      expect(
        manifest.items.single.formats.map((format) => format.runtime),
        everyElement(isNull),
      );
    },
  );

  // Item-3 contract: an incoherent or unusable CHD entry is retained for the
  // install plan to refuse; it never rejects the manifest or its Original.
  final incoherentChd = <String, void Function(Map<String, Object?> chd)>{
    'missing md5': (chd) => chd.remove('md5'),
    'missing sha1': (chd) => chd.remove('sha1'),
    'missing sha256': (chd) => chd.remove('sha256'),
    'zeroed digests': (chd) => chd
      ..['sha256'] = '0' * 64
      ..['md5'] = '0' * 32
      ..['sha1'] = '0' * 40,
    'null runtime': (chd) => chd['runtime'] = null,
    'runtime launch on the ISO': (chd) =>
        _launchOf(chd)['relativePath'] = 'disc/game.iso',
    'runtime size mismatch': (chd) =>
        _runtimeOf(chd)['minimumInstallBytes'] = '12',
    'unknown runtime content type': (chd) =>
        _runtimeOf(chd)['contentType'] = 'archive',
    'unknown runtime packaging': (chd) => _runtimeOf(chd)['packaging'] = 'zip',
    'unknown launch type': (chd) => _launchOf(chd)['type'] = 'folder',
    'filename mismatch': (chd) => chd['filename'] = 'other.chd',
    'non-CHD path': (chd) => chd
      ..['relativePath'] = 'disc/game.bin'
      ..['filename'] = 'game.bin',
    'zero size': (chd) => chd['sizeBytes'] = '0',
    'available without grant': (chd) => chd['contentGrant'] = null,
    'available without size': (chd) => chd.remove('sizeBytes'),
    'preparing with grant and runtime': (chd) => chd['state'] = 'preparing',
    'unsupported without facts': (chd) => chd
      ..['state'] = 'unsupported'
      ..['runtime'] = null
      ..['contentGrant'] = null
      ..remove('filename')
      ..remove('sizeBytes')
      ..remove('sha256')
      ..remove('md5')
      ..remove('sha1'),
  };
  for (final entry in incoherentChd.entries) {
    test('retains Original when CHD has ${entry.key}', () async {
      final value = _wirePs2();
      entry.value(_wireChd(value));
      final manifest = _expectSuccess(
        await _resultFor(responseBody: value, expectedSystemKey: 'ps2'),
      );
      final item = manifest.items.single;
      expect(item.relativePath, 'disc/game.iso');
      expect(item.sha256, _sha256);
      expect(item.contentGrant, isNotNull);
      expect(item.formats.last.format, RomDeliveryFormat.chd);
    });
  }

  test(
    'accepts a multi-file PS2 release whose available CHD has no runtime',
    () async {
      final value = _wirePs2();
      _runtime(value)
        ..['contentType'] = 'unknown'
        ..['minimumInstallBytes'] = '24';
      final second = _copyMap(_item(value))
        ..['relativePath'] = 'disc/readme.txt'
        ..['formats'] = <Object?>[];
      (_item(value)['formats']! as List<Object?>)
          .cast<Map<String, Object?>>()
          .forEach((format) => format['runtime'] = null);
      second
        ..['sha256'] = null
        ..['isAvailable'] = false
        ..['contentGrant'] = null;
      (value['items']! as List<Object?>).add(second);
      value['isComplete'] = false;
      value['isDeliveryComplete'] = false;
      final manifest = _expectSuccess(
        await _resultFor(responseBody: value, expectedSystemKey: 'ps2'),
      );
      expect(manifest.items, hasLength(2));
      expect(manifest.items.first.formats.last.runtime, isNull);
    },
  );

  final malformedChd = <String, void Function(Map<String, Object?> chd)>{
    'uppercase format': (chd) => chd['format'] = 'CHD',
    'unsafe path': (chd) => chd['relativePath'] = '../game.chd',
    'unsafe filename': (chd) => chd['filename'] = '../game.chd',
    'foreign grant': (chd) =>
        (chd['contentGrant']! as Map<String, Object?>)['downloadUrl'] =
            'https://foreign.test/delivery/content/chd.sig',
    'bad md5': (chd) => chd['md5'] = 'C' * 32,
    'numeric size': (chd) => chd['sizeBytes'] = 5,
    'unknown runtime key': (chd) => _runtimeOf(chd)['extra'] = true,
    'unsafe runtime launch path': (chd) =>
        _launchOf(chd)['relativePath'] = '/abs/game.chd',
    'non-object runtime': (chd) => chd['runtime'] = 'pcsx2',
  };
  for (final entry in malformedChd.entries) {
    test('rejects a CHD wire-shape violation: ${entry.key}', () async {
      final value = _wirePs2();
      entry.value(_wireChd(value));
      _expectFailure(
        await _resultFor(responseBody: value, expectedSystemKey: 'ps2'),
        ReleaseManifestFailureKind.malformedResponse,
        reason: entry.key,
      );
    });
  }

  test('rejects a duplicated CHD format entry', () async {
    final value = _wirePs2();
    (_item(value)['formats']! as List<Object?>).add(_copyMap(_wireChd(value)));
    _expectFailure(
      await _resultFor(responseBody: value, expectedSystemKey: 'ps2'),
      ReleaseManifestFailureKind.malformedResponse,
    );
  });

  test(
    'accepts delivered formats while keeping original materialization',
    () async {
      final value = _manifestWithFormats();
      final manifest = _expectSuccess(await _resultFor(responseBody: value));
      final item = manifest.items.single;
      expect(item.relativePath, 'chrono/game.iso');
      expect(item.sizeBytes, 12);
      expect(item.sha256, _sha256);
      expect(item.contentGrant?.downloadUrl.path, _grantUrl);
      expect(manifest.runtime.launch?.relativePath, 'chrono/game.iso');
      expect(manifest.runtime.minimumInstallBytes, 12);
    },
  );

  test(
    'accepts a normalized original filename distinct from the catalog path',
    () async {
      final value = _manifestWithFormats();
      _item(value)['relativePath'] = 'chrono/game.rvz';
      _launch(value)['relativePath'] = 'chrono/game.rvz';
      final original =
          (_item(value)['formats']! as List<Object?>).first!
              as Map<String, Object?>;
      original['relativePath'] = 'chrono/game.rvz';
      final manifest = _expectSuccess(await _resultFor(responseBody: value));
      expect(manifest.items.single.relativePath, 'chrono/game.rvz');
      expect(manifest.items.single.sizeBytes, 12);
      expect(manifest.items.single.sha256, _sha256);
      expect(manifest.runtime.launch?.relativePath, 'chrono/game.rvz');
    },
  );

  for (final encoded in <String>['chd', 'rvz']) {
    test(
      'fallback Original plus Unsupported $encoded only installs Original',
      () async {
        final wire = _manifestWithFormats();
        final platform = encoded == 'chd' ? 'ps2' : 'gc';
        wire['systemKey'] = platform;
        final formats = _item(wire)['formats']! as List<Object?>;
        final original = formats[0]! as Map<String, Object?>;
        original
          ..['runtime'] = null
          ..['runtimeApplicability'] = <String>[];
        formats[1] = <String, Object?>{
          'format': encoded,
          'label': encoded.toUpperCase(),
          'fileExtension': '.$encoded',
          'state': 'unsupported',
          'relativePath': 'chrono/game.$encoded',
          'profileVersion': 'unknown',
          'runtimeApplicability': <String>[],
        };
        final harness = await _ManifestHarness.start(responseBody: wire);
        addTearDown(harness.close);

        final manifest = _expectSuccess(
          await harness.getManifest(expectedSystemKey: platform),
        );
        final parsed = manifest.items.single.formats;
        final encodedFormat = RomDeliveryFormat.tryParse(encoded)!;
        final runtime = BuiltinRuntimeProfiles.all.singleWhere(
          (profile) =>
              profile.adapterId ==
              (encoded == 'chd'
                  ? BuiltinRuntimeProfiles.pcsx2AdapterId
                  : BuiltinRuntimeProfiles.dolphinAdapterId),
        );

        expect(parsed.map((format) => format.format), <RomDeliveryFormat>[
          RomDeliveryFormat.original,
          encodedFormat,
        ]);
        expect(parsed[0].state, RomDeliveryState.available);
        expect(parsed[0].contentGrant, isNotNull);
        expect(parsed[1].state, RomDeliveryState.unsupported);
        expect(parsed[1].contentGrant, isNull);
        expect(parsed[1].filename, isNull);
        expect(parsed[1].sizeBytes, isNull);
        expect(parsed[1].sha256, isNull);
        expect(
          parsed.every(
            (format) =>
                format.runtimeApplicability.isEmpty && format.runtime == null,
          ),
          isTrue,
        );
        final plan = ReleaseInstallPlan.select(manifest, runtime: runtime);
        expect(plan.format, RomDeliveryFormat.original);
        expect(
          plan.delivered.items.single.contentGrant,
          manifest.items.single.contentGrant,
        );
        expect(
          ReleaseInstallPlan.forFormat(
            manifest,
            encodedFormat,
            runtime: runtime,
          ),
          isNull,
        );
        expect(harness.requests, hasLength(1));
        expect(
          harness.requests.single.path,
          '/api/releases/${_releaseId.value}/manifest',
        );
      },
    );
  }

  test('accepts preparing and unsupported formats without grants', () async {
    for (final state in <String>['preparing', 'unsupported']) {
      final value = _manifestWithFormats();
      final formats = _item(value)['formats']! as List<Object?>;
      formats[1] = <String, Object?>{
        'format': 'rvz',
        'label': 'RVZ',
        'fileExtension': '.rvz',
        'state': state,
        'relativePath': 'chrono/game.rvz',
        'profileVersion': 'unknown',
        'runtimeApplicability': <String>[],
      };
      _expectSuccess(await _resultFor(responseBody: value));
    }
  });

  test('accepts any catalog label with an omitted or null extension', () async {
    for (final display in <Map<String, Object?>>[
      <String, Object?>{
        'label': 'Dolphin image',
        'fileExtension': '.${'r' * 15}',
      },
      <String, Object?>{'fileExtension': null},
    ]) {
      final value = _manifestWithFormats();
      final rvz =
          (_item(value)['formats']! as List<Object?>)[1]!
              as Map<String, Object?>;
      rvz.addAll(display);
      _expectSuccess(await _resultFor(responseBody: value));
    }
    final omitted = _manifestWithFormats();
    ((_item(omitted)['formats']! as List<Object?>)[1]! as Map<String, Object?>)
        .remove('fileExtension');
    _expectSuccess(await _resultFor(responseBody: omitted));
  });

  test(
    'accepts no formats for missing media and rejects incoherent original availability',
    () async {
      final unavailable = _unavailableManifest();
      _item(unavailable)['formats'] = <Object?>[];
      _expectSuccess(await _resultFor(responseBody: unavailable));
      for (final formats in <Object?>[
        null,
        <String, Object?>{},
        <Object?>[],
        <Object?>[null],
      ]) {
        final value = _manifest();
        _item(value)['formats'] = formats;
        _expectFailure(
          await _resultFor(responseBody: value),
          ReleaseManifestFailureKind.malformedResponse,
          reason: 'incoherent formats $formats',
        );
      }
      for (final change in <String, Object?>{
        'state': 'unsupported',
        'sha256': 'f' * 64,
        'relativePath': 'other/game.iso',
      }.entries) {
        final value = _manifestWithFormats();
        final original =
            (_item(value)['formats']! as List<Object?>).first!
                as Map<String, Object?>;
        original[change.key] = change.value;
        if (change.key == 'state') original.remove('contentGrant');
        _expectFailure(
          await _resultFor(responseBody: value),
          ReleaseManifestFailureKind.malformedResponse,
          reason: 'original ${change.key}',
        );
      }
    },
  );

  test('rejects malformed delivered formats atomically', () async {
    final cases = <String, Object?>{
      'unknown format': <String, Object?>{'format': 'iso'},
      'uppercase format': <String, Object?>{'format': 'RVZ'},
      'unknown state': <String, Object?>{'state': 'ready'},
      'numeric size': <String, Object?>{'sizeBytes': 5},
      'noncanonical size': <String, Object?>{'sizeBytes': '05'},
      'overflow size': <String, Object?>{'sizeBytes': '9223372036854775808'},
      'negative size': <String, Object?>{'sizeBytes': '-1'},
      'bad SHA256': <String, Object?>{'sha256': 'bad'},
      'bad MD5': <String, Object?>{'md5': 'A' * 32},
      'bad SHA1': <String, Object?>{'sha1': 'a' * 39},
      'unsafe path': <String, Object?>{'relativePath': '../game.rvz'},
      'unsafe filename': <String, Object?>{'filename': '../game.rvz'},
      'empty profile': <String, Object?>{'profileVersion': ''},
      'empty label': <String, Object?>{'label': ''},
      'blank label': <String, Object?>{'label': '  '},
      'non-string label': <String, Object?>{'label': 1},
      'null label': <String, Object?>{'label': null},
      'non-string extension': <String, Object?>{'fileExtension': 1},
      'undotted extension': <String, Object?>{'fileExtension': 'rvz'},
      'uppercase extension': <String, Object?>{'fileExtension': '.RVZ'},
      'path extension': <String, Object?>{'fileExtension': './rvz'},
      'bare dot extension': <String, Object?>{'fileExtension': '.'},
      'long extension': <String, Object?>{'fileExtension': '.${'r' * 16}'},
      'wrong applicability': <String, Object?>{
        'runtimeApplicability': 'dolphin',
      },
      'bad applicability': <String, Object?>{
        'runtimeApplicability': <Object?>[1],
      },
      'unknown nested key': <String, Object?>{'extra': true},
      'bad grant': <String, Object?>{
        'contentGrant': <String, Object?>{
          'downloadUrl': 'https://example.com/game.rvz',
          'expiresAt': '2026-07-17T12:00:00Z',
        },
      },
      'bad expiry': <String, Object?>{
        'contentGrant': <String, Object?>{
          'downloadUrl': '/delivery/content/rvz.signature',
          'expiresAt': '2026-02-30T12:00:00Z',
        },
      },
      'preparing with grant': <String, Object?>{'state': 'preparing'},
      'unsupported with grant': <String, Object?>{'state': 'unsupported'},
    };
    for (final entry in cases.entries) {
      final value = _manifestWithFormats();
      final formats = _item(value)['formats']! as List<Object?>;
      (formats[1]! as Map<String, Object?>).addAll(
        entry.value! as Map<String, Object?>,
      );
      _expectFailure(
        await _resultFor(responseBody: value),
        ReleaseManifestFailureKind.malformedResponse,
        reason: entry.key,
      );
    }
    for (final key in <String>[
      'format',
      'label',
      'state',
      'relativePath',
      'filename',
      'sizeBytes',
      'sha256',
      'contentGrant',
      'profileVersion',
      'runtimeApplicability',
    ]) {
      final value = _manifestWithFormats();
      final formats = _item(value)['formats']! as List<Object?>;
      (formats[1]! as Map<String, Object?>).remove(key);
      _expectFailure(
        await _resultFor(responseBody: value),
        ReleaseManifestFailureKind.malformedResponse,
        reason: 'missing $key',
      );
    }
    final duplicate = _manifestWithFormats();
    final formats = _item(duplicate)['formats']! as List<Object?>;
    formats.add(_copyMap(formats[1]! as Map<String, Object?>));
    _expectFailure(
      await _resultFor(responseBody: duplicate),
      ReleaseManifestFailureKind.malformedResponse,
      reason: 'duplicate format',
    );
    final mismatch = _manifestWithFormats();
    final original =
        (_item(mismatch)['formats']! as List<Object?>).first!
            as Map<String, Object?>;
    original['sizeBytes'] = '13';
    _expectFailure(
      await _resultFor(responseBody: mismatch),
      ReleaseManifestFailureKind.malformedResponse,
      reason: 'original size mismatch',
    );
  });

  test(
    'posts bodyless request and accepts the exact server-bound manifest',
    () async {
      final harness = await _ManifestHarness.start(responseBody: _manifest());
      addTearDown(harness.close);

      final result = await harness.getManifest();

      final request = harness.requests.single;
      expect(request.method, 'POST');
      expect(request.path, '/api/releases/${_releaseId.value}/manifest');
      expect(request.query, isEmpty);
      expect(request.authorization, 'Bearer access-token');
      expect(request.contentType, isNull);
      expect(request.body, isEmpty);
      final manifest = _expectSuccess(result);
      expect(manifest.serverInstanceId, _serverInstanceId);
      expect(manifest.releaseId, _releaseId);
      expect(manifest.titleId, _titleId);
      expect(manifest.systemKey, 'snes');
      expect(manifest.name, 'Chrono Trigger');
      expect(manifest.revision, 'Rev 1');
      expect(manifest.isComplete, isTrue);
      expect(manifest.runtime.contentType, 'single_rom');
      expect(manifest.runtime.packaging, 'direct_files');
      expect(manifest.runtime.minimumInstallBytes, 12);
      expect(manifest.runtime.launch?.type, 'file');
      expect(manifest.runtime.launch?.relativePath, 'chrono/game.sfc');
      expect(manifest.items, hasLength(1));
      final item = manifest.items.single;
      expect(item.relativePath, 'chrono/game.sfc');
      expect(item.role, 'rom');
      expect(item.sizeBytes, 12);
      expect(item.sha256, _sha256);
      expect(item.isAvailable, isTrue);
      final downloadUrl = item.contentGrant?.downloadUrl;
      final consumerOrigin = _originFor(harness.server);
      expect(downloadUrl, consumerOrigin.resolve(_grantUrl));
      expect(downloadUrl?.scheme, consumerOrigin.scheme);
      expect(downloadUrl?.host, consumerOrigin.host);
      expect(downloadUrl?.port, consumerOrigin.port);
      expect(downloadUrl?.path, _grantUrl);
      expect(downloadUrl?.hasQuery, isFalse);
      expect(downloadUrl?.hasFragment, isFalse);
      expect(item.contentGrant?.expiresAt.isUtc, isTrue);
      expect(() => manifest.items.add(item), throwsA(isA<UnsupportedError>()));
    },
  );

  test(
    'accepts only schema-permitted optional absence and null variants',
    () async {
      final cases = <String, Map<String, Object?>>{};

      final revisionAbsent = _manifest();
      revisionAbsent.remove('revision');
      cases['revision absent'] = revisionAbsent;

      final revisionNull = _manifest();
      revisionNull['revision'] = null;
      cases['revision null'] = revisionNull;

      final launchAbsent = _manifest();
      _runtime(launchAbsent)
        ..['contentType'] = 'unknown'
        ..remove('launch')
        ..['minimumInstallBytes'] = 0;
      launchAbsent['items'] = <Object?>[];
      launchAbsent['isDeliveryComplete'] = false;
      cases['launch absent'] = launchAbsent;

      final launchNull = _manifest();
      _runtime(launchNull)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 0;
      launchNull['items'] = <Object?>[];
      launchNull['isDeliveryComplete'] = false;
      cases['launch null'] = launchNull;

      final optionalsAbsent = _unavailableManifest();
      _item(optionalsAbsent)
        ..remove('sha256')
        ..remove('contentGrant');
      cases['item optionals absent'] = optionalsAbsent;

      final optionalsNull = _unavailableManifest();
      _item(optionalsNull)
        ..['sha256'] = null
        ..['contentGrant'] = null;
      cases['item optionals null'] = optionalsNull;

      final numericStrings = _manifest();
      _runtime(numericStrings)['minimumInstallBytes'] = '12';
      _item(numericStrings)['sizeBytes'] = '12';
      cases['int64 canonical strings'] = numericStrings;

      final emptyUnknown = _manifest();
      _runtime(emptyUnknown)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 0;
      emptyUnknown['items'] = <Object?>[];
      emptyUnknown['isDeliveryComplete'] = false;
      cases['empty unknown runtime'] = emptyUnknown;

      final multiUnknown = _manifest();
      final second = _copyMap(_item(multiUnknown));
      second['relativePath'] = 'chrono/second.sfc';
      final secondFormat =
          (second['formats']! as List<Object?>).single! as Map<String, Object?>;
      secondFormat
        ..['relativePath'] = 'chrono/second.sfc'
        ..['filename'] = 'second.sfc';
      multiUnknown['items'] = <Object?>[_copyMap(_item(multiUnknown)), second];
      _runtime(multiUnknown)
        ..['contentType'] = 'unknown'
        ..['minimumInstallBytes'] = 24;
      cases['multi-item unknown runtime with launch'] = multiUnknown;

      for (final entry in cases.entries) {
        final result = await _resultFor(responseBody: entry.value);
        expect(result, isA<ReleaseManifestSuccess>(), reason: entry.key);
      }
    },
  );

  test('rejects malformed root shapes atomically', () async {
    final cases = <String, Object?>{
      'non-object': <Object?>[],
      'missing root': _without(_manifest(), 'name'),
      'extra root': <String, Object?>{..._manifest(), 'extra': true},
      'wrong server type': _with(_manifest(), 'serverInstanceId', 1),
      'uppercase server': _with(
        _manifest(),
        'serverInstanceId',
        _serverInstanceId.value.toUpperCase(),
      ),
      'malformed release': _with(_manifest(), 'releaseId', 'bad-release'),
      'malformed title': _with(_manifest(), 'titleId', 'bad-title'),
      'malformed platform': _with(_manifest(), 'platformId', 'bad-platform'),
      'empty short name': _with(_manifest(), 'systemKey', '  '),
      'empty name': _with(_manifest(), 'name', ''),
      'wrong revision type': _with(_manifest(), 'revision', 1),
      'wrong complete type': _with(_manifest(), 'isComplete', 1),
      'wrong runtime type': _with(_manifest(), 'runtime', <Object?>[]),
      'wrong items type': _with(_manifest(), 'items', <String, Object?>{}),
    };

    for (final entry in cases.entries) {
      final result = await _resultFor(responseBody: entry.value);
      _expectFailure(
        result,
        ReleaseManifestFailureKind.malformedResponse,
        reason: entry.key,
      );
    }

    final truncated = await _resultFor(rawResponseBody: '{"serverInstanceId":');
    _expectFailure(
      truncated,
      ReleaseManifestFailureKind.malformedResponse,
      reason: 'truncated JSON',
    );
  });

  test('rejects malformed runtime and launch shapes atomically', () async {
    final cases = <String, Map<String, Object?>>{};

    for (final key in <String>[
      'contentType',
      'packaging',
      'minimumInstallBytes',
    ]) {
      final value = _manifest();
      _runtime(value).remove(key);
      cases['runtime missing $key'] = value;
    }

    final runtimeExtra = _manifest();
    _runtime(runtimeExtra)['extra'] = true;
    cases['runtime extra'] = runtimeExtra;

    final unsupportedContentType = _manifest();
    _runtime(unsupportedContentType)['contentType'] = 'archive';
    cases['unsupported content type'] = unsupportedContentType;

    final unsupportedPackaging = _manifest();
    _runtime(unsupportedPackaging)['packaging'] = 'zip';
    cases['unsupported packaging'] = unsupportedPackaging;

    for (final key in <String>['contentType', 'packaging']) {
      final wrongType = _manifest();
      _runtime(wrongType)[key] = 1;
      cases['runtime $key type'] = wrongType;
      final empty = _manifest();
      _runtime(empty)[key] = ' ';
      cases['runtime $key empty'] = empty;
    }

    for (final numeric in <Object?>[
      -1,
      '-1',
      '01',
      '+1',
      '1.0',
      1.5,
      '9223372036854775808',
    ]) {
      final value = _manifest();
      _runtime(value)['minimumInstallBytes'] = numeric;
      cases['minimum int64 $numeric'] = value;
    }

    final launchWrongType = _manifest();
    _runtime(launchWrongType)['launch'] = 'file';
    cases['launch wrong type'] = launchWrongType;

    for (final key in <String>['type', 'relativePath']) {
      final value = _manifest();
      _launch(value).remove(key);
      cases['launch missing $key'] = value;
    }

    final launchExtra = _manifest();
    _launch(launchExtra)['extra'] = true;
    cases['launch extra'] = launchExtra;

    final launchEmptyType = _manifest();
    _launch(launchEmptyType)['type'] = ' ';
    cases['launch empty type'] = launchEmptyType;

    final unsupportedLaunchType = _manifest();
    _launch(unsupportedLaunchType)['type'] = 'command';
    cases['unsupported launch type'] = unsupportedLaunchType;

    final singleWithoutLaunch = _manifest();
    _runtime(singleWithoutLaunch)['launch'] = null;
    cases['single item without launch'] = singleWithoutLaunch;

    final singleWithMultipleItems = _manifest();
    final secondSingleItem = _copyMap(_item(singleWithMultipleItems));
    secondSingleItem['relativePath'] = 'chrono/second.sfc';
    singleWithMultipleItems['items'] = <Object?>[
      _copyMap(_item(singleWithMultipleItems)),
      secondSingleItem,
    ];
    _runtime(singleWithMultipleItems)['minimumInstallBytes'] = 24;
    cases['single content type with multiple items'] = singleWithMultipleItems;

    final unknownWithSingleItem = _manifest();
    _runtime(unknownWithSingleItem)
      ..['contentType'] = 'unknown'
      ..['launch'] = null;
    cases['unknown content type with one item'] = unknownWithSingleItem;

    for (final path in <String>[
      '',
      '/absolute.rom',
      r'C:\game.rom',
      r'dir\game.rom',
      '../game.rom',
      'dir/../game.rom',
      'dir//game.rom',
      'dir/./game.rom',
      'dir/\u0000game.rom',
      'NUL.rom',
      'dir/AUX',
      'name?.rom',
      'name.',
    ]) {
      final value = _manifest();
      _launch(value)['relativePath'] = path.replaceAll(r'\u0000', '\u0000');
      cases['unsafe launch $path'] = value;
    }

    final launchNotItem = _manifest();
    _launch(launchNotItem)['relativePath'] = 'different.rom';
    cases['launch not an item'] = launchNotItem;

    for (final entry in cases.entries) {
      final result = await _resultFor(responseBody: entry.value);
      _expectFailure(
        result,
        ReleaseManifestFailureKind.malformedResponse,
        reason: entry.key,
      );
    }
  });

  test(
    'rejects malformed items grants and cross-field inconsistencies',
    () async {
      final cases = <String, Map<String, Object?>>{};

      final nonObjectItem = _manifest();
      nonObjectItem['items'] = <Object?>['item'];
      cases['non-object item'] = nonObjectItem;

      final malformedSecondItem = _manifest();
      final secondItem = _copyMap(_item(malformedSecondItem));
      secondItem
        ..['relativePath'] = 'chrono/second.sfc'
        ..remove('role');
      malformedSecondItem['items'] = <Object?>[
        _copyMap(_item(malformedSecondItem)),
        secondItem,
      ];
      _runtime(malformedSecondItem)['minimumInstallBytes'] = 24;
      cases['malformed second item rejects entire list'] = malformedSecondItem;

      for (final key in <String>[
        'relativePath',
        'role',
        'sizeBytes',
        'isAvailable',
      ]) {
        final value = _manifest();
        _item(value).remove(key);
        cases['item missing $key'] = value;
      }

      final itemExtra = _manifest();
      _item(itemExtra)['extra'] = true;
      cases['item extra'] = itemExtra;

      final itemRoleEmpty = _manifest();
      _item(itemRoleEmpty)['role'] = ' ';
      cases['item empty role'] = itemRoleEmpty;

      final unsupportedRole = _manifest();
      _item(unsupportedRole)['role'] = 'bios';
      cases['unsupported item role'] = unsupportedRole;

      final itemAvailableType = _manifest();
      _item(itemAvailableType)['isAvailable'] = 1;
      cases['item availability type'] = itemAvailableType;

      for (final numeric in <Object?>[
        -1,
        '-1',
        '01',
        '+1',
        '1.0',
        1.5,
        '9223372036854775808',
      ]) {
        final value = _manifest();
        _item(value)['sizeBytes'] = numeric;
        cases['item int64 $numeric'] = value;
      }

      for (final path in <String>[
        '',
        '/absolute.rom',
        r'C:\game.rom',
        r'dir\game.rom',
        '../game.rom',
        'dir/../game.rom',
        'dir//game.rom',
        'dir/./game.rom',
        'NUL.rom',
        'dir/AUX',
        'name?.rom',
        'name.',
      ]) {
        final value = _manifest();
        _item(value)['relativePath'] = path;
        _launch(value)['relativePath'] = path;
        cases['unsafe item $path'] = value;
      }

      final duplicatePaths = _manifest();
      duplicatePaths['items'] = <Object?>[
        _copyMap(_item(duplicatePaths)),
        _copyMap(_item(duplicatePaths)),
      ];
      _runtime(duplicatePaths)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 24;
      cases['duplicate item paths'] = duplicatePaths;

      final caseCollision = _manifest();
      final caseVariant = _copyMap(_item(caseCollision));
      caseVariant['relativePath'] = 'CHRONO/GAME.SFC';
      caseCollision['items'] = <Object?>[
        _copyMap(_item(caseCollision)),
        caseVariant,
      ];
      _runtime(caseCollision)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 24;
      cases['portable case-only path collision'] = caseCollision;

      final fileDirectoryCollision = _manifest();
      final filePath = _copyMap(_item(fileDirectoryCollision));
      filePath['relativePath'] = 'content';
      final nestedPath = _copyMap(_item(fileDirectoryCollision));
      nestedPath['relativePath'] = 'CONTENT/game.sfc';
      fileDirectoryCollision['items'] = <Object?>[filePath, nestedPath];
      _runtime(fileDirectoryCollision)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 24;
      cases['portable file-directory collision'] = fileDirectoryCollision;

      final wrongShaType = _manifest();
      _item(wrongShaType)['sha256'] = 1;
      cases['sha type'] = wrongShaType;
      for (final sha in <String>[
        '',
        'aa',
        List<String>.filled(64, 'A').join(),
        List<String>.filled(64, 'g').join(),
      ]) {
        final value = _manifest();
        _item(value)['sha256'] = sha;
        cases['sha value $sha'] = value;
      }

      final availableNoSha = _manifest();
      _item(availableNoSha)['sha256'] = null;
      cases['available without sha'] = availableNoSha;

      final availableNoGrant = _manifest();
      _item(availableNoGrant)['contentGrant'] = null;
      cases['available without grant'] = availableNoGrant;

      final unavailableSha = _unavailableManifest();
      _item(unavailableSha)['sha256'] = _sha256;
      cases['unavailable with sha'] = unavailableSha;

      final unavailableGrant = _unavailableManifest();
      _item(unavailableGrant)['contentGrant'] = _grant();
      cases['unavailable with grant'] = unavailableGrant;

      final grantWrongType = _manifest();
      _item(grantWrongType)['contentGrant'] = 'grant';
      cases['grant wrong type'] = grantWrongType;

      for (final key in <String>['downloadUrl', 'expiresAt']) {
        final value = _manifest();
        _grantFrom(value).remove(key);
        cases['grant missing $key'] = value;
      }

      final grantExtra = _manifest();
      _grantFrom(grantExtra)['extra'] = true;
      cases['grant extra'] = grantExtra;

      for (final url in <Object?>[
        1,
        '',
        'https://other.test/delivery/content/payload.signature',
        '//other.test/delivery/content/payload.signature',
        '/delivery/content/',
        '/delivery/content/payload.signature/extra',
        '/delivery/content/payload.signature?query=1',
        '/delivery/content/payload.signature#fragment',
        '/delivery/bios/payload.signature',
      ]) {
        final value = _manifest();
        _grantFrom(value)['downloadUrl'] = url;
        cases['grant URL $url'] = value;
      }

      for (final expiry in <Object?>[
        1,
        '',
        'not-a-date',
        '2026-02-31T12:00:00+00:00',
        '2026-07-17T25:00:00+00:00',
        '2026-07-17T12:00:00+15:00',
        '2026-07-17T12:00:00+14:01',
        '2026-07-17T12:00:00',
        '2026-07-17',
      ]) {
        final value = _manifest();
        _grantFrom(value)['expiresAt'] = expiry;
        cases['grant expiry $expiry'] = value;
      }

      final byteMismatch = _manifest();
      _runtime(byteMismatch)['minimumInstallBytes'] = '13';
      cases['minimum bytes mismatch'] = byteMismatch;

      final sumOverflow = _manifest();
      final first = _copyMap(_item(sumOverflow));
      first['relativePath'] = 'first.rom';
      final second = _copyMap(_item(sumOverflow));
      second['relativePath'] = 'second.rom';
      first['sizeBytes'] = '9223372036854775807';
      second['sizeBytes'] = '1';
      sumOverflow['items'] = <Object?>[first, second];
      _runtime(sumOverflow)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = '9223372036854775807';
      cases['item sum overflow'] = sumOverflow;

      for (final entry in cases.entries) {
        final result = await _resultFor(responseBody: entry.value);
        _expectFailure(
          result,
          ReleaseManifestFailureKind.malformedResponse,
          reason: entry.key,
        );
      }
    },
  );

  test(
    'defers Unicode normalization collisions to Gate 2 filesystem preflight',
    () async {
      final value = _manifest();
      final composed = _copyMap(_item(value));
      composed['relativePath'] = 'café.sfc';
      final decomposed = _copyMap(_item(value));
      decomposed['relativePath'] = 'cafe\u0301.sfc';
      for (final file in [composed, decomposed]) {
        final format =
            (file['formats']! as List<Object?>).single! as Map<String, Object?>;
        format
          ..['relativePath'] = file['relativePath']
          ..['filename'] = file['relativePath'];
      }
      value['items'] = <Object?>[composed, decomposed];
      _runtime(value)
        ..['contentType'] = 'unknown'
        ..['launch'] = null
        ..['minimumInstallBytes'] = 24;

      final result = await _resultFor(responseBody: value);

      final manifest = _expectSuccess(result);
      expect(manifest.items, hasLength(2));
      expect(manifest.items.map((item) => item.relativePath).toSet(), <String>{
        'café.sfc',
        'cafe\u0301.sfc',
      });
    },
  );

  test(
    'reports valid foreign expected identities as identity mismatch',
    () async {
      final cases = <String, Map<String, Object?>>{
        'server': _with(
          _manifest(),
          'serverInstanceId',
          _foreignServerInstanceId.value,
        ),
        'release': _with(_manifest(), 'releaseId', _foreignReleaseId.value),
        'title': _with(_manifest(), 'titleId', _foreignTitleId.value),
        'short name': _with(_manifest(), 'systemKey', 'psx'),
      };

      for (final entry in cases.entries) {
        final result = await _resultFor(responseBody: entry.value);
        _expectFailure(
          result,
          ReleaseManifestFailureKind.identityMismatch,
          reason: entry.key,
        );
      }
    },
  );

  test(
    'classifies specified HTTP failures without parsing manifest data',
    () async {
      const cases = <int, ReleaseManifestFailureKind>{
        HttpStatus.badRequest: ReleaseManifestFailureKind.badRequest,
        HttpStatus.unauthorized: ReleaseManifestFailureKind.unauthorized,
        HttpStatus.notFound: ReleaseManifestFailureKind.notFound,
        HttpStatus.conflict: ReleaseManifestFailureKind.conflict,
        HttpStatus.forbidden: ReleaseManifestFailureKind.clientError,
        HttpStatus.internalServerError: ReleaseManifestFailureKind.serverError,
        HttpStatus.badGateway: ReleaseManifestFailureKind.serverError,
        HttpStatus.created: ReleaseManifestFailureKind.unexpectedStatus,
      };

      for (final entry in cases.entries) {
        final harness = await _ManifestHarness.start(
          statusCode: entry.key,
          responseBody: _manifest(),
          challenge: entry.key == HttpStatus.unauthorized,
        );
        try {
          final result = await harness.getManifest();
          _expectFailure(
            result,
            entry.value,
            statusCode: entry.key,
            reason: 'HTTP ${entry.key}',
          );
          expect(harness.requests, hasLength(1), reason: 'HTTP ${entry.key}');
        } finally {
          await harness.close();
        }
      }
    },
  );

  test('preserves invalid_token bearer challenge for replay', () async {
    final harness = await _ManifestHarness.start(
      statusCode: HttpStatus.unauthorized,
      responseBody: <String, Object?>{},
      bearerChallenge: 'Bearer error="invalid_token"',
    );
    addTearDown(harness.close);

    final result = await harness.getManifest();

    expect(result, isA<ReleaseManifestFailure>());
    expect((result as ReleaseManifestFailure).isInvalidToken, isTrue);
    expect(result.bearerError, 'invalid_token');
  });

  test('does not follow redirects or contact the redirect target', () async {
    var targetRequests = 0;
    final target = await _bindServer((request) async {
      targetRequests++;
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    addTearDown(() => target.close(force: true));
    final source = await _bindServer((request) async {
      await request.drain<void>();
      request.response
        ..statusCode = HttpStatus.seeOther
        ..headers.set(
          HttpHeaders.locationHeader,
          _originFor(target).resolve('/target').toString(),
        );
      await request.response.close();
    });
    final client = HttpReleaseManifestApiClient(
      consumerApiOrigin: _originFor(source),
    );
    addTearDown(() async {
      client.close();
      await source.close(force: true);
    });

    final result = await _getManifest(client);

    _expectFailure(
      result,
      ReleaseManifestFailureKind.redirect,
      statusCode: HttpStatus.seeOther,
    );
    expect(targetRequests, 0);
  });

  test('classifies transport failure without a manifest', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final origin = _originFor(server);
    await server.close(force: true);
    final client = HttpReleaseManifestApiClient(consumerApiOrigin: origin);
    addTearDown(client.close);

    final result = await _getManifest(client);

    _expectFailure(result, ReleaseManifestFailureKind.transport);
  });

  test(
    'timeout aborts only its stalled operation while an overlap succeeds',
    () async {
      final server = await _RawStalledHttpServer.start(
        stalledBodyPrefix: '{"serverInstanceId":"',
        completeResponses: <String, String>{
          '/api/releases/${_foreignReleaseId.value}/manifest': jsonEncode(
            _manifest(
              releaseId: _foreignReleaseId,
              titleId: _foreignTitleId,
              platformShortName: 'psx',
            ),
          ),
        },
      );
      final client = HttpReleaseManifestApiClient(
        consumerApiOrigin: server.origin,
        timeout: const Duration(milliseconds: 150),
      );
      addTearDown(() async {
        client.close();
        await server.close();
      });

      final stalledFuture = _getManifest(client);
      await server.stalledRequestReached.future;
      final overlap = await client.getReleaseManifest(
        accessToken: 'access-token',
        expectedServerInstanceId: _serverInstanceId,
        releaseId: _foreignReleaseId,
        expectedTitleId: _foreignTitleId,

        expectedSystemKey: 'psx',
      );
      final stalled = await stalledFuture;

      expect(overlap, isA<ReleaseManifestSuccess>());
      _expectFailure(stalled, ReleaseManifestFailureKind.timeout);
      await server.stalledClientDisconnected.future.timeout(
        const Duration(seconds: 2),
      );
    },
  );

  test('close aborts an active stalled manifest operation', () async {
    final server = await _RawStalledHttpServer.start(
      stalledBodyPrefix: '{"serverInstanceId":"',
    );
    final client = HttpReleaseManifestApiClient(
      consumerApiOrigin: server.origin,
      timeout: const Duration(seconds: 5),
    );
    addTearDown(() async {
      client.close();
      await server.close();
    });

    final resultFuture = _getManifest(client);
    await server.stalledRequestReached.future;
    client.close();

    final result = await resultFuture;
    _expectFailure(result, ReleaseManifestFailureKind.transport);
    await server.stalledClientDisconnected.future.timeout(
      const Duration(seconds: 2),
    );
  });

  test('close aborts a manifest request while it is opening', () async {
    var requestCount = 0;
    final server = await _bindServer((request) async {
      requestCount++;
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    final client = HttpReleaseManifestApiClient(
      consumerApiOrigin: _originFor(server),
      timeout: const Duration(seconds: 5),
    );
    addTearDown(() async {
      client.close();
      await server.close(force: true);
    });

    final resultFuture = _getManifest(client);
    client.close();

    final result = await resultFuture;
    _expectFailure(result, ReleaseManifestFailureKind.transport);
    expect(requestCount, 0);
  });

  test('manifest request after close is a typed transport failure', () async {
    final server = await _bindServer((request) async {
      await request.drain<void>();
      request.response.statusCode = HttpStatus.ok;
      await request.response.close();
    });
    final client = HttpReleaseManifestApiClient(
      consumerApiOrigin: _originFor(server),
    );
    addTearDown(() async {
      client.close();
      await server.close(force: true);
    });
    client.close();

    final result = await _getManifest(client);

    _expectFailure(result, ReleaseManifestFailureKind.transport);
  });
}

final _serverInstanceId = RomdServerInstanceId.tryParse(
  'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
)!;
final _foreignServerInstanceId = RomdServerInstanceId.tryParse(
  'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
)!;
final _releaseId = RomdPublicId.tryParse(RomdPublicId.encode(1))!;
final _foreignReleaseId = RomdPublicId.tryParse(RomdPublicId.encode(2))!;
final _titleId = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
final _foreignTitleId = RomdPublicId.tryParse(RomdPublicId.encode(102))!;

const _sha256 =
    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const _grantUrl = '/delivery/content/payload.signature';

Map<String, Object?> _manifest({
  RomdPublicId? releaseId,
  RomdPublicId? titleId,
  String platformShortName = 'snes',
}) => <String, Object?>{
  'isDeliveryComplete': true,

  'serverInstanceId': _serverInstanceId.value,
  'releaseId': (releaseId ?? _releaseId).value,
  'titleId': (titleId ?? _titleId).value,

  'systemKey': platformShortName,
  'name': 'Chrono Trigger',
  'revision': 'Rev 1',
  'isComplete': true,
  'runtime': <String, Object?>{
    'contentType': 'single_rom',
    'launch': <String, Object?>{
      'type': 'file',
      'relativePath': 'chrono/game.sfc',
    },
    'packaging': 'direct_files',
    'minimumInstallBytes': 12,
  },
  'items': <Object?>[
    <String, Object?>{
      'provenance': 'Original',
      'formats': [
        {
          'format': 'original',
          'label': 'Original',
          'state': 'available',
          'relativePath': 'chrono/game.sfc',
          'filename': ('chrono/game.sfc').split('/').last,
          'sizeBytes': '12',
          'sha256': _sha256,
          'contentGrant': _grant(),
          'profileVersion': 'identity/v1',
          'runtimeApplicability': <String>[],
        },
      ],

      'relativePath': 'chrono/game.sfc',
      'role': 'rom',
      'sizeBytes': 12,
      'sha256': _sha256,
      'isAvailable': true,
      'contentGrant': _grant(),
    },
  ],
};

Map<String, Object?> _unavailableManifest() {
  final value = _manifest();
  value['isDeliveryComplete'] = false;
  _item(value)
    ..['sha256'] = null
    ..['isAvailable'] = false
    ..['contentGrant'] = null;
  final original =
      (_item(value)['formats']! as List<Object?>).single!
          as Map<String, Object?>;
  original
    ..['state'] = 'unsupported'
    ..remove('filename')
    ..remove('sizeBytes')
    ..remove('sha256')
    ..remove('contentGrant');
  return value;
}

Map<String, Object?> _manifestWithFormats() {
  final value = _manifest();
  _item(value)['relativePath'] = 'chrono/game.iso';
  _launch(value)['relativePath'] = 'chrono/game.iso';
  _item(value)['formats'] = <Object?>[
    <String, Object?>{
      'format': 'original',
      'label': 'Original',
      'state': 'available',
      'relativePath': 'chrono/game.iso',
      'filename': 'game.iso',
      'sizeBytes': '12',
      'sha256': _sha256,
      'contentGrant': _grant(),
      'profileVersion': 'rvz-import/v1',
      'runtimeApplicability': <String>[],
    },
    <String, Object?>{
      'format': 'rvz',
      'label': 'RVZ',
      'fileExtension': '.rvz',
      'state': 'available',
      'relativePath': 'chrono/game.rvz',
      'filename': 'game.rvz',
      'sizeBytes': '5',
      'sha256': 'b' * 64,
      'md5': 'c' * 32,
      'sha1': 'd' * 40,
      'contentGrant': <String, Object?>{
        'downloadUrl': '/delivery/content/rvz.signature',
        'expiresAt': '2026-07-17T12:00:00+00:00',
      },
      'profileVersion': 'rvz-canonical/v1',
      'runtimeApplicability': <String>['dolphin'],
    },
  ];
  return value;
}

Map<String, Object?> _grant() => <String, Object?>{
  'downloadUrl': _grantUrl,
  'expiresAt': '2026-07-17T12:00:00+00:00',
};

Map<String, Object?> _runtime(Map<String, Object?> value) =>
    value['runtime']! as Map<String, Object?>;

Map<String, Object?> _launch(Map<String, Object?> value) =>
    _runtime(value)['launch']! as Map<String, Object?>;

Map<String, Object?> _item(Map<String, Object?> value) =>
    (value['items']! as List<Object?>).single as Map<String, Object?>;

Map<String, Object?> _grantFrom(Map<String, Object?> value) =>
    _item(value)['contentGrant']! as Map<String, Object?>;

Map<String, Object?> _without(Map<String, Object?> value, String key) {
  value.remove(key);
  return value;
}

Map<String, Object?> _with(
  Map<String, Object?> value,
  String key,
  Object? replacement,
) {
  value[key] = replacement;
  return value;
}

Map<String, Object?> _copyMap(Map<String, Object?> value) =>
    jsonDecode(jsonEncode(value))! as Map<String, Object?>;

final class _RecordedRequest {
  const _RecordedRequest({
    required this.method,
    required this.path,
    required this.query,
    required this.authorization,
    required this.contentType,
    required this.body,
  });

  final String method;
  final String path;
  final String query;
  final String? authorization;
  final String? contentType;
  final String body;
}

final class _ManifestHarness {
  _ManifestHarness._(this.server, this.client, this.requests);

  final HttpServer server;
  final HttpReleaseManifestApiClient client;
  final List<_RecordedRequest> requests;

  static Future<_ManifestHarness> start({
    int statusCode = HttpStatus.ok,
    Object? responseBody,
    String? rawResponseBody,
    bool challenge = false,
    String? bearerChallenge,
  }) async {
    final requests = <_RecordedRequest>[];
    final server = await _bindServer((request) async {
      final body = await utf8.decodeStream(request);
      requests.add(
        _RecordedRequest(
          method: request.method,
          path: request.uri.path,
          query: request.uri.query,
          authorization: request.headers.value(HttpHeaders.authorizationHeader),
          contentType: request.headers.value(HttpHeaders.contentTypeHeader),
          body: body,
        ),
      );
      request.response
        ..statusCode = statusCode
        ..headers.contentType = ContentType.json;
      if (challenge) {
        request.response.headers.set(
          HttpHeaders.wwwAuthenticateHeader,
          'Basic realm="ROMD"',
        );
      }
      if (bearerChallenge != null) {
        request.response.headers.set(
          HttpHeaders.wwwAuthenticateHeader,
          bearerChallenge,
        );
      }
      request.response.write(rawResponseBody ?? jsonEncode(responseBody));
      await request.response.close();
    });
    return _ManifestHarness._(
      server,
      HttpReleaseManifestApiClient(consumerApiOrigin: _originFor(server)),
      requests,
    );
  }

  Future<ReleaseManifestResult> getManifest({
    String expectedSystemKey = 'snes',
  }) => _getManifest(client, expectedSystemKey: expectedSystemKey);

  Future<void> close() async {
    client.close();
    await server.close(force: true);
  }
}

Future<ReleaseManifestResult> _resultFor({
  int statusCode = HttpStatus.ok,
  Object? responseBody,
  String? rawResponseBody,
  String expectedSystemKey = 'snes',
}) async {
  final harness = await _ManifestHarness.start(
    statusCode: statusCode,
    responseBody: responseBody,
    rawResponseBody: rawResponseBody,
  );
  try {
    return await harness.getManifest(expectedSystemKey: expectedSystemKey);
  } finally {
    await harness.close();
  }
}

Future<ReleaseManifestResult> _getManifest(
  HttpReleaseManifestApiClient client, {
  String expectedSystemKey = 'snes',
}) => client.getReleaseManifest(
  accessToken: 'access-token',
  expectedServerInstanceId: _serverInstanceId,
  releaseId: _releaseId,
  expectedTitleId: _titleId,

  expectedSystemKey: expectedSystemKey,
);

String get _serverWireSingleDiscPs2 => serverWireSingleDiscPs2Manifest(
  serverInstanceId: _serverInstanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  logicalSha256: _sha256,
);

Map<String, Object?> _wirePs2() =>
    jsonDecode(_serverWireSingleDiscPs2)! as Map<String, Object?>;

Map<String, Object?> _wireChd(Map<String, Object?> value) =>
    (_item(value)['formats']! as List<Object?>).last! as Map<String, Object?>;

Map<String, Object?> _runtimeOf(Map<String, Object?> format) =>
    format['runtime']! as Map<String, Object?>;

Map<String, Object?> _launchOf(Map<String, Object?> format) =>
    _runtimeOf(format)['launch']! as Map<String, Object?>;

String get _serverWireSnesWithFormats => serverWireSnesManifestWithFormats(
  serverInstanceId: _serverInstanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  logicalSha256: _sha256,
);

ServerBoundReleaseManifest _expectSuccess(ReleaseManifestResult result) {
  expect(result, isA<ReleaseManifestSuccess>());
  return (result as ReleaseManifestSuccess).manifest;
}

void _expectFailure(
  ReleaseManifestResult result,
  ReleaseManifestFailureKind kind, {
  int? statusCode,
  String? reason,
}) {
  expect(result, isA<ReleaseManifestFailure>(), reason: reason);
  final failure = result as ReleaseManifestFailure;
  expect(failure.kind, kind, reason: reason);
  expect(failure.statusCode, statusCode, reason: reason);
  expect(result, isNot(isA<ReleaseManifestSuccess>()), reason: reason);
}

Future<HttpServer> _bindServer(
  Future<void> Function(HttpRequest request) handler,
) async {
  final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  server.listen(handler);
  return server;
}

Uri _originFor(HttpServer server) =>
    Uri.parse('http://${server.address.host}:${server.port}');

final class _RawStalledHttpServer {
  _RawStalledHttpServer._(
    this._server,
    this._stalledBodyPrefix,
    this._completeResponses,
  );

  final ServerSocket _server;
  final String _stalledBodyPrefix;
  final Map<String, String> _completeResponses;
  final Set<Socket> _sockets = <Socket>{};
  final Set<Socket> _stalledSockets = <Socket>{};
  final Completer<void> stalledRequestReached = Completer<void>();
  final Completer<void> stalledClientDisconnected = Completer<void>();
  late final StreamSubscription<Socket> _subscription;

  static Future<_RawStalledHttpServer> start({
    required String stalledBodyPrefix,
    Map<String, String> completeResponses = const <String, String>{},
  }) async {
    final socket = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final server = _RawStalledHttpServer._(
      socket,
      stalledBodyPrefix,
      completeResponses,
    );
    server._subscription = socket.listen(server._accept);
    return server;
  }

  Uri get origin => Uri.parse('http://${_server.address.host}:${_server.port}');

  void _accept(Socket socket) {
    _sockets.add(socket);
    final requestBytes = <int>[];
    var requestComplete = false;
    socket.listen(
      (chunk) {
        if (requestComplete) {
          return;
        }
        requestBytes.addAll(chunk);
        final requestHead = utf8.decode(requestBytes, allowMalformed: true);
        if (!requestHead.contains('\r\n\r\n')) {
          return;
        }
        requestComplete = true;
        final requestLine = requestHead.substring(
          0,
          requestHead.indexOf('\r\n'),
        );
        final path = requestLine.split(' ')[1];
        final completeBody = _completeResponses[path];
        if (completeBody != null) {
          unawaited(_writeCompleteResponse(socket, completeBody));
          return;
        }
        _stalledSockets.add(socket);
        unawaited(_writeStalledResponse(socket));
      },
      onDone: () => _socketEnded(socket),
      onError: (Object _) => _socketEnded(socket),
      cancelOnError: true,
    );
  }

  Future<void> _writeCompleteResponse(Socket socket, String body) async {
    final bodyBytes = utf8.encode(body);
    socket.add(
      ascii.encode(
        'HTTP/1.1 200 OK\r\n'
        'Content-Type: application/json\r\n'
        'Content-Length: ${bodyBytes.length}\r\n'
        'Connection: close\r\n'
        '\r\n',
      ),
    );
    socket.add(bodyBytes);
    await socket.flush();
    await socket.close();
  }

  Future<void> _writeStalledResponse(Socket socket) async {
    final bodyBytes = utf8.encode(_stalledBodyPrefix);
    socket.add(
      ascii.encode(
        'HTTP/1.1 200 OK\r\n'
        'Content-Type: application/json\r\n'
        'Content-Length: ${bodyBytes.length + 1024}\r\n'
        'Connection: close\r\n'
        '\r\n',
      ),
    );
    socket.add(bodyBytes);
    await socket.flush();
    if (!stalledRequestReached.isCompleted) {
      stalledRequestReached.complete();
    }
  }

  void _socketEnded(Socket socket) {
    _sockets.remove(socket);
    if (_stalledSockets.remove(socket) &&
        !stalledClientDisconnected.isCompleted) {
      stalledClientDisconnected.complete();
    }
  }

  Future<void> close() async {
    await _subscription.cancel();
    for (final socket in List<Socket>.of(_sockets)) {
      socket.destroy();
    }
    await _server.close();
  }
}

Map<String, Object?> _derivedWire() {
  final wire = _manifest();
  wire['isComplete'] = false;
  wire['isDeliveryComplete'] = true;
  final bin = _item(wire);
  bin['relativePath'] = 'chrono/track.bin';
  final format =
      (bin['formats']! as List<Object?>).single! as Map<String, Object?>;
  format
    ..['relativePath'] = 'chrono/track.bin'
    ..['filename'] = 'track.bin';
  final runtime = _runtime(wire);
  runtime
    ..['contentType'] = 'unknown'
    ..['minimumInstallBytes'] = '21'
    ..['launch'] = {'type': 'file', 'relativePath': 'chrono/disc.cue'};
  wire['items'] = [
    bin,
    <String, Object?>{
      'relativePath': 'chrono/disc.cue',
      'role': 'rom',
      'provenance': 'Derived',
      'formats': <Object?>[],
      'sizeBytes': '9',
      'sha256': 'b' * 64,
      'isAvailable': true,
      'contentGrant': {
        'downloadUrl': '/delivery/derived-descriptor/generated.signature',
        'expiresAt': '2030-01-01T00:15:00+00:00',
      },
    },
  ];
  return wire;
}
