import 'romd_public_id.dart';
import 'romd_server_instance_id.dart';

sealed class ReleaseAccessDecision {
  const ReleaseAccessDecision({
    required this.serverInstanceId,
    required this.releaseId,
  });

  final RomdServerInstanceId serverInstanceId;
  final RomdPublicId releaseId;
}

final class ReleaseAccessAllowed extends ReleaseAccessDecision {
  const ReleaseAccessAllowed({
    required super.serverInstanceId,
    required super.releaseId,
    required this.titleId,
  });

  final RomdPublicId titleId;
}

final class ReleaseAccessRevoked extends ReleaseAccessDecision {
  const ReleaseAccessRevoked({
    required super.serverInstanceId,
    required super.releaseId,
  });
}

enum ReleaseAccessFailureKind {
  badRequest,
  unauthorized,
  conflict,
  clientError,
  serverError,
  redirect,
  unexpectedStatus,
  timeout,
  transport,
  malformedResponse,
  identityMismatch,
}

sealed class ReleaseAccessResult {
  const ReleaseAccessResult();
}

final class ReleaseAccessSuccess extends ReleaseAccessResult {
  const ReleaseAccessSuccess(this.decision);

  final ReleaseAccessDecision decision;
}

final class ReleaseAccessFailure extends ReleaseAccessResult {
  const ReleaseAccessFailure(this.kind, {this.statusCode, this.bearerError});

  final ReleaseAccessFailureKind kind;
  final int? statusCode;
  final String? bearerError;

  bool get isInvalidToken =>
      kind == ReleaseAccessFailureKind.unauthorized &&
      bearerError == 'invalid_token';
}
