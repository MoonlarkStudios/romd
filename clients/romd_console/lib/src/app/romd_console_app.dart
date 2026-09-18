import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:gamepads/gamepads.dart';
import 'package:path/path.dart' as path;
import 'package:romd_console/src/play/content/data/content_file_store.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/data/drift_profile_local_library_repository.dart';
import 'package:romd_console/src/play/content/data/install_mutation_serializer.dart';
import 'package:romd_console/src/play/content/data/profile_game_cleanup_service.dart';
import 'package:romd_console/src/play/content/data/romd_install_service.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_binding_rule_repository.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_hardware_mapping_repository.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_preferences_repository.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_profile_mapping_repository.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_controller_input_provider.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_gamepad_enumerator.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_native_dependency.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping_resolver.dart';
import 'package:romd_console/src/play/controllers/domain/controller_preferences.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/dolphin_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/dolphin_executable.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/dolphin_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/managed_dolphin_settings_launcher.dart';
import 'package:romd_console/src/play/emulator/data/adapters/duckstation/duckstation_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/duckstation/duckstation_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/pcsx2/pcsx2_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/pcsx2/pcsx2_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/retroarch/retroarch_adapter.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/retroarch_dependency_resolver.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/romd_bios_catalog.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/runtime_bios_resolver.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/standalone_runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/emulator/data/emulator_launch_provider.dart';
import 'package:romd_console/src/play/emulator/data/registry/default_runtime_manager.dart';
import 'package:romd_console/src/play/emulator/data/registry/drift_runtime_override_rule_repository.dart';
import 'package:romd_console/src/play/emulator/data/registry/managed_standalone_runtime_provisioner.dart';
import 'package:romd_console/src/play/emulator/data/registry/retroarch_provisioner.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_executable_integrity.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_unpacker.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';
import 'package:romd_console/src/play/session/data/drift_play_activity_repository.dart';
import 'package:romd_console/src/play/session/data/drift_profile_play_history_repository.dart';
import 'package:romd_console/src/play/session/data/launch_authorization_store.dart';
import 'package:romd_console/src/play/session/data/online_first_launch_authorizer.dart';
import 'package:romd_console/src/play/session/data/play_activity_sync_service.dart';
import 'package:romd_console/src/play/session/data/romd_play_coordinator.dart';
import 'package:romd_console/src/play/session/data/window_manager_controller.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';
import 'package:window_manager/window_manager.dart';

import '../config/romd_environment.dart';
import '../data/consumer_api_client.dart';
import '../data/local_profiles/app_database.dart';
import '../data/local_profiles/local_profile_repository.dart';
import '../data/local_profiles/profile_server_connection_repository.dart';
import '../data/profile_credential_bootstrap.dart';
import '../data/profile_session_coordinator.dart';
import '../data/refresh_token_store.dart';
import '../data/release_access_api_client.dart';
import '../data/release_manifest_api_client.dart';
import '../data/server_discovery_api_client.dart';
import '../domain/local_profile.dart';
import '../input/console_input_mode.dart';
import '../input/gamepad_navigator.dart';
import '../presentation/console_root_screen.dart';
import '../presentation/glyph_family_scope.dart';
import '../presentation/reference_catalog_scope.dart';
import '../presentation/theme/romd_skins.dart';
import '../presentation/widgets/platform_logo_resolver.dart';
import '../presentation/widgets/players_status_shell.dart';
import 'console_canvas_scale.dart';

Stream<NormalizedGamepadEvent> _noControllerEvents() =>
    const Stream<NormalizedGamepadEvent>.empty();

typedef RomdConsolePlayServicesFactory =
    PlayServices Function(
      Uri origin,
      ConsumerApiClient consumerApiClient,
      ProfileSessionCoordinator? authenticatedSession,
      InstallAuthorityContext? installAuthority,
      ActiveLaunchSession activeLaunchSession,
      SessionControllerSlotClaims controllerSlotClaims,
      GamepadLister gamepadLister,
    );

