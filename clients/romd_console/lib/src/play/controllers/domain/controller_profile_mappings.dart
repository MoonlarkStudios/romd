import 'package:romd_console/src/play/controllers/domain/controller_binding_rules.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

final class ControllerMappingProfile {
  const ControllerMappingProfile({
    required this.localProfileId,
    required this.sdlGuid,
    required this.displayName,
    required this.templateId,
    required this.createdAt,
    required this.updatedAt,
  });

  final String localProfileId;
  final String sdlGuid;
  final String displayName;
  final ControllerTemplateId? templateId;
  final DateTime createdAt;
  final DateTime updatedAt;
}

abstract interface class ControllerProfileMappingRepository {
  /// Upserts explicit profile/GUID mapping metadata. Empty GUIDs are inert:
  /// fallback/no-SDL paths cannot create GUID-keyed rows.
  Future<void> saveProfile({
    required String localProfileId,
    required String sdlGuid,
    required String displayName,
    required ControllerTemplateId? templateId,
  });

  Future<ControllerMappingProfile?> findProfile({
    required String localProfileId,
    required String sdlGuid,
  });

  /// Upsert: one profile/GUID rule per (scope, scopeValue, action). Empty
  /// GUIDs are inert, preserving fallback behavior for controllers without SDL
  /// identity. A `null` [button] persists an explicit unbind.
  Future<void> setBinding({
    required String localProfileId,
    required String sdlGuid,
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  });

  Future<List<ControllerBindingRule>> bindingsMatching({
    required String localProfileId,
    required String sdlGuid,
    required String titleId,
  });
}
