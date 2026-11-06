import 'dart:io';

import 'package:path/path.dart' as p;

/// Computes the DuckStation user directory under a ROMD-owned root. DuckStation
/// has no libretro-style `--config` equivalent; it discovers its user directory
/// from platform conventions, so ROMD starts it with scoped environment
/// overrides and places BIOS files in the resulting `bios` directory.
final class DuckStationUserDirectory {
  const DuckStationUserDirectory({
    required Directory root,
    required String operatingSystem,
  }) : _root = root,
       _operatingSystem = operatingSystem;

  final Directory _root;
  final String _operatingSystem;

  String get rootPath => _root.path;

  String get dataDirectoryPath => switch (_operatingSystem) {
    'macos' => p.join(
      _root.path,
      'Library',
      'Application Support',
      'DuckStation',
    ),
    'windows' => p.join(_root.path, 'DuckStation'),
    _ => p.join(_root.path, 'duckstation'),
  };

  String get biosDirectoryPath => p.join(dataDirectoryPath, 'bios');

  String get settingsPath => p.join(dataDirectoryPath, 'settings.ini');

  /// DuckStation loads SDL mapping overrides from this user-owned database.
  String get gameControllerDatabasePath =>
      p.join(dataDirectoryPath, 'gamecontrollerdb.txt');

  Map<String, String> get environmentOverrides => switch (_operatingSystem) {
    'macos' => <String, String>{'HOME': _root.path},
    'windows' => <String, String>{'LOCALAPPDATA': _root.path},
    _ => <String, String>{'XDG_DATA_HOME': _root.path},
  };
}
