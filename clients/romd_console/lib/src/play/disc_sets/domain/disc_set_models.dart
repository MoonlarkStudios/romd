import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/domain/system_key.dart';

/// Group identity is never stored as, or substituted for, a release id.
final class DiscSetIdentity {
  const DiscSetIdentity(this.serverInstanceId, this.discSetId, this.snapshotId);
  final String serverInstanceId;
  final String discSetId;
  final String snapshotId;
}

final class DiscSetStatus {
  DiscSetStatus(Map<String, Object?> value) : json = Map.unmodifiable(value) {
    DiscSetWire.exact(
      value,
      {
        'contractVersion',
        'serverInstanceId',
        'discSetId',
        'titleId',
        'systemKey',
        'revision',
        'observationId',
        'name',
        'missingExpectedOrdinals',
        'discs',
      },
      optional: {'expectedDiscCount', 'archiveFormats'},
    );
    DiscSetWire.version(value);
    // Portal ZIP choices do not change this client's original-layout acquisition.
    if (value.containsKey('archiveFormats')) {
      final seen = <String>{};
      for (final choice in DiscSetWire.maps(value['archiveFormats'])) {
        DiscSetWire.exact(choice, {'format', 'label'});
        final format = DiscSetWire.text(choice, 'format');
        DiscSetWire.text(choice, 'label');
        if (!RegExp(r'^[a-z][a-z0-9-]{0,31}$').hasMatch(format) ||
            !seen.add(format)) {
          throw const FormatException('Invalid archive format metadata.');
        }
      }
    }
    serverInstanceId = DiscSetWire.server(value, 'serverInstanceId');
    discSetId = DiscSetWire.id(value, 'discSetId');
    titleId = DiscSetWire.id(value, 'titleId');
    systemKey = DiscSetWire.system(value);
    revision = DiscSetWire.guid(value, 'revision');
    observationId = DiscSetWire.digest(value, 'observationId');
    name = DiscSetWire.text(value, 'name');
    expectedDiscCount = DiscSetWire.expected(value);
    missingExpectedOrdinals = DiscSetWire.ordinals(
      value['missingExpectedOrdinals'],
    );
    discs = DiscSetWire.maps(
      value['discs'],
    ).map(DiscSetDiscStatus.new).toList(growable: false);
    DiscSetWire.members(discs.map((d) => (d.releaseId, d.ordinal)).toList());
  }
  final Map<String, Object?> json;
  late final String serverInstanceId,
      discSetId,
      titleId,
      systemKey,
      revision,
      observationId,
      name;
  late final int? expectedDiscCount;
  late final List<int> missingExpectedOrdinals;
  late final List<DiscSetDiscStatus> discs;
  List<String> get readyReleaseIds => discs
      .where((d) => d.readiness == 'ready')
      .map((d) => d.releaseId)
      .toList();
  bool get requiresPartialConsent =>
      missingExpectedOrdinals.isNotEmpty ||
      readyReleaseIds.length != discs.length;
  String get completenessLabel => expectedDiscCount == null
      ? '${discs.length} known discs; total unknown'
      : '${discs.length} known of $expectedDiscCount expected discs';
}

final class DiscSetDiscStatus {
  DiscSetDiscStatus(Map<String, Object?> value) {
    DiscSetWire.exact(
      value,
      {'releaseId', 'ordinal', 'name', 'readiness'},
      optional: {'layoutId', 'layoutRevision'},
    );
    releaseId = DiscSetWire.id(value, 'releaseId');
    ordinal = DiscSetWire.ordinal(value['ordinal']);
    name = DiscSetWire.text(value, 'name');
    readiness = DiscSetWire.text(value, 'readiness');
    if (!const {
      'ready',
      'missing-media',
      'descriptor-required',
      'unsupported-layout',
      'stale-layout',
    }.contains(readiness)) {
      throw const FormatException('Unknown disc readiness.');
    }
    layoutId = DiscSetWire.nullableText(value, 'layoutId');
    layoutRevision = DiscSetWire.nullableText(value, 'layoutRevision');
    if ((layoutId == null) != (layoutRevision == null)) {
      throw const FormatException('Incoherent layout identity.');
    }
  }
  late final String releaseId, name, readiness;
  late final int ordinal;
  late final String? layoutId, layoutRevision;
}

