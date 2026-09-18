enum IniMissingSettingPlacement {
  /// Insert managed keys before trailing blank lines in an existing section,
  /// then restore those blank lines.
  beforeTrailingBlankLines,

  /// Append managed keys after the existing section body exactly as-is.
  ///
  /// This preserves PCSX2's current merger behavior, including leaving those
  /// appended keys in the later missing-section pass.
  afterExistingBody,
}

enum IniSectionNameParsing {
  /// Trim the name inside `[section]` and reject an empty section name.
  trimAndRejectEmpty,

  /// Use the bytes inside the brackets after trimming only the full line.
  rawInsideBrackets,
}

enum IniKeyParsing {
  /// Trim the key before `=` and reject an empty key.
  trimAndRejectEmpty,

  /// Trim the key before `=`, but keep an empty key when the separator was
  /// not the first raw character in the line.
  trimAllowEmptyAfterSeparator,
}

final class IniDocument {
  IniDocument._({
    required List<String> preamble,
    required List<_IniSection> sections,
    required IniKeyParsing keyParsing,
    required IniMissingSettingPlacement missingSettingPlacement,
  }) : _preamble = preamble,
       _sections = sections,
       _keyParsing = keyParsing,
       _missingSettingPlacement = missingSettingPlacement;

  factory IniDocument.parse(
    List<String> lines, {
    required IniSectionNameParsing sectionNameParsing,
    required IniKeyParsing keyParsing,
    required IniMissingSettingPlacement missingSettingPlacement,
  }) {
    final preamble = <String>[];
    final sections = <_IniSection>[];
    _IniSection? currentSection;

    for (final line in lines) {
      final sectionName = _parseSection(line, sectionNameParsing);
      if (sectionName != null) {
        currentSection = _IniSection(name: sectionName, header: line);
        sections.add(currentSection);
      } else if (currentSection == null) {
        preamble.add(line);
      } else {
        currentSection.lines.add(line);
      }
    }

    return IniDocument._(
      preamble: preamble,
      sections: sections,
      keyParsing: keyParsing,
      missingSettingPlacement: missingSettingPlacement,
    );
  }

  final List<String> _preamble;
  final List<_IniSection> _sections;
  final IniKeyParsing _keyParsing;
  final IniMissingSettingPlacement _missingSettingPlacement;

  String merge({
    Map<String, Map<String, String>> scalarSettings =
        const <String, Map<String, String>>{},
    Map<String, Map<String, List<String>>> listSettings =
        const <String, Map<String, List<String>>>{},
  }) {
    final remainingScalar = <String, Map<String, String>>{
      for (final entry in scalarSettings.entries)
        entry.key: <String, String>{...entry.value},
    };
    final remainingLists = <String, Map<String, List<String>>>{
      for (final entry in listSettings.entries)
        entry.key: <String, List<String>>{
          for (final listEntry in entry.value.entries)
            listEntry.key: <String>[...listEntry.value],
        },
    };
    final mergedLines = <String>[..._preamble];

    for (final section in _sections) {
      final requiredScalarSection = remainingScalar[section.name];
      final requiredListSection = remainingLists[section.name];
      final emittedScalarKeys = <String>{};
      final emittedListKeys = <String>{};
      final body = <String>[];

      for (final line in section.lines) {
        final key = _parseKey(line, _keyParsing);
        final requiredList = key == null
            ? null
            : listSettings[section.name]?[key];
        if (key != null && requiredList != null) {
          if (emittedListKeys.add(key)) {
            _appendListSetting(body, key, requiredList);
            requiredListSection?.remove(key);
          }
          continue;
        }

        final requiredValue = key == null
            ? null
            : scalarSettings[section.name]?[key];
        if (key != null && requiredValue != null) {
          if (!emittedScalarKeys.add(key)) {
            continue;
          }
          body.add('$key = $requiredValue');
          requiredScalarSection?.remove(key);
        } else {
          body.add(line);
        }
      }

      _appendMissingScalarSettings(body, requiredScalarSection);
      _appendMissingListSettings(body, requiredListSection);
      mergedLines
        ..add(section.header)
        ..addAll(body);
    }

    _appendMissingSections(mergedLines, remainingScalar, remainingLists);
    return '${mergedLines.join('\n')}\n';
  }

