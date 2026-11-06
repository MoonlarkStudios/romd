import 'package:drift/drift.dart' hide isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_preferences_repository.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';

void main() {
  late AppDatabase db;
  late DriftControllerPreferencesRepository repo;
  final now = DateTime.utc(2026, 7, 7, 12);

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    repo = DriftControllerPreferencesRepository(database: db, now: () => now);
  });

  tearDown(() async {
    await db.close();
  });

  test('load returns defaults when nothing was saved', () async {
    final preferences = await repo.load();
    expect(preferences.templateId, isNull);
  });

  test('saveTemplate round-trips and re-saving replaces it', () async {
    await repo.saveTemplate(BuiltinControllerTemplates.playstationStyle.id);
    await repo.saveTemplate(BuiltinControllerTemplates.nintendoStyle.id);

    final preferences = await repo.load();
    expect(preferences.templateId, BuiltinControllerTemplates.nintendoStyle.id);
  });

  test('a stale template id loads as null but stays stored', () async {
    await db
        .into(db.controllerPreferencesRows)
        .insert(
          ControllerPreferencesRowsCompanion(
            id: const Value(1),
            templateId: const Value('retired-template'),
            updatedAt: Value(now),
          ),
        );

    final preferences = await repo.load();
    expect(preferences.templateId, isNull);

    final row = await db.select(db.controllerPreferencesRows).getSingle();
    expect(row.templateId, 'retired-template');
  });
}
