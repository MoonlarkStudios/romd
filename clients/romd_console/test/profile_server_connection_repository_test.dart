import 'package:drift/drift.dart' hide isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/local_profiles/profile_server_connection_repository.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

void main() {
  const instanceA = '11111111-1111-4111-8111-111111111111';
  const instanceB = '22222222-2222-4222-8222-222222222222';
  late AppDatabase database;
  late DriftProfileServerConnectionRepository repository;
  var now = DateTime.utc(2026, 7, 17, 12);

  setUp(() async {
    database = AppDatabase(NativeDatabase.memory());
    repository = DriftProfileServerConnectionRepository(
      database: database,
      now: () => now,
    );
    await _insertProfile(database, 'profile-1', now);
  });

  tearDown(() => database.close());

  test(
    'read distinguishes missing profile from an empty valid state',
    () async {
      expect(
        await repository.read('missing'),
        isA<ProfileServerConnectionProfileNotFound>(),
      );

      final result = await repository.read('profile-1');
      final state = (result as ProfileServerConnectionFound).state;
      expect(state.generation, 0);
      expect(state.selectedConnection, isNull);
      expect(state.pendingLocator, isNull);
    },
  );

  test(
    'pending locator replacement increments generation and defeats ABA',
    () async {
      final first = await repository.setPendingLocator(
        localProfileId: 'profile-1',
        origin: Uri.parse('HTTPS://Example.COM:443'),
      );
      final second = await repository.setPendingLocator(
        localProfileId: 'profile-1',
        origin: Uri.parse('https://other.example'),
      );
      final third = await repository.setPendingLocator(
        localProfileId: 'profile-1',
        origin: Uri.parse('https://example.com'),
      );

      expect((first as ProfileServerMutationApplied).generation, 1);
      expect((second as ProfileServerMutationApplied).generation, 2);
      expect((third as ProfileServerMutationApplied).generation, 3);
      final state =
          (await repository.read('profile-1') as ProfileServerConnectionFound)
              .state;
      expect(state.pendingLocator?.origin.toString(), 'https://example.com');

      final stale = await repository.activateDiscoveredServer(
        localProfileId: 'profile-1',
        expectedGeneration: 1,
        expectedPendingOrigin: Uri.parse('https://example.com'),
        instanceId: RomdServerInstanceId.tryParse(instanceA)!,
      );
      expect(stale, isA<ProfileServerMutationStale>());
      expect(await database.select(database.serverConnections).get(), isEmpty);
    },
  );

  test('attempt requires the exact generation and normalized origin', () async {
    await repository.setPendingLocator(
      localProfileId: 'profile-1',
      origin: Uri.parse('https://example.com'),
    );
    now = now.add(const Duration(minutes: 1));

    expect(
      await repository.recordPendingAttempt(
        localProfileId: 'profile-1',
        expectedGeneration: 0,
        expectedOrigin: Uri.parse('https://example.com'),
      ),
      isFalse,
    );
    expect(
      await repository.recordPendingAttempt(
        localProfileId: 'profile-1',
        expectedGeneration: 1,
        expectedOrigin: Uri.parse('https://wrong.example'),
      ),
      isFalse,
    );
    expect(
      await repository.recordPendingAttempt(
        localProfileId: 'profile-1',
        expectedGeneration: 1,
        expectedOrigin: Uri.parse('https://example.com'),
      ),
      isTrue,
    );

    final state =
        (await repository.read('profile-1') as ProfileServerConnectionFound)
            .state;
    expect(state.pendingLocator?.lastAttemptAt?.isAtSameMomentAs(now), isTrue);
  });

  test('activation atomically selects a discovered instance', () async {
    await repository.setPendingLocator(
      localProfileId: 'profile-1',
      origin: Uri.parse('https://canonical.example'),
    );
    now = now.add(const Duration(minutes: 1));

    final activated = await repository.activateDiscoveredServer(
      localProfileId: 'profile-1',
      expectedGeneration: 1,
      expectedPendingOrigin: Uri.parse('https://canonical.example'),
      instanceId: RomdServerInstanceId.tryParse(instanceA)!,
    );

    expect((activated as ProfileServerMutationApplied).generation, 2);
    final state =
        (await repository.read('profile-1') as ProfileServerConnectionFound)
            .state;
    expect(state.generation, 2);
    expect(state.pendingLocator, isNull);
    expect(state.selectedConnection?.instanceId.value, instanceA);
    expect(
      state.selectedConnection?.origin.toString(),
      'https://canonical.example',
    );
  });

  test('wrong pending origin makes zero activation writes', () async {
    await repository.setPendingLocator(
      localProfileId: 'profile-1',
      origin: Uri.parse('https://expected.example'),
    );

    final result = await repository.activateDiscoveredServer(
      localProfileId: 'profile-1',
      expectedGeneration: 1,
      expectedPendingOrigin: Uri.parse('https://wrong.example'),
      instanceId: RomdServerInstanceId.tryParse(instanceA)!,
    );

    expect(result, isA<ProfileServerMutationStale>());
    expect(await database.select(database.serverConnections).get(), isEmpty);
    final profile =
        (await database.select(database.localProfiles).get()).single;
    expect(profile.serverSelectionGeneration, 1);
    expect(profile.selectedServerInstanceId, isNull);
    final pending =
        (await database.select(database.pendingServerLocators).get()).single;
    expect(pending.normalizedOrigin, 'https://expected.example');
  });

  test('rediscovery moves one instance while preserving firstSeenAt', () async {
    await _activate(
      repository,
      profileId: 'profile-1',
      generation: 0,
      origin: 'https://first.example',
      instanceId: instanceA,
    );
    final first =
        (await database.select(database.serverConnections).get()).single;
    now = now.add(const Duration(hours: 1));
    await repository.setPendingLocator(
      localProfileId: 'profile-1',
      origin: Uri.parse('https://moved.example'),
    );
    final activated = await repository.activateDiscoveredServer(
      localProfileId: 'profile-1',
      expectedGeneration: 3,
      expectedPendingOrigin: Uri.parse('https://moved.example'),
      instanceId: RomdServerInstanceId.tryParse(instanceA)!,
    );
    expect(activated, isA<ProfileServerMutationApplied>());

    final moved =
        (await database.select(database.serverConnections).get()).single;
    expect(moved.firstSeenAt, first.firstSeenAt);
    expect(moved.lastSeenAt.isAtSameMomentAs(now), isTrue);
    expect(moved.lastKnownOrigin, 'https://moved.example');
  });

  test('same locator may discover a replacement instance', () async {
    await _activate(
      repository,
      profileId: 'profile-1',
      generation: 0,
      origin: 'https://same.example',
      instanceId: instanceA,
    );
    await repository.setPendingLocator(
      localProfileId: 'profile-1',
      origin: Uri.parse('https://same.example'),
    );
    await repository.activateDiscoveredServer(
      localProfileId: 'profile-1',
      expectedGeneration: 3,
      expectedPendingOrigin: Uri.parse('https://same.example'),
      instanceId: RomdServerInstanceId.tryParse(instanceB)!,
    );

    final state =
        (await repository.read('profile-1') as ProfileServerConnectionFound)
            .state;
    expect(state.selectedConnection?.instanceId.value, instanceB);
    expect(
      await database.select(database.serverConnections).get(),
      hasLength(2),
    );
  });

  test('known selection and clear are compare-and-set mutations', () async {
    await _insertConnection(database, instanceA, 'https://a.example', now);

    final selected = await repository.selectKnownServer(
      localProfileId: 'profile-1',
      expectedGeneration: 0,
      instanceId: RomdServerInstanceId.tryParse(instanceA)!,
    );
    expect((selected as ProfileServerMutationApplied).generation, 1);
    expect(
      await repository.clearSelection(
        localProfileId: 'profile-1',
        expectedGeneration: 0,
      ),
      isA<ProfileServerMutationStale>(),
    );
    expect(
      await repository.clearSelection(
        localProfileId: 'profile-1',
        expectedGeneration: 1,
      ),
      isA<ProfileServerMutationApplied>(),
    );
  });

  test('invalid persisted origin fails closed', () async {
    await database
        .into(database.pendingServerLocators)
        .insert(
          PendingServerLocatorsCompanion.insert(
            localProfileId: 'profile-1',
            normalizedOrigin: 'https://example.com/path',
            createdAt: now,
          ),
        );

    expect(
      await repository.read('profile-1'),
      isA<ProfileServerConnectionProjectionInconsistent>(),
    );
  });

  test('activation rollback leaves no partially selected authority', () async {
    await repository.setPendingLocator(
      localProfileId: 'profile-1',
      origin: Uri.parse('https://example.com'),
    );
    await database.customStatement('''
CREATE TRIGGER reject_pending_delete
BEFORE DELETE ON pending_server_locators
BEGIN
  SELECT RAISE(ABORT, 'injected rollback');
END
''');

    await expectLater(
      repository.activateDiscoveredServer(
        localProfileId: 'profile-1',
        expectedGeneration: 1,
        expectedPendingOrigin: Uri.parse('https://example.com'),
        instanceId: RomdServerInstanceId.tryParse(instanceA)!,
      ),
      throwsA(anything),
    );

    expect(await database.select(database.serverConnections).get(), isEmpty);
    final profile =
        (await database.select(database.localProfiles).get()).single;
    expect(profile.selectedServerInstanceId, isNull);
    expect(profile.serverSelectionGeneration, 1);
    expect(
      await database.select(database.pendingServerLocators).get(),
      hasLength(1),
    );
  });
}

