import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/play/content/data/content_file_store.dart';

final class _RejectingProbe implements TargetFilesystemPathProbe {
  List<String> paths = <String>[];

  @override
  Future<bool> pathsAreDistinct({
    required Directory scratch,
    required Iterable<String> relativePaths,
    required File Function(Directory root, String relativePath) resolveWithin,
  }) async {
    paths = relativePaths.toList(growable: false);
    return false;
  }
}

void main() {
  late Directory base;
  late ContentFileStore store;

  setUp(() {
    base = Directory.systemTemp.createTempSync('romd_cfs_test');
    store = ContentFileStore(
      baseDir: base,
      serverInstanceId: '11111111-1111-4111-8111-111111111111',
    );
  });

  tearDown(() {
    if (base.existsSync()) {
      base.deleteSync(recursive: true);
    }
  });

  test('content root is keyed by server/platform/title/release', () {
    final root = store.contentRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
      releaseId: 'rel-1',
    );
    expect(
      root.path,
      p.join(
        base.path,
        'content',
        'servers',
        '11111111-1111-4111-8111-111111111111',
        'snes',
        'title-1',
        'rel-1',
      ),
    );
  });

  test('staging and backup are siblings of the content root', () {
    final content = store.contentRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
      releaseId: 'rel-1',
    );
    final staging = store.stagingRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
      releaseId: 'rel-1',
    );
    final backup = store.backupRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
      releaseId: 'rel-1',
    );

    expect(p.dirname(staging.path), p.dirname(content.path));
    expect(p.dirname(backup.path), p.dirname(content.path));
    expect(staging.path, '${content.path}.staging');
    expect(backup.path, '${content.path}.backup');
  });

  test('saves and states are profile/title scoped while config is shared', () {
    final save = store.saveRoot(
      localProfileId: 'profile-1',
      platformShortName: 'snes',
      titleId: 'title-1',
    );
    final state = store.stateRoot(
      localProfileId: 'profile-1',
      platformShortName: 'snes',
      titleId: 'title-1',
    );
    final config = store.configRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
    );

    expect(
      save.path,
      p.join(
        base.path,
        'profiles',
        'profile-1',
        'servers',
        '11111111-1111-4111-8111-111111111111',
        'games',
        'snes',
        'title-1',
        'saves',
      ),
    );
    expect(
      state.path,
      p.join(
        base.path,
        'profiles',
        'profile-1',
        'servers',
        '11111111-1111-4111-8111-111111111111',
        'games',
        'snes',
        'title-1',
        'states',
      ),
    );
    expect(
      config.path,
      p.join(
        base.path,
        'config',
        'servers',
        '11111111-1111-4111-8111-111111111111',
        'snes',
        'title-1',
      ),
    );

    final contentTree = p.join(base.path, 'content');
    expect(p.isWithin(contentTree, save.path), isFalse);
    expect(p.isWithin(contentTree, state.path), isFalse);
    expect(p.isWithin(contentTree, config.path), isFalse);
  });

  test('profiles are isolated and releases of one title share roots', () {
    final first = store.saveRoot(
      localProfileId: 'profile-1',
      platformShortName: 'snes',
      titleId: 'title-1',
    );
    final secondProfile = store.saveRoot(
      localProfileId: 'profile-2',
      platformShortName: 'snes',
      titleId: 'title-1',
    );

    expect(first.path, isNot(secondProfile.path));
    expect(first.path, isNot(contains('rel-1')));
    expect(first.path, isNot(contains('rel-2')));
  });

  test('resolveWithin joins a safe relative path under the root', () {
    final root = store.contentRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
      releaseId: 'rel-1',
    );
    final file = store.resolveWithin(root, 'chrono/chrono-trigger.sfc');

    expect(file.path, p.join(root.path, 'chrono', 'chrono-trigger.sfc'));
    expect(p.isWithin(root.path, file.path), isTrue);
  });

  test('resolveWithin rejects traversal and absolute paths', () {
    final root = store.contentRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
      releaseId: 'rel-1',
    );

    expect(
      () => store.resolveWithin(root, '../escape.sfc'),
      throwsArgumentError,
    );
    expect(
      () => store.resolveWithin(root, 'a/../../escape.sfc'),
      throwsArgumentError,
    );
    expect(() => store.resolveWithin(root, '/etc/passwd'), throwsArgumentError);
    expect(() => store.resolveWithin(root, ''), throwsArgumentError);
  });

  test('path-segment keys reject separators and traversal', () {
    expect(
      () => store.contentRoot(
        platformShortName: 'sn/es',
        titleId: 'title-1',
        releaseId: 'rel-1',
      ),
      throwsArgumentError,
    );
    expect(
      () => store.saveRoot(
        localProfileId: 'profile-1',
        platformShortName: 'snes',
        titleId: '..',
      ),
      throwsArgumentError,
    );
    expect(
      () => store.stateRoot(
        localProfileId: '../profile-1',
        platformShortName: 'snes',
        titleId: 'title-1',
      ),
      throwsArgumentError,
    );
  });

  test('same ids on another instance resolve to a disjoint namespace', () {
    final other = ContentFileStore(
      baseDir: base,
      serverInstanceId: '22222222-2222-4222-8222-222222222222',
    );
    final first = store.contentRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
      releaseId: 'release-1',
    );
    final second = other.contentRoot(
      platformShortName: 'snes',
      titleId: 'title-1',
      releaseId: 'release-1',
    );

    expect(first.path, isNot(second.path));
  });

  test('target-filesystem path probe runs before staging', () async {
    final probe = _RejectingProbe();
    final probed = ContentFileStore(
      baseDir: base,
      serverInstanceId: '11111111-1111-4111-8111-111111111111',
      pathProbe: probe,
    );

    expect(
      await probed.pathsAreDistinctOnTarget(
        scratch: Directory(p.join(base.path, 'preflight')),
        relativePaths: const <String>['Café.rom', 'Café.rom'],
      ),
      isFalse,
    );
    expect(probe.paths, hasLength(2));
    expect(Directory(p.join(base.path, 'preflight')).existsSync(), isFalse);
  });

  test('canonical scan rejects a symlinked server instance root', () async {
    final external = Directory(p.join(base.path, 'external'))
      ..createSync(recursive: true);
    final title = RomdPublicId.encode(201);
    final release = RomdPublicId.encode(101);
    Directory(
      p.join(external.path, 'snes', title, release),
    ).createSync(recursive: true);
    store.serverContentRoot.parent.createSync(recursive: true);
    await Link(store.serverContentRoot.path).create(external.path);

    expect(await store.scanCanonicalArtifacts(), isEmpty);
  });

  test('canonical scan rejects a symlinked artifact root', () async {
    final external = Directory(p.join(base.path, 'external'))
      ..createSync(recursive: true);
    final title = RomdPublicId.encode(201);
    final release = RomdPublicId.encode(101);
    final artifact = store.contentRoot(
      platformShortName: 'snes',
      titleId: title,
      releaseId: release,
    );
    artifact.parent.createSync(recursive: true);
    await Link(artifact.path).create(external.path);

    expect(await store.scanCanonicalArtifacts(), isEmpty);
  });
}
