import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_models.dart';

import 'disc_set_fixture.dart';

void main() {
  Map<String, Object?> withDerivedCue() {
    final wire = fixture();
    final cue = DiscSetWire.maps(
      DiscSetWire.maps(wire['discs']).first['files'],
    ).first;
    cue['provenance'] = 'Derived';
    DiscSetWire.map(cue['contentGrant'])['downloadUrl'] =
        '/delivery/derived-descriptor/token.signature';
    return wire;
  }

  test(
    'derived CUE retains provenance and actual identity in grant-free stored plans',
    () {
      final wire = withDerivedCue();
      final parsed = DiscSetPlan.parse(wire, origin: origin);
      expect(parsed.files.first.provenance, ContentFileProvenance.derived);
      expect(parsed.files.first.sha256, digest(payload('game.cue')));
      final restored = DiscSetPlan.parse(
        jsonDecode(parsed.encode()),
        origin: origin,
        persisted: true,
      );
      expect(restored.files.first.provenance, ContentFileProvenance.derived);
      expect(restored.files.first.url, isNull);
      expect(restored.encode(), contains('Derived'));
      expect(restored.encode(), isNot(contains('contentGrant')));
      expect(parsed.hasSameSelectedSnapshot(plan()), isFalse);
    },
  );
  for (final defect in [
    'missing',
    'unknown',
    'case',
    'wrong-purpose',
    'wrong-path',
    'original-layout',
  ]) {
    test('derived disc source refuses $defect', () {
      final wire = withDerivedCue();
      final disc = DiscSetWire.maps(wire['discs']).first;
      final cue = DiscSetWire.maps(disc['files']).first;
      switch (defect) {
        case 'missing':
          cue.remove('provenance');
        case 'unknown':
          cue['provenance'] = 'Other';
        case 'case':
          cue['provenance'] = 'derived';
        case 'wrong-purpose':
          DiscSetWire.map(cue['contentGrant'])['downloadUrl'] =
              '/delivery/content/token.signature';
        case 'wrong-path':
          cue['relativePath'] = 'disc-01/game.iso';
          disc['entryPoint'] = 'disc-01/game.iso';
        case 'original-layout':
          disc['layoutId'] = setId;
          disc['layoutRevision'] = revision;
      }
      expect(
        () => DiscSetPlan.parse(wire, origin: origin),
        throwsFormatException,
      );
    });
  }

  test(
    'original plan retains ordered structured identities and strips every grant',
    () {
      final p = plan();
      expect(p.selectedReleaseIds, releases);
      expect(p.files.length, 5);
      expect(p.playlistText, 'disc-01/game.cue\ndisc-02/game.cue\n');
      expect(p.encode(), isNot(contains('token.signature')));
      expect(p.encode(), isNot(contains('contentGrant')));
      final restored = DiscSetPlan.parse(
        jsonDecode(p.encode()),
        origin: origin,
        persisted: true,
      );
      expect(restored.encode(), p.encode());
      expect(restored.files.every((f) => f.url == null), isTrue);
      expect(restored.identity.discSetId, setId);
    },
  );
  test('snapshot plan cannot be mutated after validation', () {
    final p = plan();
    expect(() => p.discs.first['ordinal'] = 8, throwsUnsupportedError);
    expect(() => p.selection['kind'] = 'partial', throwsUnsupportedError);
  });
  test(
    'one-disc partial retains original identity and explicit omitted member',
    () {
      final p = plan(partial: true);
      expect(p.discs.length, 1);
      expect(p.allowPartial, isTrue);
      expect(p.selection['omittedDiscs'], [
        {'releaseId': releases.last, 'ordinal': 2},
      ]);
    },
  );
  test(
    'unknown total and descriptor missing are distinct from authorization',
    () {
      final status = DiscSetStatus(statusFixture(unavailable: true));
      expect(status.expectedDiscCount, isNull);
      expect(status.completenessLabel, '2 known discs; total unknown');
      expect(status.readyReleaseIds, [releases.first]);
      expect(status.requiresPartialConsent, isTrue);
    },
  );
  test('portal archive choices preserve original-layout status semantics', () {
    final wire = statusFixture(unavailable: true);
    wire['archiveFormats'] = [
      {'format': 'original', 'label': 'Original ZIP'},
      {'format': 'chd', 'label': 'CHD where available'},
    ];
    final status = DiscSetStatus(wire);
    expect(status.readyReleaseIds, [releases.first]);
    expect(status.requiresPartialConsent, isTrue);
    wire['archiveFormats'] = [
      {'format': 'original', 'label': 'Original'},
      {'format': 'original', 'label': 'Duplicate'},
    ];
    expect(() => DiscSetStatus(wire), throwsFormatException);
    wire['archiveFormats'] = [
      {'format': 'chd'},
    ];
    expect(() => DiscSetStatus(wire), throwsFormatException);
  });
  final mutations = <String, void Function(Map<String, Object?>)>{
    'unknown version': (v) => v['contractVersion'] = 2,
    'floating version': (v) => v['contractVersion'] = 1.0,
    'unknown field': (v) => v['unexpected'] = 'ignore me',
    'invalid server': (v) => v['serverInstanceId'] = 'server',
    'invalid public id': (v) => v['discSetId'] = 'fake-release',
    'invalid system': (v) => v['systemKey'] = '../psx',
    'missing selection': (v) => v.remove('selection'),
    'duplicate ordinal': (v) =>
        DiscSetWire.maps(v['discs']).last['ordinal'] = 1,
    'duplicate release': (v) =>
        DiscSetWire.maps(v['discs']).last['releaseId'] = releases.first,
    'reverse ordering': (v) =>
        v['discs'] = DiscSetWire.maps(v['discs']).reversed.toList(),
    'foreign selected member': (v) =>
        DiscSetWire.map(v['selection'])['selectedReleaseIds'] = [setId],
    'partial without consent': (v) =>
        DiscSetWire.map(v['selection'])['missingExpectedOrdinals'] = [3],
    'missing entrypoint': (v) =>
        DiscSetWire.maps(v['discs']).first['entryPoint'] = 'disc-01/absent.cue',
    'unsupported descriptor': (v) =>
        DiscSetWire.maps(v['discs']).first['entryPoint'] = 'disc-01/game.gdi',
    'numeric bytes': (v) => DiscSetWire.maps(
      DiscSetWire.maps(v['discs']).first['files'],
    ).first['sizeBytes'] = 3,
    'overflow bytes': (v) => DiscSetWire.maps(
      DiscSetWire.maps(v['discs']).first['files'],
    ).first['sizeBytes'] = '9223372036854775808',
    'unsafe path': (v) => DiscSetWire.maps(
      DiscSetWire.maps(v['discs']).first['files'],
    ).last['relativePath'] = '../track.bin',
    'reserved name': (v) => DiscSetWire.maps(
      DiscSetWire.maps(v['discs']).first['files'],
    ).last['relativePath'] = 'disc-01/CON.bin',
    'case collision': (v) => DiscSetWire.maps(
      DiscSetWire.maps(v['discs']).first['files'],
    ).last['relativePath'] = 'DISC-01/GAME.CUE',
    'foreign grant': (v) => DiscSetWire.map(
      DiscSetWire.maps(
        DiscSetWire.maps(v['discs']).first['files'],
      ).first['contentGrant'],
    )['downloadUrl'] = 'https://foreign.example/file',
    'playlist tamper': (v) => DiscSetWire.map(v['playlist'])['text'] =
        'disc-02/game.cue\ndisc-01/game.cue\n',
    'playlist artifact mismatch': (v) =>
        DiscSetWire.map(v['playlist'])['artifactId'] = 'unrelated',
    'layout pair mismatch': (v) =>
        DiscSetWire.maps(v['discs']).first['layoutId'] = 'layout',
  };
  for (final mutation in mutations.entries) {
    test('rejects ${mutation.key}', () {
      final raw = fixture();
      mutation.value(raw);
      expect(
        () => DiscSetPlan.parse(raw, origin: origin),
        throwsFormatException,
      );
    });
  }
  for (final path in [
    '/absolute',
    'a/../track.bin',
    'a\\track.bin',
    'a//b',
    'a/./b',
    'a/track.',
    'a/track ',
    'a/nul.bin',
    'a/track\n.bin',
  ]) {
    test(
      'rejects unsafe portable path $path',
      () => expect(
        () => DiscSetWire.path({'p': path}, 'p'),
        throwsFormatException,
      ),
    );
  }
}
