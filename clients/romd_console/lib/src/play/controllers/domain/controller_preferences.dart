import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

/// Device-wide controller preferences: which template renders glyphs (and
/// later carries binding overrides). Player-slot assignment is a separate
/// session concern — see `SessionControllerSlotClaims`.
final class ControllerPreferences {
  const ControllerPreferences({this.templateId});

  static const ControllerPreferences defaults = ControllerPreferences();

  /// The chosen built-in template id, or null to use the generic template.
  /// Stale ids (a template removed in an update) resolve as null.
  final ControllerTemplateId? templateId;
}

abstract interface class ControllerPreferencesRepository {
  Future<ControllerPreferences> load();

  Future<void> saveTemplate(ControllerTemplateId templateId);
}
