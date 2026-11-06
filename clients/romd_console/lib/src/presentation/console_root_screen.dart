import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';

import '../config/romd_environment.dart';
import '../data/consumer_api_client.dart';
import '../data/local_profiles/local_profile_repository.dart';
import '../data/local_profiles/profile_server_connection_repository.dart';
import '../data/profile_credential_bootstrap.dart';
import '../data/profile_session_coordinator.dart';
import '../data/reference_catalog.dart';
import '../data/refresh_token_store.dart';
import '../domain/consumer_account.dart';
import '../domain/local_profile.dart';
import '../domain/profile_server_connection.dart';
import '../domain/romd_server_instance_id.dart';
import 'catalog/catalog_operation_context.dart';
import 'console_home_screen.dart';
import 'entry_stage.dart';
import 'launcher/home_dock.dart';
import 'launcher/launcher_surface_screen.dart';
import 'profile_settings_screen.dart';
import 'reference_catalog_scope.dart';
import 'romd_login_screen.dart';
import 'romd_server_origin_screen.dart';
import 'theme/console_theme_context.dart';
import 'widgets/connection_status_indicator.dart';
import 'widgets/console_ambient_background.dart';
import 'widgets/console_clock.dart';
import 'widgets/controllers_screen.dart';
import 'widgets/emulators_screen.dart';
import 'widgets/profile_avatar.dart';
import 'widgets/profile_connection_ring.dart';
import 'widgets/storage_screen.dart';

typedef ConsumerApiClientFactory = ConsumerApiClient Function(Uri origin);
typedef PlayServicesFactory =
    PlayServices Function(
      Uri origin,
      ConsumerApiClient consumerApiClient,
      ProfileSessionCoordinator? authenticatedSession,
      InstallAuthorityContext? installAuthority,
    );
typedef ServerOriginPrompt =
    Future<Uri?> Function(BuildContext context, Uri initialOrigin);
typedef PlayersContextChanged =
    void Function(LocalProfile? profile, VoidCallback? onSwitchProfile);

final class ConsoleRootScreen extends StatefulWidget {
  const ConsoleRootScreen({
    required this.environment,
    required this.createConsumerApiClient,
    required this.localProfileRepository,
    required this.profileServerConnectionRepository,
    required this.refreshTokenStore,
    required this.createServerDiscoveryApiClient,
    required this.createPlayServices,
    required this.onExitApp,
    this.referenceCatalog,
    this.serverOriginPrompt,
    this.controllerInputProvider = const GamepadsControllerInputProvider(),
    this.onPlayServicesChanged,
    this.onEntryStartedChanged,
    this.onProfileActiveChanged,
    this.onPlayersContextChanged,
    super.key,
  });

  final ReferenceCatalogController? referenceCatalog;
  final RomdEnvironment environment;
  final ConsumerApiClientFactory createConsumerApiClient;
  final LocalProfileRepository localProfileRepository;
  final ProfileServerConnectionRepository profileServerConnectionRepository;
  final RefreshTokenStore refreshTokenStore;
  final ServerDiscoveryApiClientFactory createServerDiscoveryApiClient;
  final PlayServicesFactory createPlayServices;
  final VoidCallback onExitApp;
  final ServerOriginPrompt? serverOriginPrompt;
  final ControllerInputProvider controllerInputProvider;
  final ValueChanged<PlayServices?>? onPlayServicesChanged;
  final ValueChanged<bool>? onEntryStartedChanged;
  final ValueChanged<bool>? onProfileActiveChanged;
  final PlayersContextChanged? onPlayersContextChanged;

  @override
  State<ConsoleRootScreen> createState() => _ConsoleRootScreenState();
}

enum _RootView { home, login, settings }

final class _CatalogOperationChanges extends ChangeNotifier {
  void invalidate() => notifyListeners();
}

