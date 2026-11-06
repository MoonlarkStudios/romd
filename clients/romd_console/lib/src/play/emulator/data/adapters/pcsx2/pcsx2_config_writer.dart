import 'dart:io';

import 'package:romd_console/src/play/emulator/data/adapters/ini/ini_document.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';

import 'pcsx2_user_directory.dart';

const _managedGameControllerMappingMarker = '# ROMD managed controller mapping';
const _managedGameControllerMappingEndMarker =
    '# End ROMD managed controller mapping';

abstract interface class Pcsx2ConfigWriter {
  Future<void> write({
    required Pcsx2UserDirectory userDirectory,
    required String? executablePath,
    String? saveRoot,
    String? stateRoot,
  });
}

/// Maintains ROMD's non-controller PCSX2 settings without claiming ownership
/// of PCSX2's controller ports or bindings.
///
/// The rejected controller experiment wrote a framed SDL database entry. Its
/// cleanup remains here so ordinary PCSX2 input cannot inherit that stale raw
/// mapping. Existing Pad and InputSources sections are deliberately preserved.
final class Pcsx2IniConfigWriter implements Pcsx2ConfigWriter {
  const Pcsx2IniConfigWriter({
    ProcessRunner processRunner = const SystemProcessRunner(),
  }) : _processRunner = processRunner;

  final ProcessRunner _processRunner;

  @override
  Future<void> write({
    required Pcsx2UserDirectory userDirectory,
    required String? executablePath,
    String? saveRoot,
    String? stateRoot,
  }) async {
    if ((saveRoot == null) != (stateRoot == null)) {
      throw ArgumentError('Save and state roots must be provided together.');
    }
    await _createDirectories(userDirectory);
    await _removeManagedGameControllerMapping(userDirectory);
    await _createDefaultConfigWhenMissing(userDirectory, executablePath);

    final settingsFile = File(userDirectory.settingsPath);
    final existingLines = await settingsFile.exists()
        ? await settingsFile.readAsLines()
        : const <String>[];
    final merged =
        IniDocument.parse(
          existingLines,
          sectionNameParsing: IniSectionNameParsing.rawInsideBrackets,
          keyParsing: IniKeyParsing.trimAllowEmptyAfterSeparator,
          missingSettingPlacement: IniMissingSettingPlacement.afterExistingBody,
        ).merge(
          scalarSettings: _romdSettings(
            saveRoot: saveRoot,
            stateRoot: stateRoot,
          ),
        );

    await settingsFile.writeAsString(merged);
  }

  Future<void> _createDirectories(Pcsx2UserDirectory userDirectory) async {
    await Directory(userDirectory.dataDirectoryPath).create(recursive: true);
    await Directory(userDirectory.biosDirectoryPath).create(recursive: true);
    await Directory(
      userDirectory.settingsDirectoryPath,
    ).create(recursive: true);
  }

  Future<void> _createDefaultConfigWhenMissing(
    Pcsx2UserDirectory userDirectory,
    String? executablePath,
  ) async {
    if (executablePath == null ||
        await File(userDirectory.settingsPath).exists()) {
      return;
    }

    await _processRunner.run(
      executable: executablePath,
      arguments: const <String>['-testconfig'],
      workingDirectory: userDirectory.dataDirectoryPath,
      environment: userDirectory.environmentOverrides,
    );
  }

  Future<void> _removeManagedGameControllerMapping(
    Pcsx2UserDirectory userDirectory,
  ) async {
    final database = File(userDirectory.gameControllerDatabasePath);
    if (!await database.exists()) {
      return;
    }
    final existingLines = await database.readAsLines();
    final preservedLines = <String>[];
    var insideManagedMapping = false;
    var foundManagedMapping = false;
    for (final line in existingLines) {
      if (line == _managedGameControllerMappingMarker) {
        if (insideManagedMapping) {
          return;
        }
        insideManagedMapping = true;
        foundManagedMapping = true;
        continue;
      }
      if (line == _managedGameControllerMappingEndMarker) {
        if (!insideManagedMapping) {
          return;
        }
        insideManagedMapping = false;
        continue;
      }
      if (!insideManagedMapping) {
        preservedLines.add(line);
      }
    }
    if (insideManagedMapping || !foundManagedMapping) {
      return;
    }
    if (preservedLines.isEmpty) {
      await database.delete();
      return;
    }
    await database.writeAsString('${preservedLines.join('\n')}\n');
  }

  Map<String, Map<String, String>> _romdSettings({
    String? saveRoot,
    String? stateRoot,
  }) => <String, Map<String, String>>{
    'UI': <String, String>{
      'SettingsVersion': '1',
      'SetupWizardIncomplete': 'false',
      'InhibitScreensaver': 'true',
    },
    'Folders': <String, String>{
      'Bios': 'bios',
      if (saveRoot != null && stateRoot != null) ...<String, String>{
        'MemoryCards': saveRoot,
        'Savestates': stateRoot,
      },
    },
    'EmuCore': <String, String>{'EnableFastBoot': 'true'},
    'AutoUpdater': <String, String>{'CheckAtStartup': 'false'},
  };
}
