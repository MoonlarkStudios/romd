enum LocalProfileEntryMode { open, pin }

abstract final class RomdServerOrigins {
  static const defaultValue = 'http://localhost:11338';

  static Uri get defaultUri => Uri.parse(defaultValue);

  static Uri parse(String value) {
    final trimmed = value.trim();
    if (trimmed.isEmpty) {
      throw const FormatException('ROMD server origin is required.');
    }

    final withScheme = trimmed.contains('://') ? trimmed : 'http://$trimmed';
    final uri = Uri.parse(withScheme);
    final scheme = uri.scheme.toLowerCase();
    final path = uri.path;

    if ((scheme != 'http' && scheme != 'https') || uri.host.isEmpty) {
      throw FormatException('Invalid ROMD server origin.', value);
    }
    if ((path.isNotEmpty && path != '/') ||
        uri.hasQuery ||
        uri.hasFragment ||
        uri.userInfo.isNotEmpty) {
      throw FormatException('Use only the server origin.', value);
    }

    return uri.hasPort
        ? Uri(scheme: scheme, host: uri.host, port: uri.port)
        : Uri(scheme: scheme, host: uri.host);
  }

  static Uri? tryParse(String value) {
    try {
      return parse(value);
    } on FormatException {
      return null;
    }
  }
}

final class LocalProfile {
  const LocalProfile({
    required this.id,
    required this.displayName,
    required this.avatarKey,
    required this.accentColor,
    required this.romdServerOrigin,
    required this.entryMode,
    required this.createdAt,
    required this.updatedAt,
    this.lastUsedAt,
    this.romdAccountLink,
  });

  final String id;
  final String displayName;
  final String avatarKey;
  final int accentColor;

  /// The ROMD server this profile connects to, or `null` for a purely local
  /// profile that has not attached a server. A local profile can still browse
  /// and launch installed content offline; a server is opt-in.
  final Uri? romdServerOrigin;
  final LocalProfileEntryMode entryMode;
  final DateTime createdAt;
  final DateTime updatedAt;
  final DateTime? lastUsedAt;
  final RomdAccountLink? romdAccountLink;

  /// Whether this profile has a ROMD server attached to connect to.
  bool get hasServer => romdServerOrigin != null;
}

final class RomdAccountLink {
  const RomdAccountLink({
    required this.romdUserId,
    required this.username,
    required this.email,
    required this.linkedAt,
    this.lastLoginAt,
  });

  final String romdUserId;
  final String username;
  final String email;
  final DateTime linkedAt;
  final DateTime? lastLoginAt;
}

final class CreateLocalProfileRequest {
  const CreateLocalProfileRequest({
    required this.displayName,
    this.avatarKey = 'default',
    this.accentColor = 0xff1fbf8f,
    this.romdServerOrigin,
    this.entryMode = LocalProfileEntryMode.open,
  });

  final String displayName;
  final String avatarKey;
  final int accentColor;
  final Uri? romdServerOrigin;
  final LocalProfileEntryMode entryMode;
}
