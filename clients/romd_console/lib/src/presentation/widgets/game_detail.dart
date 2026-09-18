import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';

import '../../data/consumer_api_client.dart';
import '../../domain/console_game.dart';
import '../../input/console_input_mode.dart';
import '../../input/console_intents.dart';
import '../catalog/catalog_operation_context.dart';
import '../theme/console_theme_context.dart';
import 'byte_format.dart';
import 'console_action_button.dart';
import 'console_ambient_background.dart';
import 'console_circle_button.dart';
import 'console_hint_bar.dart';
import 'controller_help_screen.dart';
import 'cover_art.dart';
import 'game_record.dart';
import 'media_viewer_screen.dart';
import 'meta_chip.dart';
import 'platform_badge.dart';
import 'platform_presentation.dart';
import 'players_status_shell.dart';

const _launchHandoffOverlayKey = ValueKey<String>('game-detail-launch-handoff');

ConsoleGameDetail _localDetail(ConsoleGame game) => ConsoleGameDetail(
  id: game.id,
  platformId: game.platformId,
  platformName: game.platformName,
  title: game.title,
  description: null,
  publisher: null,
  developer: null,
  genre: game.genre,
  releaseDate: game.releaseDate,
  players: null,
  rating: game.rating,
  media: const <ConsoleMediaRef>[],
  releases: game.defaultReleaseId == null
      ? const <ConsoleRelease>[]
      : <ConsoleRelease>[
          ConsoleRelease(
            id: game.defaultReleaseId!,
            name: game.title,
            revision: null,
            regions: const <String>[],
            languages: const <String>[],
            sizeBytes: 0,
            isComplete: true,
          ),
        ],
  defaultReleaseId: game.defaultReleaseId,
);

/// Pushes the cover-forward Game View, flying [heroTag]'s cover in via Hero and
/// fading the surrounding page. Shared by the library rails and the
/// platform/collection drill-in grids so every entry point animates identically.
Future<void> pushGameDetail(
  BuildContext context, {
  required ConsoleGame game,
  required String heroTag,
  required ConsumerApiClient consumerApiClient,
  required CatalogOperationContext? operation,
  required PlayServices playServices,
  required String localProfileId,
}) {
  final motion = context.motion;
  return Navigator.of(context).push<void>(
    PageRouteBuilder<void>(
      transitionDuration: motion.resolve(context, motion.routeEnter),
      reverseTransitionDuration: motion.resolve(context, motion.routeExit),
      pageBuilder: (context, animation, secondaryAnimation) => GameDetailScreen(
        game: game,
        consumerApiClient: consumerApiClient,
        operation: operation,
        playServices: playServices,
        localProfileId: localProfileId,
        heroTag: heroTag,
      ),
      transitionsBuilder: (context, animation, secondaryAnimation, child) =>
          FadeTransition(
            opacity: CurvedAnimation(
              parent: animation,
              curve: motion.emphasizedCurve,
            ),
            child: child,
          ),
    ),
  );
}

final class GameDetailScreen extends StatefulWidget {
  const GameDetailScreen({
    required this.game,
    required this.consumerApiClient,
    required this.operation,
    required this.playServices,
    required this.localProfileId,
    required this.heroTag,
    super.key,
  });

  final ConsoleGame game;
  final ConsumerApiClient consumerApiClient;
  final CatalogOperationContext? operation;
  final PlayServices playServices;
  final String localProfileId;

  /// Matches the tapped rail card's Hero tag so the cover flies in without
  /// stretch.
  final String heroTag;

  @override
  State<GameDetailScreen> createState() => _GameDetailScreenState();
}

