import 'dart:async';

import 'package:flutter/material.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/presentation/catalog/all_games/discover_all_games_boundary.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/catalog/discover_catalog_session.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/discover_shell.dart';
import 'package:romd_console/src/presentation/catalog/featured/discover_featured_boundary.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';

/// Discover route composition.
///
/// Remote authority and snapshot lifecycle stay in [DiscoverCatalogSession].
/// Feature boundaries own their renderer, traversal, and restoration state.
final class CatalogLauncherDestination extends StatefulWidget {
  const CatalogLauncherDestination({
    required this.consumerApiClient,
    required this.playServices,
    required this.operation,
    required this.active,
    required this.onBack,
    required this.onConnect,
    required this.onOpenGame,
    required this.onSearch,
    this.autofocusNavigation = false,
    super.key,
  });

  final ConsumerApiClient consumerApiClient;
  final PlayServices playServices;
  final CatalogOperationContext? operation;
  final bool active;
  final VoidCallback onBack;
  final VoidCallback onConnect;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;
  final ValueChanged<DiscoverCatalogSession> onSearch;
  final bool autofocusNavigation;

  @override
  State<CatalogLauncherDestination> createState() =>
      CatalogLauncherDestinationState();
}

final class CatalogLauncherDestinationState
    extends State<CatalogLauncherDestination> {
  late final DiscoverCatalogSession _session;
  final DiscoverShellController _controller = DiscoverShellController();
  final DiscoverFocusHandoff _focusHandoff = DiscoverFocusHandoff();
  final GlobalKey<DiscoverAllGamesBoundaryState> _allGamesKey =
      GlobalKey<DiscoverAllGamesBoundaryState>();
  Color? _featuredAmbientTone;

  @override
  void initState() {
    super.initState();
    _session = DiscoverCatalogSession(
      consumerApiClient: widget.consumerApiClient,
      installService: widget.playServices.install,
      operation: widget.operation,
    )..addListener(_onSessionChanged);
    _controller.addListener(_onSectionChanged);
    if (widget.active) unawaited(_ensureActiveSectionLoaded());
  }

  @override
  void didUpdateWidget(covariant CatalogLauncherDestination oldWidget) {
    super.didUpdateWidget(oldWidget);
    final operationChanged = switch ((oldWidget.operation, widget.operation)) {
      (null, null) => false,
      (final CatalogOperationContext old, final CatalogOperationContext next) =>
        !old.hasSameOperation(next),
      _ => true,
    };
    if (operationChanged) {
      _featuredAmbientTone = null;
    }
    _session.updateDependencies(
      consumerApiClient: widget.consumerApiClient,
      installService: widget.playServices.install,
      operation: widget.operation,
    );
    if (widget.active) unawaited(_ensureActiveSectionLoaded());
  }

  @override
  void dispose() {
    _controller
      ..removeListener(_onSectionChanged)
      ..dispose();
    _session
      ..removeListener(_onSessionChanged)
      ..dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final operation = widget.operation;
    if (_session.privacyCleared ||
        (operation != null && !operation.isRequestCurrent)) {
      return const CatalogPrivacyShield();
    }
    final navigation = DiscoverFeatureNavigation(
      onSelectSection: _selectFromFeatureNavigation,
      onSearch: () => widget.onSearch(_session),
      onConnect: widget.onConnect,
    );
    final featuredActive =
        widget.active && _controller.section == DiscoverSection.featured;
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: Stack(
        children: <Widget>[
          Positioned.fill(
            child: ConsoleAmbientBackground(
              dimmed: true,
              tone: featuredActive ? _featuredAmbientTone : null,
            ),
          ),
          SafeArea(
            child: DiscoverShell(
              controller: _controller,
              focusHandoff: _focusHandoff,
              onSearch: () => widget.onSearch(_session),
              onBack: widget.onBack,
              onFilters: _controller.section == DiscoverSection.allGames
                  ? () => _allGamesKey.currentState?.focusFilters()
                  : null,
              slots: DiscoverFeatureSlots(
                featured: (_) => DiscoverFeaturedBoundary(
                  focusHandoff: _focusHandoff,
                  input: DiscoverFeaturedInput(
                    snapshot: _session.featured,
                    focus: _session.focusFor(DiscoverSection.featured),
                    installedReleaseIds: _session.installedReleaseIds,
                    operationAvailable: operation != null,
                    active:
                        widget.active &&
                        _controller.section == DiscoverSection.featured,
                    autofocusNavigation: widget.autofocusNavigation,
                  ),
                  callbacks: DiscoverFeaturedCallbacks(
                    navigation: navigation,
                    onOpenGame: widget.onOpenGame,
                    onRetry: () =>
                        unawaited(_session.loadFeatured(force: true)),
                    detailFor: _session.detailFor,
                    loadDetail: _session.loadDetail,
                    onSelectedTone: _onSelectedFeaturedTone,
                    onHeroChromeProgress:
                        _controller.updateFeaturedChromeProgress,
                    onRememberFocus:
                        ({required itemId, required scrollOffset}) {
                          _session.rememberFocus(
                            DiscoverSection.featured,
                            itemId: itemId,
                            scrollOffset: scrollOffset,
                          );
                        },
                  ),
                ),
                allGames: (_) => DiscoverAllGamesBoundary(
                  key: _allGamesKey,
                  focusHandoff: _focusHandoff,
                  input: DiscoverAllGamesInput(
                    snapshot: _session.allGames,
                    focus: _session.focusFor(DiscoverSection.allGames),
                    sort: _session.allGamesSort,
                    systems: _session.systems,
                    installedReleaseIds: _session.installedReleaseIds,
                    platformId: _session.allGamesPlatformId,
                    operationAvailable: operation != null,
                    active:
                        widget.active &&
                        _controller.section == DiscoverSection.allGames,
                    autofocusNavigation: widget.autofocusNavigation,
                  ),
                  callbacks: DiscoverAllGamesCallbacks(
                    navigation: navigation,
                    onOpenGame: widget.onOpenGame,
                    onSelectSort: (sort) =>
                        unawaited(_session.configureAllGames(sort: sort)),
                    onSelectPlatform: (platformId) => unawaited(
                      _session.configureAllGames(
                        platformId: platformId,
                        clearPlatform: platformId == null,
                      ),
                    ),
                    onClearFilters: () => unawaited(
                      _session.configureAllGames(
                        sort: DiscoverCatalogSort.title,
                        clearPlatform: true,
                      ),
                    ),
                    onLoadMore: () => unawaited(_session.loadMoreAllGames()),
                    onRetry: () =>
                        unawaited(_session.loadAllGames(force: true)),
                    onRetrySystems: () =>
                        unawaited(_session.loadSystems(force: true)),
                    detailFor: _session.detailFor,
                    loadDetail: _session.loadDetail,
                    onRememberFocus:
                        ({required itemId, required scrollOffset}) {
                          _session.rememberFocus(
                            DiscoverSection.allGames,
                            itemId: itemId,
                            scrollOffset: scrollOffset,
                          );
                        },
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  void _onSessionChanged() {
    if (mounted) setState(() {});
  }

  void _onSelectedFeaturedTone(Color tone) {
    if (!mounted || tone == _featuredAmbientTone) return;
    setState(() => _featuredAmbientTone = tone);
  }

  void _onSectionChanged() {
    if (mounted) setState(() {});
    unawaited(_ensureActiveSectionLoaded());
  }

  Future<void> _ensureActiveSectionLoaded() async {
    final section = _controller.section;
    if (section == DiscoverSection.allGames) {
      await Future.wait(<Future<void>>[
        _session.ensureLoaded(DiscoverSection.allGames),
        _session.loadSystems(),
      ]);
      return;
    }
    await _session.ensureLoaded(section);
  }

  void _selectFromFeatureNavigation(DiscoverSection section) {
    _controller.select(section);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _controller.section == section) {
        _focusHandoff.restoreContent(section);
      }
    });
  }
}
