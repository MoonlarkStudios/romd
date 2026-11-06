import 'dart:async';

import 'package:flutter/material.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';

import '../theme/console_theme_context.dart';
import 'byte_format.dart';
import 'console_action_button.dart';
import 'console_settings.dart';
import 'console_status_state.dart';

/// Supported orderings for the active profile's installed-content list.
enum StorageInstallSort {
  size('Size', Icons.data_usage_outlined),
  title('Title', Icons.sort_by_alpha),
  installed('Installed date', Icons.event_available_outlined),
  lastPlayed('Last played', Icons.history_outlined);

  const StorageInstallSort(this.label, this.icon);

  final String label;
  final IconData icon;
}

/// Settings → Storage for the active local profile.
///
/// This surface reports manifest-recorded installed content only. It does not
/// infer filesystem capacity or free space from the install records.
final class StorageScreen extends StatefulWidget {
  const StorageScreen({
    required this.installService,
    required this.onBack,
    this.clockNow,
    this.initialSort = StorageInstallSort.size,
    super.key,
  });

  final InstallService installService;
  final VoidCallback onBack;
  final DateTime Function()? clockNow;
  final StorageInstallSort initialSort;

  @override
  State<StorageScreen> createState() => _StorageScreenState();
}

final class _StorageScreenState extends State<StorageScreen> {
  final FocusNode _sortFocus = FocusNode(debugLabel: 'storage-sort');
  final FocusNode _retryFocus = FocusNode(debugLabel: 'storage-retry');
  final Map<String, FocusNode> _installFocusNodes = <String, FocusNode>{};

  StreamSubscription<List<LocalInstall>>? _subscription;
  List<LocalInstall>? _installs;
  StorageInstallSort _sort = StorageInstallSort.size;
  Object? _loadError;
  String? _operationError;
  String? _removingReleaseId;
  String? _focusAfterRefresh;
  int _watchGeneration = 0;

  bool get _busy => _removingReleaseId != null;

  @override
  void initState() {
    super.initState();
    _sort = widget.initialSort;
    _subscribe();
  }

