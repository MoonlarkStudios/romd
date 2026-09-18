/// Progress while ensuring a managed runtime artifact is installed.
sealed class RuntimeProvisionProgress {
  const RuntimeProvisionProgress();
}

final class RuntimeProvisionStarted extends RuntimeProvisionProgress {
  const RuntimeProvisionStarted();
}

/// Downloading an artifact. [totalBytes] is null when the source does not
/// report it — show an indeterminate indicator.
final class RuntimeProvisionDownloading extends RuntimeProvisionProgress {
  const RuntimeProvisionDownloading({
    required this.label,
    required this.receivedBytes,
    this.totalBytes,
  });
  final String label;
  final int receivedBytes;
  final int? totalBytes;
}

final class RuntimeProvisionUnpacking extends RuntimeProvisionProgress {
  const RuntimeProvisionUnpacking(this.label);
  final String label;
}

/// Terminal success — a managed RetroArch runtime + core are ready.
final class RuntimeProvisionReady extends RuntimeProvisionProgress {
  const RuntimeProvisionReady({
    required this.retroArchPath,
    required this.corePath,
  });
  final String retroArchPath;
  final String corePath;
}

/// Terminal success — a managed standalone runtime executable is ready.
final class RuntimeExecutableReady extends RuntimeProvisionProgress {
  const RuntimeExecutableReady({required this.executablePath});

  final String executablePath;
}

/// Terminal failure with a fixed, sanitized message.
final class RuntimeProvisionFailed extends RuntimeProvisionProgress {
  const RuntimeProvisionFailed(this.message);
  final String message;
}

/// Ensures a managed runtime + core are installed on this device, downloading
/// from the catalog source on first use. Idempotent.
abstract interface class RuntimeProvisioner {
  Stream<RuntimeProvisionProgress> ensure({required String coreId});
}

/// Ensures a managed standalone runtime executable is installed on this device.
/// Idempotent.
abstract interface class RuntimeExecutableProvisioner {
  Stream<RuntimeProvisionProgress> ensure();
}
