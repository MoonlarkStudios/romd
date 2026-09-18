import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/local_profiles/local_profile_repository.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

void main() {
  late AppDatabase database;
  late DriftLocalProfileRepository repository;
  var idCounter = 0;
  var now = DateTime(2026, 1, 1, 12);
  final serverInstanceId = RomdServerInstanceId.tryParse(
    '11111111-1111-4111-8111-111111111111',
  )!;

  Future<int> selectServer(String profileId) async {
    await database
        .into(database.serverConnections)
        .insertOnConflictUpdate(
          ServerConnectionsCompanion.insert(
            instanceId: serverInstanceId.value,
            lastKnownOrigin: 'https://library.example:5002',
            firstSeenAt: now,
            lastSeenAt: now,
          ),
        );
    await (database.update(
      database.localProfiles,
    )..where((row) => row.id.equals(profileId))).write(
      LocalProfilesCompanion(
        selectedServerInstanceId: Value(serverInstanceId.value),
        serverSelectionGeneration: const Value(1),
      ),
    );
    return 1;
  }

  setUp(() {
    database = AppDatabase(NativeDatabase.memory());
    repository = DriftLocalProfileRepository(
      database: database,
      now: () => now,
      createId: () => 'profile-${++idCounter}',
    );
  });

  tearDown(() async {
    await database.close();
  });

  test('RomdServerOrigins parses origin-only inputs', () {
    expect(
      RomdServerOrigins.parse('library.local:5002').toString(),
      'http://library.local:5002',
    );
    expect(
      RomdServerOrigins.parse('https://library.example').toString(),
      'https://library.example',
    );
  });

  test('RomdServerOrigins rejects paths and non-http schemes', () {
    expect(
      () => RomdServerOrigins.parse('https://library.example/romd'),
      throwsFormatException,
    );
    expect(
      () => RomdServerOrigins.parse('ftp://library.example'),
      throwsFormatException,
    );
  });

  test('createProfile trims and persists local profile metadata', () async {
    final profile = await repository.createProfile(
      const CreateLocalProfileRequest(displayName: '  Player One  '),
    );

    expect(profile.id, 'profile-1');
    expect(profile.displayName, 'Player One');
    expect(profile.avatarKey, 'default');
    // No server requested → a purely local profile.
    expect(profile.romdServerOrigin, isNull);
    expect(profile.hasServer, isFalse);
    expect(profile.entryMode, LocalProfileEntryMode.open);
    expect(profile.createdAt, now);
    expect(profile.updatedAt, now);
    expect(profile.lastUsedAt, isNull);
  });

  test('requested server starts as pending and is not mounted', () async {
    final profile = await repository.createProfile(
      CreateLocalProfileRequest(
        displayName: 'Player One',
        romdServerOrigin: Uri.parse('https://library.example:5002'),
      ),
    );

    expect(profile.romdServerOrigin, isNull);
    final pending =
        (await database.select(database.pendingServerLocators).get()).single;
    expect(pending.localProfileId, profile.id);
    expect(pending.normalizedOrigin, 'https://library.example:5002');
  });

  test('createProfile rejects empty display names', () async {
    expect(
      () => repository.createProfile(
        const CreateLocalProfileRequest(displayName: '   '),
      ),
      throwsArgumentError,
    );
  });

  test('listProfiles orders by last used then creation time', () async {
    final first = await repository.createProfile(
      const CreateLocalProfileRequest(displayName: 'First'),
    );
    now = DateTime(2026, 1, 1, 13);
    final second = await repository.createProfile(
      const CreateLocalProfileRequest(displayName: 'Second'),
    );
    now = DateTime(2026, 1, 1, 14);
    await repository.markLastUsed(first.id);

    final profiles = await repository.listProfiles();

    expect(profiles.map((profile) => profile.id), <String>[
      first.id,
      second.id,
    ]);
    expect(profiles.first.lastUsedAt, now);
  });

  test('linkRomdAccount persists ROMD account metadata', () async {
    final profile = await repository.createProfile(
      const CreateLocalProfileRequest(displayName: 'Player'),
    );

    final generation = await selectServer(profile.id);
    final linked = await repository.linkRomdAccountToSelectedServer(
      localProfileId: profile.id,
      expectedGeneration: generation,
      serverInstanceId: serverInstanceId,
      accountLink: RomdAccountLink(
        romdUserId: 'romd-user-1',
        username: 'player',
        email: 'player@example.com',
        linkedAt: now,
        lastLoginAt: now,
      ),
    );

    expect(linked.romdAccountLink?.romdUserId, 'romd-user-1');
    expect(linked.romdAccountLink?.username, 'player');
    expect(linked.romdAccountLink?.email, 'player@example.com');
    expect(linked.romdAccountLink?.linkedAt, now);
    expect(linked.romdAccountLink?.lastLoginAt, now);
    expect(
      (await database.select(database.romdAccountLinks).get())
          .single
          .serverInstanceId,
      serverInstanceId.value,
    );
  });

  test('stale generation cannot bind an account to the selection', () async {
    final profile = await repository.createProfile(
      const CreateLocalProfileRequest(displayName: 'Player'),
    );
    await selectServer(profile.id);

    await expectLater(
      repository.linkRomdAccountToSelectedServer(
        localProfileId: profile.id,
        expectedGeneration: 0,
        serverInstanceId: serverInstanceId,
        accountLink: RomdAccountLink(
          romdUserId: 'romd-user-1',
          username: 'player',
          email: 'player@example.com',
          linkedAt: now,
        ),
      ),
      throwsStateError,
    );
    expect(await database.select(database.romdAccountLinks).get(), isEmpty);
  });

  test('linkRomdAccount updates existing ROMD account metadata', () async {
    final profile = await repository.createProfile(
      const CreateLocalProfileRequest(displayName: 'Player'),
    );
    final generation = await selectServer(profile.id);
    await repository.linkRomdAccountToSelectedServer(
      localProfileId: profile.id,
      expectedGeneration: generation,
      serverInstanceId: serverInstanceId,
      accountLink: RomdAccountLink(
        romdUserId: 'romd-user-1',
        username: 'old',
        email: 'old@example.com',
        linkedAt: now,
      ),
    );

    final linked = await repository.linkRomdAccountToSelectedServer(
      localProfileId: profile.id,
      expectedGeneration: generation,
      serverInstanceId: serverInstanceId,
      accountLink: RomdAccountLink(
        romdUserId: 'romd-user-1',
        username: 'new',
        email: 'new@example.com',
        linkedAt: now,
        lastLoginAt: now,
      ),
    );

    expect(linked.romdAccountLink?.username, 'new');
    expect(linked.romdAccountLink?.email, 'new@example.com');
    expect(linked.romdAccountLink?.lastLoginAt, now);
  });

  test('unlinkRomdAccount removes ROMD account metadata', () async {
    final profile = await repository.createProfile(
      const CreateLocalProfileRequest(displayName: 'Player'),
    );
    await repository.linkRomdAccount(
      localProfileId: profile.id,
      accountLink: RomdAccountLink(
        romdUserId: 'romd-user-1',
        username: 'player',
        email: 'player@example.com',
        linkedAt: now,
      ),
    );

    final unlinked = await repository.unlinkRomdAccount(
      localProfileId: profile.id,
    );

    expect(unlinked.romdAccountLink, isNull);
  });

  test(
    'legacy origin update remains non-authoritative and clears account link',
    () async {
      final profile = await repository.createProfile(
        const CreateLocalProfileRequest(displayName: 'Player'),
      );
      await repository.linkRomdAccount(
        localProfileId: profile.id,
        accountLink: RomdAccountLink(
          romdUserId: 'romd-user-1',
          username: 'player',
          email: 'player@example.com',
          linkedAt: now,
        ),
      );

      final updated = await repository.updateRomdServerOrigin(
        localProfileId: profile.id,
        serverOrigin: Uri.parse('https://library.example'),
      );

      expect(updated.romdServerOrigin, isNull);
      expect(updated.romdAccountLink, isNull);
    },
  );

  test('createProfile without an origin makes a local-only profile', () async {
    final profile = await repository.createProfile(
      const CreateLocalProfileRequest(displayName: 'Local Player'),
    );

    final reloaded = (await repository.listProfiles()).single;
    expect(profile.romdServerOrigin, isNull);
    expect(reloaded.romdServerOrigin, isNull);
    expect(reloaded.hasServer, isFalse);
  });

  test('updateRomdServerOrigin(null) detaches the server', () async {
    final profile = await repository.createProfile(
      CreateLocalProfileRequest(
        displayName: 'Player',
        romdServerOrigin: Uri.parse('https://library.example'),
      ),
    );
    await repository.linkRomdAccount(
      localProfileId: profile.id,
      accountLink: RomdAccountLink(
        romdUserId: 'romd-user-1',
        username: 'player',
        email: 'player@example.com',
        linkedAt: now,
      ),
    );

    final detached = await repository.updateRomdServerOrigin(
      localProfileId: profile.id,
      serverOrigin: null,
    );

    expect(detached.romdServerOrigin, isNull);
    expect(detached.hasServer, isFalse);
    expect(detached.romdAccountLink, isNull);
  });

  test('watchProfiles emits profile updates', () async {
    final emissions = repository.watchProfiles();

    final expectation = expectLater(
      emissions,
      emits(
        predicate<List<LocalProfile>>(
          (profiles) =>
              profiles.length == 1 && profiles.single.displayName == 'Player',
        ),
      ),
    );

    await repository.createProfile(
      const CreateLocalProfileRequest(displayName: 'Player'),
    );
    await expectation;
  });
}