  @override
  void didUpdateWidget(StorageScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(oldWidget.installService, widget.installService)) {
      unawaited(_subscription?.cancel());
      _installs = null;
      _loadError = null;
      _operationError = null;
      _removingReleaseId = null;
      _focusAfterRefresh = null;
      _subscribe();
    }
  }

  @override
  void dispose() {
    _watchGeneration++;
    unawaited(_subscription?.cancel());
    _sortFocus.dispose();
    _retryFocus.dispose();
    for (final node in _installFocusNodes.values) {
      node.dispose();
    }
    super.dispose();
  }

  void _subscribe() {
    final generation = ++_watchGeneration;
    final service = widget.installService;
    _subscription = service.watchInstalled().listen(
      (installs) {
        if (generation != _watchGeneration ||
            !identical(service, widget.installService)) {
          return;
        }
        _receiveInstalls(installs);
      },
      onError: (Object error) {
        if (!mounted ||
            generation != _watchGeneration ||
            !identical(service, widget.installService)) {
          return;
        }
        setState(() => _loadError = error);
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _retryFocus.requestFocus();
        });
      },
    );
  }

  void _retry() {
    unawaited(_subscription?.cancel());
    setState(() {
      _installs = null;
      _loadError = null;
    });
    _subscribe();
  }

  void _receiveInstalls(List<LocalInstall> installs) {
    if (!mounted) return;
    final activeIds = installs.map((install) => install.releaseId).toSet();
    final staleEntries = _installFocusNodes.entries
        .where((entry) => !activeIds.contains(entry.key))
        .toList(growable: false);
    final requestedFocus = _focusAfterRefresh;
    setState(() {
      _installs = List<LocalInstall>.unmodifiable(installs);
      _loadError = null;
      if (requestedFocus == null || activeIds.contains(requestedFocus)) {
        _focusAfterRefresh = null;
      }
    });
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      if (requestedFocus != null && activeIds.contains(requestedFocus)) {
        _installFocusNodes[requestedFocus]?.requestFocus();
      } else if (requestedFocus != null) {
        _sortFocus.requestFocus();
      }
      for (final entry in staleEntries) {
        if (_installFocusNodes[entry.key] == entry.value) {
          _installFocusNodes.remove(entry.key);
          entry.value.dispose();
        }
      }
    });
  }

  List<LocalInstall> get _sortedInstalls {
    final result = List<LocalInstall>.of(_installs ?? const <LocalInstall>[]);
    result.sort((left, right) {
      final comparison = switch (_sort) {
        StorageInstallSort.size => right.sizeBytes.compareTo(left.sizeBytes),
        StorageInstallSort.title => _compareText(
          left.titleName,
          right.titleName,
        ),
        StorageInstallSort.installed => right.installedAt.compareTo(
          left.installedAt,
        ),
        StorageInstallSort.lastPlayed => _compareNullableDateDescending(
          left.lastPlayedAt,
          right.lastPlayedAt,
        ),
      };
      if (comparison != 0) return comparison;
      return _compareText(left.titleName, right.titleName);
    });
    return result;
  }

  Future<void> _chooseSort() async {
    if (_busy) return;
    final chosen = await showDialog<StorageInstallSort>(
      context: context,
      barrierColor: context.consoleColors.scrim,
      builder: (context) => SimpleDialog(
        title: const Text('Sort installed games'),
        children: <Widget>[
          for (final option in StorageInstallSort.values)
            ListTile(
              autofocus: option == _sort,
              leading: Icon(option.icon),
              title: Text(option.label),
              trailing: option == _sort ? const Icon(Icons.check) : null,
              onTap: () => Navigator.of(context).pop(option),
            ),
        ],
      ),
    );
    if (chosen == null || !mounted) return;
    setState(() => _sort = chosen);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _sortFocus.requestFocus();
    });
  }

  Future<void> _confirmRemoval(LocalInstall install) async {
    if (_busy) return;
    final service = widget.installService;
    final confirmed = await showDialog<bool>(
      context: context,
      barrierColor: context.consoleColors.scrim,
      builder: (context) => AlertDialog(
        title: Text('Remove ${install.titleName}?'),
        content: const Text(
          'This removes the game from the active profile. Saves remain. '
          'Shared physical content may remain while another profile uses it.',
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
    if (confirmed == true &&
        mounted &&
        identical(service, widget.installService)) {
      await _remove(install);
    }
  }

  Future<void> _remove(LocalInstall install) async {
    if (_busy) return;
    final service = widget.installService;
    final ordered = _sortedInstalls;
    final index = ordered.indexWhere(
      (candidate) => candidate.releaseId == install.releaseId,
    );
    final nextFocus = index >= 0 && index + 1 < ordered.length
        ? ordered[index + 1].releaseId
        : index > 0
        ? ordered[index - 1].releaseId
        : null;
    setState(() {
      _removingReleaseId = install.releaseId;
      _operationError = null;
      _focusAfterRefresh = nextFocus ?? '';
    });
    try {
      await service.uninstall(install.releaseId);
    } on Object {
      if (!mounted || !identical(service, widget.installService)) return;
      setState(() {
        _removingReleaseId = null;
        _focusAfterRefresh = null;
        _operationError =
            "Couldn't remove ${install.titleName} from the active profile. Try again.";
      });
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) {
          _installFocusNodes[install.releaseId]?.requestFocus();
        }
      });
      return;
    }
    if (!mounted || !identical(service, widget.installService)) return;
    setState(() => _removingReleaseId = null);
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text('${install.titleName} removed from the active profile.'),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final Widget content;
    if (_loadError != null) {
      content = _buildError();
    } else if (_installs case final installs?) {
      content = _buildContent(installs);
    } else {
      content = Padding(
        padding: EdgeInsets.symmetric(vertical: layout.xxl),
        child: const ConsoleStatusState(
          loading: true,
          title: 'Reading installed content',
          message: 'Loading this profile’s installed-content records…',
        ),
      );
    }
    return ConsoleSettingsShell(
      eyebrow: 'Settings',
      title: 'Storage',
      onBack: widget.onBack,
      clockNow: widget.clockNow,
      contextPanel: switch (_installs) {
        final installs? => _StorageOverviewPanel(installs: installs),
        null => null,
      },
      child: content,
    );
  }

  Widget _buildError() => Padding(
    padding: EdgeInsets.symmetric(vertical: context.layout.xxl),
    child: ConsoleStatusState(
      icon: Icons.error_outline_rounded,
      title: 'Installed content unavailable',
      message:
          "Ottercade couldn't read this profile's installed-content records.",
      action: ConsoleActionButton(
        key: const ValueKey<String>('storage-retry'),
        label: 'Try again',
        icon: Icons.refresh_rounded,
        focusNode: _retryFocus,
        autofocus: true,
        onPressed: _retry,
      ),
    ),
  );

  Widget _buildContent(List<LocalInstall> installs) {
    final sorted = _sortedInstalls;
    if (sorted.isEmpty) {
      return Padding(
        padding: EdgeInsets.symmetric(vertical: context.layout.xxl),
        child: const ConsoleStatusState(
          icon: Icons.sports_esports_outlined,
          title: 'No installed games',
          message:
              'This profile has no installed games. Install one from its '
              'game page.',
        ),
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        if (_operationError case final error?) ...<Widget>[
          _StorageOperationError(message: error),
          SizedBox(height: context.layout.md),
        ],
        ConsoleSettingsSection(
          title: 'Installed games',
          description: 'Select a release to remove it from the active profile.',
          children: <Widget>[
            ConsoleSettingsNavigationRow(
              key: const ValueKey<String>('storage-sort'),
              icon: _sort.icon,
              title: 'Sort installed games',
              subtitle: 'Choose how this profile’s installed releases appear.',
              value: _sort.label,
              focusNode: _sortFocus,
              autofocus: true,
              enabled: !_busy,
              onPressed: _chooseSort,
            ),
            for (final install in sorted)
              ConsoleSettingsActionRow(
                key: ValueKey<String>('storage-install-${install.releaseId}'),
                icon: Icons.sports_esports_outlined,
                title: install.titleName,
                subtitle: _installDescription(install),
                value: _removingReleaseId == install.releaseId
                    ? 'Removing…'
                    : formatBytes(install.sizeBytes),
                focusNode: _installFocusNodes.putIfAbsent(
                  install.releaseId,
                  () => FocusNode(
                    debugLabel: 'storage-install-${install.releaseId}',
                  ),
                ),
                enabled: !_busy,
                onPressed: () => _confirmRemoval(install),
              ),
          ],
        ),
      ],
    );
  }
}

