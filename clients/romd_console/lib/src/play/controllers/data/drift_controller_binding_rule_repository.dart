import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/domain/controller_binding_rules.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

/// Drift-backed store for device-wide controller binding rules. Global-scope
/// values are normalized to `''` on write, and reads only match global rows
/// with that value — so a malformed row can never produce duplicate global
/// bindings. Rows whose stored enum names no longer parse (schema-forward
/// tolerance) are likewise skipped, not deleted — they become reachable again
/// if the name returns. A NULL button round-trips as an explicit unbind.
final class DriftControllerBindingRuleRepository
    implements ControllerBindingRuleRepository {
  DriftControllerBindingRuleRepository({
    required AppDatabase database,
    DateTime Function()? now,
  }) : _db = database,
       _now = now ?? DateTime.now;

  final AppDatabase _db;
  final DateTime Function() _now;

  @override
  Future<void> setBinding({
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => _db
      .into(_db.controllerBindingRules)
      .insertOnConflictUpdate(
        ControllerBindingRulesCompanion(
          scope: Value(scope.name),
          scopeValue: Value(_normalize(scope, scopeValue)),
          action: Value(action.name),
          button: Value(button?.name),
          updatedAt: Value(_now()),
        ),
      );

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String titleId,
  }) async {
    final rows =
        await (_db.select(_db.controllerBindingRules)..where(
              (t) =>
                  (t.scope.equals(ControllerBindingScope.global.name) &
                      t.scopeValue.equals('')) |
                  (t.scope.equals(ControllerBindingScope.title.name) &
                      t.scopeValue.equals(titleId)),
            ))
            .get();
    return rows.map(_toDomain).nonNulls.toList(growable: false);
  }

  static String _normalize(ControllerBindingScope scope, String scopeValue) =>
      scope == ControllerBindingScope.global ? '' : scopeValue;

  static ControllerBindingRule? _toDomain(ControllerBindingRuleRow row) {
    final scope = ControllerBindingScope.values.asNameMap()[row.scope];
    final action = RomdAction.values.asNameMap()[row.action];
    if (scope == null || action == null) {
      return null;
    }

    // NULL means explicitly unbound; a non-null name that no longer parses
    // means a stale row, which is skipped rather than misread as an unbind.
    final GamepadButtonPosition? button;
    if (row.button case final storedName?) {
      final parsed = GamepadButtonPosition.values.asNameMap()[storedName];
      if (parsed == null) {
        return null;
      }
      button = parsed;
    } else {
      button = null;
    }

    return ControllerBindingRule(
      scope: scope,
      scopeValue: row.scopeValue,
      action: action,
      button: button,
      updatedAt: row.updatedAt,
    );
  }
}
