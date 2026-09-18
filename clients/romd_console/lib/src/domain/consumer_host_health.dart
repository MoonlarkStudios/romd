final class ConsumerHostHealth {
  const ConsumerHostHealth({required this.status, this.reason});

  const ConsumerHostHealth.unavailable(String reason)
    : status = 'Unavailable',
      reason = reason;

  final String status;
  final String? reason;

  bool get isHealthy => status.toLowerCase() == 'healthy';
}
