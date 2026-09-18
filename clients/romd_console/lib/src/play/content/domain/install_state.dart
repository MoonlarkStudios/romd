/// The content lifecycle of a release on this device.
///
/// Runtime availability ("bsnes not found") is deliberately NOT modeled here —
/// that is a launch-time concern, not a property of installed content.
enum InstallState {
  /// No local content / no record.
  remoteOnly,

  /// Content is downloading.
  installing,

  /// Downloaded content is being verified (size + SHA-256).
  verifying,

  /// Content is materialized and verified — ready to launch.
  installed,

  /// A download or IO error left the install incomplete; retryable.
  installFailed,

  /// Verification failed (hash mismatch) — a fresh download is required.
  corrupt,
}