final class RomdConsoleApp extends StatefulWidget {
  const RomdConsoleApp({
    required this.environment,
    this.consumerApiClientFactory,
    this.localProfileRepository,
    this.profileServerConnectionRepository,
    this.refreshTokenStore,
    this.serverDiscoveryApiClientFactory,
    this.playServicesFactory,
    this.serverOriginPrompt,
    this.controllerSlotClaims,
    this.gamepadLister,
    this.contentBaseDir,
    this.enableGamepad = true,
    this.controllerInputProvider,
    this.exitApplication,
    this.theme,
    this.darkTheme,
    this.themeMode = ThemeMode.dark,
    super.key,
  });

  final RomdEnvironment environment;
  final ConsumerApiClientFactory? consumerApiClientFactory;
  final LocalProfileRepository? localProfileRepository;
  final ProfileServerConnectionRepository? profileServerConnectionRepository;
  final RefreshTokenStore? refreshTokenStore;
  final ServerDiscoveryApiClientFactory? serverDiscoveryApiClientFactory;

  /// Origin-aware test seam. Production builds one graph per fresh API client.
  final RomdConsolePlayServicesFactory? playServicesFactory;
  final ServerOriginPrompt? serverOriginPrompt;
  final SessionControllerSlotClaims? controllerSlotClaims;
  final GamepadLister? gamepadLister;

  /// Base directory for installed content / runtimes (resolved in `main` via
  /// `getApplicationSupportDirectory`). Required only when
  /// [playServicesFactory] is not injected.
  final Directory? contentBaseDir;

  /// Whether to bridge a physical gamepad into the navigation. Disabled in
  /// widget tests, which have no gamepad backend.
  final bool enableGamepad;
  final ControllerInputProvider? controllerInputProvider;

  /// Testable desktop boundary. Production closes the managed app window.
  final AsyncCallback? exitApplication;

  /// Runtime skin injection. Production remains dark-only until the canonical
  /// light skin is designed; tests and previews can supply both themes now.
  final ThemeData? theme;
  final ThemeData? darkTheme;
  final ThemeMode themeMode;

  @override
  State<RomdConsoleApp> createState() => _RomdConsoleAppState();
}

final class _RomdConsoleAppState extends State<RomdConsoleApp> {
  static const Map<String, String> _coreOverrides = <String, String>{
    'nes': String.fromEnvironment('ROMD_RETROARCH_NES_CORE'),
    'snes': String.fromEnvironment('ROMD_RETROARCH_SNES_CORE'),
    'n64': String.fromEnvironment('ROMD_RETROARCH_N64_CORE'),
    'gb': String.fromEnvironment('ROMD_RETROARCH_GB_CORE'),
    'gbc': String.fromEnvironment('ROMD_RETROARCH_GBC_CORE'),
    'gba': String.fromEnvironment('ROMD_RETROARCH_GBA_CORE'),
    'genesis': String.fromEnvironment('ROMD_RETROARCH_GENESIS_CORE'),
  };

  AppDatabase? _database;
  ProfileGameCleanupService? _profileGameCleanupService;
  HttpDownloadClient? _ownedDownloadClient;
  final InstallMutationSerializer _installMutationSerializer =
      InstallMutationSerializer();
  final ActiveLaunchSession _activeLaunchSession = ActiveLaunchSession();
  final ValueNotifier<ConsoleInputMode> _inputMode =
      ValueNotifier<ConsoleInputMode>(ConsoleInputMode.keyboard);
  final ValueNotifier<GlyphFamily> _glyphFamily = ValueNotifier<GlyphFamily>(
    GlyphFamily.generic,
  );
  GamepadNavigator? _gamepad;
  final ControllerNavigationGate _controllerNavigationGate =
      ControllerNavigationGate();
  PlayServices? _activePlayServices;
  late final SessionControllerSlotClaims _controllerSlotClaims =
      widget.controllerSlotClaims ?? SessionControllerSlotClaims();
  final GlobalKey<NavigatorState> _navigatorKey = GlobalKey<NavigatorState>();
  bool _profileActive = false;
  LocalProfile? _activeProfile;
  VoidCallback? _onSwitchProfile;
  late final ControllerInputProvider _controllerInputProvider =
      widget.controllerInputProvider ?? _createControllerInputProvider();
  late final GamepadLister _gamepadLister =
      widget.gamepadLister ?? _controllerInputProvider.listGamepads;