/// Immutable metadata persisted without transport grants. Exact playlist bytes
/// and selected ordered membership belong to this snapshot, not to filenames.
final class DiscSetPlan {
  DiscSetPlan._(this.json, this.files, this.playlistText);
  final Map<String, Object?> json;
  final List<DiscSetFile> files;
  final String playlistText;
  String get serverInstanceId => json['serverInstanceId']! as String;
  String get discSetId => json['discSetId']! as String;
  String get snapshotId => json['deliverySnapshotId']! as String;
  String get titleId => json['titleId']! as String;
  String get systemKey => json['systemKey']! as String;
  String get name => json['name']! as String;
  String get revision => json['revision']! as String;
  String get observationId => json['observationId']! as String;
  Map<String, Object?> get selection => DiscSetWire.map(json['selection']);
  List<String> get selectedReleaseIds =>
      DiscSetWire.strings(selection['selectedReleaseIds']);
  List<Map<String, Object?>> get discs => DiscSetWire.maps(json['discs']);
  Map<String, Object?> get playlist => DiscSetWire.map(json['playlist']);
  String get playlistPath => playlist['relativePath']! as String;
  bool get allowPartial => selection['kind'] == 'partial';
  DiscSetIdentity get identity =>
      DiscSetIdentity(serverInstanceId, discSetId, snapshotId);
  Map<String, Object?> get accessRequest => {
    'contractVersion': 1,
    'serverInstanceId': serverInstanceId,
    'revision': revision,
    'deliverySnapshotId': snapshotId,
    'selectedReleaseIds': selectedReleaseIds,
    'layoutVersions': [
      for (final disc in discs)
        {
          'releaseId': disc['releaseId'],
          'layoutId': disc['layoutId'],
          'layoutRevision': disc['layoutRevision'],
        },
    ],
    'allowPartial': allowPartial,
  };
  Map<String, Object?> get renewalRequest => {
    'contractVersion': 1,
    'revision': revision,
    'observationId': observationId,
    'selectedReleaseIds': selectedReleaseIds,
    'allowPartial': allowPartial,
    'requiredSnapshotId': snapshotId,
  };
  String encode() => jsonEncode(DiscSetWire.canonical(json));

  /// Observation freshness is not part of the selected immutable snapshot.
  bool hasSameSelectedSnapshot(DiscSetPlan other) {
    final left = {...json}..remove('observationId');
    final right = {...other.json}..remove('observationId');
    return jsonEncode(DiscSetWire.canonical(left)) ==
        jsonEncode(DiscSetWire.canonical(right));
  }

