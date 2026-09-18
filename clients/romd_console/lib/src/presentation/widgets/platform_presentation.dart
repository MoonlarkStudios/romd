import 'package:flutter/widgets.dart';

import '../reference_catalog_scope.dart';
import '../theme/console_theme_context.dart';

/// Resolved tonal family for an archival cover plate.
final class CoverTone {
  const CoverTone({required this.base, required this.glow, required this.mono});

  factory CoverTone.fromFamily(List<Color> family) {
    if (family.length < 3) {
      throw StateError('A cover-tone family requires base, glow, and mono.');
    }
    return CoverTone(base: family[0], glow: family[1], mono: family[2]);
  }

  final Color base;
  final Color glow;
  final Color mono;
}

/// How a platform reads in the console UI: its full [label], a compact
/// [shortCode] for dense captions (so `PLAT · YEAR` never truncates), and the
/// [tone] family used to render archival cover plates.
final class PlatformPresentation {
  const PlatformPresentation({
    required this.label,
    required this.shortCode,
    required this.tone,
  });

  final String label;
  final String shortCode;
  final CoverTone tone;
}

PlatformPresentation platformPresentationFor(
  BuildContext context, {
  required String platformId,
  required String platformName,
}) {
  final system = ReferenceCatalogScope.of(context)?.system(platformId);
  final shortCode =
      system?['compactLabel'] as String? ?? _acronym(platformName);
  return PlatformPresentation(
    label: system?['name'] as String? ?? platformName,
    shortCode: shortCode,
    tone: _toneFor(
      context.artwork.coverToneFamilies,
      shortCode,
      platformId.isEmpty ? platformName : platformId,
    ),
  );
}

/// A 2–3 letter title monogram for the cover plate. Skips articles and roman
/// numerals so "Breath of Fire II" reads BF, "Chrono Trigger" reads CT.
String monogramFor(String title) {
  final cleaned = title.replaceAll(RegExp("[:,'’\\-]"), ' ');
  final words = cleaned
      .split(RegExp(r'\s+'))
      .where(
        (word) =>
            word.isNotEmpty && !_monogramSkip.contains(word.toLowerCase()),
      )
      .toList(growable: false);

  final initials = words
      .map((word) => word.characters.first)
      .join()
      .toUpperCase();

  if (initials.length > 3) {
    return initials.substring(0, 3);
  }
  if (initials.isNotEmpty) {
    return initials;
  }

  final trimmed = title.trim();
  if (trimmed.isEmpty) {
    return '?';
  }
  return trimmed.characters.take(2).toString().toUpperCase();
}

const _monogramSkip = <String>{
  'of',
  'the',
  'and',
  'a',
  'ii',
  'iii',
  'iv',
  'v',
  'vi',
  'vii',
  'viii',
  'ix',
  'x',
};

String _acronym(String name) {
  final words = name
      .split(RegExp(r'\s+'))
      .where(
        (word) =>
            word.isNotEmpty && !_monogramSkip.contains(word.toLowerCase()),
      )
      .toList(growable: false);

  if (words.length >= 2) {
    final letters = words
        .map((word) => word.characters.first)
        .join()
        .toUpperCase();
    return letters.length > 4 ? letters.substring(0, 4) : letters;
  }

  final compact = name.replaceAll(RegExp(r'\s+'), '');
  if (compact.isEmpty) {
    return '—';
  }
  return compact.characters.take(4).toString().toUpperCase();
}

CoverTone _toneFor(
  List<List<Color>> families,
  String shortCode,
  String hashKey,
) {
  if (families.isEmpty) {
    throw StateError('The active skin has no cover-tone families.');
  }

  final hash = hashKey.codeUnits.fold<int>(
    0,
    (current, codeUnit) => (current * 31 + codeUnit) & 0x7fffffff,
  );
  final familyIndex = _toneFamilyIndexByCode[shortCode] ?? hash;
  return CoverTone.fromFamily(families[familyIndex % families.length]);
}

const _toneFamilyIndexByCode = <String, int>{
  'SNES': 0,
  'SFC': 0,
  'GEN': 1,
  'SMS': 1,
  'NES': 2,
  'FC': 2,
  'GB': 3,
  'GBC': 3,
  'GBA': 3,
  'PS1': 3,
  'PS2': 3,
  'N64': 4,
  'GCN': 4,
  'ARC': 1,
};