  @override
  void initState() {
    super.initState();
    HardwareKeyboard.instance.addHandler(_handleKeyEvent);
    if (widget.enableGamepad) {
      _gamepad = GamepadNavigator(
        onInputModeChanged: _setInputMode,
        controllerInputProvider: _controllerInputProvider,
        controllerNavigationGate: _controllerNavigationGate,
      );
    }
    if (widget.playServicesFactory == null &&
        widget.contentBaseDir != null &&
        widget.localProfileRepository == null &&
        widget.profileServerConnectionRepository == null) {
      unawaited(_cleanupOrphanedContent());
    }
  }

  late final LocalProfileRepository _localProfileRepository =
      widget.localProfileRepository ?? _createLocalProfileRepository();
  late final ProfileServerConnectionRepository
  _profileServerConnectionRepository =
      widget.profileServerConnectionRepository ??
      switch (_localProfileRepository) {
        final ProfileServerConnectionRepository repository => repository,
        _ => DriftProfileServerConnectionRepository(
          database: _ensureDatabase(),
        ),
      };
  late final RefreshTokenStore _refreshTokenStore = SerializedRefreshTokenStore(
    widget.refreshTokenStore ?? SecureRefreshTokenStore(),
  );

  @override
  Widget build(BuildContext context) => MaterialApp(
    navigatorKey: _navigatorKey,
    title: 'Ottercade',
    debugShowCheckedModeBanner: false,
    theme: widget.theme ?? RomdSkins.baselineLight(),
    darkTheme: widget.darkTheme ?? widget.theme ?? RomdSkins.baselineDark(),
    themeMode: widget.themeMode,
    // Normalize oversized windows (fullscreen TVs) back onto the 1280×720
    // design canvas so the 10-ft type ramp holds at any output resolution.
    // Controller UI scopes sit here — above the Navigator — so pushed routes
    // (Controllers, tester, controller help) render gamepad hints consistently.
    builder: (context, child) => ReferenceCatalogScope(
      controller: _referenceCatalog,
      child: AnimatedBuilder(
        animation: _referenceCatalog,
        builder: (context, _) => PlatformLogoResolverScope(
          resolver: _referenceCatalog,
          revision: _referenceCatalog.catalog?.revision,
          child: Listener(
            onPointerDown: (_) => _setInputMode(ConsoleInputMode.keyboard),
            onPointerSignal: (_) => _setInputMode(ConsoleInputMode.keyboard),
            child: ConsoleInputModeScope(
              notifier: _inputMode,
              child: ConsoleCanvasScale(
                child: GlyphFamilyScope(
                  notifier: _glyphFamily,
                  child: PlayersStatusShell(
                    navigatorKey: _navigatorKey,
                    controllerInputProvider: _controllerInputProvider,
                    slotClaims: _controllerSlotClaims,
                    gamepadLister: _gamepadLister,
                    visible: _profileActive,
                    activeProfile: _activeProfile,
                    localProfileRepository: _localProfileRepository,
                    onSwitchProfile: _onSwitchProfile,
                    activeLaunchSession: _activeLaunchSession,
                    navigationGate: _controllerNavigationGate,
                    child: child!,
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    ),
    home: ConsoleRootScreen(
      referenceCatalog: _referenceCatalog,
      environment: widget.environment,
      createConsumerApiClient:
          widget.consumerApiClientFactory ?? _createConsumerApiClient,
      localProfileRepository: _localProfileRepository,
      profileServerConnectionRepository: _profileServerConnectionRepository,
      refreshTokenStore: _refreshTokenStore,
      createServerDiscoveryApiClient:
          widget.serverDiscoveryApiClientFactory ??
          _createServerDiscoveryApiClient,
      createPlayServices:
          (origin, consumerApiClient, authenticatedSession, installAuthority) =>
              (widget.playServicesFactory ?? _createPlayServices)(
                origin,
                consumerApiClient,
                authenticatedSession,
                installAuthority,
                _activeLaunchSession,
                _controllerSlotClaims,
                _gamepadLister,
              ),
      serverOriginPrompt: widget.serverOriginPrompt,
      controllerInputProvider: _controllerInputProvider,
      onEntryStartedChanged: _setEntryStarted,
      onProfileActiveChanged: _setProfileActive,
      onPlayersContextChanged: _setPlayersContext,
      onPlayServicesChanged: _setPlayServices,
      onExitApp: _exitApplication,
    ),
  );

  late final _referenceCatalog = ReferenceCatalogController(
    widget.contentBaseDir == null
        ? null
        : Directory(
            path.join(widget.contentBaseDir!.path, 'reference-catalog'),
          ),
  );

  @override
  void dispose() {
    HardwareKeyboard.instance.removeHandler(_handleKeyEvent);
    _gamepad?.dispose();
    _referenceCatalog.dispose();
    _inputMode.dispose();
    _glyphFamily.dispose();
    _ownedDownloadClient?.close();
    _database?.close();
    super.dispose();
  }

  bool _handleKeyEvent(KeyEvent event) {
    if (event is KeyDownEvent || event is KeyRepeatEvent) {
      _setInputMode(ConsoleInputMode.keyboard);
    }
    return false;
  }

  void _setInputMode(ConsoleInputMode mode) {
    if (_inputMode.value != mode) {
      _inputMode.value = mode;
    }
  }

  ServerDiscoveryApiClient _createServerDiscoveryApiClient(Uri origin) =>
      HttpServerDiscoveryApiClient(consumerApiOrigin: origin);

  void _setEntryStarted(bool started) {
    if (!started) {
      // Attract is the boundary of a couch session. Durable controller
      // preferences remain intact; only ephemeral player seating is reset.
      unawaited(_controllerSlotClaims.clearAll());
    }
  }

  void _setProfileActive(bool active) {
    if (_profileActive != active && mounted) {
      setState(() => _profileActive = active);
    }
  }

  void _setPlayersContext(
    LocalProfile? activeProfile,
    VoidCallback? onSwitchProfile,
  ) {
    if ((_activeProfile != activeProfile ||
            _onSwitchProfile != onSwitchProfile) &&
        mounted) {
      setState(() {
        _activeProfile = activeProfile;
        _onSwitchProfile = onSwitchProfile;
      });
    }
  }

  void _setPlayServices(PlayServices? services) {
    if (!identical(_activePlayServices, services) && mounted) {
      setState(() => _activePlayServices = services);
    }
  }

  void _exitApplication() {
    unawaited((widget.exitApplication ?? windowManager.close)());
  }

  /// Seeds the glyph family from the persisted template choice; best-effort —
  /// glyphs stay generic if the store is unavailable. Later changes flow
  /// through the scope's notifier from the Controllers screen.
  Future<void> _seedGlyphFamily(
    ControllerPreferencesRepository preferences,
  ) async {
    try {
      final stored = await preferences.load();
      final templateId = stored.templateId;
      final template = templateId == null
          ? null
          : BuiltinControllerTemplates.byId(templateId);
      if (mounted && template != null) {
        _glyphFamily.value = template.glyphFamily;
      }
    } on Object {
      // Keep the generic family.
    }
  }

  AppDatabase _ensureDatabase() => _database ??= AppDatabase.open();

  ProfileGameCleanupService _ensureProfileGameCleanupService() =>
      _profileGameCleanupService ??= ProfileGameCleanupService(
        database: _ensureDatabase(),
        baseDir: widget.contentBaseDir!,
        serializer: _installMutationSerializer,
        refreshTokenStore: _refreshTokenStore,
      );

  Future<void> _cleanupOrphanedContent() async {
    try {
      await _ensureProfileGameCleanupService().cleanupOrphans();
    } on Object catch (error) {
      if (kDebugMode) debugPrint('[content] orphan cleanup deferred: $error');
    }
  }

  LocalProfileRepository _createLocalProfileRepository() =>
      DriftLocalProfileRepository(database: _ensureDatabase());

  ConsumerApiClient _createConsumerApiClient(Uri origin) =>
      RomdConsumerApiClient(consumerApiOrigin: origin);

  SdlNativeDependencyProvisioner _createSdlProvisioner() {
    final baseDir = widget.contentBaseDir!;
    final downloadClient = _ownedDownloadClient ??= HttpDownloadClient();
    const sdlEnvironment = SystemSdlNativeEnvironment();
    return SdlNativeDependencyProvisioner(
      artifactResolver: const SdlNativeCatalog().sdl3,
      installer: const MacosSdlNativeInstaller(
        commandRunner: SystemSdlNativeCommandRunner(),
      ),
      downloadClient: downloadClient,
      nativeRoot: Directory(path.join(baseDir.path, 'native')),
      operatingSystem: sdlEnvironment.operatingSystem,
      architecture: sdlEnvironment.architecture,
    );
  }

  ControllerInputProvider _createControllerInputProvider() {
    if (!widget.enableGamepad) {
      return CallbackControllerInputProvider(
        listGamepads: _listRootGamepads,
        events: _noControllerEvents,
      );
    }
    return ProvisionedSdlControllerInputProvider(
      ensureSdl3: _createSdlProvisioner().ensureSdl3,
      apiFactory: FfiSdlGamepadApi.open,
      fallback: const GamepadsControllerInputProvider(),
    );
  }

  Future<List<ConnectedGamepad>> _listRootGamepads() {
    final gamepadLister = widget.gamepadLister;
    return gamepadLister == null ? listConnectedGamepads() : gamepadLister();
  }

  PlayServices _createPlayServices(
    Uri origin,
    ConsumerApiClient consumerApiClient,
    ProfileSessionCoordinator? authenticatedSession,
    InstallAuthorityContext? installAuthority,
    ActiveLaunchSession activeLaunchSession,
    SessionControllerSlotClaims controllerSlotClaims,
    GamepadLister gamepadLister,
  ) {
    final baseDir = widget.contentBaseDir!;
    final database = _ensureDatabase();
    final downloadClient = _ownedDownloadClient ??= HttpDownloadClient();
    final fileStore = ContentFileStore(
      baseDir: baseDir,
      serverInstanceId: installAuthority?.connection.instanceId.value,
    );

    final runtimesRoot = Directory(path.join(baseDir.path, 'runtimes'));
    final probeEnv = SystemRuntimeProbeEnvironment(
      managedRuntimesRoot: runtimesRoot.path,
    );
    final runtimeManager = DefaultRuntimeManager(environment: probeEnv);
    final biosCatalog = RomdBiosCatalog(
      apiClient: consumerApiClient,
      authenticatedSession: authenticatedSession,
    );
    final biosResolver = RuntimeBiosResolver(
      biosCatalog: biosCatalog,
      downloadClient: downloadClient,
    );
    final duckStationUserDirectory = DuckStationUserDirectory(
      root: Directory(path.join(runtimesRoot.path, 'duckstation', 'user')),
      operatingSystem: probeEnv.operatingSystem,
    );
    final pcsx2UserDirectory = Pcsx2UserDirectory(
      root: Directory(path.join(runtimesRoot.path, 'pcsx2', 'user')),
      operatingSystem: probeEnv.operatingSystem,
    );
    final dolphinUserDirectory = DolphinUserDirectory(
      root: Directory(path.join(runtimesRoot.path, 'dolphin', 'user')),
    );
    final releaseAccessApiClient = HttpReleaseAccessApiClient(
      consumerApiOrigin: origin,
    );
    final launchReleaseAccessApiClient = HttpReleaseAccessApiClient(
      consumerApiOrigin: origin,
    );
    final releaseManifestApiClient = HttpReleaseManifestApiClient(
      consumerApiOrigin: origin,
    );
    final installs = DriftLocalInstallRepository(
      database: database,
      serverInstanceId: installAuthority?.connection.instanceId,
      fileStore: fileStore,
    );
    final playHistory = DriftProfilePlayHistoryRepository(
      database: database,
      installs: installs,
      authority: installAuthority,
    );
    final playActivity = installAuthority == null
        ? null
        : DriftPlayActivityRepository(
            database: database,
            authority: installAuthority,
          );
    final PlayActivityApiClient? playActivityApiClient =
        consumerApiClient is PlayActivityApiClient
        ? consumerApiClient as PlayActivityApiClient
        : null;
    final playActivitySync =
        playActivity == null ||
            authenticatedSession == null ||
            playActivityApiClient == null
        ? null
        : PlayActivitySyncService(
            repository: playActivity,
            apiClient: playActivityApiClient,
            authenticatedSession: authenticatedSession,
          );
    if (playActivitySync != null) unawaited(playActivitySync.syncPending());
    final localLibrary = DriftProfileLocalLibraryRepository(
      database: database,
      installs: installs,
      authority: installAuthority,
    );
    final install = RomdInstallService(
      releaseAccessApiClient: releaseAccessApiClient,
      releaseManifestApiClient: releaseManifestApiClient,
      database: database,
      authority: installAuthority,
      authenticatedSession: authenticatedSession,
      downloadClient: downloadClient,
      verifier: const ContentVerifier(),
      fileStore: fileStore,
      installs: installs,
      profileLocalLibrary: localLibrary,
      mutationSerializer: _installMutationSerializer,
      cleanupService: _ensureProfileGameCleanupService(),
    );

    final runtimeUnpacker = switch (probeEnv.operatingSystem) {
      'linux' => const LinuxRuntimeUnpacker(
        processRunner: SystemProcessRunner(),
      ),
      _ => const MacosRuntimeUnpacker(processRunner: SystemProcessRunner()),
    };
    final provisioner = RetroArchProvisioner(
      downloadClient: downloadClient,
      unpacker: runtimeUnpacker,
      catalog: const RuntimeCatalog(),
      runtimesRoot: runtimesRoot,
      operatingSystem: probeEnv.operatingSystem,
      architecture: probeEnv.architecture,
    );
    final duckStationProvisioner = ManagedStandaloneRuntimeProvisioner(
      runtimeDescriptor: duckStationRuntimeDescriptor,
      displayName: 'DuckStation',
      artifactResolver: const RuntimeCatalog().duckStation,
      downloadClient: downloadClient,
      unpacker: runtimeUnpacker,
      runtimesRoot: runtimesRoot,
      operatingSystem: probeEnv.operatingSystem,
      architecture: probeEnv.architecture,
    );
    final pcsx2Provisioner = ManagedStandaloneRuntimeProvisioner(
      runtimeDescriptor: pcsx2RuntimeDescriptor,
      displayName: 'PCSX2',
      artifactResolver: const RuntimeCatalog().pcsx2,
      downloadClient: downloadClient,
      unpacker: runtimeUnpacker,
      runtimesRoot: runtimesRoot,
      operatingSystem: probeEnv.operatingSystem,
      architecture: probeEnv.architecture,
    );
    final dolphinProvisioner = ManagedStandaloneRuntimeProvisioner(
      runtimeDescriptor: dolphinRuntimeDescriptor,
      displayName: 'Dolphin',
      artifactResolver: const RuntimeCatalog().dolphin,
      downloadClient: downloadClient,
      unpacker: runtimeUnpacker,
      runtimesRoot: runtimesRoot,
      operatingSystem: probeEnv.operatingSystem,
      architecture: probeEnv.architecture,
      installedRuntimeValidator: (runtimePath) => verifyRuntimeExecutableSha256(
        runtimePath: runtimePath,
        processExecutableResolver: dolphinProcessExecutable,
        expectedSha256: RuntimeCatalog.dolphinExecutableSha256,
      ),
    );

    final retroArchDependencyResolver = RetroArchDependencyResolver(
      provisioner: provisioner,
      runtimeManager: runtimeManager,
      biosCatalog: biosCatalog,
      biosRoot: Directory(path.join(baseDir.path, 'bios')),
      downloadClient: downloadClient,
      coreOverridesByPlatform: <String, String>{
        for (final MapEntry(:key, :value) in _coreOverrides.entries)
          if (value.isNotEmpty) key: value,
      },
    );
    final duckStationDependencyResolver = StandaloneRuntimeDependencyResolver(
      runtimeDescriptor: duckStationRuntimeDescriptor,
      runtimeManager: runtimeManager,
      biosResolver: biosResolver,
      biosDirectory: (_) =>
          Directory(duckStationUserDirectory.biosDirectoryPath),
      provisioner: duckStationProvisioner,
    );
    final pcsx2DependencyResolver = StandaloneRuntimeDependencyResolver(
      runtimeDescriptor: pcsx2RuntimeDescriptor,
      runtimeManager: runtimeManager,
      biosResolver: biosResolver,
      biosDirectory: (_) => Directory(pcsx2UserDirectory.biosDirectoryPath),
      provisioner: pcsx2Provisioner,
    );
    final dolphinDependencyResolver = StandaloneRuntimeDependencyResolver(
      runtimeDescriptor: dolphinRuntimeDescriptor,
      runtimeManager: runtimeManager,
      provisioner: dolphinProvisioner,
    );

    // One rule store shared by the coordinator's resolver and the picker's
    // read/write seams, so a persisted pick is visible on the next resolve.
    final runtimeRules = DriftRuntimeOverrideRuleRepository(database: database);
    final runtimeResolver = LocalRuntimeResolver(rules: runtimeRules);
    final controllerPreferences = DriftControllerPreferencesRepository(
      database: database,
    );
    final controllerProfileMappings = DriftControllerProfileMappingRepository(
      database: database,
    );
    final controllerHardwareMappings = DriftControllerHardwareMappingRepository(
      database: database,
    );
    final controllerMappingResolver = ControllerMappingResolver(
      rules: DriftControllerBindingRuleRepository(database: database),
      profileRules: controllerProfileMappings,
    );
    // Seating is session state (console model): first-use/start order at boot,
    // reassigned via the Change Order ceremony, gone on app restart.
    _seedGlyphFamily(controllerPreferences);

    final emulatorLaunchProvider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        BuiltinRuntimeProfiles.retroArchAdapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: retroArchDependencyResolver,
          adapterFactory: () => const RetroArchAdapter(),
        ),
        BuiltinRuntimeProfiles.duckStationAdapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: duckStationDependencyResolver,
          adapterFactory: () =>
              DuckStationAdapter(userDirectory: duckStationUserDirectory),
        ),
        BuiltinRuntimeProfiles.pcsx2AdapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: pcsx2DependencyResolver,
          adapterFactory: () => Pcsx2Adapter(userDirectory: pcsx2UserDirectory),
        ),
        BuiltinRuntimeProfiles.dolphinAdapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: dolphinDependencyResolver,
          adapterFactory: () =>
              DolphinAdapter(userDirectory: dolphinUserDirectory),
        ),
      },
      controllerMappings: controllerMappingResolver,
      controllerHardwareMappings: controllerHardwareMappings,
      controllerPreferences: controllerPreferences,
      controllerSlotClaims: controllerSlotClaims,
      gamepadLister: gamepadLister,
    );

    const windowController = WindowManagerController();
    final coordinator = RomdPlayCoordinator(
      resolver: runtimeResolver,
      launchProviders: [emulatorLaunchProvider],
      playHistory: playHistory,
      authorizer: OnlineFirstLaunchAuthorizer(
        authority: installAuthority,
        store: DriftLaunchAuthorizationStore(
          database: database,
          installs: installs,
        ),
        accessApiClient: launchReleaseAccessApiClient,
        authenticatedSession: authenticatedSession,
      ),
      installedTargetResolver: install,
      mutationSerializer: _installMutationSerializer,
      runtimeRules: runtimeRules,
      playActivity: playActivity,
      playActivitySync: playActivitySync,
      activeLaunchSession: activeLaunchSession,
      windowController: windowController,
    );
    final emulatorSettingsLauncher = ManagedDolphinSettingsLauncher(
      provisioner: dolphinProvisioner,
      userDirectory: dolphinUserDirectory,
      activeLaunchSession: activeLaunchSession,
      windowController: windowController,
    );

    return PlayServices(
      install: install,
      localLibrary: localLibrary,
      coordinator: coordinator,
      playHistory: playHistory,
      playActivity: playActivity,
      runtimeResolver: runtimeResolver,
      runtimeRules: runtimeRules,
      activeLaunchSession: activeLaunchSession,
      controllerPreferences: controllerPreferences,
      controllerSlotClaims: controllerSlotClaims,
      controllerHardwareMappings: controllerHardwareMappings,
      controllerMappingResolver: controllerMappingResolver,
      emulatorSettingsLauncher: emulatorSettingsLauncher,
      gamepadLister: gamepadLister,
      onClose: () {
        releaseAccessApiClient.close();
        releaseManifestApiClient.close();
      },
    );
  }
}