  static DiscSetPlan parse(
    Object? raw, {
    required Uri origin,
    bool persisted = false,
  }) {
    final input = DiscSetWire.map(jsonDecode(jsonEncode(raw)));
    DiscSetWire.exact(
      input,
      {
        'contractVersion',
        'serverInstanceId',
        'discSetId',
        'titleId',
        'systemKey',
        'revision',
        'observationId',
        'name',
        'deliverySnapshotId',
        'hasAllExpectedDiscs',
        'selection',
        'discs',
        'playlist',
      },
      optional: {'expectedDiscCount'},
    );
    DiscSetWire.version(input);
    DiscSetWire.server(input, 'serverInstanceId');
    DiscSetWire.id(input, 'discSetId');
    DiscSetWire.id(input, 'titleId');
    DiscSetWire.system(input);
    DiscSetWire.guid(input, 'revision');
    DiscSetWire.digest(input, 'observationId');
    DiscSetWire.text(input, 'name');
    DiscSetWire.digest(input, 'deliverySnapshotId');
    DiscSetWire.expected(input);
    if (input['hasAllExpectedDiscs'] is! bool)
      throw const FormatException('Expected completeness.');
    final selection = DiscSetWire.map(input['selection']);
    DiscSetWire.exact(selection, {
      'kind',
      'completeness',
      'selectedReleaseIds',
      'omittedDiscs',
      'missingExpectedOrdinals',
    });
    final selected = DiscSetWire.strings(selection['selectedReleaseIds']);
    if (!const {'all-known', 'partial'}.contains(selection['kind']) ||
        !const {
          'complete',
          'unknown-total',
          'partial',
        }.contains(selection['completeness']) ||
        (selection['kind'] == 'partial') !=
            (selection['completeness'] == 'partial')) {
      throw const FormatException('Invalid selection.');
    }
    final missing = DiscSetWire.ordinals(selection['missingExpectedOrdinals']);
    final omitted = DiscSetWire.maps(selection['omittedDiscs']);
    DiscSetWire.members(
      omitted
          .map(
            (d) => (
              DiscSetWire.id(d, 'releaseId'),
              DiscSetWire.ordinal(d['ordinal']),
            ),
          )
          .toList(),
      allowEmpty: true,
    );
    if (selection['kind'] == 'all-known' &&
        (missing.isNotEmpty || omitted.isNotEmpty)) {
      throw const FormatException('Missing partial consent.');
    }
    for (final disc in omitted) {
      DiscSetWire.exact(disc, {'releaseId', 'ordinal'});
    }
    final discs = DiscSetWire.maps(input['discs']);
    DiscSetWire.members(
      discs
          .map(
            (d) => (
              DiscSetWire.id(d, 'releaseId'),
              DiscSetWire.ordinal(d['ordinal']),
            ),
          )
          .toList(),
    );
    if (jsonEncode(selected) !=
            jsonEncode(discs.map((d) => d['releaseId']).toList()) ||
        omitted.any((d) => selected.contains(d['releaseId'])))
      throw const FormatException('Selection mismatch.');
    final expected = DiscSetWire.expected(input);
    final knownOrdinals = {
      ...discs.map((d) => d['ordinal']),
      ...omitted.map((d) => d['ordinal']),
    };
    if (knownOrdinals.length != discs.length + omitted.length)
      throw const FormatException('Duplicate known ordinal.');
    final actualMissing = expected == null
        ? <int>[]
        : [
            for (var i = 1; i <= expected; i++)
              if (!knownOrdinals.contains(i)) i,
          ];
    if (jsonEncode(actualMissing) != jsonEncode(missing))
      throw const FormatException('Missing expectation mismatch.');
    final complete =
        expected != null &&
        discs.length == expected &&
        [
          for (var i = 1; i <= expected; i++) i,
        ].every((n) => discs.any((d) => d['ordinal'] == n));
    if (input['hasAllExpectedDiscs'] != complete ||
        (selection['completeness'] == 'complete') !=
            (complete && selection['kind'] == 'all-known') ||
        (selection['completeness'] == 'unknown-total' && expected != null))
      throw const FormatException('Completeness mismatch.');
    final paths = <String>{};
    final files = <DiscSetFile>[];
    final grantFreeDiscs = <Map<String, Object?>>[];
    var totalBytes = 0;
    for (final disc in discs) {
      DiscSetWire.exact(
        disc,
        {'releaseId', 'ordinal', 'name', 'entryPoint', 'files'},
        optional: {'layoutId', 'layoutRevision'},
      );
      DiscSetWire.text(disc, 'name');
      final layoutId = DiscSetWire.nullableText(disc, 'layoutId');
      final layoutRevision = DiscSetWire.nullableText(disc, 'layoutRevision');
      if ((layoutId == null) != (layoutRevision == null))
        throw const FormatException('Incoherent layout identity.');
      final entry = DiscSetWire.path(disc, 'entryPoint');
      if (!const ['.cue', '.iso', '.img'].any(entry.toLowerCase().endsWith)) {
        throw const FormatException('Unsupported original entry point.');
      }
      final ownFiles = DiscSetWire.maps(disc['files']);
      if (ownFiles.isEmpty || !ownFiles.any((f) => f['relativePath'] == entry))
        throw const FormatException('Missing entry point.');
      final stripped = <Map<String, Object?>>[];
      for (final file in ownFiles) {
        DiscSetWire.exact(file, {
          'relativePath',
          'sizeBytes',
          'sha256',
          'provenance',
          if (!persisted) 'contentGrant',
        });
        final path = DiscSetWire.path(file, 'relativePath');
        if (!paths.add(path.toLowerCase()))
          throw const FormatException('Colliding paths.');
        final size = DiscSetWire.bytes(file['sizeBytes']);
        totalBytes += size;
        if (totalBytes > 9223372036854775807)
          throw const FormatException('Size overflow.');
        final hash = DiscSetWire.digest(file, 'sha256');
        final provenance = ContentFileProvenance.parse(file['provenance']);
        if (provenance == ContentFileProvenance.derived &&
            (!path.toLowerCase().endsWith('.cue') || layoutId != null)) {
          throw const FormatException('Incoherent derived disc descriptor.');
        }
        Uri? url;
        DateTime? expiry;
        if (!persisted) {
          final grant = DiscSetWire.map(file['contentGrant']);
          DiscSetWire.exact(grant, {'downloadUrl', 'expiresAt'});
          final urlText = DiscSetWire.text(grant, 'downloadUrl');
          final route = provenance == ContentFileProvenance.derived
              ? 'derived-descriptor'
              : '(?:content|original-layout)';
          if (!RegExp(
            '^/delivery/$route/[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\$',
          ).hasMatch(urlText))
            throw const FormatException('Unsafe grant.');
          url = origin.resolve(urlText);
          expiry = DateTime.tryParse(DiscSetWire.text(grant, 'expiresAt'));
          if (expiry == null ||
              !RegExp(
                r'(Z|[+-]\d\d:\d\d)$',
              ).hasMatch(grant['expiresAt']! as String))
            throw const FormatException('Invalid expiry.');
        } else if (file.containsKey('contentGrant')) {
          throw const FormatException('Persisted capability.');
        }
        files.add(DiscSetFile(path, size, hash, provenance, url, expiry));
        stripped.add({
          'relativePath': path,
          'sizeBytes': size.toString(),
          'sha256': hash,
          'provenance': provenance.wireName,
        });
      }
      grantFreeDiscs.add({...disc, 'files': stripped});
    }
    final playlist = DiscSetWire.map(input['playlist']);
    DiscSetWire.exact(playlist, {
      'artifactId',
      'relativePath',
      'sha256',
      'sizeBytes',
      'encoding',
      'text',
    });
    final playlistPath = DiscSetWire.path(playlist, 'relativePath');
    if (playlistPath != 'playlist.m3u' || playlist['encoding'] != 'utf-8')
      throw const FormatException('Unsupported playlist.');
    final text = DiscSetWire.text(playlist, 'text');
    final bytes = utf8.encode(text);
    final hash = DiscSetWire.digest(playlist, 'sha256');
    if (bytes.length > 1024 * 1024 ||
        bytes.length != DiscSetWire.bytes(playlist['sizeBytes']) ||
        sha256.convert(bytes).toString() != hash ||
        playlist['artifactId'] != 'm3u-sha256:$hash' ||
        text != '${discs.map((d) => d['entryPoint']).join('\n')}\n' ||
        !paths.add(playlistPath))
      throw const FormatException('Playlist mismatch.');
    for (final path in paths) {
      if (paths.any((other) => other != path && other.startsWith('$path/')))
        throw const FormatException('File/directory conflict.');
    }
    files.add(
      DiscSetFile(
        playlistPath,
        bytes.length,
        hash,
        ContentFileProvenance.derived,
        null,
        null,
      ),
    );
    return DiscSetPlan._(
      DiscSetWire.freeze({...input, 'discs': grantFreeDiscs})
          as Map<String, Object?>,
      List.unmodifiable(files),
      text,
    );
  }
}