final class _ConsoleRootScreenState extends State<ConsoleRootScreen>
    with WidgetsBindingObserver {
  LocalProfile? _selectedProfile;
  ConsumerLoginSession? _session;
  ProfileSessionCoordinator? _sessionCoordinator;
  late final ProfileCredentialBootstrap _credentialBootstrap =
      ProfileCredentialBootstrap(
        refreshTokenStore: widget.refreshTokenStore,
        createServerDiscoveryApiClient: widget.createServerDiscoveryApiClient,
      );
  ConsumerApiClient? _activeConsumerApiClient;
  ConsumerApiClient? _ownedConsumerApiClient;
  PlayServices? _activePlayServices;
  Uri? _activeServerOrigin;
  InstallAuthorityContext? _activeInstallAuthority;
  ProfileServerConnectionState? _serverSelection;
  _ProfileOriginGeneration? _loginGeneration;
  int _operationEpoch = 0;
  final _CatalogOperationChanges _catalogOperationChanges =
      _CatalogOperationChanges();
  bool _connectInFlight = false;
  bool _servicesActive = false;
  bool _connecting = false;
  _RootView _view = _RootView.home;
  bool _started = false;
  ControllerSlotClaim? _pendingControllerStartClaim;
  HomeDockAction? _launcherReturnFocus;

  // The AnimatedSwitcher keys screens by identity; when the launcher leaves
  // the stage and returns within one cross-fade, both homes would otherwise
  // share a key. Bumping this generation whenever home stops being the built
  // screen keeps consecutive homes distinct.
  bool _builtHome = false;
  int _homeGeneration = 0;

  // The selected avatar stays above both AnimatedSwitcher children until it
  // lands on the home corner, mirroring the persistent clock transition.
  LocalProfile? _profileTransition;
  bool _profileTransitionAtHome = false;
  final ValueNotifier<HomeSurfaceContext?> _surfaceContext =
      ValueNotifier<HomeSurfaceContext?>(null);

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _operationEpoch++;
    _catalogOperationChanges.invalidate();
    _connectInFlight = false;
    _clearProfileServices(notify: false);
    _catalogOperationChanges.dispose();
    _surfaceContext.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) {
      if (_session case final session?)
        unawaited(widget.referenceCatalog?.refresh(session.token));
      final coordinator = _sessionCoordinator;
      if (coordinator != null) {
        unawaited(coordinator.refreshIfNeeded(SessionRefreshReason.resume));
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    _scheduleSurfaceContextSync();
    final layout = context.layout;
    final motion = context.motion;
    final launcherClock = _selectedProfile != null && _view == _RootView.home;
    final profileTransition = _profileTransition;
    final buildingHome =
        _selectedProfile != null &&
        _activeConsumerApiClient != null &&
        _activePlayServices != null &&
        (_view == _RootView.home ||
            (_view == _RootView.login && _loginGeneration == null));
    if (_builtHome && !buildingHome) {
      _homeGeneration++;
    }
    _builtHome = buildingHome;
    return Stack(
      children: <Widget>[
        // Match the native first-paint safeguard even if ambient artwork has
        // not resolved yet.
        Positioned.fill(
          child: ColoredBox(color: context.artwork.entryBackdrop),
        ),
        // Attract keeps a quiet current behind the identity. Profile selection
        // restores the established full wave-and-particle treatment; in-app
        // surfaces freeze and dim it.
        Positioned.fill(
          child: ConsoleAmbientBackground(
            dimmed: _selectedProfile != null,
            effectIntensity: _selectedProfile == null && !_started ? 0.25 : 1,
          ),
        ),
        Positioned.fill(
          child: AnimatedSwitcher(
            duration: motion.resolve(context, motion.screenTransition),
            child: _buildScreen(),
          ),
        ),
        if (_selectedProfile == null || _view == _RootView.home)
          Positioned.fill(
            child: SafeArea(
              child: LayoutBuilder(
                builder: (context, constraints) => Stack(
                  children: <Widget>[
                    AnimatedPositioned(
                      duration: motion.resolve(
                        context,
                        motion.chromeTransition,
                      ),
                      curve: motion.spatialCurve,
                      top: launcherClock
                          ? layout.screenChrome.navigationClockTop
                          : layout.screenChrome.clockInset,
                      right: launcherClock
                          ? layout.screenGutter
                          : layout.screenChrome.clockInset,
                      child: IgnorePointer(
                        child: ConsoleClock(
                          key: const ValueKey<String>('shared-console-clock'),
                          compact: launcherClock,
                        ),
                      ),
                    ),
                    if (profileTransition != null && launcherClock)
                      AnimatedPositioned(
                        duration: motion.resolve(
                          context,
                          motion.chromeTransition,
                        ),
                        curve: motion.spatialCurve,
                        left: _profileTransitionAtHome
                            ? layout.screenGutter +
                                  layout.focusRingStroke +
                                  layout.focusRingGap
                            : (constraints.maxWidth -
                                      layout.avatarXl *
                                          motion.interaction.focusedScale) /
                                  2,
                        top: _profileTransitionAtHome
                            ? layout.lg +
                                  layout.focusRingStroke +
                                  layout.focusRingGap
                            : (constraints.maxHeight -
                                      layout.avatarXl *
                                          motion.interaction.focusedScale) /
                                  2,
                        width: _profileTransitionAtHome
                            ? layout.avatarSm
                            : layout.avatarXl * motion.interaction.focusedScale,
                        height: _profileTransitionAtHome
                            ? layout.avatarSm
                            : layout.avatarXl * motion.interaction.focusedScale,
                        onEnd: _finishProfileTransition,
                        child: IgnorePointer(
                          child: ProfileConnectionRing(
                            status: _profileConnectionStatus(profileTransition),
                            indicatorKey: const ValueKey<String>(
                              'shared-profile-connection',
                            ),
                            child: SizedBox.expand(
                              key: const ValueKey<String>(
                                'shared-profile-avatar',
                              ),
                              child: FittedBox(
                                child: ProfileAvatar(
                                  avatarKey: profileTransition.avatarKey,
                                  accentColor: Color(
                                    profileTransition.accentColor,
                                  ),
                                  size: layout.avatarXl,
                                ),
                              ),
                            ),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
            ),
          ),
      ],
    );
  }

  // Each branch is wrapped in a _FocusOnMount so the incoming screen claims
  // keyboard focus after the AnimatedSwitcher cross-fade. The ValueKey lives on
  // the wrapper because AnimatedSwitcher diffs (and animates) on its direct
  // child's key. The entry stage manages its own focus and carries the key
  // directly.
  //
  // Local-first: a selected profile lands straight in the launcher (home) with
  // no session. Connecting to a ROMD server and opening settings are opt-in
  // views reached from within the launcher — never a gate in front of it.
  Widget _buildScreen() {
    final selectedProfile = _selectedProfile;

    if (selectedProfile == null) {
      // A single morphing stage spans attract + profile selection. `_started`
      // is only read here, on (re)mount, so backing out reopens the stage
      // directly in the selection state rather than at attract.
      return EntryStage(
        key: const ValueKey<String>('entry'),
        repository: widget.localProfileRepository,
        defaultRomdServerOrigin: widget.environment.consumerApiOrigin,
        initialStarted: _started,
        onStartedChanged: _handleStartedChanged,
        controllerInputProvider: widget.controllerInputProvider,
        onControllerStarted: _recordControllerStart,
        onSelected: _selectProfile,
      );
    }

    final consumerApiClient = _activeConsumerApiClient;
    final playServices = _activePlayServices;
    if (consumerApiClient == null || playServices == null) {
      return const _FocusOnMount(
        key: ValueKey<String>('profile-services'),
        child: Scaffold(
          backgroundColor: Colors.transparent,
          body: Center(child: CircularProgressIndicator()),
        ),
      );
    }

    switch (_view) {
      case _RootView.login:
        if (_loginGeneration == null) {
          return _buildHome(selectedProfile, consumerApiClient, playServices);
        }
        return _FocusOnMount(
          key: ValueKey<String>(
            'login_${selectedProfile.id}_${_loginGeneration!.serverInstanceId}',
          ),
          child: RomdLoginScreen(
            localProfile: selectedProfile,
            consumerApiClient: consumerApiClient,
            onAuthenticated: _handleAuthenticated,
            onBack: _closeLoginToHome,
          ),
        );
      case _RootView.settings:
        return _FocusOnMount(
          key: ValueKey<String>('settings_${selectedProfile.id}'),
          child: ProfileSettingsScreen(
            localProfile: selectedProfile,
            playActivity: playServices.playActivity,
            connected: _session != null,
            onConnect: _startConnect,
            onChangeServer: _changeServer,
            onRemoveServer: _removeServer,
            onSignOut: _signOut,
            onStorage: () => _openStorage(playServices),
            onControllers: () => _openControllers(playServices),
            onEmulation: () => _openEmulation(playServices),
            onSwitchProfile: _returnToProfileSelection,
            onBack: _closeToHome,
          ),
        );
      case _RootView.home:
        return _buildHome(selectedProfile, consumerApiClient, playServices);
    }
  }

  Widget _buildHome(
    LocalProfile selectedProfile,
    ConsumerApiClient consumerApiClient,
    PlayServices playServices,
  ) => _FocusOnMount(
    key: ValueKey<String>('home_${selectedProfile.id}_$_homeGeneration'),
    child: ConsoleHomeScreen(
      consumerApiClient: consumerApiClient,
      playServices: playServices,
      localProfile: selectedProfile,
      session: _session,
      connecting: _connecting,
      surfaceContext: _surfaceContext,
      onConnect: _startConnect,
      onOpenControllers: () => _openControllers(playServices),
      onOpenSettings: _openSettings,
      onSwitchProfile: _returnToProfileSelection,
      onExitApp: widget.onExitApp,
      initialDockFocus: _launcherReturnFocus,
      hideProfileAvatar: _profileTransition != null,
    ),
  );

  /// Republishes the live surface context after this frame so pushed
  /// surfaces (Library, Catalog) always read the current service graph and
  /// catalog operation. Published post-frame because notifying route subtrees
  /// mid-build is not allowed.
  void _scheduleSurfaceContextSync() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final profile = _selectedProfile;
      final consumerApiClient = _activeConsumerApiClient;
      final playServices = _activePlayServices;
      final next =
          profile == null || consumerApiClient == null || playServices == null
          ? null
          : HomeSurfaceContext(
              localProfileId: profile.id,
              consumerApiClient: consumerApiClient,
              playServices: playServices,
              catalogOperation: _catalogOperationContext(
                selectedProfile: profile,
                consumerApiClient: consumerApiClient,
                playServices: playServices,
              ),
            );
      final current = _surfaceContext.value;
      final unchanged = switch ((current, next)) {
        (null, null) => true,
        (final HomeSurfaceContext a, final HomeSurfaceContext b) =>
          a.hasSameContext(b),
        _ => false,
      };
      if (!unchanged) {
        _surfaceContext.value = next;
      }
    });
  }

  CatalogOperationContext? _catalogOperationContext({
    required LocalProfile selectedProfile,
    required ConsumerApiClient consumerApiClient,
    required PlayServices playServices,
  }) {
    final authority = _activeInstallAuthority;
    final authenticatedSession = _sessionCoordinator;
    if (authority == null ||
        authenticatedSession == null ||
        authenticatedSession.session == null ||
        authority.localProfileId != selectedProfile.id) {
      return null;
    }
    final operationEpoch = _operationEpoch;
    return CatalogOperationContext(
      authority: authority,
      authenticatedSession: authenticatedSession,
      epoch: operationEpoch,
      isAuthorityCurrent: () =>
          mounted &&
          _operationEpoch == operationEpoch &&
          _selectedProfile?.id == selectedProfile.id &&
          identical(_activeConsumerApiClient, consumerApiClient) &&
          identical(_activePlayServices, playServices) &&
          _sameAuthority(_activeInstallAuthority, authority),
      changes: _catalogOperationChanges,
    );
  }

  Future<void> _selectProfile(LocalProfile profile) async {
    final operationEpoch = _advanceOperationEpoch();
    await widget.localProfileRepository.markLastUsed(profile.id);
    if (!_isOperationCurrent(operationEpoch)) {
      return;
    }

    final selectionResult = await widget.profileServerConnectionRepository.read(
      profile.id,
    );
    if (!_isOperationCurrent(operationEpoch)) {
      return;
    }
    final selection = switch (selectionResult) {
      ProfileServerConnectionFound(:final state) => state,
      ProfileServerConnectionProfileNotFound() ||
      ProfileServerConnectionProjectionInconsistent() => null,
    };
    final selectedConnection = selection?.selectedConnection;
    final projectedProfile = _projectProfile(profile, selectedConnection);
    _activateProfileServices(_authorityFrom(profile, selection));
    await _applyPendingControllerStartClaim();
    if (!_isOperationCurrent(operationEpoch)) {
      return;
    }
    final animateProfileTransition =
        context.motion.resolve(context, context.motion.chromeTransition) !=
        Duration.zero;
    setState(() {
      _selectedProfile = projectedProfile;
      _serverSelection = selection;
      _session = null;
      _view = _RootView.home;
      _connecting = selectedConnection != null;
      _launcherReturnFocus = null;
      _profileTransition = animateProfileTransition ? projectedProfile : null;
      _profileTransitionAtHome = false;
    });
    if (animateProfileTransition) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted || _profileTransition?.id != projectedProfile.id) {
          return;
        }
        setState(() => _profileTransitionAtHome = true);
      });
    }
    widget.onProfileActiveChanged?.call(true);
    widget.onPlayersContextChanged?.call(
      projectedProfile,
      _returnToProfileSelection,
    );

    if (selection == null) {
      _showCredentialMessage(
        'Local profile server state is unavailable. Connect ROMD to repair it.',
      );
      setState(() => _connecting = false);
      return;
    }
    if (selectedConnection != null) {
      await _restoreSelectedSession(
        profile: projectedProfile,
        selection: selection,
        operationEpoch: operationEpoch,
      );
    }
  }

  void _recordControllerStart(ControllerSlotClaim claim) {
    _pendingControllerStartClaim = claim;
  }

  void _handleStartedChanged(bool started) {
    setState(() => _started = started);
    if (!started) {
      _pendingControllerStartClaim = null;
    }
    widget.onEntryStartedChanged?.call(started);
  }

  Future<ControllerSlotClaim?> _applyPendingControllerStartClaim() async {
    final claim = _pendingControllerStartClaim;
    final playServices = _activePlayServices;
    if (claim == null || playServices == null) {
      return null;
    }
    final activeClaim = await _enrichControllerStartClaim(
      claim,
      playServices.gamepadLister,
    );
    final claimed = await playServices.controllerSlotClaims
        .claimPlayerOneIfEmpty(activeClaim);
    _pendingControllerStartClaim = null;
    return claimed ? activeClaim : null;
  }

  Future<ControllerSlotClaim> _enrichControllerStartClaim(
    ControllerSlotClaim claim,
    GamepadLister gamepadLister,
  ) async {
    if (claim.hasExactIdentity || !claim.hasProviderId) {
      return claim;
    }
    try {
      final pads = await gamepadLister();
      for (final pad in pads) {
        if (claim.matchesProvider(pad)) {
          return ControllerSlotClaim.fromGamepad(pad);
        }
      }
    } on Object {
      // Keep the pending fallback claim.
    }
    return claim;
  }

  // Silent sign-in runs behind the local-first launcher. The selected
  // connection remains mounted while its locator is re-verified; pending state
  // never becomes authority or owns a service graph.
  Future<void> _restoreSelectedSession({
    required LocalProfile profile,
    required ProfileServerConnectionState selection,
    required int operationEpoch,
  }) async {
    final selected = selection.selectedConnection;
    if (selected == null) {
      return;
    }
    final generation = await _beginPendingBootstrap(
      profile: profile,
      origin: selected.origin,
      operationEpoch: operationEpoch,
      expectedServerInstanceId: selected.instanceId,
    );
    if (generation != null) {
      await _runCredentialBootstrap(generation, openLoginWhenMissing: false);
    }
  }

  Future<_ProfileOriginGeneration?> _beginPendingBootstrap({
    required LocalProfile profile,
    required Uri origin,
    required int operationEpoch,
    required RomdServerInstanceId? expectedServerInstanceId,
  }) async {
    final ProfileServerMutationResult result;
    try {
      result = await widget.profileServerConnectionRepository.setPendingLocator(
        localProfileId: profile.id,
        origin: origin,
      );
    } on Object {
      if (_isSelectedOperationCurrent(profile, operationEpoch)) {
        setState(() => _connecting = false);
        _showCredentialMessage(
          'ROMD server selection could not be saved. Try again.',
        );
      }
      return null;
    }
    if (!_isSelectedOperationCurrent(profile, operationEpoch)) {
      return null;
    }
    switch (result) {
      case ProfileServerMutationApplied(:final generation):
        final refreshed = await widget.profileServerConnectionRepository.read(
          profile.id,
        );
        if (!_isSelectedOperationCurrent(profile, operationEpoch) ||
            refreshed is! ProfileServerConnectionFound ||
            refreshed.state.generation != generation) {
          return null;
        }
        setState(() {
          _serverSelection = refreshed.state;
          _connecting = true;
        });
        return _ProfileOriginGeneration(
          profileId: profile.id,
          serverOrigin: origin,
          operationEpoch: operationEpoch,
          generation: generation,
          expectedServerInstanceId: expectedServerInstanceId,
        );
      case ProfileServerMutationProfileNotFound() ||
          ProfileServerMutationProjectionInconsistent():
        _showCredentialMessage(
          'ROMD server selection could not be saved. Try again.',
        );
        if (mounted) {
          setState(() => _connecting = false);
        }
        return null;
      case ProfileServerMutationStale():
        return null;
    }
  }

  Future<void> _runCredentialBootstrap(
    _ProfileOriginGeneration generation, {
    required bool openLoginWhenMissing,
  }) async {
    final bootstrapResult = await _credentialBootstrap.run(
      profileId: generation.profileId,
      serverOrigin: generation.serverOrigin,
      expectedServerInstanceId: generation.expectedServerInstanceId,
      isCurrent: () => _isCurrent(generation),
    );
    if (!_isCurrent(generation)) {
      return;
    }

    switch (bootstrapResult) {
      case ProfileCredentialBootstrapReady(:final serverInstanceId):
        final activation = await widget.profileServerConnectionRepository
            .activateDiscoveredServer(
              localProfileId: generation.profileId,
              expectedGeneration: generation.generation,
              expectedPendingOrigin: generation.serverOrigin,
              instanceId: serverInstanceId,
            );
        if (!_isCurrent(generation) ||
            activation is! ProfileServerMutationApplied) {
          return;
        }
        final selectedResult = await widget.profileServerConnectionRepository
            .read(generation.profileId);
        if (!_isOperationCurrent(generation.operationEpoch) ||
            selectedResult is! ProfileServerConnectionFound ||
            selectedResult.state.generation != activation.generation ||
            selectedResult.state.selectedConnection?.instanceId !=
                serverInstanceId) {
          return;
        }
        final selected = selectedResult.state.selectedConnection!;
        final currentProfile = await _reloadProfile(generation.profileId);
        if (currentProfile == null ||
            currentProfile.id != generation.profileId) {
          return;
        }
        final activatedGeneration = generation.afterActivation(
          activation.generation,
          serverInstanceId,
        );
        _session = null;
        _activateProfileServices(
          _authorityFrom(currentProfile, selectedResult.state),
        );
        setState(() {
          _serverSelection = selectedResult.state;
          _selectedProfile = _projectProfile(currentProfile, selected);
          _connecting = true;
        });
        await _restoreSession(
          generation: activatedGeneration,
          openLoginOnFailure: openLoginWhenMissing,
        );
      case ProfileCredentialServerReplaced():
        await _recordFailedPendingAttempt(generation);
        _finishBootstrapFailure(
          generation,
          'This address now identifies a different ROMD server. Choose Connect to continue.',
        );
      case ProfileCredentialLegacyCleanupFailed():
        await _recordFailedPendingAttempt(generation);
        _finishBootstrapFailure(
          generation,
          'Secure sign-in cleanup could not finish. Try Connect again.',
        );
      case ProfileCredentialDiscoveryFailed():
        await _recordFailedPendingAttempt(generation);
        _finishBootstrapFailure(
          generation,
          'ROMD server identity could not be verified. Check the server and try again.',
        );
      case ProfileCredentialBootstrapCancelled():
        return;
    }
  }

  Future<void> _recordFailedPendingAttempt(
    _ProfileOriginGeneration generation,
  ) async {
    await widget.profileServerConnectionRepository.recordPendingAttempt(
      localProfileId: generation.profileId,
      expectedGeneration: generation.generation,
      expectedOrigin: generation.serverOrigin,
    );
  }

  Future<void> _restoreSession({
    required _ProfileOriginGeneration generation,
    bool openLoginOnFailure = false,
  }) async {
    final coordinator = _sessionCoordinator;
    final result = coordinator == null
        ? const SessionRefreshFailure(SessionRefreshFailureKind.staleAuthority)
        : await coordinator.restore();
    if (!_isCurrent(generation)) {
      return;
    }

    switch (result) {
      case SessionRefreshSuccess():
        setState(() => _connecting = false);
      case SessionRefreshFailure(:final kind):
        if (kind == SessionRefreshFailureKind.credentialStorage) {
          _finishBootstrapFailure(
            generation,
            'Secure sign-in could not be updated. Try Connect again.',
          );
        } else if (openLoginOnFailure &&
            _isCurrent(generation) &&
            (kind == SessionRefreshFailureKind.invalidGrant ||
                kind == SessionRefreshFailureKind.missingCredential)) {
          setState(() {
            _connecting = false;
            _loginGeneration = generation;
            _view = _RootView.login;
          });
        } else {
          _finishConnecting(generation);
        }
    }
  }

  void _finishBootstrapFailure(
    _ProfileOriginGeneration generation,
    String message,
  ) {
    if (!_isCurrent(generation)) {
      return;
    }
    setState(() {
      _connecting = false;
      _loginGeneration = null;
      _view = _RootView.home;
    });
    _showCredentialMessage(message);
  }

  void _finishConnecting(_ProfileOriginGeneration generation) {
    if (_isCurrent(generation)) {
      setState(() => _connecting = false);
    }
  }

  bool _isCurrent(_ProfileOriginGeneration generation) {
    final profile = _selectedProfile;
    return mounted &&
        profile != null &&
        profile.id == generation.profileId &&
        _operationEpoch == generation.operationEpoch &&
        _serverSelection?.generation == generation.generation;
  }

  // Opt-in connect from the launcher or settings. The persisted pending
  // locator is verified before it can replace the selected connection.
  Future<void> _startConnect() async {
    if (_connectInFlight) {
      return;
    }
    _connectInFlight = true;
    final operationEpoch = _advanceOperationEpoch(resetConnect: false);
    final profile = _selectedProfile;
    if (profile == null) {
      _finishConnectOperation(operationEpoch);
      return;
    }
    try {
      var origin =
          _serverSelection?.pendingLocator?.origin ??
          _serverSelection?.selectedConnection?.origin;
      if (origin == null) {
        origin = await _promptForOrigin(
          profile,
          operationEpoch,
          widget.environment.consumerApiOrigin,
        );
      }
      if (origin == null ||
          !_isSelectedOperationCurrent(profile, operationEpoch)) {
        return;
      }
      setState(() {
        _session = null;
        _connecting = true;
        _loginGeneration = null;
      });
      final generation = await _beginPendingBootstrap(
        profile: profile,
        origin: origin,
        operationEpoch: operationEpoch,
        expectedServerInstanceId: null,
      );
      if (generation != null) {
        await _runCredentialBootstrap(generation, openLoginWhenMissing: true);
      }
    } finally {
      _finishConnectOperation(operationEpoch);
    }
  }

  Future<Uri?> _promptForOrigin(
    LocalProfile profile,
    int operationEpoch,
    Uri initialOrigin,
  ) async {
    Uri? serverOrigin;
    try {
      serverOrigin = await _showServerOriginPrompt(initialOrigin);
    } on Object {
      if (_isSelectedOperationCurrent(profile, operationEpoch)) {
        _showCredentialMessage(
          'ROMD server settings could not be opened. Try again.',
        );
      }
      return null;
    }
    if (serverOrigin == null ||
        !_isSelectedOperationCurrent(profile, operationEpoch)) {
      return null;
    }
    return serverOrigin;
  }

  Future<void> _handleAuthenticated(ConsumerLoginSession session) async {
    final profile = _selectedProfile;
    final loginGeneration = _loginGeneration;
    if (profile == null ||
        loginGeneration == null ||
        !_isCurrent(loginGeneration) ||
        loginGeneration.serverInstanceId == null) {
      return;
    }

    final repository = widget.localProfileRepository;
    if (repository is! ServerBoundLocalProfileRepository) {
      _finishBootstrapFailure(
        loginGeneration,
        'ROMD account binding is unavailable. Try Connect again.',
      );
      return;
    }
    final boundRepository = repository as ServerBoundLocalProfileRepository;

    final coordinator = _sessionCoordinator;
    final accepted = coordinator == null
        ? const SessionRefreshFailure(SessionRefreshFailureKind.staleAuthority)
        : await coordinator.acceptAuthenticatedSession(session);
    if (accepted case SessionRefreshFailure(:final kind)) {
      if (_isCurrent(loginGeneration) &&
          kind == SessionRefreshFailureKind.credentialStorage) {
        _finishBootstrapFailure(
          loginGeneration,
          'Secure sign-in could not be saved. Try Connect again.',
        );
      }
      return;
    }
    if (!_isCurrent(loginGeneration) ||
        !identical(_loginGeneration, loginGeneration)) {
      return;
    }

    final LocalProfile linkedProfile;
    try {
      final timestamp = DateTime.now();
      linkedProfile = await boundRepository.linkRomdAccountToSelectedServer(
        localProfileId: profile.id,
        expectedGeneration: loginGeneration.generation,
        serverInstanceId: loginGeneration.serverInstanceId!,
        accountLink: RomdAccountLink(
          romdUserId: session.account.id,
          username: session.account.username,
          email: session.account.email,
          linkedAt: profile.romdAccountLink?.linkedAt ?? timestamp,
          lastLoginAt: timestamp,
        ),
      );
    } on Object {
      final cleanup = await coordinator!.signOut();
      if (_isCurrent(loginGeneration)) {
        _finishBootstrapFailure(
          loginGeneration,
          cleanup is SessionRefreshFailure &&
                  cleanup.kind == SessionRefreshFailureKind.credentialStorage
              ? 'Secure sign-in cleanup could not finish. Try Connect again.'
              : 'ROMD account could not be linked. Try Connect again.',
        );
      }
      return;
    }
    if (!_isCurrent(loginGeneration) ||
        !identical(_loginGeneration, loginGeneration)) {
      return;
    }

    setState(() {
      _selectedProfile = linkedProfile;
      _connecting = false;
      _loginGeneration = null;
      _view = _RootView.home;
    });
  }

  Future<void> _signOut() async {
    final operationEpoch = _advanceOperationEpoch();
    final profile = _selectedProfile;
    if (profile == null) {
      return;
    }

    final coordinator = _sessionCoordinator;
    _loginGeneration = null;
    setState(() {
      _session = null;
      _connecting = false;
      _view = _RootView.home;
    });
    if (coordinator != null) {
      final result = await coordinator.signOut();
      if (result case SessionRefreshFailure(
        kind: SessionRefreshFailureKind.remoteSignOut,
      )) {
        if (_isSelectedOperationCurrent(profile, operationEpoch)) {
          _showCredentialMessage(
            'Signed out locally. The server could not be reached to revoke this session; ask an administrator to revoke it.',
          );
        }
      }
      if (result case SessionRefreshFailure(
        kind: SessionRefreshFailureKind.credentialStorage,
      )) {
        if (_isSelectedOperationCurrent(profile, operationEpoch)) {
          _showCredentialMessage(
            'Stored sign-in could not be cleared. Try again.',
          );
        }
        return;
      }
    }
    final localProfile = await widget.localProfileRepository.unlinkRomdAccount(
      localProfileId: profile.id,
    );
    if (!_isSelectedOperationCurrent(profile, operationEpoch)) {
      return;
    }

    setState(() {
      _selectedProfile = localProfile;
    });
  }

  Future<void> _changeServer() async {
    final profile = _selectedProfile;
    if (profile == null) {
      return;
    }

    final promptEpoch = _operationEpoch;
    final current = _serverSelection?.selectedConnection?.origin;
    final serverOrigin = await _promptForOrigin(
      profile,
      promptEpoch,
      current ?? widget.environment.consumerApiOrigin,
    );
    if (!_isSelectedOperationCurrent(profile, promptEpoch)) {
      return;
    }
    if (serverOrigin == null || serverOrigin == current) {
      return;
    }
    final operationEpoch = _advanceOperationEpoch();
    final generation = await _beginPendingBootstrap(
      profile: profile,
      origin: serverOrigin,
      operationEpoch: operationEpoch,
      expectedServerInstanceId: null,
    );
    if (generation != null) {
      await _runCredentialBootstrap(generation, openLoginWhenMissing: true);
    }
  }

  Future<void> _removeServer() async {
    final operationEpoch = _advanceOperationEpoch();
    final profile = _selectedProfile;
    final selection = _serverSelection;
    if (profile == null ||
        selection == null ||
        selection.selectedConnection == null) {
      return;
    }

    final ProfileServerMutationResult result;
    try {
      result = await widget.profileServerConnectionRepository.clearSelection(
        localProfileId: profile.id,
        expectedGeneration: selection.generation,
      );
    } on Object {
      if (_isSelectedOperationCurrent(profile, operationEpoch)) {
        _showCredentialMessage('ROMD server could not be removed. Try again.');
      }
      return;
    }
    if (!_isSelectedOperationCurrent(profile, operationEpoch)) {
      return;
    }
    if (result is! ProfileServerMutationApplied) {
      _showCredentialMessage('ROMD server could not be removed. Try again.');
      return;
    }
    final refreshed = await widget.profileServerConnectionRepository.read(
      profile.id,
    );
    if (!_isSelectedOperationCurrent(profile, operationEpoch) ||
        refreshed is! ProfileServerConnectionFound ||
        refreshed.state.generation != result.generation) {
      return;
    }
    _session = null;
    _loginGeneration = null;
    _activateProfileServices(null);
    setState(() {
      _selectedProfile = _projectProfile(profile, null);
      _serverSelection = refreshed.state;
      _connecting = false;
      _view = _RootView.home;
    });
  }

  void _openControllers(PlayServices playServices) {
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => ConsoleAmbientBackground(
          dimmed: true,
          child: ControllersScreen(
            preferences: playServices.controllerPreferences,
            controllerInputProvider: widget.controllerInputProvider,
            hardwareMappings: playServices.controllerHardwareMappings,
          ),
        ),
      ),
    );
  }

  void _openStorage(PlayServices playServices) {
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => ConsoleAmbientBackground(
          dimmed: true,
          child: StorageScreen(
            installService: playServices.install,
            onBack: () => Navigator.of(context).maybePop(),
          ),
        ),
      ),
    );
  }

  void _openEmulation(PlayServices playServices) {
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => ConsoleAmbientBackground(
          dimmed: true,
          child: EmulatorsScreen(
            launcher: playServices.emulatorSettingsLauncher,
          ),
        ),
      ),
    );
  }

  void _openSettings() => setState(() {
    _launcherReturnFocus = HomeDockAction.settings;
    _view = _RootView.settings;
  });

  void _closeToHome() => setState(() => _view = _RootView.home);

  void _closeLoginToHome() {
    _advanceOperationEpoch();
    _loginGeneration = null;
    setState(() => _view = _RootView.home);
  }

  void _returnToProfileSelection() {
    widget.onProfileActiveChanged?.call(false);
    widget.onPlayersContextChanged?.call(null, null);
    _advanceOperationEpoch();
    _loginGeneration = null;
    setState(() {
      _selectedProfile = null;
      _serverSelection = null;
      _session = null;
      _connecting = false;
      _view = _RootView.home;
      _launcherReturnFocus = null;
      _profileTransition = null;
      _profileTransitionAtHome = false;
    });
    _clearProfileServices();
  }

  void _finishProfileTransition() {
    if (!_profileTransitionAtHome || _profileTransition == null) {
      return;
    }
    setState(() {
      _profileTransition = null;
      _profileTransitionAtHome = false;
    });
  }

  ConnectionStatus _profileConnectionStatus(LocalProfile profile) {
    if (_connecting) return ConnectionStatus.connecting;
    if (_session != null) return ConnectionStatus.connected;
    return profile.hasServer
        ? ConnectionStatus.offline
        : ConnectionStatus.local;
  }

  int _advanceOperationEpoch({bool resetConnect = true}) {
    _operationEpoch++;
    _catalogOperationChanges.invalidate();
    if (resetConnect) {
      _connectInFlight = false;
    }
    return _operationEpoch;
  }

  bool _isOperationCurrent(int operationEpoch) =>
      mounted && _operationEpoch == operationEpoch;

  void _finishConnectOperation(int operationEpoch) {
    if (_operationEpoch == operationEpoch) {
      _connectInFlight = false;
    }
  }

  bool _isSelectedOperationCurrent(LocalProfile profile, int operationEpoch) {
    final selected = _selectedProfile;
    return mounted &&
        selected != null &&
        selected.id == profile.id &&
        _operationEpoch == operationEpoch;
  }

  Future<Uri?> _showServerOriginPrompt(Uri initialOrigin) {
    final prompt = widget.serverOriginPrompt;
    return prompt == null
        ? RomdServerOriginScreen.show(context, initialOrigin: initialOrigin)
        : prompt(context, initialOrigin);
  }

  void _showCredentialMessage(String message) {
    final messenger = ScaffoldMessenger.of(context);
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  LocalProfile _projectProfile(
    LocalProfile profile,
    ProfileServerConnection? selectedConnection,
  ) => LocalProfile(
    id: profile.id,
    displayName: profile.displayName,
    avatarKey: profile.avatarKey,
    accentColor: profile.accentColor,
    romdServerOrigin: selectedConnection?.origin,
    entryMode: profile.entryMode,
    createdAt: profile.createdAt,
    updatedAt: profile.updatedAt,
    lastUsedAt: profile.lastUsedAt,
    romdAccountLink: selectedConnection == null
        ? null
        : profile.romdAccountLink,
  );

  Future<LocalProfile?> _reloadProfile(String profileId) async {
    try {
      for (final profile
          in await widget.localProfileRepository.listProfiles()) {
        if (profile.id == profileId) {
          return profile;
        }
      }
    } on Object {
      return null;
    }
    return null;
  }

  // Services are keyed by the complete immutable profile/server authority.
  // An origin match alone is never enough to reuse a graph.
  void _activateProfileServices(InstallAuthorityContext? authority) {
    final serverOrigin = authority?.connection.origin;
    if (_servicesActive &&
        _activeServerOrigin == serverOrigin &&
        _sameAuthority(_activeInstallAuthority, authority) &&
        _activeConsumerApiClient != null &&
        _activePlayServices != null) {
      return;
    }

    _clearProfileServices(notify: false);

    final clientOrigin = serverOrigin ?? widget.environment.consumerApiOrigin;
    final consumerApiClient = widget.createConsumerApiClient(clientOrigin);
    _activeConsumerApiClient = consumerApiClient;
    widget.referenceCatalog?.activate(
      authority?.connection.instanceId.value,
      consumerApiClient is ReferenceCatalogApiClient
          ? consumerApiClient as ReferenceCatalogApiClient
          : null,
    );
    _activeServerOrigin = serverOrigin;
    _activeInstallAuthority = authority;
    final authenticatedSession = authority == null
        ? null
        : ProfileSessionCoordinator(
            authority: authority,
            consumerApiClient: consumerApiClient,
            refreshTokenStore: widget.refreshTokenStore,
            isAuthorityCurrent: () =>
                mounted &&
                _servicesActive &&
                _sameAuthority(_activeInstallAuthority, authority),
            telemetry: kDebugMode
                ? (event) => debugPrint(
                    '[auth] refresh reason=${event.reason.name} '
                    'outcome=${event.outcome} '
                    'authorityGeneration=${event.authorityGeneration} '
                    'latencyMs=${event.elapsed.inMilliseconds}',
                  )
                : null,
          );
    _sessionCoordinator = authenticatedSession;
    authenticatedSession?.addListener(_handleSessionCoordinatorChanged);
    _activePlayServices = widget.createPlayServices(
      clientOrigin,
      consumerApiClient,
      authenticatedSession,
      authority,
    );
    _servicesActive = true;
    widget.onPlayServicesChanged?.call(_activePlayServices);
    _ownedConsumerApiClient = consumerApiClient;
  }

  void _handleSessionCoordinatorChanged() {
    if (!mounted) return;
    final next = _sessionCoordinator?.session;
    _catalogOperationChanges.invalidate();
    setState(() => _session = next);
    if (next != null) unawaited(widget.referenceCatalog?.refresh(next.token));
  }

  InstallAuthorityContext? _authorityFrom(
    LocalProfile profile,
    ProfileServerConnectionState? selection,
  ) {
    final connection = selection?.selectedConnection;
    if (selection == null ||
        connection == null ||
        profile.id != selection.localProfileId) {
      return null;
    }
    return InstallAuthorityContext(
      localProfileId: profile.id,
      connection: connection,
      generation: selection.generation,
    );
  }

  bool _sameAuthority(
    InstallAuthorityContext? left,
    InstallAuthorityContext? right,
  ) => switch ((left, right)) {
    (null, null) => true,
    (final InstallAuthorityContext a, final InstallAuthorityContext b) =>
      a.hasSameAuthority(b),
    _ => false,
  };

  void _clearProfileServices({bool notify = true}) {
    _sessionCoordinator
      ?..removeListener(_handleSessionCoordinatorChanged)
      ..dispose();
    _sessionCoordinator = null;
    _activePlayServices?.close();
    _ownedConsumerApiClient?.close();
    _ownedConsumerApiClient = null;
    _activeConsumerApiClient = null;
    _activePlayServices = null;
    _activeServerOrigin = null;
    _activeInstallAuthority = null;
    _servicesActive = false;
    widget.referenceCatalog?.activate(null, null);
    if (notify) {
      widget.onPlayServicesChanged?.call(null);
    }
  }
}

