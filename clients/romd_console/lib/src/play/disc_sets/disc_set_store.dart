import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/content_file_store.dart';

import 'domain/disc_set_models.dart';

/// Separate top-level namespace: existing release scanning/eviction cannot
/// mistake a group snapshot for a release. Saves are edition/set isolated.
final class DiscSetFileStore {
  DiscSetFileStore(this.baseDir, this.serverInstanceId);
  final Directory baseDir;
  final String serverInstanceId;
  Directory root(DiscSetPlan plan) {
    if (plan.serverInstanceId != serverInstanceId)
      throw StateError('Foreign server.');
    DiscSetWire.server({'server': serverInstanceId}, 'server');
    DiscSetWire.id({'set': plan.discSetId}, 'set');
    DiscSetWire.digest({'snapshot': plan.snapshotId}, 'snapshot');
    return Directory(
      p.join(
        baseDir.path,
        'disc-sets',
        'servers',
        serverInstanceId,
        plan.discSetId,
        plan.snapshotId,
      ),
    );
  }

  Directory staging(DiscSetPlan plan) =>
      Directory('${root(plan).path}.staging');
  Directory scratch(DiscSetPlan plan) =>
      Directory('${root(plan).path}.preflight');
  Directory durable(DiscSetPlan plan, String profile, String kind) {
    if (!const {'saves', 'states', 'config'}.contains(kind) ||
        profile.isEmpty ||
        profile.contains(RegExp(r'[/\\]')) ||
        profile == '.' ||
        profile == '..')
      throw ArgumentError('Unsafe root.');
    root(plan);
    return Directory(
      p.join(
        baseDir.path,
        'profiles',
        profile,
        'servers',
        serverInstanceId,
        'disc-sets',
        plan.discSetId,
        kind,
      ),
    );
  }

  File file(Directory root, String path) {
    DiscSetWire.path({'path': path}, 'path');
    return File(p.join(root.path, p.joinAll(path.split('/'))));
  }

  Future<void> assertSafe(Directory root, {bool allowFiles = false}) async {
    final base = p.normalize(p.absolute(baseDir.path));
    final absolute = p.normalize(p.absolute(root.path));
    if (!p.isWithin(base, absolute))
      throw const FileSystemException('Unsafe group root.');
    if (await FileSystemEntity.type(base, followLinks: false) !=
        FileSystemEntityType.directory)
      throw const FileSystemException('Unsafe group base.');
    var current = base;
    for (final segment in p.split(p.relative(absolute, from: base))) {
      current = p.join(current, segment);
      final type = await FileSystemEntity.type(current, followLinks: false);
      if (type != FileSystemEntityType.notFound &&
          type != FileSystemEntityType.directory)
        throw const FileSystemException('Unsafe group parent.');
    }
    if (allowFiles && await root.exists()) {
      await for (final entry in root.list(
        recursive: true,
        followLinks: false,
      )) {
        final type = await FileSystemEntity.type(
          entry.path,
          followLinks: false,
        );
        if (type != FileSystemEntityType.directory &&
            type != FileSystemEntityType.file)
          throw const FileSystemException('Unsafe group entry.');
      }
    }
  }

  /// Canonical filesystem paths account for macOS normalization without
  /// accepting unverified sidecars from an interrupted/contaminated directory.
  Future<bool> hasOnlyExpectedFiles(
    Directory root,
    Iterable<String> paths,
  ) async {
    final expected = <String>{};
    for (final path in paths) {
      expected.add(await file(root, path).resolveSymbolicLinks());
    }
    final actual = <String>{};
    await for (final entry in root.list(recursive: true, followLinks: false)) {
      final type = await FileSystemEntity.type(entry.path, followLinks: false);
      if (type == FileSystemEntityType.directory) continue;
      if (type != FileSystemEntityType.file) return false;
      actual.add(await entry.resolveSymbolicLinks());
    }
    return actual.length == expected.length && actual.containsAll(expected);
  }

  Future<bool> distinct(DiscSetPlan plan) =>
      ContentFileStore(
        baseDir: baseDir,
        serverInstanceId: serverInstanceId,
      ).pathsAreDistinctOnTarget(
        scratch: scratch(plan),
        relativePaths: plan.files.map((f) => f.relativePath),
      );
}
