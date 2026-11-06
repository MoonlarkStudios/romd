enum DiscSetFailureKind { unavailable, unauthorized, denied, stale, malformed }

final class DiscSetException implements Exception {
  const DiscSetException(this.kind);
  final DiscSetFailureKind kind;
}
