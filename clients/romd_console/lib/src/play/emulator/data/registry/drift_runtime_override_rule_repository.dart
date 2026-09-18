import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// Drift-backed store for device-wide runtime override rules. Platform scope
/// values are normalized (trim + lowercase) at this boundary on both write
/// and read so lookups never depend on caller casing.
final class DriftRuntimeOverrideRuleRepository
    implements RuntimeOverrideRuleRepository {
  DriftRuntimeOverrideRuleRepository({
    required AppDatabase database,
    DateTime Function()? now,
  }) : _db = database,
       _now = now ?? DateTime.now;

  final AppDatabase _db;
  final DateTime Function() _now;

  @override
  Future<void> setRule({
    required RuntimeOverrideScope scope,
    required String scopeValue,
    required RuntimeProfileId profileId,
  }) => _db
      .into(_db.runtimeOverrideRules)
      .insertOnConflictUpdate(
        RuntimeOverrideRulesCompanion(
          scope: Value(scope.name),
          scopeValue: Value(_normalize(scope, scopeValue)),
          profileId: Value(profileId.value),
          updatedAt: Value(_now()),
        ),
      );

  @override
  Future<List<RuntimeOverrideRule>> rulesMatching({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) async {
    final platform = _normalize(
      RuntimeOverrideScope.platform,
      platformShortName,
    );
    final rows =
        await (_db.select(_db.runtimeOverrideRules)..where(
              (t) =>
                  (t.scope.equals(RuntimeOverrideScope.platform.name) &
                      t.scopeValue.equals(platform)) |
                  (t.scope.equals(RuntimeOverrideScope.title.name) &
                      t.scopeValue.equals(titleId)) |
                  (t.scope.equals(RuntimeOverrideScope.release.name) &
                      t.scopeValue.equals(releaseId)),
            ))
            .get();
    return rows.map(_toDomain).toList(growable: false);
  }

  static String _normalize(RuntimeOverrideScope scope, String scopeValue) =>
      scope == RuntimeOverrideScope.platform
      ? scopeValue.trim().toLowerCase()
      : scopeValue;

  static RuntimeOverrideRule _toDomain(RuntimeOverrideRuleRow row) =>
      RuntimeOverrideRule(
        scope: RuntimeOverrideScope.values.byName(row.scope),
        scopeValue: row.scopeValue,
        profileId: RuntimeProfileId(row.profileId),
        updatedAt: row.updatedAt,
      );
}
