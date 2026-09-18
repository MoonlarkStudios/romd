import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_profile_mappings.dart';

import 'controller_binding_rules.dart';

/// Folds the agreed precedence into one effective mapping:
/// built-in default → device template → legacy global/title →
/// active-profile/GUID global/title → assigned-profile/GUID global/title.
/// Const-constructible without a rules repository for defaults-only
/// resolution, mirroring `LocalRuntimeResolver`.
final class ControllerMappingResolver {
  const ControllerMappingResolver({
    ControllerBindingRuleRepository? rules,
    ControllerProfileMappingRepository? profileRules,
  }) : _rules = rules,
       _profileRules = profileRules;

  final ControllerBindingRuleRepository? _rules;
  final ControllerProfileMappingRepository? _profileRules;

  Future<ControllerMapping> resolve({
    required ControllerTemplate template,
    String? titleId,
    String? localProfileId,
    String? fallbackLocalProfileId,
    String? sdlGuid,
  }) async {
    final capabilities = template.capabilities;
    var mapping = BuiltinControllerTemplates.defaultMapping
        .filteredFor(capabilities)
        .overlaidWith(template.overrides.filteredFor(capabilities));

    final rules = _rules;
    if (rules != null) {
      mapping = _applyScopedLayers(
        mapping,
        await rules.bindingsMatching(titleId: titleId ?? ''),
        titleId: titleId,
        capabilities: capabilities,
      );
    }

    final profileRules = _profileRules;
    final profileId = localProfileId?.trim();
    final fallbackProfileId = fallbackLocalProfileId?.trim();
    final guid = sdlGuid?.trim();
    if (profileRules == null || guid == null || guid.isEmpty) {
      return mapping;
    }

    if (fallbackProfileId != null &&
        fallbackProfileId.isNotEmpty &&
        fallbackProfileId != profileId) {
      mapping = _applyScopedLayers(
        mapping,
        await profileRules.bindingsMatching(
          localProfileId: fallbackProfileId,
          sdlGuid: guid,
          titleId: titleId ?? '',
        ),
        titleId: titleId,
        capabilities: capabilities,
      );
    }

    if (profileId == null || profileId.isEmpty) {
      return mapping;
    }
    return _applyScopedLayers(
      mapping,
      await profileRules.bindingsMatching(
        localProfileId: profileId,
        sdlGuid: guid,
        titleId: titleId ?? '',
      ),
      titleId: titleId,
      capabilities: capabilities,
    );
  }

  static ControllerMapping _applyScopedLayers(
    ControllerMapping base,
    List<ControllerBindingRule> rules, {
    required String? titleId,
    required ControllerCapabilities capabilities,
  }) {
    final withGlobal = base.overlaidWith(
      _layer(rules, ControllerBindingScope.global, capabilities),
    );
    return titleId == null
        ? withGlobal
        : withGlobal.overlaidWith(
            _layer(rules, ControllerBindingScope.title, capabilities),
          );
  }

  // Explicit-unbind rules (null button) stay in the layer so they shadow
  // earlier layers' bindings. Unsupported non-null buttons are ignored before
  // overlay so they cannot erase an inherited usable binding.
  static ControllerMapping _layer(
    List<ControllerBindingRule> rules,
    ControllerBindingScope scope,
    ControllerCapabilities capabilities,
  ) => ControllerMapping(<RomdAction, GamepadButtonPosition?>{
    for (final rule in rules)
      if (rule.scope == scope &&
          (rule.button == null || capabilities.supports(rule.button!)))
        rule.action: rule.button,
  });
}