  void _appendMissingScalarSettings(
    List<String> body,
    Map<String, String>? missingSettings,
  ) {
    _appendMissingSettings(
      body,
      missingSettings,
      (line, entry) => line.add('${entry.key} = ${entry.value}'),
    );
  }

  void _appendMissingListSettings(
    List<String> body,
    Map<String, List<String>>? missingSettings,
  ) {
    _appendMissingSettings(
      body,
      missingSettings,
      (line, entry) => _appendListSetting(line, entry.key, entry.value),
    );
  }

  void _appendMissingSettings<T>(
    List<String> body,
    Map<String, T>? missingSettings,
    void Function(List<String> body, MapEntry<String, T> entry) append,
  ) {
    if (missingSettings == null || missingSettings.isEmpty) {
      return;
    }

    switch (_missingSettingPlacement) {
      case IniMissingSettingPlacement.beforeTrailingBlankLines:
        final trailingBlankLines = <String>[];
        while (body.isNotEmpty && body.last.trim().isEmpty) {
          trailingBlankLines.insert(0, body.removeLast());
        }

        for (final entry in missingSettings.entries) {
          append(body, entry);
        }
        body.addAll(trailingBlankLines);
        missingSettings.clear();
      case IniMissingSettingPlacement.afterExistingBody:
        for (final entry in missingSettings.entries) {
          append(body, entry);
        }
    }
  }
}

void _appendMissingSections(
  List<String> lines,
  Map<String, Map<String, String>> remainingScalar,
  Map<String, Map<String, List<String>>> remainingLists,
) {
  final missingSections = <String>{
    ...remainingScalar.entries
        .where((entry) => entry.value.isNotEmpty)
        .map((entry) => entry.key),
    ...remainingLists.entries
        .where((entry) => entry.value.isNotEmpty)
        .map((entry) => entry.key),
  }.toList(growable: false);
  if (missingSections.isNotEmpty && lines.isNotEmpty) {
    lines.add('');
  }

  for (var i = 0; i < missingSections.length; i++) {
    final section = missingSections[i];
    lines.add('[$section]');
    final scalarEntries = remainingScalar[section]?.entries;
    if (scalarEntries != null) {
      for (final entry in scalarEntries) {
        lines.add('${entry.key} = ${entry.value}');
      }
    }
    final listEntries = remainingLists[section]?.entries;
    if (listEntries != null) {
      for (final entry in listEntries) {
        _appendListSetting(lines, entry.key, entry.value);
      }
    }
    if (i != missingSections.length - 1) {
      lines.add('');
    }
  }
}

void _appendListSetting(List<String> lines, String key, List<String> values) {
  for (final value in values) {
    lines.add('$key = $value');
  }
}

String? _parseSection(String line, IniSectionNameParsing parsing) {
  final trimmed = line.trim();
  if (!trimmed.startsWith('[') || !trimmed.endsWith(']')) {
    return null;
  }

  final section = trimmed.substring(1, trimmed.length - 1);
  return switch (parsing) {
    IniSectionNameParsing.trimAndRejectEmpty => switch (section.trim()) {
      final parsed when parsed.isNotEmpty => parsed,
      _ => null,
    },
    IniSectionNameParsing.rawInsideBrackets => section,
  };
}

String? _parseKey(String line, IniKeyParsing parsing) {
  final separatorIndex = line.indexOf('=');
  if (separatorIndex <= 0) {
    return null;
  }

  final key = line.substring(0, separatorIndex).trim();
  return switch (parsing) {
    IniKeyParsing.trimAndRejectEmpty => key.isEmpty ? null : key,
    IniKeyParsing.trimAllowEmptyAfterSeparator => key,
  };
}

final class _IniSection {
  _IniSection({required this.name, required this.header});

  final String name;
  final String header;
  final List<String> lines = <String>[];
}