Future<void> _activate(
  DriftProfileServerConnectionRepository repository, {
  required String profileId,
  required int generation,
  required String origin,
  required String instanceId,
}) async {
  final pending = await repository.setPendingLocator(
    localProfileId: profileId,
    origin: Uri.parse(origin),
  );
  expect((pending as ProfileServerMutationApplied).generation, generation + 1);
  final activated = await repository.activateDiscoveredServer(
    localProfileId: profileId,
    expectedGeneration: generation + 1,
    expectedPendingOrigin: Uri.parse(origin),
    instanceId: RomdServerInstanceId.tryParse(instanceId)!,
  );
  expect(
    (activated as ProfileServerMutationApplied).generation,
    generation + 2,
  );
}

Future<void> _insertProfile(
  AppDatabase database,
  String id,
  DateTime timestamp,
) => database
    .into(database.localProfiles)
    .insert(
      LocalProfilesCompanion.insert(
        id: id,
        displayName: id,
        avatarKey: 'default',
        accentColor: 0xff1fbf8f,
        romdServerOrigin: const Value(''),
        entryMode: 'open',
        createdAt: timestamp,
        updatedAt: timestamp,
      ),
    );

Future<void> _insertConnection(
  AppDatabase database,
  String instanceId,
  String origin,
  DateTime timestamp,
) => database
    .into(database.serverConnections)
    .insert(
      ServerConnectionsCompanion.insert(
        instanceId: instanceId,
        lastKnownOrigin: origin,
        firstSeenAt: timestamp,
        lastSeenAt: timestamp,
      ),
    );