final class DiscSetFile {
  const DiscSetFile(
    this.relativePath,
    this.sizeBytes,
    this.sha256,
    this.provenance,
    this.url,
    this.expiresAt,
  );
  final String relativePath, sha256;
  final ContentFileProvenance provenance;
  final int sizeBytes;
  final Uri? url;
  final DateTime? expiresAt;
}

abstract final class DiscSetWire {
  static void exact(
    Map<String, Object?> v,
    Set<String> required, {
    Set<String> optional = const {},
  }) {
    if (!required.every(v.containsKey) ||
        v.keys.any((k) => !required.contains(k) && !optional.contains(k)))
      throw const FormatException('Unsupported contract fields.');
  }

  static Object? canonical(Object? value) {
    if (value is Map<String, Object?>) {
      final keys = value.keys.toList()..sort();
      return {for (final key in keys) key: canonical(value[key])};
    }
    if (value is List<Object?>) return value.map(canonical).toList();
    return value;
  }

  static Object? freeze(Object? value) {
    if (value is Map<String, Object?>)
      return Map<String, Object?>.unmodifiable({
        for (final key in value.keys) key: freeze(value[key]),
      });
    if (value is List<Object?>)
      return List<Object?>.unmodifiable(value.map(freeze));
    return value;
  }

  static Map<String, Object?> map(Object? value) {
    if (value is! Map<String, Object?>)
      throw const FormatException('Expected object.');
    return value;
  }