/// Quiet totals-and-guidance panel beside the installed list. Warning color
/// plays no role here: removal is confirmed in its dialog, so resting content
/// stays in the ordinary text ramp.
final class _StorageOverviewPanel extends StatelessWidget {
  const _StorageOverviewPanel({required this.installs});

  final List<LocalInstall> installs;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final totalBytes = installs.fold<int>(
      0,
      (total, install) => total + install.sizeBytes,
    );
    final count = installs.length;
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
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Text(
            'THIS DEVICE',
            style: context.text.sectionLabel.copyWith(color: colors.textMuted),
          ),
          SizedBox(height: layout.md),
          Text(formatBytes(totalBytes), style: context.text.sectionHeading),
          SizedBox(height: layout.xxs),
          Text(
            count == 1
                ? 'across 1 installed game'
                : 'across $count installed games',
            style: context.text.bodyCompact.copyWith(color: colors.textMuted),
          ),
          SizedBox(height: layout.md),
          Divider(
            height: layout.hairlineStroke,
            thickness: layout.hairlineStroke,
            color: colors.hairline,
          ),
          SizedBox(height: layout.md),
          Text(
            'Totals come from release manifests, not device capacity. '
            'Removing a game keeps its saves.',
            style: context.text.bodyCompact.copyWith(color: colors.textMuted),
          ),
        ],
      ),
    );
  }
}

final class _StorageOperationError extends StatelessWidget {
  const _StorageOperationError({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Semantics(
    liveRegion: true,
    child: Row(
      children: <Widget>[
        Icon(Icons.error_outline, color: context.consoleColors.warning),
        SizedBox(width: context.layout.sm),
        Expanded(
          child: Text(
            message,
            style: context.text.metadata.copyWith(
              color: context.consoleColors.warning,
            ),
          ),
        ),
      ],
    ),
  );
}

String _installDescription(LocalInstall install) {
  final platform = install.platformName?.trim();
  final platformLabel = platform == null || platform.isEmpty
      ? install.platformShortName
      : platform;
  final release = install.releaseRevision?.trim();
  final releaseLabel = release == null || release.isEmpty
      ? install.releaseName
      : '${install.releaseName} · $release';
  final lastPlayed = install.lastPlayedAt == null
      ? 'Never played'
      : 'Last played ${_formatDate(install.lastPlayedAt!)}';
  return '$platformLabel · $releaseLabel\n'
      'Installed ${_formatDate(install.installedAt)} · $lastPlayed';
}

String _formatDate(DateTime date) {
  const months = <String>[
    'Jan',
    'Feb',
    'Mar',
    'Apr',
    'May',
    'Jun',
    'Jul',
    'Aug',
    'Sep',
    'Oct',
    'Nov',
    'Dec',
  ];
  final local = date.toLocal();
  return '${months[local.month - 1]} ${local.day}, ${local.year}';
}

int _compareText(String left, String right) =>
    left.toLowerCase().compareTo(right.toLowerCase());

int _compareNullableDateDescending(DateTime? left, DateTime? right) {
  if (left == null && right == null) return 0;
  if (left == null) return 1;
  if (right == null) return -1;
  return right.compareTo(left);
}
