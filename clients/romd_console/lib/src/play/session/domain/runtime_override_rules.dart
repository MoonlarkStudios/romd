import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// Scope of one override rule, most-specific first at resolution time:
/// release → title → platform. (An explicit per-launch choice tier arrives
/// with a later slice.)
enum RuntimeOverrideScope { platform, title, release }

/// One device-wide rule: "content matching [scope]/[scopeValue] prefers
/// [profileId]". Device-wide by design — installs are device-scoped too, and
/// household members sharing a console share core preferences.
final class RuntimeOverrideRule {
  const RuntimeOverrideRule({
    required this.scope,
    required this.scopeValue,
    required this.profileId,
    required this.updatedAt,
  });

  final RuntimeOverrideScope scope;

  /// Platform short name (lowercase, trimmed), titleId, or releaseId,
  /// depending on [scope].
  final String scopeValue;

  final RuntimeProfileId profileId;

  final DateTime updatedAt;
}

abstract interface class RuntimeOverrideRuleRepository {
  /// Upsert: one rule per (scope, scopeValue). Picking the shipped default
  /// pins it — there is deliberately no clear/delete in this slice.
  Future<void> setRule({
    required RuntimeOverrideScope scope,
    required String scopeValue,
    required RuntimeProfileId profileId,
  });

  /// Every rule whose (scope, scopeValue) matches the given identity — the
  /// resolver applies precedence, the repository stays dumb.
  Future<List<RuntimeOverrideRule>> rulesMatching({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  });
}
