import 'package:flutter/material.dart';

import '../domain/local_profile.dart';
import '../play/session/domain/play_activity.dart';
import 'theme/console_theme_context.dart';
import 'widgets/console_settings.dart';
import 'widgets/profile_avatar.dart';

/// Settings for the active local profile and this device.
///
/// The root remains navigation-only. Profile, server, and account actions live
/// in a hierarchical detail page so opening Settings never focuses an
/// immediate state-changing action.
final class ProfileSettingsScreen extends StatefulWidget {
  const ProfileSettingsScreen({
    required this.localProfile,
    required this.connected,
    required this.onConnect,
    required this.onChangeServer,
    required this.onRemoveServer,
    required this.onSignOut,
    required this.onStorage,
    required this.onControllers,
    required this.onEmulation,
    required this.onSwitchProfile,
    required this.onBack,
    this.playActivity,
    this.clockNow,
    super.key,
  });

  final LocalProfile localProfile;
  final bool connected;
  final PlayActivityRepository? playActivity;
  final VoidCallback onConnect;
  final VoidCallback onChangeServer;
  final VoidCallback onRemoveServer;
  final VoidCallback onSignOut;
  final VoidCallback onStorage;
  final VoidCallback onControllers;
  final VoidCallback onEmulation;
  final VoidCallback onSwitchProfile;
  final VoidCallback onBack;
  final DateTime Function()? clockNow;

  @override
  State<ProfileSettingsScreen> createState() => _ProfileSettingsScreenState();
}

enum _SettingsPage { root, profileAndAccount }

final class _ProfileSettingsScreenState extends State<ProfileSettingsScreen> {
  final FocusNode _profileAndAccountFocus = FocusNode(
    debugLabel: 'settings-profile-and-account',
  );
  final FocusNode _storageFocus = FocusNode(debugLabel: 'settings-storage');
  final FocusNode _controllersFocus = FocusNode(
    debugLabel: 'settings-controllers',
  );
  final FocusNode _emulationFocus = FocusNode(debugLabel: 'settings-emulation');
  final FocusNode _playActivityFocus = FocusNode(
    debugLabel: 'settings-play-activity',
  );
  _SettingsPage _page = _SettingsPage.root;

  @override
  void dispose() {
    _profileAndAccountFocus.dispose();
    _storageFocus.dispose();
    _controllersFocus.dispose();
    _emulationFocus.dispose();
    _playActivityFocus.dispose();
    super.dispose();
  }