final class _GameDetailScreenState extends State<GameDetailScreen>
    with SingleTickerProviderStateMixin {
  late Future<ConsoleGameDetail> _detailFuture = _loadDetail();
  late final AnimationController _intro = AnimationController(vsync: this);
  bool _introStarted = false;
  _PlayPhase _phase = _PlayPhase.idle;
  double? _installProgress;
  String? _runtimeActivityLabel;
  ConsoleGameDetail? _detail;
  List<LocalInstall> _titleInstalls = const <LocalInstall>[];
  String? _selectedReleaseId;

  // Runtime candidates for the selected install, fetched off its identity (no
  // path resolution) and cached so the action row and picker render
  // synchronously. [_candidatesReleaseId] records which install the cache
  // belongs to, dropping stale fetches when the selection moves mid-flight.
  RuntimeCandidates? _runtimeCandidates;
  String? _candidatesReleaseId;

  // Cancellation plumbing: the active install/provision subscription is held so
  // a Cancel can tear it down (and abort the download) even while parked between
  // progress events — a flag-and-break can't interrupt a parked `await`.
  bool _cancelRequested = false;
  StreamSubscription<dynamic>? _activeSub;
  StreamSubscription<List<LocalInstall>>? _installsSub;
  StreamSubscription<ProfileLocalLibraryResult>? _localLibrarySub;
  Timer? _authorityWatch;
  Completer<_StreamOutcome>? _activeOutcome;

  bool get _busy => _phase != _PlayPhase.idle;
  bool _accessRequired = false;
  bool _privacyCleared = false;
  bool _disposing = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _intro.duration = context.motion.resolve(context, context.motion.intro);
    if (!_introStarted) {
      _introStarted = true;
      _intro.forward();
    }
  }

  bool get _cancellable =>
      _phase == _PlayPhase.preparing ||
      _phase == _PlayPhase.installingRuntime ||
      _phase == _PlayPhase.installing;

  Map<String, LocalInstall> get _installedByReleaseId => <String, LocalInstall>{
    for (final install in _titleInstalls) install.releaseId: install,
  };

  Set<String> get _installedReleaseIds => _installedByReleaseId.keys.toSet();

  String? get _currentReleaseId =>
      _selectedReleaseId ?? _preferredReleaseId(_detail);

  LocalInstall? get _selectedInstall {
    final releaseId = _currentReleaseId;
    return releaseId == null ? null : _installedByReleaseId[releaseId];
  }

  /// The "Run with" action appears only when the resolver offers a real
  /// choice for the selected install; a single candidate keeps today's UI.
  bool get _hasRunWithChoice =>
      _selectedInstall != null &&
      _candidatesReleaseId == _selectedInstall!.releaseId &&
      (_runtimeCandidates?.candidates.length ?? 0) > 1;

  String? _preferredReleaseId(ConsoleGameDetail? detail) {
    if (_selectedReleaseId case final selected?) {
      return selected;
    }
    final releaseIds = detail?.releases.map((release) => release.id).toSet();
    for (final install in _titleInstalls) {
      if (releaseIds == null || releaseIds.contains(install.releaseId)) {
        return install.releaseId;
      }
    }
    return detail?.defaultReleaseId ??
        widget.game.defaultReleaseId ??
        (detail?.releases.isEmpty == false ? detail!.releases.first.id : null);
  }

  ConsoleRelease? _releaseById(ConsoleGameDetail? detail, String releaseId) {
    if (detail == null) {
      return null;
    }
    for (final release in detail.releases) {
      if (release.id == releaseId) {
        return release;
      }
    }
    return null;
  }

  void _selectRelease(String releaseId) {
    if (_selectedReleaseId == releaseId) {
      return;
    }
    setState(() => _selectedReleaseId = releaseId);
    unawaited(_refreshRuntimeCandidates());
  }

  Future<void> _refreshRuntimeCandidates() async {
    final install = _selectedInstall;
    if (install == null) {
      if (_runtimeCandidates != null) {
        setState(() {
          _runtimeCandidates = null;
          _candidatesReleaseId = null;
        });
      }
      return;
    }
    final candidates = await widget.playServices.runtimeResolver.resolve(
      PlayIdentity.ofInstall(install),
    );
    if (!mounted || _selectedInstall?.releaseId != install.releaseId) {
      return;
    }
    setState(() {
      _runtimeCandidates = candidates;
      _candidatesReleaseId = install.releaseId;
    });
  }

  @override
  void initState() {
    super.initState();
    _installsSub = widget.playServices.install
        .watchInstalledForTitle(widget.game.id)
        .listen((installs) {
          if (!mounted) {
            return;
          }
          setState(() {
            _titleInstalls = installs;
            _selectedReleaseId = _preferredReleaseId(_detail);
          });
          unawaited(_refreshRuntimeCandidates());
        });
    _localLibrarySub = widget.playServices.localLibrary
        .watchLibrary(sort: ProfileLocalLibrarySort.title)
        .listen((result) {
          if (!mounted) return;
          final required = switch (result) {
            ProfileLocalLibraryReady(:final titles) => titles.any(
              (title) =>
                  title.install.titleId == widget.game.id &&
                  title.state == ProfileLocalLibraryState.accessRequired,
            ),
            ProfileLocalLibraryNoServer() ||
            ProfileLocalLibraryUnavailable() => false,
          };
          if (_accessRequired != required) {
            setState(() => _accessRequired = required);
          }
        });
    if (widget.operation != null) {
      widget.operation!.changes?.addListener(_closeIfStale);
      if (widget.operation!.changes == null) {
        _authorityWatch = Timer.periodic(
          const Duration(milliseconds: 250),
          (_) => _closeIfStale(),
        );
      }
    }
  }

  @override
  void didUpdateWidget(covariant GameDetailScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    final previous = oldWidget.operation;
    final current = widget.operation;
    if (previous == null && current == null) return;
    if (previous != null &&
        current != null &&
        previous.hasSameOperation(current)) {
      return;
    }
    previous?.changes?.removeListener(_closeIfStale);
    _authorityWatch?.cancel();
    _authorityWatch = null;
    current?.changes?.addListener(_closeIfStale);
    if (current != null && current.changes == null) {
      _authorityWatch = Timer.periodic(
        const Duration(milliseconds: 250),
        (_) => _closeIfStale(),
      );
    }
    _privacyCleared = false;
    _detail = null;
    _detailFuture = _loadDetail();
  }

  @override
  void dispose() {
    _disposing = true;
    _cancelActiveStream();
    unawaited(_installsSub?.cancel());
    unawaited(_localLibrarySub?.cancel());
    _authorityWatch?.cancel();
    widget.operation?.changes?.removeListener(_closeIfStale);
    _intro.dispose();
    super.dispose();
  }

  Future<ConsoleGameDetail> _loadDetail() async {
    final operation = widget.operation;
    if (operation == null) return _localDetail(widget.game);
    if (!operation.isRequestCurrent) {
      throw const ConsumerApiException(
        'Catalog operation is no longer current.',
      );
    }
    final detail = await operation.executeConsumer(
      (accessToken) => widget.consumerApiClient.getTitle(
        accessToken: accessToken,
        titleId: widget.game.id,
      ),
    );
    if (!operation.isRequestCurrent) {
      throw const ConsumerApiException(
        'Catalog operation is no longer current.',
      );
    }
    if (detail.id != widget.game.id) {
      throw const ConsumerApiException('Unexpected title response.');
    }
    if (mounted && operation.isRequestCurrent) {
      setState(() {
        _detail = detail;
        _selectedReleaseId = _preferredReleaseId(detail);
      });
      unawaited(_refreshRuntimeCandidates());
    }
    return detail;
  }

  void _closeIfStale() {
    final operation = widget.operation;
    if (!mounted ||
        operation == null ||
        operation.isCurrent ||
        _privacyCleared) {
      return;
    }
    _privacyCleared = true;
    _cancelRequested = true;
    _authorityWatch?.cancel();
    _cancelActiveStream();
    setState(() {});
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) Navigator.of(context).popUntil((route) => route.isFirst);
    });
  }

  @override
  Widget build(BuildContext context) => CatalogPrivacyBoundary(
    operation: widget.operation,
    child: Stack(
      children: <Widget>[
        FutureBuilder<ConsoleGameDetail>(
          future: _detailFuture,
          builder: (context, snapshot) => _DetailScaffold(
            game: widget.game,
            heroTag: widget.heroTag,
            detail: snapshot.data,
            loading: snapshot.connectionState != ConnectionState.done,
            error: snapshot.hasError,
            accessRequired: _accessRequired,
            canAcquire: widget.operation?.isRequestCurrent ?? false,
            preparingPlay: _busy,
            selectedReleaseId: _currentReleaseId,
            installedReleaseIds: _installedReleaseIds,
            selectedInstall: _selectedInstall,
            showRunWith: _hasRunWithChoice,
            intro: _intro,
            onPrimaryAction: _runPrimaryAction,
            onCancelPlay: _requestCancel,
            onSelectRelease: _selectRelease,
            onUninstall: _uninstallSelected,
            onRunWith: _showRunWithPicker,
            onShowRecord: _showRecord,
            onShowControls: _showControls,
          ),
        ),
        if (_phase == _PlayPhase.launching)
          Positioned.fill(
            child: ColoredBox(
              key: _launchHandoffOverlayKey,
              color: context.consoleColors.mediaBackdrop,
            ),
          )
        else if (_busy)
          _PlayStatusBanner(
            label: _phaseLabel,
            detail: _phaseDetail,
            progress: _installProgress,
            onCancel: _cancellable ? _requestCancel : null,
          ),
      ],
    ),
  );

  String get _phaseLabel => switch (_phase) {
    _PlayPhase.idle => '',
    _PlayPhase.preparing => 'Preparing…',
    _PlayPhase.installingRuntime =>
      _installProgress == null
          ? 'Installing $_runtimeInstallLabel…'
          : 'Installing $_runtimeInstallLabel… ${(_installProgress! * 100).round()}%',
    _PlayPhase.installing =>
      _installProgress == null
          ? 'Installing…'
          : 'Installing ${(_installProgress! * 100).round()}%',
    _PlayPhase.uninstalling => 'Removing from profile…',
    _PlayPhase.launching => 'Launching…',
    _PlayPhase.cancelling => 'Cancelling…',
  };

  String get _runtimeInstallLabel {
    final label = _runtimeActivityLabel?.trim();
    return label == null || label.isEmpty ? 'runtime' : label;
  }

  // The first-run runtime fetch is the longest, least-obvious wait — reassure
  // the player it's a one-time cost rather than a stall.
  String? get _phaseDetail => switch (_phase) {
    _PlayPhase.installingRuntime => 'First-time setup · this happens once',
    _ => null,
  };

  void _showRecord() {
    final operation = widget.operation;
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (context) => CatalogPrivacyBoundary(
          operation: operation,
          child: GameRecordScreen(
            game: widget.game,
            detailFuture: _detailFuture,
          ),
        ),
      ),
    );
  }

  void _showControls() {
    final operation = widget.operation;
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (context) => CatalogPrivacyBoundary(
          operation: operation,
          child: const ControllerHelpScreen(),
        ),
      ),
    );
  }

  Future<void> _runPrimaryAction() async {
    final install = _selectedInstall;
    if (install == null) {
      await _installSelected();
    } else {
      final snapshot = await reviewPlayersBeforePlay(
        context,
        slotClaims: widget.playServices.controllerSlotClaims,
        gamepadLister: widget.playServices.gamepadLister,
      );
      if (snapshot == null) {
        return;
      }
      if (widget.operation case final operation? when !operation.isCurrent) {
        return;
      }
      await _launchInstalled(install, snapshot);
    }
  }

  // Install downloads, verifies, and materializes only. Launching is a separate
  // Play action so the title's state is explicit.
  Future<void> _installSelected() async {
    final operation = widget.operation;
    if (_busy || operation == null || !operation.isRequestCurrent) {
      return;
    }
    setState(() {
      _phase = _PlayPhase.preparing;
      _installProgress = null;
      _runtimeActivityLabel = null;
      _cancelRequested = false;
    });

    ConsoleGameDetail? detail;
    try {
      detail = await _detailFuture;
    } on Object {
      detail = null;
    }
    if (!mounted || _disposing) {
      return;
    }
    if (!operation.isRequestCurrent || _privacyCleared) {
      _cancelRequested = true;
      return;
    }
    if (_cancelRequested) {
      _settleCancelled();
      return;
    }
    if (detail == null) {
      _fail('This title’s Catalog details are unavailable. Try again.');
      return;
    }

    final releaseId = _currentReleaseId;
    if (releaseId == null) {
      _fail('No playable release for this title.');
      return;
    }
    final release = _releaseById(detail, releaseId);

    final target = PlayTarget(
      releaseId: releaseId,
      titleId: widget.game.id,
      displayName: widget.game.title,
      localProfileId: widget.localProfileId,
      platformId: detail.platformId,
      platformName: detail.platformName,
      coverUrl: detail.poster?.url,
      artwork: detail.artwork,
      releaseName: release?.name,
      releaseRevision: release?.revision,
    );

    if (!operation.isRequestCurrent || _privacyCleared) {
      _cancelRequested = true;
      return;
    }

    ResolvedPlayTarget? resolved;
    var installFailed = false;
    final installOutcome = await _drive<InstallProgress>(
      widget.playServices.install.install(target, operation: operation),
      (progress) {
        switch (progress) {
          case InstallStarted():
            setState(() => _phase = _PlayPhase.preparing);
          case InstallDownloading(:final receivedBytes, :final totalBytes):
            setState(() {
              _phase = _PlayPhase.installing;
              _installProgress = totalBytes > 0
                  ? receivedBytes / totalBytes
                  : null;
            });
          case InstallVerifying():
            setState(() => _phase = _PlayPhase.installing);
          case InstallCompleted(resolved: final completed):
            resolved = completed;
          case InstallFailed(:final message):
            installFailed = true;
            _fail(message);
        }
      },
    );
    if (!mounted || _disposing) {
      return;
    }
    if (!operation.isCurrent || _privacyCleared) return;
    switch (installOutcome) {
      case _StreamOutcome.cancelled:
        _settleCancelled();
        return;
      case _StreamOutcome.error:
        if (!installFailed) {
          _fail('Something went wrong preparing this game.');
        }
        return;
      case _StreamOutcome.done:
        if (installFailed) {
          return;
        }
    }

    final launchTarget = resolved;
    if (launchTarget == null) {
      _fail("This game didn't finish installing.");
      return;
    }

    setState(() {
      _selectedReleaseId = launchTarget.releaseId;
      _phase = _PlayPhase.idle;
      _installProgress = null;
    });
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text('${release?.name ?? 'Release'} installed.')),
    );
  }

  Future<void> _launchInstalled(
    LocalInstall install,
    ReviewedLaunchSnapshot initialSnapshot, {
    RuntimeProfileId? requestedRuntimeProfileId,
  }) async {
    if (_busy) {
      return;
    }
    setState(() {
      _phase = _PlayPhase.preparing;
      _installProgress = null;
      _runtimeActivityLabel = null;
      _cancelRequested = false;
    });

    final request = PlayRequest(
      releaseId: install.releaseId,
      titleId: install.titleId,
      displayName: install.titleName,
      requestedRuntimeProfileId: requestedRuntimeProfileId,
    );

    var snapshot = initialSnapshot;
    while (mounted) {
      final reviewRequired = await _launchRequest(request, snapshot);
      if (!reviewRequired || !mounted) {
        return;
      }
      final reviewed = await reviewPlayersBeforePlay(
        context,
        slotClaims: widget.playServices.controllerSlotClaims,
        gamepadLister: widget.playServices.gamepadLister,
      );
      if (reviewed == null) {
        return;
      }
      snapshot = reviewed;
    }
  }

  Future<bool> _launchRequest(
    PlayRequest request,
    ReviewedLaunchSnapshot controllerSnapshot,
  ) async {
    // The coordinator owns resolve → provision → launch. First Play downloads
    // any managed runtime pieces; subsequent Plays find them already present
    // and skip straight through.
    var controllerReviewRequired = false;
    final outcome = await _drive<PlayProgress>(
      widget.playServices.coordinator.play(
        request,
        controllerSnapshot: controllerSnapshot,
      ),
      (event) {
        switch (event) {
          case PlayPreparingRuntime(:final activity):
            _onDependencyActivity(activity);
          case PlayLaunching():
            setState(() => _phase = _PlayPhase.launching);
          case PlayRuntimePreferenceSaved(
            :final platformLabel,
            :final runtimeDisplayName,
          ):
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(
                content: Text(
                  '$platformLabel games will now use $runtimeDisplayName.',
                ),
              ),
            );
          case PlayCompleted(:final result):
            if (result is LaunchControllerReviewRequired) {
              controllerReviewRequired = true;
              setState(() => _phase = _PlayPhase.idle);
            } else {
              _onPlayCompleted(result);
            }
        }
      },
    );
    if (!mounted) {
      return false;
    }
    switch (outcome) {
      case _StreamOutcome.cancelled:
        _settleCancelled();
      case _StreamOutcome.error:
        // Defensive: the coordinator converts failures into PlayCompleted.
        _fail('Something went wrong preparing the runtime.');
      case _StreamOutcome.done:
        break;
    }
    return controllerReviewRequired;
  }

  void _onPlayCompleted(LaunchResult result) {
    switch (result) {
      case LaunchExited():
        setState(() {
          _phase = _PlayPhase.idle;
          _installProgress = null;
          _runtimeActivityLabel = null;
        });
      case LaunchRuntimeMissing(:final runtimeName):
        _fail(_runtimeMissingMessage(runtimeName));
      case LaunchUnsupportedPlatform():
        _fail('This platform is not playable on this device yet.');
      case LaunchFailed(:final message):
        _fail(message);
      case LaunchControllerReviewRequired():
        setState(() => _phase = _PlayPhase.idle);
      case LaunchAccessNotGranted():
        _fail('Add this game to this profile’s Local Library before playing.');
      case LaunchAccessRevoked():
        _fail('Access is required from this ROMD Library.');
    }
  }

  String _runtimeMissingMessage(String? runtimeName) {
    final name = runtimeName?.trim();
    if (name == null || name.isEmpty) {
      return "Couldn't find the selected runtime. Install it to play this title.";
    }
    return "Couldn't find $name. Install $name to play this title.";
  }

  // The runtime choice travels inside the unresolved launch request. The
  // coordinator persists it only after exact profile/server authorization and
  // installed-target resolution have succeeded.
  Future<void> _showRunWithPicker() async {
    final install = _selectedInstall;
    final candidates = _runtimeCandidates;
    final preferred = candidates?.preferred;
    if (_busy || install == null || candidates == null || preferred == null) {
      return;
    }
    final platformLabel = install.platformName ?? install.platformShortName;
    final chosen = await showDialog<RuntimeProfile>(
      context: context,
      barrierColor: context.consoleColors.scrim,
      builder: (context) => CatalogPrivacyBoundary(
        operation: widget.operation,
        child: _RunWithDialog(
          candidates: candidates.candidates,
          preferredId: preferred.id,
          platformLabel: platformLabel,
        ),
      ),
    );
    if (chosen == null || !mounted) {
      return;
    }
    if (widget.operation case final operation? when !operation.isCurrent) {
      return;
    }
    final snapshot = await reviewPlayersBeforePlay(
      context,
      slotClaims: widget.playServices.controllerSlotClaims,
      gamepadLister: widget.playServices.gamepadLister,
    );
    if (snapshot != null) {
      await _launchInstalled(
        install,
        snapshot,
        requestedRuntimeProfileId: chosen.id,
      );
      if (mounted) unawaited(_refreshRuntimeCandidates());
    }
  }

  Future<void> _uninstallSelected() async {
    final install = _selectedInstall;
    final operation = widget.operation;
    if (_busy ||
        install == null ||
        (operation != null && !operation.isCurrent)) {
      return;
    }
    // Removal is destructive, so it always passes through an explicit
    // confirmation surface — the same contract the Storage screen follows.
    final confirmed = await showDialog<bool>(
      context: context,
      barrierColor: context.consoleColors.scrim,
      builder: (context) => CatalogPrivacyBoundary(
        operation: operation,
        child: AlertDialog(
          title: Text('Remove ${widget.game.title}?'),
          content: const Text(
            'This removes the game from the active profile. Saves remain. '
            'Shared files stay while another profile uses them.',
          ),
          actions: <Widget>[
            ConsoleActionButton(
              label: 'Cancel',
              kind: ConsoleActionKind.quiet,
              autofocus: true,
              onPressed: () => Navigator.of(context).pop(false),
            ),
            ConsoleActionButton(
              label: 'Remove',
              kind: ConsoleActionKind.quiet,
              destructive: true,
              onPressed: () => Navigator.of(context).pop(true),
            ),
          ],
        ),
      ),
    );
    if (confirmed != true ||
        !mounted ||
        (operation != null && !operation.isCurrent)) {
      return;
    }
    setState(() {
      _phase = _PlayPhase.uninstalling;
      _installProgress = null;
      _runtimeActivityLabel = null;
      _cancelRequested = false;
    });
    try {
      await widget.playServices.install.uninstall(
        install.releaseId,
        operation: operation,
      );
    } on Object {
      if (mounted) {
        _fail("Couldn't remove this game from the profile.");
      }
      return;
    }
    if (!mounted ||
        _disposing ||
        _privacyCleared ||
        (operation != null && !operation.isCurrent)) {
      return;
    }
    setState(() {
      _selectedReleaseId = install.releaseId;
      _phase = _PlayPhase.idle;
      _installProgress = null;
      _runtimeActivityLabel = null;
    });
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text('${install.releaseName} removed from this profile.'),
      ),
    );
  }

  // Only non-terminal activity arrives here: terminal dependency outcomes
  // surface as PlayCompleted, so the type excludes them by construction.
  void _onDependencyActivity(DependencyActivity activity) {
    switch (activity) {
      case DependencyDownloading(:final receivedBytes, :final totalBytes):
        setState(() {
          _phase = _PlayPhase.installingRuntime;
          _installProgress = (totalBytes != null && totalBytes > 0)
              ? receivedBytes / totalBytes
              : null;
          _runtimeActivityLabel = activity.label;
        });
      case DependencyUnpacking(:final label):
        setState(() {
          _phase = _PlayPhase.installingRuntime;
          _installProgress = null; // unpacking has no byte total
          _runtimeActivityLabel = label;
        });
    }
  }

  // Drives [stream], routing events through [onEvent], while holding the live
  // subscription so [_requestCancel] can abort it mid-flight. Resolves to how
  // the stream ended (done / error / cancelled).
  Future<_StreamOutcome> _drive<T>(
    Stream<T> stream,
    void Function(T event) onEvent,
  ) {
    final outcome = Completer<_StreamOutcome>();
    final subscription = stream.listen(
      (event) {
        if (_cancelRequested || !mounted) {
          return;
        }
        onEvent(event);
      },
      onError: (Object _, StackTrace __) {
        if (!outcome.isCompleted) {
          outcome.complete(_StreamOutcome.error);
        }
      },
      onDone: () {
        if (!outcome.isCompleted) {
          outcome.complete(_StreamOutcome.done);
        }
      },
      cancelOnError: false,
    );
    _activeSub = subscription;
    _activeOutcome = outcome;
    outcome.future.whenComplete(() {
      if (identical(_activeSub, subscription)) {
        _activeSub = null;
        _activeOutcome = null;
      }
    });
    return outcome.future;
  }

  // Aborts the in-flight install/provision (and the underlying download).
  void _requestCancel() {
    if (!_cancellable || _cancelRequested) {
      return;
    }
    _cancelRequested = true;
    setState(() {
      _phase = _PlayPhase.cancelling;
      _installProgress = null;
      _runtimeActivityLabel = null;
    });
    _cancelActiveStream();
  }

  void _cancelActiveStream() {
    final subscription = _activeSub;
    final outcome = _activeOutcome;
    _activeSub = null;
    _activeOutcome = null;
    if (outcome != null && !outcome.isCompleted) {
      outcome.complete(_StreamOutcome.cancelled);
    }
    unawaited(subscription?.cancel());
  }

  void _settleCancelled() {
    if (!mounted || _privacyCleared || _disposing) return;
    setState(() {
      _phase = _PlayPhase.idle;
      _installProgress = null;
      _runtimeActivityLabel = null;
    });
    _cancelRequested = false;
  }

  void _fail(String message) {
    if (!mounted || _disposing || _privacyCleared) return;
    setState(() {
      _phase = _PlayPhase.idle;
      _installProgress = null;
      _runtimeActivityLabel = null;
    });
    final messenger = ScaffoldMessenger.of(context)..clearSnackBars();
    messenger.showSnackBar(SnackBar(content: Text(message)));
  }
}

