import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_preferences.dart';

/// In-memory [ControllerPreferencesRepository] for tests that build
/// [PlayServices] or a coordinator without a drift database. Slot claims
/// need no fake — `SessionControllerSlotClaims` is already in-memory.
final class InMemoryControllerPreferences
    implements ControllerPreferencesRepository {
  InMemoryControllerPreferences([
    this.preferences = ControllerPreferences.defaults,
  ]);

  ControllerPreferences preferences;

  @override
  Future<ControllerPreferences> load() async => preferences;

  @override
  Future<void> saveTemplate(ControllerTemplateId templateId) async {
    preferences = ControllerPreferences(templateId: templateId);
  }
}