  static List<Map<String, Object?>> maps(Object? value) {
    if (value is! List<Object?>) throw const FormatException('Expected list.');
    return value.map(map).toList(growable: false);
  }

  static void version(Map<String, Object?> v) {
    if (v['contractVersion'] is! int || v['contractVersion'] != 1)
      throw const FormatException('Unsupported disc-set contract.');
  }

  static String text(Map<String, Object?> v, String k) {
    final s = v[k];
    if (s is! String || s.trim().isEmpty || s.length > 1024 * 1024)
      throw const FormatException('Expected text.');
    return s;
  }

  static String? nullableText(Map<String, Object?> v, String k) =>
      v[k] == null ? null : text(v, k);
  static String id(Map<String, Object?> v, String k) {
    final s = text(v, k);
    if (RomdPublicId.tryParse(s) == null)
      throw const FormatException('Invalid public id.');
    return s;
  }

  static String server(Map<String, Object?> v, String k) {
    final s = text(v, k);
    if (RomdServerInstanceId.tryParse(s) == null)
      throw const FormatException('Invalid server.');
    return s;
  }

  static String system(Map<String, Object?> v) {
    final s = text(v, 'systemKey');
    if (!isSystemKey(s)) throw const FormatException('Invalid system.');
    return s;
  }

  static String guid(Map<String, Object?> v, String k) {
    final s = text(v, k);
    if (!RegExp(
      r'^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$',
    ).hasMatch(s))
      throw const FormatException('Invalid revision.');
    return s;
  }

  static String digest(Map<String, Object?> v, String k) {
    final s = text(v, k);
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(s))
      throw const FormatException('Invalid digest.');
    return s;
  }

  static int ordinal(Object? v) {
    if (v is! int || v < 1 || v > 99)
      throw const FormatException('Invalid ordinal.');
    return v;
  }

  static int? expected(Map<String, Object?> v) =>
      v['expectedDiscCount'] == null ? null : ordinal(v['expectedDiscCount']);
  static List<int> ordinals(Object? v) {
    if (v is! List<Object?>) throw const FormatException('Expected ordinals.');
    final result = v.map(ordinal).toList();
    if (result.toSet().length != result.length)
      throw const FormatException('Duplicate ordinal.');
    return result;
  }

  static List<String> strings(Object? v) {
    if (v is! List<Object?> ||
        v.any((x) => x is! String || RomdPublicId.tryParse(x) == null))
      throw const FormatException('Expected identities.');
    return v.cast<String>();
  }

  static int bytes(Object? v) {
    if (v is! String || !RegExp(r'^(0|[1-9]\d*)$').hasMatch(v))
      throw const FormatException('Expected decimal byte count.');
    final n = int.tryParse(v);
    if (n == null || n < 0 || n > 9223372036854775807)
      throw const FormatException('Byte count overflow.');
    return n;
  }

  static void members(List<(String, int)> members, {bool allowEmpty = false}) {
    if ((!allowEmpty && members.isEmpty) ||
        members.length > 99 ||
        members.map((d) => d.$1).toSet().length != members.length ||
        members.map((d) => d.$2).toSet().length != members.length)
      throw const FormatException('Invalid membership.');
    for (var i = 1; i < members.length; i++) {
      if (members[i - 1].$2 >= members[i].$2)
        throw const FormatException('Unordered membership.');
    }
  }

  static String path(Map<String, Object?> v, String k) {
    final s = text(v, k);
    if (s.startsWith('/') ||
        s.contains(r'\') ||
        s.contains(RegExp(r'[<>:"|?*\x00-\x1f\x7f]')) ||
        s
            .split('/')
            .any(
              (p) =>
                  p.isEmpty ||
                  p == '.' ||
                  p == '..' ||
                  p.endsWith('.') ||
                  p.endsWith(' ') ||
                  RegExp(
                    r'^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)',
                    caseSensitive: false,
                  ).hasMatch(p),
            ))
      throw const FormatException('Unsafe portable path.');
    return s;
  }
}