enum _PlayPhase {
  idle,
  preparing,
  installingRuntime,
  installing,
  uninstalling,
  launching,
  cancelling,
}

/// How a driven install/provision stream ended.
enum _StreamOutcome { done, error, cancelled }

/// Transient status strip shown over the detail view while a title is being
/// prepared / installed / launched. Shows a Cancel affordance whenever the
/// current phase can be aborted ([onCancel] non-null).
final class _PlayStatusBanner extends StatelessWidget {
  const _PlayStatusBanner({
    required this.label,
    required this.progress,
    this.detail,
    this.onCancel,
  });

  final String label;
  final String? detail;
  final double? progress;
  final VoidCallback? onCancel;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    final metadata = context.text.metadata;
    return Positioned(
      top: 0,
      left: 0,
      right: 0,
      child: SafeArea(
        child: Padding(
          padding: EdgeInsets.symmetric(
            horizontal: layout.screenGutter,
            vertical: layout.lg,
          ),
          child: Container(
            padding: EdgeInsets.symmetric(
              horizontal: layout.xl,
              vertical: layout.md,
            ),
            decoration: BoxDecoration(
              color: colors.dialogSurface,
              borderRadius: BorderRadius.circular(layout.panelRadius),
              border: Border.all(color: colors.selectionBorder),
              boxShadow: context.elevation.dialog,
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: <Widget>[
                          Text(
                            label,
                            style: context.text.eyebrow.copyWith(
                              color: colors.textStrong,
                            ),
                          ),
                          if (detail case final detail?) ...<Widget>[
                            SizedBox(height: layout.xxs),
                            Text(
                              detail,
                              style: metadata.copyWith(color: colors.textFaint),
                            ),
                          ],
                        ],
                      ),
                    ),
                    if (onCancel != null) ...<Widget>[
                      SizedBox(width: layout.md),
                      _CancelChip(onCancel: onCancel!),
                    ],
                  ],
                ),
                SizedBox(height: layout.sm),
                ClipRRect(
                  borderRadius: BorderRadius.circular(layout.pillRadius),
                  child: LinearProgressIndicator(
                    value: progress,
                    minHeight: layout.xxs,
                    backgroundColor: colors.hairline,
                    color: context.colors.primary,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The banner's Cancel control — mouse-tappable, with a keycap hint that the
/// gamepad B / Esc also aborts (the owning screen routes DismissIntent here
/// while a play is in flight).
final class _CancelChip extends StatelessWidget {
  const _CancelChip({required this.onCancel});

  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    final glyph = ConsoleInputModeScope.of(context) == ConsoleInputMode.gamepad
        ? 'B'
        : 'Esc';
    return Semantics(
      button: true,
      label: 'Cancel',
      child: GestureDetector(
        onTap: onCancel,
        child: Container(
          padding: EdgeInsets.symmetric(
            horizontal: layout.md,
            vertical: layout.sm,
          ),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(layout.controlRadius),
            border: Border.all(color: colors.borderStrong),
            color: colors.controlRestFill,
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Text(
                glyph,
                style: context.text.metadata.copyWith(color: colors.textFaint),
              ),
              SizedBox(width: layout.xs),
              Text(
                'Cancel',
                style: context.text.utilityLabel.copyWith(
                  color: colors.textMuted,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

final class _DetailScaffold extends StatelessWidget {
  const _DetailScaffold({
    required this.game,
    required this.heroTag,
    required this.detail,
    required this.loading,
    required this.error,
    required this.accessRequired,
    required this.canAcquire,
    required this.preparingPlay,
    required this.selectedReleaseId,
    required this.installedReleaseIds,
    required this.selectedInstall,
    required this.showRunWith,
    required this.intro,
    required this.onPrimaryAction,
    required this.onCancelPlay,
    required this.onSelectRelease,
    required this.onUninstall,
    required this.onRunWith,
    required this.onShowRecord,
    required this.onShowControls,
  });

  final ConsoleGame game;
  final String heroTag;
  final ConsoleGameDetail? detail;
  final bool loading;
  final bool error;
  final bool accessRequired;
  final bool canAcquire;
  final bool preparingPlay;
  final String? selectedReleaseId;
  final Set<String> installedReleaseIds;
  final LocalInstall? selectedInstall;
  final bool showRunWith;
  final Animation<double> intro;
  final VoidCallback onPrimaryAction;
  final VoidCallback onCancelPlay;
  final ValueChanged<String> onSelectRelease;
  final VoidCallback onUninstall;
  final VoidCallback onRunWith;
  final VoidCallback onShowRecord;
  final VoidCallback onShowControls;

  @override
  Widget build(BuildContext context) {
    final backdropUrl = detail != null ? detail!.hero?.url : game.hero?.url;
    final tone = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    ).tone;

    final layout = context.layout;
    return Scaffold(
      backgroundColor: context.colors.surface,
      body: Stack(
        children: <Widget>[
          const Positioned.fill(child: ConsoleAmbientBackground(dimmed: true)),
          // Key art: a dropped-in image when present, else the archival monogram
          // backdrop. Either way it runs through the same legibility pipeline so
          // text below stays readable.
          if (backdropUrl != null)
            Positioned.fill(
              child: _BackdropImage(
                url: backdropUrl,
                contain:
                    (detail != null ? detail!.hero : game.hero)?.contain ??
                    false,
              ),
            )
          else
            Positioned.fill(
              child: _MonogramBackdrop(tone: tone, title: game.title),
            ),
          Positioned.fill(child: _BackdropShade(tone: tone)),
          SafeArea(
            child: Column(
              children: <Widget>[
                Expanded(
                  child: Padding(
                    padding: EdgeInsets.fromLTRB(
                      layout.screenGutter,
                      layout.xl,
                      layout.screenGutter,
                      layout.sm,
                    ),
                    child: LayoutBuilder(
                      builder: (context, constraints) => _DetailContent(
                        game: game,
                        heroTag: heroTag,
                        detail: detail,
                        loading: loading,
                        error: error,
                        accessRequired: accessRequired,
                        canAcquire: canAcquire,
                        preparingPlay: preparingPlay,
                        selectedReleaseId: selectedReleaseId,
                        installedReleaseIds: installedReleaseIds,
                        selectedInstall: selectedInstall,
                        showRunWith: showRunWith,
                        intro: intro,
                        wide: constraints.maxWidth >= 1080,
                        compact: constraints.maxHeight < 620,
                        onPrimaryAction: onPrimaryAction,
                        onCancelPlay: onCancelPlay,
                        onSelectRelease: onSelectRelease,
                        onUninstall: onUninstall,
                        onRunWith: onRunWith,
                        onShowRecord: onShowRecord,
                        onShowControls: onShowControls,
                      ),
                    ),
                  ),
                ),
                ConsoleFooterBar(
                  hints: <ConsoleHint>[
                    const ConsoleHint(
                      glyph: ConsoleHintGlyphs.confirm,
                      gamepadGlyph: 'A',
                      label: 'Select',
                    ),
                    const ConsoleHint(
                      glyph: ConsoleHintGlyphs.navigate,
                      gamepadGlyph: 'D-PAD',
                      label: 'Navigate',
                    ),
                    if (game.canLaunch)
                      const ConsoleHint(
                        glyph: 'Y',
                        gamepadGlyph: 'Y',
                        label: 'Details',
                      ),
                    if (game.canLaunch)
                      const ConsoleHint(
                        glyph: 'C',
                        gamepadGlyph: 'X',
                        label: 'Shortcuts',
                      ),
                    const ConsoleHint(
                      glyph: 'Esc',
                      gamepadGlyph: 'B',
                      label: 'Back',
                    ),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The focusable region of the Game View. Owns a single explicit selection
/// (`_DetailZone` + media index) and all key handling — deliberately *not*
/// Flutter's spatial traversal, so controller/keyboard navigation is fully
/// predictable: Play is selected on open, ◂ ▸ toggle Play/Details, ▼ drops into
/// the media row, Enter activates, Esc/B go back, Y opens the record.
final class _DetailContent extends StatefulWidget {
  const _DetailContent({
    required this.game,
    required this.heroTag,
    required this.detail,
    required this.loading,
    required this.error,
    required this.accessRequired,
    required this.canAcquire,
    required this.preparingPlay,
    required this.selectedReleaseId,
    required this.installedReleaseIds,
    required this.selectedInstall,
    required this.showRunWith,
    required this.intro,
    required this.wide,
    required this.compact,
    required this.onPrimaryAction,
    required this.onCancelPlay,
    required this.onSelectRelease,
    required this.onUninstall,
    required this.onRunWith,
    required this.onShowRecord,
    required this.onShowControls,
  });

  final ConsoleGame game;
  final String heroTag;
  final ConsoleGameDetail? detail;
  final bool loading;
  final bool error;
  final bool accessRequired;
  final bool canAcquire;
  final bool preparingPlay;
  final String? selectedReleaseId;
  final Set<String> installedReleaseIds;
  final LocalInstall? selectedInstall;
  final bool showRunWith;
  final Animation<double> intro;
  final bool wide;

  /// True when the viewport is too short for the full column (small windows,
  /// non-16:9 outputs) — the synopsis tightens so the stage stays glanceable.
  final bool compact;
  final VoidCallback onPrimaryAction;
  final VoidCallback onCancelPlay;
  final ValueChanged<String> onSelectRelease;
  final VoidCallback onUninstall;
  final VoidCallback onRunWith;
  final VoidCallback onShowRecord;
  final VoidCallback onShowControls;

  @override
  State<_DetailContent> createState() => _DetailContentState();
}

enum _DetailZone {
  back,
  about,
  play,
  details,
  controls,
  runWith,
  releases,
  uninstall,
  media,
}

final class _DetailContentState extends State<_DetailContent> {
  final FocusNode _node = FocusNode(debugLabel: 'GameDetail');
  final ScrollController _contentScroll = ScrollController();
  final ScrollController _releaseScroll = ScrollController();
  final ScrollController _mediaScroll = ScrollController();

  // Anchors for the vertical zones inside the scrolling content column. The
  // selection drives the viewport: whenever the zone changes, its anchor is
  // scrolled into view, so every zone stays reachable on short windows where
  // the column runs past the fold.
  final GlobalKey _aboutAnchor = GlobalKey(debugLabel: 'zone-about');
  final GlobalKey _actionsAnchor = GlobalKey(debugLabel: 'zone-actions');
  final GlobalKey _releasesAnchor = GlobalKey(debugLabel: 'zone-releases');
  final GlobalKey _cacheAnchor = GlobalKey(debugLabel: 'zone-cache');
  final GlobalKey _mediaAnchor = GlobalKey(debugLabel: 'zone-media');

  late _DetailZone _zone = widget.game.canLaunch
      ? _DetailZone.play
      : _DetailZone.details;
  int _releaseIndex = 0;
  int _mediaIndex = 0;

  double get _releaseTileWidth => context.layout.releaseRail.strip.tileWidth;
  double get _releaseTileGap => context.layout.releaseRail.strip.tileGap;
  double get _mediaTileWidth => context.layout.mediaRail.tileWidth;
  double get _mediaTileGap => context.layout.mediaRail.tileGap;

  List<ConsoleRelease> get _releases =>
      widget.detail?.releases ?? const <ConsoleRelease>[];

  List<ConsoleMediaRef> get _media =>
      widget.detail?.media ?? const <ConsoleMediaRef>[];

  @override
  void didUpdateWidget(covariant _DetailContent oldWidget) {
    super.didUpdateWidget(oldWidget);
    // The cache panel unmounts when the selected release is uninstalled (or
    // the selection moves to a non-installed release) — vacate its zone so the
    // selection never points at a control that no longer exists.
    if (_zone == _DetailZone.uninstall && !_hasUninstall) {
      _zone = _releases.isEmpty ? _buttonZone : _DetailZone.releases;
    }
    // Likewise the Run-with action leaves the row when the selection moves to
    // a release without a runtime choice.
    if (_zone == _DetailZone.runWith && !widget.showRunWith) {
      _zone = _buttonZone;
    }
    final selectedReleaseId = widget.selectedReleaseId;
    if (selectedReleaseId == null || _releases.isEmpty) {
      return;
    }
    final nextIndex = _releases.indexWhere(
      (release) => release.id == selectedReleaseId,
    );
    if (nextIndex >= 0 && nextIndex != _releaseIndex) {
      _releaseIndex = nextIndex;
    }
  }

  @override
  void dispose() {
    _node.dispose();
    _contentScroll.dispose();
    _releaseScroll.dispose();
    _mediaScroll.dispose();
    super.dispose();
  }

  // Moves the selection and brings the new zone's anchor into the vertical
  // viewport. [also] runs inside the same setState (index clamps etc.).
  void _setZone(_DetailZone zone, {VoidCallback? also}) {
    setState(() {
      _zone = zone;
      also?.call();
    });
    _revealZone(zone);
  }

  void _revealZone(_DetailZone zone) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) {
        return;
      }
      if (zone == _DetailZone.back) {
        // The back chrome is pinned above the scroll view — return the column
        // to its top so the title context is visible alongside it.
        if (_contentScroll.hasClients && _contentScroll.offset > 0) {
          final motion = context.motion;
          _contentScroll.animateTo(
            0,
            duration: motion.resolve(context, motion.contentTransition),
            curve: motion.spatialCurve,
          );
        }
        return;
      }
      final anchor = switch (zone) {
        _DetailZone.about => _aboutAnchor,
        _DetailZone.play ||
        _DetailZone.details ||
        _DetailZone.controls ||
        _DetailZone.runWith => _actionsAnchor,
        _DetailZone.releases => _releasesAnchor,
        _DetailZone.uninstall => _cacheAnchor,
        _DetailZone.media => _mediaAnchor,
        _DetailZone.back => null,
      };
      final anchoredContext = anchor?.currentContext;
      if (anchoredContext != null) {
        final motion = anchoredContext.motion;
        Scrollable.ensureVisible(
          anchoredContext,
          alignment: 0.5,
          duration: motion.resolve(anchoredContext, motion.contentTransition),
          curve: motion.spatialCurve,
        );
      }
    });
  }

  void _move(TraversalDirection direction) {
    switch (direction) {
      case TraversalDirection.up:
        _moveUp();
      case TraversalDirection.down:
        _moveDown();
      case TraversalDirection.left:
        _moveHorizontal(-1);
      case TraversalDirection.right:
        _moveHorizontal(1);
    }
  }

  List<_DetailZone> get _buttonZones => <_DetailZone>[
    if (widget.game.canLaunch) _DetailZone.play,
    _DetailZone.details,
    if (widget.game.canLaunch) _DetailZone.controls,
    if (widget.showRunWith) _DetailZone.runWith,
  ];

  _DetailZone get _buttonZone => _buttonZones.first;

  bool get _hasAbout => widget.detail?.description?.trim().isNotEmpty ?? false;

  bool get _hasUninstall => widget.selectedInstall != null;

  void _moveUp() {
    switch (_zone) {
      case _DetailZone.media:
        _setZone(
          _hasUninstall
              ? _DetailZone.uninstall
              : _releases.isEmpty
              ? _buttonZone
              : _DetailZone.releases,
        );
      case _DetailZone.uninstall:
        _setZone(_releases.isEmpty ? _buttonZone : _DetailZone.releases);
      case _DetailZone.play:
      case _DetailZone.details:
      case _DetailZone.controls:
      case _DetailZone.runWith:
        _setZone(_hasAbout ? _DetailZone.about : _DetailZone.back);
      case _DetailZone.about:
        _setZone(_DetailZone.back);
      case _DetailZone.releases:
        _setZone(_buttonZone);
      case _DetailZone.back:
        break;
    }
  }

  void _moveDown() {
    switch (_zone) {
      case _DetailZone.back:
        _setZone(_hasAbout ? _DetailZone.about : _buttonZone);
      case _DetailZone.about:
        _setZone(_buttonZone);
      case _DetailZone.play:
      case _DetailZone.details:
      case _DetailZone.controls:
      case _DetailZone.runWith:
        if (_releases.isNotEmpty) {
          _enterReleases();
        } else if (_hasUninstall) {
          _setZone(_DetailZone.uninstall);
        } else if (_media.isNotEmpty) {
          _enterMedia();
        }
      case _DetailZone.releases:
        if (_hasUninstall) {
          _setZone(_DetailZone.uninstall);
        } else if (_media.isNotEmpty) {
          _enterMedia();
        }
      case _DetailZone.uninstall:
        if (_media.isNotEmpty) {
          _enterMedia();
        }
      case _DetailZone.media:
        break;
    }
  }

  void _enterReleases() {
    _setZone(
      _DetailZone.releases,
      also: () {
        _releaseIndex = _releaseIndex.clamp(0, _releases.length - 1).toInt();
      },
    );
    _selectRelease(_releaseIndex);
    _scrollReleaseIntoView();
  }

  void _enterMedia() {
    _setZone(
      _DetailZone.media,
      also: () {
        _mediaIndex = _mediaIndex.clamp(0, _media.length - 1).toInt();
      },
    );
    _scrollMediaIntoView();
  }

  void _moveHorizontal(int delta) {
    switch (_zone) {
      case _DetailZone.play:
      case _DetailZone.details:
      case _DetailZone.controls:
      case _DetailZone.runWith:
        final zones = _buttonZones;
        final current = zones.indexOf(_zone);
        final next = (current + delta).clamp(0, zones.length - 1).toInt();
        if (next != current) {
          setState(() => _zone = zones[next]);
        }
      case _DetailZone.media:
        final next = (_mediaIndex + delta).clamp(0, _media.length - 1).toInt();
        if (next != _mediaIndex) {
          setState(() => _mediaIndex = next);
          _scrollMediaIntoView();
        }
      case _DetailZone.releases:
        final next = (_releaseIndex + delta)
            .clamp(0, _releases.length - 1)
            .toInt();
        if (next != _releaseIndex) {
          setState(() => _releaseIndex = next);
          _selectRelease(next);
          _scrollReleaseIntoView();
        }
      case _DetailZone.back:
      case _DetailZone.about:
      case _DetailZone.uninstall:
        break;
    }
  }

  void _activate() {
    switch (_zone) {
      case _DetailZone.back:
        Navigator.of(context).maybePop();
      case _DetailZone.about:
        _openAbout();
      case _DetailZone.play:
        if (widget.game.canLaunch && !widget.preparingPlay) {
          widget.onPrimaryAction();
        }
      case _DetailZone.details:
        widget.onShowRecord();
      case _DetailZone.controls:
        if (widget.game.canLaunch) {
          widget.onShowControls();
        }
      case _DetailZone.runWith:
        if (!widget.preparingPlay) {
          widget.onRunWith();
        }
      case _DetailZone.releases:
        _selectRelease(_releaseIndex);
      case _DetailZone.uninstall:
        if (!widget.preparingPlay) {
          widget.onUninstall();
        }
      case _DetailZone.media:
        _openMedia(_mediaIndex);
    }
  }

  // Full synopsis in a focused overlay — the inline description clamps at five
  // lines, so long text is reachable rather than silently cut.
  void _openAbout() {
    final description = widget.detail?.description;
    if (description == null || description.trim().isEmpty) {
      return;
    }
    final operation = CatalogPrivacyBoundary.maybeOperationOf(context);
    showDialog<void>(
      context: context,
      barrierColor: context.consoleColors.scrim,
      builder: (context) => CatalogPrivacyBoundary(
        operation: operation,
        child: _AboutDialog(title: widget.game.title, description: description),
      ),
    );
  }

  void _selectRelease(int index) {
    if (index < 0 || index >= _releases.length) {
      return;
    }
    widget.onSelectRelease(_releases[index].id);
  }

  void _openMedia(int index) {
    if (index < 0 || index >= _media.length) {
      return;
    }
    setState(() {
      _zone = _DetailZone.media;
      _mediaIndex = index;
    });
    final operation = CatalogPrivacyBoundary.maybeOperationOf(context);
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (context) => CatalogPrivacyBoundary(
          operation: operation,
          child: MediaViewerScreen(media: _media, initialIndex: index),
        ),
      ),
    );
  }

  void _scrollMediaIntoView() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_mediaScroll.hasClients) {
        return;
      }
      final target = (_mediaIndex * (_mediaTileWidth + _mediaTileGap)).clamp(
        0.0,
        _mediaScroll.position.maxScrollExtent,
      );
      final motion = context.motion;
      _mediaScroll.animateTo(
        target,
        duration: motion.resolve(context, motion.contentTransition),
        curve: motion.spatialCurve,
      );
    });
  }

  void _scrollReleaseIntoView() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_releaseScroll.hasClients) {
        return;
      }
      final target = (_releaseIndex * (_releaseTileWidth + _releaseTileGap))
          .clamp(0.0, _releaseScroll.position.maxScrollExtent);
      final motion = context.motion;
      _releaseScroll.animateTo(
        target,
        duration: motion.resolve(context, motion.selection),
        curve: motion.spatialCurve,
      );
    });
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final cover = Hero(
      tag: widget.heroTag,
      child: SizedBox(
        width: widget.wide ? 208 : 150,
        child: AspectRatio(
          aspectRatio: kCoverAspectRatio,
          child: CoverArt(
            coverUrl: widget.detail != null
                ? widget.detail!.poster?.url
                : widget.game.poster?.url ?? widget.game.coverUrl,
            contain:
                (widget.detail != null
                        ? widget.detail!.poster
                        : widget.game.poster)
                    ?.contain ??
                true,
            platformId: widget.game.platformId,
            platformName: widget.game.platformName,
            title: widget.game.title,
            borderRadius: layout.panelRadius,
          ),
        ),
      ),
    );

    final info = _PrimaryDetail(
      game: widget.game,
      detail: widget.detail,
      loading: widget.loading,
      error: widget.error,
      accessRequired: widget.accessRequired,
      canAcquire: widget.canAcquire,
      preparingPlay: widget.preparingPlay,
      selectedReleaseId: widget.selectedReleaseId,
      installedReleaseIds: widget.installedReleaseIds,
      selectedInstall: widget.selectedInstall,
      showRunWith: widget.showRunWith,
      aboutSelected: _zone == _DetailZone.about,
      playSelected: _zone == _DetailZone.play,
      detailsSelected: _zone == _DetailZone.details,
      controlsSelected: _zone == _DetailZone.controls,
      runWithSelected: _zone == _DetailZone.runWith,
      releasesSelected: _zone == _DetailZone.releases,
      uninstallSelected: _zone == _DetailZone.uninstall,
      focusedReleaseIndex: _zone == _DetailZone.releases ? _releaseIndex : -1,
      releaseScroll: _releaseScroll,
      releaseTileWidth: _releaseTileWidth,
      releaseTileGap: _releaseTileGap,
      descriptionMaxLines: widget.compact ? 3 : 5,
      aboutAnchor: _aboutAnchor,
      actionsAnchor: _actionsAnchor,
      releasesAnchor: _releasesAnchor,
      cacheAnchor: _cacheAnchor,
      onPrimaryAction: widget.onPrimaryAction,
      onSelectRelease: widget.onSelectRelease,
      onUninstall: widget.onUninstall,
      onRunWith: widget.onRunWith,
      onOpenAbout: _openAbout,
      onShowRecord: widget.onShowRecord,
      onShowControls: widget.onShowControls,
    );

    return Shortcuts(
      shortcuts: const <ShortcutActivator, Intent>{
        SingleActivator(LogicalKeyboardKey.keyY): ShowDetailsIntent(),
        SingleActivator(LogicalKeyboardKey.keyC): ShowControlsIntent(),
      },
      child: Actions(
        actions: <Type, Action<Intent>>{
          DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
            onInvoke: (intent) {
              _move(intent.direction);
              return null;
            },
          ),
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              _activate();
              return null;
            },
          ),
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              // While a play is in flight, B / Esc cancels it instead of
              // leaving the screen (which would abandon the download blindly).
              if (widget.preparingPlay) {
                widget.onCancelPlay();
              } else {
                Navigator.of(context).maybePop();
              }
              return null;
            },
          ),
          ShowDetailsIntent: CallbackAction<ShowDetailsIntent>(
            onInvoke: (_) {
              widget.onShowRecord();
              return null;
            },
          ),
          ShowControlsIntent: CallbackAction<ShowControlsIntent>(
            onInvoke: (_) {
              if (widget.game.canLaunch) {
                widget.onShowControls();
              }
              return null;
            },
          ),
        },
        child: Focus(
          focusNode: _node,
          autofocus: true,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              _intoView(
                widget.intro,
                const Interval(0, 0.6),
                Align(
                  alignment: Alignment.centerLeft,
                  child: ConsoleCircleButton(
                    selected: _zone == _DetailZone.back,
                    onTap: () => Navigator.of(context).maybePop(),
                  ),
                ),
              ),
              Expanded(
                child: Align(
                  alignment: Alignment.bottomLeft,
                  child: SingleChildScrollView(
                    controller: _contentScroll,
                    padding: EdgeInsets.only(top: layout.sm, bottom: layout.md),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: <Widget>[
                        Row(
                          crossAxisAlignment: CrossAxisAlignment.end,
                          children: <Widget>[
                            cover,
                            SizedBox(width: layout.xl),
                            Expanded(
                              child: _intoView(
                                widget.intro,
                                const Interval(0.12, 0.8),
                                info,
                              ),
                            ),
                          ],
                        ),
                        if (_media.isNotEmpty)
                          _intoView(
                            widget.intro,
                            const Interval(0.3, 1),
                            _MediaRow(
                              key: _mediaAnchor,
                              media: _media,
                              controller: _mediaScroll,
                              tileWidth: _mediaTileWidth,
                              tileGap: _mediaTileGap,
                              selectedIndex: _zone == _DetailZone.media
                                  ? _mediaIndex
                                  : -1,
                              onActivate: _openMedia,
                            ),
                          ),
                      ],
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Staggered fade + rise for a piece of detail content as the page opens. The
/// Hero cover is intentionally excluded — it flies in on its own.
Widget _intoView(Animation<double> intro, Interval interval, Widget child) {
  final anim = CurvedAnimation(parent: intro, curve: interval);
  return AnimatedBuilder(
    animation: anim,
    builder: (context, child) => Opacity(
      opacity: anim.value,
      child: Transform.translate(
        offset: Offset(0, context.layout.xl * (1 - anim.value)),
        child: child,
      ),
    ),
    child: child,
  );
}

final class _PrimaryDetail extends StatelessWidget {
  const _PrimaryDetail({
    required this.game,
    required this.detail,
    required this.loading,
    required this.error,
    required this.accessRequired,
    required this.canAcquire,
    required this.preparingPlay,
    required this.selectedReleaseId,
    required this.installedReleaseIds,
    required this.selectedInstall,
    required this.showRunWith,
    required this.aboutSelected,
    required this.playSelected,
    required this.detailsSelected,
    required this.controlsSelected,
    required this.runWithSelected,
    required this.releasesSelected,
    required this.uninstallSelected,
    required this.focusedReleaseIndex,
    required this.releaseScroll,
    required this.releaseTileWidth,
    required this.releaseTileGap,
    required this.descriptionMaxLines,
    required this.aboutAnchor,
    required this.actionsAnchor,
    required this.releasesAnchor,
    required this.cacheAnchor,
    required this.onPrimaryAction,
    required this.onSelectRelease,
    required this.onUninstall,
    required this.onRunWith,
    required this.onOpenAbout,
    required this.onShowRecord,
    required this.onShowControls,
  });

  final ConsoleGame game;
  final ConsoleGameDetail? detail;
  final bool loading;
  final bool error;
  final bool accessRequired;
  final bool canAcquire;
  final bool preparingPlay;
  final String? selectedReleaseId;
  final Set<String> installedReleaseIds;
  final LocalInstall? selectedInstall;
  final bool showRunWith;
  final bool aboutSelected;
  final bool playSelected;
  final bool detailsSelected;
  final bool controlsSelected;
  final bool runWithSelected;
  final bool releasesSelected;
  final bool uninstallSelected;
  final int focusedReleaseIndex;
  final ScrollController releaseScroll;
  final double releaseTileWidth;
  final double releaseTileGap;
  final int descriptionMaxLines;

  /// Zone anchors owned by the parent — [Scrollable.ensureVisible] targets so
  /// the selection can pull each section into the vertical viewport.
  final GlobalKey aboutAnchor;
  final GlobalKey actionsAnchor;
  final GlobalKey releasesAnchor;
  final GlobalKey cacheAnchor;
  final VoidCallback onPrimaryAction;
  final ValueChanged<String> onSelectRelease;
  final VoidCallback onUninstall;
  final VoidCallback onRunWith;
  final VoidCallback onOpenAbout;
  final VoidCallback onShowRecord;
  final VoidCallback onShowControls;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    final platform = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    );
    final install = selectedInstall;
    final primaryLabel = preparingPlay
        ? 'Preparing'
        : accessRequired
        ? 'Check access'
        : install == null
        ? 'Install'
        : 'Play';
    final primaryIcon = accessRequired
        ? Icons.lock_outline
        : install == null
        ? Icons.download
        : Icons.play_arrow;

    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 640),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Text(
            game.title,
            maxLines: 3,
            overflow: TextOverflow.ellipsis,
            style: context.text.hero.copyWith(color: colors.textStrong),
          ),
          SizedBox(height: layout.md),
          Wrap(
            spacing: layout.sm,
            runSpacing: layout.sm,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: <Widget>[
              PlatformBadge(label: platform.label),
              if (game.releaseYear case final year?)
                MetaChip(label: year.toString(), icon: Icons.event),
              if ((detail?.genre ?? game.genre) case final genre?)
                MetaChip(label: genre),
              if ((detail?.rating ?? game.rating) case final rating?)
                MetaChip(label: rating.toStringAsFixed(1), icon: Icons.star),
              if (detail?.players case final players?)
                MetaChip(label: '$players player', icon: Icons.groups),
              // State chips follow the colour contract: green only for READY
              // (positive), neutral for everything else. "Access required" and
              // "Not yet playable" are facts awaiting an action, not failures,
              // so neither wears the warn colour reserved for real breakage.
              MetaChip(
                label: accessRequired
                    ? 'ACCESS REQUIRED'
                    : game.canLaunch
                    ? 'READY'
                    : 'NOT YET PLAYABLE',
                icon: accessRequired
                    ? Icons.lock_outline
                    : game.canLaunch
                    ? Icons.circle
                    : Icons.schedule,
                color: game.canLaunch && !accessRequired
                    ? colors.connected
                    : null,
              ),
            ],
          ),
          // The about block carries 10px of internal ring padding on each
          // side, so the neighbouring gaps are trimmed to keep the rhythm.
          SizedBox(height: layout.sm),
          if (loading)
            LinearProgressIndicator(minHeight: layout.hairlineStroke * 2),
          if (error)
            Text(
              'Details unavailable',
              style: context.text.metadata.copyWith(color: colors.textMuted),
            ),
          if (detail?.description case final description?)
            _AboutBlock(
              key: aboutAnchor,
              description: description,
              maxLines: descriptionMaxLines,
              selected: aboutSelected,
              onOpen: onOpenAbout,
            ),
          SizedBox(height: layout.xl),
          // A Wrap rather than a Row: the optional Run-with action would
          // overflow the 640px info column on one line, so it flows onto a
          // second line while the three resident actions keep their layout.
          Wrap(
            key: actionsAnchor,
            spacing: layout.md,
            runSpacing: layout.sm,
            children: <Widget>[
              _ActionSlot(
                selected: playSelected,
                child: SizedBox(
                  width: 210,
                  height: layout.settings.actionHeight,
                  child: ConsoleActionButton(
                    label: primaryLabel,
                    kind: ConsoleActionKind.primary,
                    icon: primaryIcon,
                    busy: preparingPlay,
                    onPressed:
                        game.canLaunch &&
                            selectedReleaseId != null &&
                            (install != null || canAcquire) &&
                            !preparingPlay
                        ? onPrimaryAction
                        : null,
                  ),
                ),
              ),
              _ActionSlot(
                selected: detailsSelected,
                child: SizedBox(
                  width: 132,
                  height: layout.settings.actionHeight,
                  child: ConsoleActionButton(
                    label: 'Details',
                    onPressed: onShowRecord,
                  ),
                ),
              ),
              if (game.canLaunch)
                _ActionSlot(
                  selected: controlsSelected,
                  child: SizedBox(
                    width: 146,
                    height: layout.settings.actionHeight,
                    child: ConsoleActionButton(
                      label: 'Shortcuts',
                      onPressed: onShowControls,
                    ),
                  ),
                ),
              if (showRunWith)
                _ActionSlot(
                  selected: runWithSelected,
                  child: SizedBox(
                    width: 146,
                    height: layout.settings.actionHeight,
                    child: ConsoleActionButton(
                      label: 'Run with',
                      onPressed: preparingPlay ? null : onRunWith,
                    ),
                  ),
                ),
            ],
          ),
          if (detail?.releases case final List<ConsoleRelease> releases
              when releases.isNotEmpty)
            _ReleaseStrip(
              key: releasesAnchor,
              releases: releases,
              selectedReleaseId: selectedReleaseId,
              installedReleaseIds: installedReleaseIds,
              focusedIndex: focusedReleaseIndex,
              controller: releaseScroll,
              tileWidth: releaseTileWidth,
              tileGap: releaseTileGap,
              onSelectRelease: onSelectRelease,
            ),
          if (install != null)
            _LocalCachePanel(
              key: cacheAnchor,
              install: install,
              selected: uninstallSelected,
              onUninstall: onUninstall,
            ),
        ],
      ),
    );
  }
}

/// Draws the teal selection ring + halo around an action button when it holds
/// the current selection. Padding is constant so selection never shifts layout.
/// The button stays mouse-tappable but cannot take focus — the parent owns it.
final class _ActionSlot extends StatelessWidget {
  const _ActionSlot({required this.selected, required this.child});

  final bool selected;
  final Widget child;

  @override
  Widget build(BuildContext context) => AnimatedContainer(
    duration: context.motion.resolve(context, context.motion.focus),
    curve: context.motion.standardCurve,
    padding: EdgeInsets.all(context.layout.xxs),
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(context.layout.controlRadius),
      border: Border.all(
        color: selected
            ? context.consoleColors.focusBorder
            : Colors.transparent,
        width: context.layout.focusStroke,
      ),
      boxShadow: selected
          ? context.elevation.focusGlow
          : context.elevation.none,
    ),
    child: ExcludeFocus(child: child),
  );
}

/// The clamped synopsis as a selectable zone. The selection ring occupies real
/// layout space (constant padding, transparent until selected) so it can never
/// be clipped by the scroll viewport; a [Transform.translate] pulls the block
/// left by the ring inset so the text stays flush with the title above.
/// Activating opens the full text in [_AboutDialog]. A `READ MORE` tag appears
/// only when the text actually clips, so short descriptions stay quiet.
final class _AboutBlock extends StatelessWidget {
  const _AboutBlock({
    required this.description,
    required this.maxLines,
    required this.selected,
    required this.onOpen,
    super.key,
  });

