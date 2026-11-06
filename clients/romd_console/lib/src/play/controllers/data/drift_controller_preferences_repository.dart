import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_preferences.dart';

/// Drift-backed store for the single device-wide controller preferences row.
/// Stale template ids (a built-in removed in an update) load as null so the
/// caller's generic fallback applies; the stored value is kept, not deleted.
final class DriftControllerPreferencesRepository
    implements ControllerPreferencesRepository {
  DriftControllerPreferencesRepository({
    required AppDatabase database,
    DateTime Function()? now,
  }) : _db = database,
       _now = now ?? DateTime.now;

  static const int _rowId = 1;

  final AppDatabase _db;
  final DateTime Function() _now;

  @override
  Future<ControllerPreferences> load() async {
    final row = await (_db.select(
      _db.controllerPreferencesRows,
    )..where((t) => t.id.equals(_rowId))).getSingleOrNull();
    if (row == null) {
      return ControllerPreferences.defaults;
    }

    final storedTemplateId = row.templateId;
    final templateId = storedTemplateId == null
        ? null
        : BuiltinControllerTemplates.byId(
            ControllerTemplateId(storedTemplateId),
          )?.id;
    return ControllerPreferences(templateId: templateId);
  }

  @override
  Future<void> saveTemplate(ControllerTemplateId templateId) => _db
      .into(_db.controllerPreferencesRows)
      .insertOnConflictUpdate(
        ControllerPreferencesRowsCompanion(
          id: const Value(_rowId),
          templateId: Value(templateId.value),
          updatedAt: Value(_now()),
        ),
      );
}