/// Claims keyboard focus for the wrapped screen once it mounts.
///
/// During an [AnimatedSwitcher] cross-fade the outgoing screen stays mounted
/// and keeps primary focus, which swallows the incoming screen's `autofocus`
/// and leaves nothing focused once the transition ends — so keyboard input is
/// dead until the user clicks. Requesting focus on this scope after the first
/// frame restores keyboard control immediately, in both transition directions.
final class _FocusOnMount extends StatefulWidget {
  const _FocusOnMount({required this.child, super.key});

  final Widget child;

  @override
  State<_FocusOnMount> createState() => _FocusOnMountState();
}

final class _FocusOnMountState extends State<_FocusOnMount> {
  final FocusScopeNode _scope = FocusScopeNode();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        _scope.requestFocus();
      }
    });
  }

  @override
  void dispose() {
    _scope.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      FocusScope(node: _scope, child: widget.child);
}

/// One in-process profile+origin selection epoch. Gate 2 will persist the
/// selected instance and monotonic generation; until then every app restart
/// intentionally starts over with cleanup and anonymous discovery.
final class _ProfileOriginGeneration {
  _ProfileOriginGeneration({
    required this.profileId,
    required this.serverOrigin,
    required this.operationEpoch,
    required this.generation,
    required this.expectedServerInstanceId,
    this.serverInstanceId,
  });

  final String profileId;
  final Uri serverOrigin;
  final int operationEpoch;
  final int generation;
  final RomdServerInstanceId? expectedServerInstanceId;
  final RomdServerInstanceId? serverInstanceId;

  _ProfileOriginGeneration afterActivation(
    int activatedGeneration,
    RomdServerInstanceId activatedServerInstanceId,
  ) => _ProfileOriginGeneration(
    profileId: profileId,
    serverOrigin: serverOrigin,
    operationEpoch: operationEpoch,
    generation: activatedGeneration,
    expectedServerInstanceId: activatedServerInstanceId,
    serverInstanceId: activatedServerInstanceId,
  );
}