  void _backToRoot() {
    setState(() => _page = _SettingsPage.root);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _profileAndAccountFocus.requestFocus();
    });
  }

  @override
  Widget build(BuildContext context) => switch (_page) {
    _SettingsPage.root => _buildRoot(),
    _SettingsPage.profileAndAccount => _ProfileAndAccountSettingsPage(
      localProfile: widget.localProfile,
      connected: widget.connected,
      onConnect: widget.onConnect,
      onChangeServer: widget.onChangeServer,
      onRemoveServer: widget.onRemoveServer,
      onSignOut: widget.onSignOut,
      onSwitchProfile: widget.onSwitchProfile,
      onBack: _backToRoot,
      clockNow: widget.clockNow,
    ),
  };

  Widget _buildRoot() {
    final profile = widget.localProfile;
    return ConsoleSettingsShell(
      title: 'Settings',
      onBack: widget.onBack,
      clockNow: widget.clockNow,
      contextPanel: _SettingsIdentityPanel(
        localProfile: profile,
        connected: widget.connected,
      ),
      child: ConsoleSettingsSection(
        children: <Widget>[
          ConsoleSettingsNavigationRow(
            key: const ValueKey<String>('settings-account-row'),
            icon: Icons.manage_accounts_outlined,
            title: 'Profile & Account',
            subtitle: 'Local profile and ROMD account',
            value: profile.displayName,
            autofocus: true,
            focusNode: _profileAndAccountFocus,
            onPressed: () =>
                setState(() => _page = _SettingsPage.profileAndAccount),
          ),
          ConsoleSettingsNavigationRow(
            key: const ValueKey<String>('settings-storage-row'),
            icon: Icons.storage_outlined,
            title: 'Storage',
            subtitle: 'Installed games and local content',
            focusNode: _storageFocus,
            onPressed: widget.onStorage,
          ),
          ConsoleSettingsNavigationRow(
            key: const ValueKey<String>('settings-controllers-row'),
            icon: Icons.sports_esports_outlined,
            title: 'Controllers',
            subtitle: 'Controller setup and button prompts',
            focusNode: _controllersFocus,
            onPressed: widget.onControllers,
          ),
          ConsoleSettingsNavigationRow(
            key: const ValueKey<String>('settings-emulation-row'),
            icon: Icons.tune,
            title: 'Emulation',
            subtitle: 'Advanced emulator settings',
            focusNode: _emulationFocus,
            onPressed: widget.onEmulation,
          ),
          if (widget.playActivity case final playActivity?)
            FutureBuilder<bool>(
              future: playActivity.isSyncEnabled(),
              builder: (context, snapshot) => ConsoleSettingsActionRow(
                key: const ValueKey<String>('settings-play-activity-row'),
                icon: Icons.history_outlined,
                title: 'Sync play activity with ROMD',
                subtitle: 'Profile and server specific',
                value: snapshot.data == true ? 'On' : 'Local only',
                focusNode: _playActivityFocus,
                onPressed: () => _configurePlayActivitySync(playActivity),
              ),
            ),
        ],
      ),
    );
  }

  Future<void> _configurePlayActivitySync(
    PlayActivityRepository repository,
  ) async {
    final enabled = await repository.isSyncEnabled();
    if (!mounted) return;
    final choice = await showDialog<_PlayActivityChoice>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(
          enabled
              ? 'Stop syncing play activity?'
              : 'Sync play activity with ROMD?',
        ),
        content: Text(
          enabled
              ? 'New sessions will remain on this console. Sessions already sent to ROMD are not deleted.'
              : 'Choose whether ROMD receives only sessions started after enabling sync or all locally stored sessions.',
        ),
        actions: <Widget>[
          TextButton(
            autofocus: true,
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel'),
          ),
          if (enabled)
            TextButton(
              onPressed: () =>
                  Navigator.of(context).pop(_PlayActivityChoice.disable),
              child: const Text('Stop syncing'),
            )
          else ...<Widget>[
            TextButton(
              onPressed: () =>
                  Navigator.of(context).pop(_PlayActivityChoice.futureOnly),
              child: const Text('Future activity only'),
            ),
            TextButton(
              onPressed: () =>
                  Navigator.of(context).pop(_PlayActivityChoice.includeStored),
              child: const Text('Include stored sessions'),
            ),
          ],
        ],
      ),
    );
    if (choice == null) return;
    await switch (choice) {
      _PlayActivityChoice.disable => repository.setSyncEnabled(enabled: false),
      _PlayActivityChoice.futureOnly => repository.setSyncEnabled(
        enabled: true,
      ),
      _PlayActivityChoice.includeStored => repository.setSyncEnabled(
        enabled: true,
        scope: PlayActivityEnableScope.includeStoredSessions,
      ),
    };
    if (mounted) setState(() {});
  }
}

enum _PlayActivityChoice { disable, futureOnly, includeStored }

final class _ProfileAndAccountSettingsPage extends StatelessWidget {
  const _ProfileAndAccountSettingsPage({
    required this.localProfile,
    required this.connected,
    required this.onConnect,
    required this.onChangeServer,
    required this.onRemoveServer,
    required this.onSignOut,
    required this.onSwitchProfile,
    required this.onBack,
    this.clockNow,
  });

  final LocalProfile localProfile;
  final bool connected;
  final VoidCallback onConnect;
  final VoidCallback onChangeServer;
  final VoidCallback onRemoveServer;
  final VoidCallback onSignOut;
  final VoidCallback onSwitchProfile;
  final VoidCallback onBack;
  final DateTime Function()? clockNow;

