import 'dart:async';

import 'package:flutter/material.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_failure.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_library.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_models.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';

import 'console_action_button.dart';
import 'players_status_shell.dart';

/// Shared focusable route for remote sets and profile-owned local snapshots.
final class DiscSetsScreen extends StatefulWidget {
  const DiscSetsScreen({
    required this.services,
    this.titleId,
    this.titleName,
    super.key,
  });
  final PlayServices services;
  final String? titleId, titleName;
  @override
  State<DiscSetsScreen> createState() => _DiscSetsScreenState();
}

final class _DiscSetsScreenState extends State<DiscSetsScreen>
    implements InstallOperationLease {
  List<DiscSetStatus> _statuses = [];
  List<LocalDiscSet> _local = [];
  String? _message;
  bool _busy = false, _cancelled = false, _loading = true;
  bool _installing = false;
  double? _progress;
  Timer? _authorityWatch;
  bool _authorityChecking = false;
  DiscSetLibrary get _service => widget.services.discSets!;
  @override
  bool get isCurrent => mounted && !_cancelled;
  @override
  void initState() {
    super.initState();
    unawaited(_refresh());
    _authorityWatch = Timer.periodic(const Duration(milliseconds: 250), (
      _,
    ) async {
      if (_authorityChecking || !mounted) return;
      _authorityChecking = true;
      final current = await _service.current();
      _authorityChecking = false;
      if (mounted && !current) {
        _cancelled = true;
        Navigator.of(context).popUntil((route) => route.isFirst);
      }
    });
  }

  @override
  void dispose() {
    _cancelled = true;
    _authorityWatch?.cancel();
    super.dispose();
  }

  Future<void> _refresh({bool preserveMessage = false}) async {
    final local = await _service.installed(titleId: widget.titleId);
    List<DiscSetStatus> statuses = [];
    String? message;
    if (widget.titleId case final title?) {
      try {
        statuses = await _service.list(title);
      } on Object {
        message =
            'Disc availability could not be refreshed. Acquired sets remain listed below.';
      }
    }
    if (!mounted || !await _service.current() || !mounted) return;
    setState(() {
      _local = local;
      _statuses = statuses;
      _message = message ?? (preserveMessage ? _message : null);
      _loading = false;
    });
  }

  Future<bool> _confirmPartial(DiscSetStatus status) async =>
      await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('Install a partial disc set?'),
          content: SingleChildScrollView(
            child: Text(
              [
                'Only these discs will be installed:',
                ...status.discs
                    .where((d) => d.readiness == 'ready')
                    .map((d) => 'Disc ${d.ordinal}: ${d.name}'),
                if (status.missingExpectedOrdinals.isNotEmpty)
                  'Missing expected discs: ${status.missingExpectedOrdinals.join(', ')}',
                ...status.discs
                    .where((d) => d.readiness != 'ready')
                    .map(
                      (d) => 'Disc ${d.ordinal}: ${_readiness(d.readiness)}',
                    ),
                'This selection may not contain everything needed to finish the game.',
              ].join('\n'),
            ),
          ),
          actions: [
            ConsoleActionButton(
              label: 'Cancel',
              autofocus: true,
              onPressed: () => Navigator.pop(context, false),
            ),
            ConsoleActionButton(
              label: 'Install selected discs',
              onPressed: () => Navigator.pop(context, true),
            ),
          ],
        ),
      ) ??
      false;
  Future<void> _install(DiscSetStatus status, {bool resume = false}) async {
    if (_busy) return;
    if (!resume &&
        status.requiresPartialConsent &&
        !await _confirmPartial(status))
      return;
    if (!mounted) return;
    setState(() {
      _busy = true;
      _cancelled = false;
      _progress = null;
      _installing = true;
      _message = 'Preparing original discs…';
    });
    try {
      final DiscSetPlan plan;
      if (resume) {
        final pending = await _service.pending(status.discSetId);
        if (pending == null)
          throw const DiscSetException(DiscSetFailureKind.stale);
        plan = pending;
      } else {
        plan = await _service.select(
          status,
          partial: status.requiresPartialConsent,
        );
      }
      await _service.install(
        plan,
        titleName: widget.titleName ?? plan.name,
        operation: this,
        progress: (received, total) {
          if (mounted)
            setState(() {
              _progress = total > 0 ? received / total : null;
              _message = 'Installing original discs…';
            });
        },
      );
      if (mounted) setState(() => _message = 'Disc set installed.');
    } on DiscSetException catch (error) {
      if (mounted)
        setState(
          () => _message = error.kind == DiscSetFailureKind.stale
              ? 'This selection changed. Refresh and choose Install update to create a new selection.'
              : 'Installation paused. Retry to resume the same selection when access is available.',
        );
    } on Object {
      if (mounted)
        setState(
          () => _message =
              'Installation paused. Verified files are retained for retry.',
        );
    } finally {
      if (mounted) {
        setState(() {
          _busy = false;
          _installing = false;
          _progress = null;
        });
        await _refresh(preserveMessage: true);
      }
    }
  }

  Future<void> _repair(LocalDiscSet install) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _installing = true;
      _cancelled = false;
      _progress = null;
      _message = 'Repairing installed selection…';
    });
    try {
      await _service.install(
        install.plan,
        titleName: install.titleName,
        operation: this,
        progress: (received, total) {
          if (mounted)
            setState(() => _progress = total > 0 ? received / total : null);
        },
      );
      if (mounted) setState(() => _message = 'Installed selection repaired.');
    } on DiscSetException catch (error) {
      if (mounted)
        setState(
          () => _message = error.kind == DiscSetFailureKind.stale
              ? 'This installed selection changed. Open the title and choose an explicit install update.'
              : 'Repair paused. Retry when access is available.',
        );
    } on Object {
      if (mounted)
        setState(
          () => _message =
              'Repair paused. Verified files are retained for retry.',
        );
    } finally {
      if (mounted) {
        setState(() {
          _busy = false;
          _installing = false;
          _progress = null;
        });
        await _refresh(preserveMessage: true);
      }
    }
  }

  Future<void> _play(LocalDiscSet install) async {
    final playback = widget.services.discSetPlayback;
    if (_busy || playback == null) return;
    final snapshot = await reviewPlayersBeforePlay(
      context,
      slotClaims: widget.services.controllerSlotClaims,
      gamepadLister: widget.services.gamepadLister,
    );
    if (!mounted || snapshot == null) return;
    setState(() {
      _busy = true;
      _message = 'Verifying disc-set access…';
    });
    try {
      await for (final progress in playback.play(
        install,
        controllerSnapshot: snapshot,
      )) {
        if (!mounted) return;
        setState(
          () => _message = switch (progress) {
            PlayPreparingRuntime() => 'Preparing playlist runtime…',
            PlayLaunching() => 'Launching playlist…',
            PlayCompleted(result: LaunchFailed(:final message)) => message,
            PlayCompleted(result: LaunchAccessNotGranted()) ||
            PlayCompleted(
              result: LaunchAccessRevoked(),
            ) => 'Access required from this Library.',
            PlayCompleted(result: LaunchRuntimeMissing()) =>
              'The playlist runtime is unavailable.',
            PlayCompleted() => 'Returned from disc-set playback.',
            PlayRuntimePreferenceSaved() => 'Runtime selected.',
          },
        );
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _remove(LocalDiscSet install) async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await _service.uninstall(install);
      await _refresh();
    } on Object {
      if (mounted)
        setState(() => _message = 'The disc set could not be removed safely.');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Scaffold(
      appBar: AppBar(
        title: Text(
          widget.titleName == null
              ? 'Installed disc sets'
              : '${widget.titleName} · Disc sets',
        ),
      ),
      body: Actions(
        actions: {
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              if (_installing) {
                setState(() {
                  _cancelled = true;
                  _message = 'Pausing…';
                });
              } else if (!_busy) {
                Navigator.maybePop(context);
              }
              return null;
            },
          ),
        },
        child: FocusTraversalGroup(
          child: ListView(
            padding: EdgeInsets.all(layout.lg),
            children: [
              if (_loading) const LinearProgressIndicator(),
              if (_message case final message?)
                Text(message, style: context.text.body),
              if (_progress != null) LinearProgressIndicator(value: _progress),
              if (_installing)
                ConsoleActionButton(
                  label: 'Pause installation',
                  onPressed: () {
                    setState(() => _cancelled = true);
                  },
                ),
              if (!_busy)
                ConsoleActionButton(
                  label: 'Refresh',
                  autofocus: true,
                  onPressed: () => unawaited(_refresh()),
                ),
              if (!_loading && _statuses.isEmpty && _local.isEmpty)
                const Text('No disc sets available for this profile.'),
              for (final status in _statuses) ...[
                SizedBox(height: layout.lg),
                Text(status.name, style: context.text.sectionHeading),
                Text(status.completenessLabel),
                for (final disc in status.discs)
                  Text(
                    'Disc ${disc.ordinal}: ${disc.name} · ${_readiness(disc.readiness)}',
                  ),
                if (status.missingExpectedOrdinals.isNotEmpty)
                  Text(
                    'Missing expected discs: ${status.missingExpectedOrdinals.join(', ')}',
                  ),
                Wrap(
                  spacing: layout.sm,
                  children: [
                    ConsoleActionButton(
                      label:
                          _local.any(
                            (l) => l.plan.discSetId == status.discSetId,
                          )
                          ? 'Install update'
                          : status.requiresPartialConsent
                          ? 'Choose partial install'
                          : 'Install known discs',
                      onPressed: _busy || status.readyReleaseIds.isEmpty
                          ? null
                          : () => unawaited(_install(status)),
                    ),
                    ConsoleActionButton(
                      label: 'Resume interrupted install',
                      onPressed: _busy
                          ? null
                          : () => unawaited(_install(status, resume: true)),
                    ),
                  ],
                ),
              ],
              for (final install in _local) ...[
                SizedBox(height: layout.lg),
                Text(
                  '${install.titleName} · ${install.plan.name}',
                  style: context.text.sectionHeading,
                ),
                Text(
                  install.plan.selection['completeness'] == 'unknown-total'
                      ? '${install.plan.discs.length} installed discs; total unknown'
                      : '${install.plan.discs.length} discs installed · ${install.plan.selection['completeness']}',
                ),
                for (final disc in install.plan.discs)
                  Text('Disc ${disc['ordinal']}: ${disc['name']}'),
                if (!install.authorized)
                  const Text('Access required from this Library.'),
                Wrap(
                  spacing: layout.sm,
                  children: [
                    ConsoleActionButton(
                      label:
                          widget.services.discSetPlayback?.runtimeQualified ==
                              true
                          ? 'Play disc set'
                          : widget.services.discSetPlayback?.supportsSystem(
                                  install.plan.systemKey,
                                ) ==
                                true
                          ? 'Playback qualification pending'
                          : 'Original playlist runtime unsupported',
                      onPressed:
                          _busy ||
                              widget
                                      .services
                                      .discSetPlayback
                                      ?.runtimeQualified !=
                                  true
                          ? null
                          : () => unawaited(_play(install)),
                    ),
                    ConsoleActionButton(
                      label: 'Repair installed selection',
                      onPressed: _busy
                          ? null
                          : () => unawaited(_repair(install)),
                    ),
                    ConsoleActionButton(
                      label: 'Remove from profile',
                      destructive: true,
                      onPressed: _busy
                          ? null
                          : () => unawaited(_remove(install)),
                    ),
                  ],
                ),
                const Text(
                  'SwanStation disc switching: Start + Select opens the Quick Menu. Choose Disc Control, eject the disc, select the disc index, then insert it. Existing individual-disc saves are not imported.',
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

String _readiness(String value) => switch (value) {
  'ready' => 'Ready',
  'descriptor-required' =>
    'Original descriptor required; contact an administrator',
  'missing-media' => 'Media unavailable',
  'stale-layout' => 'Original layout needs repair',
  _ => 'Original layout not supported',
};
