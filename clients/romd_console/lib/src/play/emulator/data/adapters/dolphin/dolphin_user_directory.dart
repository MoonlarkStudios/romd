import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/session/domain/play_target.dart';

/// ROMD-owned shared Dolphin user directory.
///
/// Dolphin's `-u` switch scopes all mutable configuration beneath this root.
/// Native memory-card data and save states are linked to the active title's
/// durable save/state roots immediately before that title launches. Dolphin's
/// settings UI may create empty `GC` and `StateSaves` directory skeletons;
/// ROMD may replace only those data-free skeletons with its managed links.
final class DolphinUserDirectory {
  const DolphinUserDirectory({required this.root});

  final Directory root;

  String get rootPath => root.path;
  String get configDirectoryPath => p.join(rootPath, 'Config');
  String get controllerSettingsPath =>
      p.join(configDirectoryPath, 'GCPadNew.ini');
  String get settingsPath => p.join(configDirectoryPath, 'Dolphin.ini');
  String get hotkeySettingsPath => p.join(configDirectoryPath, 'Hotkeys.ini');
  String get gameCubeSavePath => p.join(rootPath, 'GC');
  String get stateSavePath => p.join(rootPath, 'StateSaves');
  String get screenshotPath => p.join(rootPath, 'ScreenShots');

  Iterable<String> get directories => <String>[
    rootPath,
    configDirectoryPath,
    screenshotPath,
  ];

  Future<void> prepareBase() async {
    for (final path in directories) {
      await Directory(path).create(recursive: true);
    }
  }

  Future<void> prepareForTarget(ResolvedPlayTarget target) async {
    await prepareBase();
    await Directory(target.saveRoot).create(recursive: true);
    await Directory(target.stateRoot).create(recursive: true);
    await _ensureLink(gameCubeSavePath, target.saveRoot);
    await _ensureLink(stateSavePath, target.stateRoot);
  }

  static Future<void> _ensureLink(String linkPath, String targetPath) async {
    final type = await FileSystemEntity.type(linkPath, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      await Link(linkPath).create(p.absolute(targetPath));
      return;
    }
    if (type == FileSystemEntityType.directory) {
      await _replaceEmptyDirectoryTreeWithLink(linkPath, targetPath);
      return;
    }
    if (type != FileSystemEntityType.link) {
      throw FileSystemException(
        'Dolphin durable path contains data Ottercade will not replace.',
        linkPath,
      );
    }
    final existingTarget = await Link(linkPath).target();
    final resolvedExisting = p.normalize(
      p.isAbsolute(existingTarget)
          ? existingTarget
          : p.join(p.dirname(linkPath), existingTarget),
    );
    if (resolvedExisting == p.normalize(p.absolute(targetPath))) {
      return;
    }
    await Link(linkPath).delete();
    await Link(linkPath).create(p.absolute(targetPath));
  }

  static Future<void> _replaceEmptyDirectoryTreeWithLink(
    String linkPath,
    String targetPath,
  ) async {
    final displaced = Directory(
      '$linkPath.romd-empty-$pid-${DateTime.now().microsecondsSinceEpoch}',
    );
    await Directory(linkPath).rename(displaced.path);
    final deletedDirectories = <String>[];
    var createdManagedLink = false;
    try {
      final emptyDirectories = await _emptyDirectoriesDeepestFirst(displaced);
      await Link(linkPath).create(p.absolute(targetPath));
      createdManagedLink = true;
      for (final directory in emptyDirectories) {
        await directory.delete();
        deletedDirectories.add(
          p.relative(directory.path, from: displaced.path),
        );
      }
    } on Object {
      if (createdManagedLink &&
          await FileSystemEntity.type(linkPath, followLinks: false) ==
              FileSystemEntityType.link) {
        await Link(linkPath).delete();
      }
      if (await FileSystemEntity.type(displaced.path, followLinks: false) ==
              FileSystemEntityType.directory &&
          await FileSystemEntity.type(linkPath, followLinks: false) ==
              FileSystemEntityType.notFound) {
        await displaced.rename(linkPath);
        for (final relativePath in deletedDirectories.reversed) {
          if (relativePath != '.') {
            await Directory(
              p.join(linkPath, relativePath),
            ).create(recursive: true);
          }
        }
      }
      rethrow;
    }
  }

  static Future<List<Directory>> _emptyDirectoriesDeepestFirst(
    Directory root,
  ) async {
    final directories = <Directory>[];
    await for (final entity in root.list(recursive: true, followLinks: false)) {
      if (entity is! Directory) {
        throw FileSystemException(
          'Dolphin durable path contains data Ottercade will not replace.',
          root.path,
        );
      }
      directories.add(entity);
    }
    directories.sort(
      (left, right) => right.path.length.compareTo(left.path.length),
    );
    return <Directory>[...directories, root];
  }
}