  final String description;
  final int maxLines;
  final bool selected;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    // Synopsis is prose: the reading face, never the metadata mono.
    final bodyStyle = context.text.body.copyWith(color: colors.textBody);
    final ringInset = layout.md;
    return Semantics(
      button: true,
      label: 'Read full description',
      child: GestureDetector(
        onTap: onOpen,
        child: Transform.translate(
          offset: Offset(-ringInset, 0),
          child: AnimatedContainer(
            duration: context.motion.resolve(context, context.motion.focus),
            curve: context.motion.standardCurve,
            padding: EdgeInsets.symmetric(
              horizontal: ringInset,
              vertical: layout.sm,
            ),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(layout.panelRadius),
              border: Border.all(
                color: selected ? colors.focusBorder : Colors.transparent,
                width: layout.focusStroke,
              ),
              color: selected ? colors.focusFill : Colors.transparent,
            ),
            child: LayoutBuilder(
              builder: (context, constraints) {
                final painter = TextPainter(
                  text: TextSpan(text: description, style: bodyStyle),
                  maxLines: maxLines,
                  textDirection: TextDirection.ltr,
                )..layout(maxWidth: constraints.maxWidth);
                final clipped = painter.didExceedMaxLines;
                painter.dispose();

                return Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: <Widget>[
                    Text(
                      description,
                      maxLines: maxLines,
                      overflow: TextOverflow.ellipsis,
                      style: bodyStyle,
                    ),
                    if (clipped) ...<Widget>[
                      SizedBox(height: layout.xs),
                      Text(
                        'READ MORE',
                        style: context.text.eyebrow.copyWith(
                          color: selected
                              ? colors.focusBorder
                              : colors.textFaint,
                        ),
                      ),
                    ],
                  ],
                );
              },
            ),
          ),
        ),
      ),
    );
  }
}

