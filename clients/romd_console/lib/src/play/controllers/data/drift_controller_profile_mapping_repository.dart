import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/domain/controller_binding_rules.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_profile_mappings.dart';

final class DriftControllerProfileMappingRepository
    implements ControllerProfileMappingRepository {
  DriftControllerProfileMappingRepository({
    required AppDatabase database,
    DateTime Function()? now,
  }) : _db = database,
       _now = now ?? DateTime.now;

  final AppDatabase _db;
  final DateTime Function() _now;

  @override
  Future<void> saveProfile({
    required String localProfileId,
    required String sdlGuid,
    required String displayName,
    required ControllerTemplateId? templateId,
  }) async {
    final guid = _normalizeGuid(sdlGuid);
    if (guid == null) {
      return;
    }

    final timestamp = _now();
    final existing =
        await (_db.select(_db.controllerMappingProfiles)..where(
              (t) =>
                  t.localProfileId.equals(localProfileId) &
                  t.sdlGuid.equals(guid),
            ))
            .getSingleOrNull();
    if (existing == null) {
      await _db
          .into(_db.controllerMappingProfiles)
          .insert(
            ControllerMappingProfilesCompanion(
              localProfileId: Value(localProfileId),
              sdlGuid: Value(guid),
              displayName: Value(displayName),
              templateId: Value(templateId?.value),
              createdAt: Value(timestamp),
              updatedAt: Value(timestamp),
            ),
          );
      return;
    }

    await (_db.update(_db.controllerMappingProfiles)..where(
          (t) =>
              t.localProfileId.equals(localProfileId) & t.sdlGuid.equals(guid),
        ))
        .write(
          ControllerMappingProfilesCompanion(
            displayName: Value(displayName),
            templateId: Value(templateId?.value),
            updatedAt: Value(timestamp),
          ),
        );
  }

  @override
  Future<ControllerMappingProfile?> findProfile({
    required String localProfileId,
    required String sdlGuid,
  }) async {
    final guid = _normalizeGuid(sdlGuid);
    if (guid == null) {
      return null;
    }

    final row =
        await (_db.select(_db.controllerMappingProfiles)..where(
              (t) =>
                  t.localProfileId.equals(localProfileId) &
                  t.sdlGuid.equals(guid),
            ))
            .getSingleOrNull();
    if (row == null) {
      return null;
    }

    return ControllerMappingProfile(
      localProfileId: row.localProfileId,
      sdlGuid: row.sdlGuid,
      displayName: row.displayName,
      templateId: row.templateId == null
          ? null
          : ControllerTemplateId(row.templateId!),
      createdAt: row.createdAt,
      updatedAt: row.updatedAt,
    );
  }

  @override
  Future<void> setBinding({
    required String localProfileId,
    required String sdlGuid,
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) {
    final guid = _normalizeGuid(sdlGuid);
    if (guid == null) {
      return Future<void>.value();
    }

    return _db
        .into(_db.controllerProfileBindingRules)
        .insertOnConflictUpdate(
          ControllerProfileBindingRulesCompanion(
            localProfileId: Value(localProfileId),
            sdlGuid: Value(guid),
            scope: Value(scope.name),
            scopeValue: Value(_normalizeScopeValue(scope, scopeValue)),
            action: Value(action.name),
            button: Value(button?.name),
            updatedAt: Value(_now()),
          ),
        );
  }

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String localProfileId,
    required String sdlGuid,
    required String titleId,
  }) async {
    final guid = _normalizeGuid(sdlGuid);
    if (guid == null) {
      return const <ControllerBindingRule>[];
    }

    final rows =
        await (_db.select(_db.controllerProfileBindingRules)..where(
              (t) =>
                  t.localProfileId.equals(localProfileId) &
                  t.sdlGuid.equals(guid) &
                  ((t.scope.equals(ControllerBindingScope.global.name) &
                          t.scopeValue.equals('')) |
                      (t.scope.equals(ControllerBindingScope.title.name) &
                          t.scopeValue.equals(titleId))),
            ))
            .get();
    return rows.map(_toDomain).nonNulls.toList(growable: false);
  }

  static String? _normalizeGuid(String sdlGuid) {
    final trimmed = sdlGuid.trim();
    return trimmed.isEmpty ? null : trimmed;
  }

  static String _normalizeScopeValue(
    ControllerBindingScope scope,
    String scopeValue,
  ) => scope == ControllerBindingScope.global ? '' : scopeValue;

  static ControllerBindingRule? _toDomain(ControllerProfileBindingRuleRow row) {
    final scope = ControllerBindingScope.values.asNameMap()[row.scope];
    final action = RomdAction.values.asNameMap()[row.action];
    if (scope == null || action == null) {
      return null;
    }

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