  Future<void> _confirmRemove(BuildContext context) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Remove ROMD server?'),
        content: const Text(
          'This disconnects the local profile from its ROMD server and clears '
          'the stored sign-in for that server.',
        ),
        actions: <Widget>[
          TextButton(
            autofocus: true,
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          TextButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Remove'),
          ),
        ],
      ),
    );
    if (confirmed == true) onRemoveServer();
  }

  @override
  Widget build(BuildContext context) {
    final hasServer = localProfile.hasServer;
    final username = localProfile.romdAccountLink?.username;
    final accountValue = connected && username != null
        ? username
        : username != null
        ? 'Last signed in as $username'
        : hasServer
        ? 'Not signed in'
        : 'No server';
    return ConsoleSettingsHierarchicalPage(
      eyebrow: 'Settings',
      title: 'Profile & Account',
      description: 'Manage the active profile and its ROMD account.',
      onBack: onBack,
      clockNow: clockNow,
      categories: <ConsoleSettingsCategory>[
        ConsoleSettingsCategory(
          id: 'profile',
          icon: Icons.person_outline,
          title: 'Local Profile',
          subtitle: localProfile.displayName,
          child: _LocalProfileSettings(
            localProfile: localProfile,
            onSwitchProfile: onSwitchProfile,
          ),
        ),
        ConsoleSettingsCategory(
          id: 'account',
          icon: connected
              ? Icons.cloud_done_outlined
              : Icons.cloud_off_outlined,
          title: 'ROMD Account',
          subtitle: connected
              ? username ?? 'Connected'
              : hasServer
              ? 'Not connected'
              : 'Not configured',
          child: ConsoleSettingsSection(
            title: 'ROMD Account',
            children: <Widget>[
              ConsoleSettingsValueRow(
                icon: Icons.dns_outlined,
                title: 'Server address',
                value:
                    localProfile.romdServerOrigin?.toString() ??
                    'No server attached',
              ),
              ConsoleSettingsValueRow(
                icon: connected
                    ? Icons.cloud_done_outlined
                    : Icons.cloud_off_outlined,
                title: 'Account',
                value: accountValue,
              ),
              ConsoleSettingsActionRow(
                icon: hasServer ? Icons.sync_alt : Icons.add_link,
                title: hasServer ? 'Change server' : 'Add ROMD server',
                onPressed: hasServer ? onChangeServer : onConnect,
              ),
              if (hasServer && !connected)
                ConsoleSettingsActionRow(
                  icon: Icons.login,
                  title: 'Connect ROMD',
                  subtitle: 'Sign in to the configured ROMD server',
                  onPressed: onConnect,
                )
              else
                ConsoleSettingsActionRow(
                  icon: Icons.logout,
                  title: 'Sign out',
                  onPressed: onSignOut,
                ),
              if (hasServer && !connected)
                ConsoleSettingsActionRow(
                  icon: Icons.link_off,
                  title: 'Remove server',
                  destructive: true,
                  onPressed: () => _confirmRemove(context),
                ),
            ],
          ),
        ),
      ],
    );
  }
}

/// Quiet identity summary beside the Settings root column: who is signed in
/// and where this device points. Display-only; every action stays in the
/// focusable rows.
final class _SettingsIdentityPanel extends StatelessWidget {
  const _SettingsIdentityPanel({
    required this.localProfile,
    required this.connected,
  });

  final LocalProfile localProfile;
  final bool connected;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final host = localProfile.romdServerOrigin?.host;
    final statusLabel = connected
        ? host == null
              ? 'Connected'
              : 'Connected · $host'
        : localProfile.hasServer
        ? 'Not connected'
        : 'No server';
    return Container(
      padding: EdgeInsets.all(layout.lg),
      decoration: BoxDecoration(
        color: colors.panelSurface,
        borderRadius: BorderRadius.circular(layout.panelRadius),
        border: Border.all(
          color: colors.panelBorder,
          width: layout.hairlineStroke,
        ),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          ProfileAvatar(
            avatarKey: localProfile.avatarKey,
            accentColor: Color(localProfile.accentColor),
            size: layout.avatarSm,
          ),
          SizedBox(width: layout.md),
          Expanded(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Text(
                  localProfile.displayName,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: context.text.supportingTitle,
                ),
                SizedBox(height: layout.xxs),
                Text(
                  statusLabel,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: context.text.metadata.copyWith(
                    color: connected ? colors.connected : colors.textMuted,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

final class _LocalProfileSettings extends StatelessWidget {
  const _LocalProfileSettings({
    required this.localProfile,
    required this.onSwitchProfile,
  });

  final LocalProfile localProfile;
  final VoidCallback onSwitchProfile;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: <Widget>[
      Center(
        child: ProfileAvatar(
          avatarKey: localProfile.avatarKey,
          accentColor: Color(localProfile.accentColor),
          size: context.layout.avatarLg,
        ),
      ),
      SizedBox(height: context.layout.md),
      Text(
        localProfile.displayName,
        textAlign: TextAlign.center,
        style: context.text.sectionHeading.copyWith(
          color: context.consoleColors.textStrong,
        ),
      ),
      SizedBox(height: context.layout.xl),
      ConsoleSettingsSection(
        title: 'Profile actions',
        children: <Widget>[
          ConsoleSettingsNavigationRow(
            key: const ValueKey<String>('settings-profile-row'),
            icon: Icons.switch_account_outlined,
            title: 'Switch profile',
            subtitle: 'Return to profile selection',
            onPressed: onSwitchProfile,
          ),
        ],
      ),
    ],
  );
}