/// Full-synopsis overlay. Esc / B / Enter close it; ▲ ▼ scroll when the text
/// runs past the panel.
final class _AboutDialog extends StatefulWidget {
  const _AboutDialog({required this.title, required this.description});

  final String title;
  final String description;

  @override
  State<_AboutDialog> createState() => _AboutDialogState();
}

final class _AboutDialogState extends State<_AboutDialog> {
  final ScrollController _scroll = ScrollController();

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  void _scrollBy(TraversalDirection direction) {
    final delta = switch (direction) {
      TraversalDirection.up => -160.0,
      TraversalDirection.down => 160.0,
      TraversalDirection.left || TraversalDirection.right => 0.0,
    };
    if (delta == 0 || !_scroll.hasClients) {
      return;
    }
    _scroll.animateTo(
      (_scroll.offset + delta).clamp(0.0, _scroll.position.maxScrollExtent),
      duration: context.motion.resolve(context, context.motion.selection),
      curve: context.motion.spatialCurve,
    );
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Actions(
      actions: <Type, Action<Intent>>{
        DismissIntent: CallbackAction<DismissIntent>(
          onInvoke: (_) {
            Navigator.of(context).maybePop();
            return null;
          },
        ),
        ActivateIntent: CallbackAction<ActivateIntent>(
          onInvoke: (_) {
            Navigator.of(context).maybePop();
            return null;
          },
        ),
        DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
          onInvoke: (intent) {
            _scrollBy(intent.direction);
            return null;
          },
        ),
      },
      child: Focus(
        autofocus: true,
        child: Dialog(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 680, maxHeight: 520),
            child: Padding(
              padding: layout.panelPadding,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  _DialogHeading(eyebrow: 'ABOUT', title: widget.title),
                  SizedBox(height: layout.md),
                  Flexible(
                    child: SingleChildScrollView(
                      controller: _scroll,
                      child: Text(
                        widget.description,
                        style: context.text.body.copyWith(
                          color: context.consoleColors.textBody,
                        ),
                      ),
                    ),
                  ),
                  SizedBox(height: layout.lg),
                  const ConsoleHintBar(
                    hints: <ConsoleHint>[
                      ConsoleHint(
                        glyph: '↕',
                        gamepadGlyph: 'D-PAD',
                        label: 'Scroll',
                      ),
                      ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: 'B',
                        label: 'Close',
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The "Run with" picker overlay, mirroring [_AboutDialog]'s focused-dialog
/// idiom: ▲ ▼ move the selection, Enter returns the chosen profile to the
/// caller, Esc / B closes with nothing. The currently preferred candidate
/// carries a check so the pinned default is always visible.
final class _RunWithDialog extends StatefulWidget {
  const _RunWithDialog({
    required this.candidates,
    required this.preferredId,
    required this.platformLabel,
  });

  final List<RuntimeProfile> candidates;
  final RuntimeProfileId preferredId;
  final String platformLabel;

  @override
  State<_RunWithDialog> createState() => _RunWithDialogState();
}

final class _RunWithDialogState extends State<_RunWithDialog> {
  // The selection opens on the preferred candidate so Enter without movement
  // pins the current default rather than silently switching cores.
  late int _index = widget.candidates
      .indexWhere((candidate) => candidate.id == widget.preferredId)
      .clamp(0, widget.candidates.length - 1);

  void _moveBy(TraversalDirection direction) {
    final delta = switch (direction) {
      TraversalDirection.up => -1,
      TraversalDirection.down => 1,
      TraversalDirection.left || TraversalDirection.right => 0,
    };
    final next = (_index + delta).clamp(0, widget.candidates.length - 1);
    if (next != _index) {
      setState(() => _index = next);
    }
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Actions(
      actions: <Type, Action<Intent>>{
        DismissIntent: CallbackAction<DismissIntent>(
          onInvoke: (_) {
            Navigator.of(context).maybePop();
            return null;
          },
        ),
        ActivateIntent: CallbackAction<ActivateIntent>(
          onInvoke: (_) {
            Navigator.of(context).pop(widget.candidates[_index]);
            return null;
          },
        ),
        DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
          onInvoke: (intent) {
            _moveBy(intent.direction);
            return null;
          },
        ),
      },
      child: Focus(
        autofocus: true,
        child: Dialog(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 520),
            child: Padding(
              padding: layout.panelPadding,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  _DialogHeading(
                    eyebrow: 'RUN WITH',
                    title: '${widget.platformLabel} games',
                  ),
                  SizedBox(height: layout.md),
                  for (final (index, candidate) in widget.candidates.indexed)
                    _RunWithCandidateRow(
                      key: ValueKey<String>(
                        'runtime-candidate-${candidate.id.value}',
                      ),
                      displayName: candidate.displayName,
                      focused: index == _index,
                      preferred: candidate.id == widget.preferredId,
                      onTap: () => Navigator.of(context).pop(candidate),
                    ),
                  SizedBox(height: layout.sm),
                  const ConsoleHintBar(
                    hints: <ConsoleHint>[
                      ConsoleHint(
                        glyph: '↕',
                        gamepadGlyph: 'D-PAD',
                        label: 'Navigate',
                      ),
                      ConsoleHint(
                        glyph: ConsoleHintGlyphs.confirm,
                        gamepadGlyph: 'A',
                        label: 'Select',
                      ),
                      ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: 'B',
                        label: 'Close',
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Shared heading treatment for focused detail dialogs.
final class _DialogHeading extends StatelessWidget {
  const _DialogHeading({required this.eyebrow, required this.title});

  final String eyebrow;
  final String title;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: <Widget>[
      Text(
        eyebrow,
        style: context.text.eyebrow.copyWith(
          color: context.consoleColors.textFaint,
        ),
      ),
      SizedBox(height: context.layout.xs),
      Text(
        title,
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: context.text.sectionHeading.copyWith(
          color: context.consoleColors.textStrong,
        ),
      ),
    ],
  );
}

/// One runtime candidate in the picker: the accent ring follows the dialog's
/// selection, the check marks the currently preferred profile.
final class _RunWithCandidateRow extends StatelessWidget {
  const _RunWithCandidateRow({
    required this.displayName,
    required this.focused,
    required this.preferred,
    required this.onTap,
    super.key,
  });

  final String displayName;
  final bool focused;
  final bool preferred;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    return Semantics(
      button: true,
      selected: preferred,
      label: displayName,
      child: GestureDetector(
        onTap: onTap,
        child: AnimatedContainer(
          duration: context.motion.resolve(context, context.motion.focus),
          margin: EdgeInsets.only(bottom: layout.sm),
          padding: EdgeInsets.symmetric(
            horizontal: layout.md,
            vertical: layout.md,
          ),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(layout.controlRadius),
            border: Border.all(
              color: focused ? colors.focusBorder : colors.hairline,
              width: focused ? layout.focusStroke : layout.hairlineStroke,
            ),
            color: focused ? colors.focusFill : colors.panelSurface,
          ),
          child: Row(
            children: <Widget>[
              Expanded(
                child: Text(
                  displayName,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: context.text.sectionLabel.copyWith(
                    color: colors.textStrong,
                  ),
                ),
              ),
              if (preferred)
                Icon(
                  Icons.check_circle,
                  size: layout.iconSm,
                  color: colors.connected,
                ),
            ],
          ),
        ),
      ),
    );
  }
}

final class _ReleaseStrip extends StatelessWidget {
  const _ReleaseStrip({
    required this.releases,
    required this.selectedReleaseId,
    required this.installedReleaseIds,
    required this.focusedIndex,
    required this.controller,
    required this.tileWidth,
    required this.tileGap,
    required this.onSelectRelease,
    super.key,
  });

  final List<ConsoleRelease> releases;
  final String? selectedReleaseId;
  final Set<String> installedReleaseIds;
  final int focusedIndex;
  final ScrollController controller;
  final double tileWidth;
  final double tileGap;
  final ValueChanged<String> onSelectRelease;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Padding(
      padding: EdgeInsets.only(top: layout.lg),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Text(
            'RELEASES',
            style: context.text.eyebrow.copyWith(
              color: context.consoleColors.textFaint,
            ),
          ),
          SizedBox(height: layout.sm),
          SizedBox(
            height: layout.releaseRail.height,
            child: ListView.separated(
              controller: controller,
              scrollDirection: Axis.horizontal,
              itemCount: releases.length,
              separatorBuilder: (_, _) => SizedBox(width: tileGap),
              itemBuilder: (context, index) {
                final release = releases[index];
                return _ReleaseTile(
                  release: release,
                  width: tileWidth,
                  selected: release.id == selectedReleaseId,
                  focused: index == focusedIndex,
                  installed: installedReleaseIds.contains(release.id),
                  onTap: () => onSelectRelease(release.id),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

final class _ReleaseTile extends StatelessWidget {
  const _ReleaseTile({
    required this.release,
    required this.width,
    required this.selected,
    required this.focused,
    required this.installed,
    required this.onTap,
  });

  final ConsoleRelease release;
  final double width;
  final bool selected;
  final bool focused;
  final bool installed;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    final borderColor = focused
        ? colors.focusBorder
        : selected
        ? colors.selectionBorder
        : colors.hairline;
    return Semantics(
      button: true,
      selected: selected,
      label: release.name,
      child: GestureDetector(
        onTap: onTap,
        child: AnimatedContainer(
          duration: context.motion.resolve(context, context.motion.focus),
          width: width,
          padding: EdgeInsets.symmetric(
            horizontal: layout.md,
            vertical: layout.sm,
          ),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(layout.controlRadius),
            border: Border.all(
              color: borderColor,
              width: focused ? layout.focusStroke : layout.hairlineStroke,
            ),
            color: selected ? colors.selectionFill : colors.panelSurface,
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisAlignment: MainAxisAlignment.center,
            children: <Widget>[
              Row(
                children: <Widget>[
                  Expanded(
                    child: Text(
                      release.name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: context.text.sectionLabel.copyWith(
                        color: colors.textStrong,
                      ),
                    ),
                  ),
                  if (installed)
                    Icon(
                      Icons.check_circle,
                      size: layout.iconSm,
                      color: colors.connected,
                    ),
                ],
              ),
              SizedBox(height: layout.xs),
              Text(
                <String>[
                  release.revision ?? 'Original',
                  formatBytes(release.sizeBytes),
                ].join(' / '),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: context.text.metadata.copyWith(color: colors.textFaint),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

final class _LocalCachePanel extends StatelessWidget {
  const _LocalCachePanel({
    required this.install,
    required this.selected,
    required this.onUninstall,
    super.key,
  });

  final LocalInstall install;

  /// Whether the screen's selection currently rests on removal (the cache
  /// panel is a zone in the detail's explicit navigation model).
  final bool selected;
  final VoidCallback onUninstall;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    return Padding(
      padding: EdgeInsets.only(top: layout.md),
      child: DecoratedBox(
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(layout.panelRadius),
          border: Border.all(
            color: colors.panelBorder,
            width: layout.hairlineStroke,
          ),
          color: colors.panelSurface,
        ),
        child: Padding(
          padding: layout.panelPadding,
          child: Row(
            children: <Widget>[
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: <Widget>[
                    Wrap(
                      spacing: layout.md,
                      runSpacing: layout.sm,
                      children: <Widget>[
                        _CacheFact(
                          label: 'CACHE',
                          value: formatBytes(install.sizeBytes),
                        ),
                        _CacheFact(
                          label: 'FILES',
                          value: install.items.length.toString(),
                        ),
                        _CacheFact(
                          label: 'INSTALLED',
                          value: _dateLabel(install.installedAt),
                        ),
                      ],
                    ),
                    SizedBox(height: layout.xxs),
                    Text(
                      'Removes this profile’s copy. Shared files stay while '
                      'another profile uses them.',
                      style: context.text.metadata.copyWith(
                        color: colors.textMuted,
                      ),
                    ),
                  ],
                ),
              ),
              SizedBox(width: layout.sm),
              _ActionSlot(
                selected: selected,
                child: ConsoleActionButton(
                  label: 'Remove from profile',
                  icon: Icons.delete_outline,
                  destructive: true,
                  onPressed: onUninstall,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

final class _CacheFact extends StatelessWidget {
  const _CacheFact({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    mainAxisSize: MainAxisSize.min,
    children: <Widget>[
      Text(
        label,
        style: context.text.utilityLabel.copyWith(
          color: context.consoleColors.textFaint,
        ),
      ),
      SizedBox(height: context.layout.xxs),
      Text(
        value,
        style: context.text.metadata.copyWith(
          color: context.consoleColors.textMuted,
        ),
      ),
    ],
  );
}

String _dateLabel(DateTime value) {
  final local = value.toLocal();
  final month = local.month.toString().padLeft(2, '0');
  final day = local.day.toString().padLeft(2, '0');
  return '${local.year}-$month-$day';
}

/// The media strip — a horizontal second row of screenshot thumbnails beneath
/// the main content. Each tile highlights on selection and opens the full-screen
/// viewer on activate.
final class _MediaRow extends StatelessWidget {
  const _MediaRow({
    required this.media,
    required this.controller,
    required this.tileWidth,
    required this.tileGap,
    required this.selectedIndex,
    required this.onActivate,
    super.key,
  });

  final List<ConsoleMediaRef> media;
  final ScrollController controller;
  final double tileWidth;
  final double tileGap;
  final int selectedIndex;
  final ValueChanged<int> onActivate;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Padding(
      padding: EdgeInsets.only(top: layout.xl),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Text(
            'MEDIA',
            style: context.text.eyebrow.copyWith(
              color: context.consoleColors.textFaint,
            ),
          ),
          SizedBox(height: layout.sm),
          SizedBox(
            height: tileWidth * 9 / 16 + 6,
            child: ListView.separated(
              controller: controller,
              scrollDirection: Axis.horizontal,
              padding: EdgeInsets.symmetric(vertical: layout.xxs),
              itemCount: media.length,
              separatorBuilder: (_, _) => SizedBox(width: tileGap),
              itemBuilder: (context, index) => _MediaTile(
                media: media[index],
                width: tileWidth,
                selected: index == selectedIndex,
                onTap: () => onActivate(index),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

final class _MediaTile extends StatelessWidget {
  const _MediaTile({
    required this.media,
    required this.width,
    required this.selected,
    required this.onTap,
  });

  final ConsoleMediaRef media;
  final double width;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    return GestureDetector(
      onTap: onTap,
      child: AnimatedContainer(
        duration: context.motion.resolve(context, context.motion.focus),
        curve: context.motion.standardCurve,
        width: width,
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(layout.controlRadius),
          border: Border.all(
            color: selected ? colors.focusBorder : colors.hairline,
            width: selected ? layout.focusStroke : layout.hairlineStroke,
          ),
          boxShadow: selected
              ? context.elevation.focusGlow
              : context.elevation.none,
        ),
        child: ClipRRect(
          borderRadius: BorderRadius.circular(layout.controlRadius),
          child: AspectRatio(
            aspectRatio: 16 / 9,
            child: Image.network(
              media.url.toString(),
              fit: BoxFit.cover,
              // Decode at the tile's constant design width — a measured width
              // would shift with the selection border (1↔2px, animated), change
              // the cache key, and re-decode with a fade on every focus move.
              cacheWidth: decodeWidthForLogical(context, width),
              frameBuilder: coverFadeIn,
              errorBuilder: (context, error, stackTrace) => ColoredBox(
                color: colors.mediaBackdrop,
                child: Icon(Icons.image_not_supported, color: colors.textFaint),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

final class _BackdropImage extends StatelessWidget {
  const _BackdropImage({required this.url, required this.contain});

  final Uri url;
  final bool contain;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => Image(
      image: ResizeImage.resizeIfNeeded(
        decodeWidthFor(context, constraints),
        null,
        artworkImageProvider(url),
      ),
      fit: contain ? BoxFit.contain : BoxFit.cover,
      alignment: Alignment.centerRight,
      frameBuilder: coverFadeIn,
      errorBuilder: (context, error, stackTrace) => const SizedBox.shrink(),
    ),
  );
}

/// The archival key-art plate shown when no real backdrop image exists: a
/// platform-toned gradient with a large, faint title monogram. Intentional
/// placeholder — a dropped-in image replaces it.
final class _MonogramBackdrop extends StatelessWidget {
  const _MonogramBackdrop({required this.tone, required this.title});

  final CoverTone tone;
  final String title;

  @override
  Widget build(BuildContext context) {
    final rendering = context.artwork.coverRendering;
    return DecoratedBox(
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: <Color>[tone.glow, tone.base, context.colors.surface],
          stops: <double>[0, rendering.backdropGradientMiddleStop, 1],
        ),
      ),
      child: Align(
        alignment: rendering.backdropMonogramAlignment,
        child: FittedBox(
          child: Text(
            monogramFor(title),
            style: context.artwork.coverMonogramStyle.copyWith(
              color: tone.mono.withValues(
                alpha: rendering.backdropMonogramAlpha,
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Deterministic legibility pipeline over the key art: a teal-tinted inner
/// vignette, a bottom-up dark scrim, and a faint CRT scanline — tuned so a real
/// dropped-in image stays readable rather than muddy.
final class _BackdropShade extends StatelessWidget {
  const _BackdropShade({required this.tone});

  final CoverTone tone;

  @override
  Widget build(BuildContext context) {
    final surface = context.colors.surface;
    final rendering = context.artwork.coverRendering;
    return Stack(
      fit: StackFit.expand,
      children: <Widget>[
        DecoratedBox(
          decoration: BoxDecoration(
            gradient: RadialGradient(
              center: rendering.shadeCenter,
              radius: rendering.shadeRadius,
              colors: <Color>[
                tone.mono.withValues(alpha: rendering.shadeToneAlpha),
                Colors.transparent,
                surface.withValues(alpha: rendering.shadeOuterAlpha),
              ],
              stops: <double>[0, rendering.shadeRadialMiddleStop, 1],
            ),
          ),
        ),
        DecoratedBox(
          decoration: BoxDecoration(
            gradient: LinearGradient(
              begin: Alignment.topCenter,
              end: Alignment.bottomCenter,
              colors: <Color>[
                surface.withValues(alpha: rendering.shadeTopAlpha),
                surface.withValues(alpha: rendering.shadeMiddleAlpha),
                surface.withValues(alpha: rendering.shadeBottomAlpha),
              ],
              stops: rendering.shadeLinearStops,
            ),
          ),
        ),
      ],
    );
  }
}
