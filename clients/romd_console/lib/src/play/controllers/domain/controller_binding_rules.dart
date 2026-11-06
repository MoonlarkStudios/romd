import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

/// Scope of one binding rule, least-specific first at resolution time:
/// global → title. Chords don't vary per release, so per-game rules key on
/// titleId. (The device-template layer is code-defined; per-launch/session
/// tiers arrive only with a concrete need.)
enum ControllerBindingScope { global, title }

/// One rebind rule: within [scope]/[scopeValue], [action] is chorded via
/// [button]. The legacy repository stores this shape device-wide; the
/// profile/GUID repository stores the same rule under a local profile and SDL
/// GUID.
final class ControllerBindingRule {
  const ControllerBindingRule({
    required this.scope,
    required this.scopeValue,
    required this.action,
    required this.button,
    required this.updatedAt,
  });

  final ControllerBindingScope scope;

  /// `''` for [ControllerBindingScope.global], titleId for
  /// [ControllerBindingScope.title].
  final String scopeValue;

  final RomdAction action;

  /// `null` means explicitly unbound: the chord is disabled at this scope,
  /// shadowing anything an earlier layer bound. Distinct from having no rule
  /// at all, which inherits.
  final GamepadButtonPosition? button;

  final DateTime updatedAt;
}

abstract interface class ControllerBindingRuleRepository {
  /// Upsert: one rule per (scope, scopeValue, action). Rebinding to the
  /// default pins it — there is deliberately no clear/delete in this slice
  /// (reset-to-inherit arrives with the mapping UI). [scopeValue] is ignored
  /// for the global scope. A `null` [button] persists an explicit unbind.
  Future<void> setBinding({
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  });

  /// Every rule whose (scope, scopeValue) matches the given identity — the
  /// resolver applies precedence, the repository stays dumb.
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String titleId,
  });
}
