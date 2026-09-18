import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

import 'builtin_runtime_profiles.dart';
import 'runtime_override_rules.dart';

/// The content identity runtime resolution operates on — everything rule
/// scopes can match. Path-free so callers holding only install metadata can
/// resolve. (Content hashes join this identity in a later slice.)
final class PlayIdentity {
  const PlayIdentity({
    required this.platformShortName,
    required this.titleId,
    required this.releaseId,
  });

  factory PlayIdentity.ofTarget(ResolvedPlayTarget target) => PlayIdentity(
    platformShortName: target.platformShortName,
    titleId: target.titleId,
    releaseId: target.releaseId,
  );

  factory PlayIdentity.ofInstall(LocalInstall install) => PlayIdentity(
    platformShortName: install.platformShortName,
    titleId: install.titleId,
    releaseId: install.releaseId,
  );

  final String platformShortName;
  final String titleId;
  final String releaseId;
}

/// The candidates for launching one resolved target, plus which one the client
/// would pick unprompted. One call returns both so [preferred] can never
/// diverge from the candidate list it was derived from.
final class RuntimeCandidates {
  const RuntimeCandidates({required this.candidates, required this.preferred});

  /// Empty when the target's platform is unsupported.
  final List<RuntimeProfile> candidates;

  /// Null iff [candidates] is empty.
  final RuntimeProfile? preferred;
}

/// Async from day one: later slices consult persisted rules (DB) and content
/// identity (IO) — the signature must not need to change when they arrive.
abstract interface class RuntimeResolver {
  Future<RuntimeCandidates> resolve(PlayIdentity identity);
}

/// Merges the built-in profiles with persisted device-local override rules.
///
/// Candidates are every profile supporting the target's platform, in shipped
/// order — the built-in default first. Rules never filter that list; they
/// only pick which candidate is [RuntimeCandidates.preferred].
final class LocalRuntimeResolver implements RuntimeResolver {
  /// Null [rules] means built-in behavior only (preferred = first candidate),
  /// which keeps the resolver const-constructible where no database exists.
  const LocalRuntimeResolver({
    List<RuntimeProfile> profiles = BuiltinRuntimeProfiles.all,
    RuntimeOverrideRuleRepository? rules,
  }) : _profiles = profiles,
       _rules = rules;

  final List<RuntimeProfile> _profiles;
  final RuntimeOverrideRuleRepository? _rules;

  /// Most-specific scope wins when multiple rules match one target.
  static const List<RuntimeOverrideScope> _precedence = <RuntimeOverrideScope>[
    RuntimeOverrideScope.release,
    RuntimeOverrideScope.title,
    RuntimeOverrideScope.platform,
  ];

  @override
  Future<RuntimeCandidates> resolve(PlayIdentity identity) async {
    final candidates = _profiles
        .where(
          (profile) => profile.supportsPlatform(identity.platformShortName),
        )
        .toList(growable: false);
    return RuntimeCandidates(
      candidates: candidates,
      preferred: candidates.isEmpty
          ? null
          : await _preferred(candidates, identity),
    );
  }

  /// Walks the matching rules most-specific first (release → title →
  /// platform); the first rule whose profileId names a candidate wins. A rule
  /// referencing an unknown or non-candidate profile is skipped — it went
  /// stale after an app update — and the walk falls through to the next tier.
  /// No matching rule means the first candidate (the built-in default) wins.
  Future<RuntimeProfile> _preferred(
    List<RuntimeProfile> candidates,
    PlayIdentity identity,
  ) async {
    final rules = _rules;
    if (rules == null) {
      return candidates.first;
    }
    final matching = await rules.rulesMatching(
      platformShortName: identity.platformShortName,
      titleId: identity.titleId,
      releaseId: identity.releaseId,
    );
    for (final scope in _precedence) {
      for (final rule in matching.where((rule) => rule.scope == scope)) {
        for (final candidate in candidates) {
          if (candidate.id == rule.profileId) {
            return candidate;
          }
        }
      }
    }
    return candidates.first;
  }
}
