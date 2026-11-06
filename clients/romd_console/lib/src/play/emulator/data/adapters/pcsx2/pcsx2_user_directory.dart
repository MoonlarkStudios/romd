import 'dart:io';

import 'package:path/path.dart' as p;

/// ROMD-scoped PCSX2 user directory.
///
/// New PCSX2 builds document `-datapath`, but older Qt builds reject it. ROMD
/// therefore launches with the compatibility CLI and scopes standard paths
/// through environment variables instead.
final class Pcsx2UserDirectory {
  const Pcsx2UserDirectory({
    required Directory root,
    required String operatingSystem,
  }) : _root = root,
       _operatingSystem = operatingSystem;

  final Directory _root;
  final String _operatingSystem;

  String get rootPath => _root.path;

  String get dataDirectoryPath => switch (_operatingSystem) {
    'macos' => p.join(_root.path, 'Library', 'Application Support', 'PCSX2'),
    'windows' => p.join(_root.path, 'PCSX2'),
    _ => p.join(_root.path, 'pcsx2'),
  };

  String get settingsDirectoryPath => p.join(dataDirectoryPath, 'inis');

  String get settingsPath => p.join(settingsDirectoryPath, 'PCSX2.ini');

  String get biosDirectoryPath => p.join(dataDirectoryPath, 'bios');

  String get gameControllerDatabasePath =>
      p.join(dataDirectoryPath, 'game_controller_db.txt');

  Map<String, String> get environmentOverrides => switch (_operatingSystem) {
    'macos' => <String, String>{'HOME': _root.path},
    'windows' => <String, String>{'LOCALAPPDATA': _root.path},
    _ => <String, String>{
      'XDG_CONFIG_HOME': _root.path,
      'XDG_DATA_HOME': _root.path,
    },
  };
}
