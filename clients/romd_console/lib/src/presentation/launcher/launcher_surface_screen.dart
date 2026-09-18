import 'package:flutter/foundation.dart' show ValueListenable;
import 'package:flutter/material.dart';

import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/catalog/discover_catalog_session.dart';
import 'package:romd_console/src/presentation/catalog/search/discover_search_route.dart';
import 'package:romd_console/src/presentation/launcher/catalog_launcher_destination.dart';
import 'package:romd_console/src/presentation/launcher/library_launcher_destination.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';
import 'package:romd_console/src/presentation/widgets/console_page_band.dart';
import 'package:romd_console/src/presentation/widgets/game_detail.dart';

/// Live service graph handed to pushed launcher surfaces.
///
/// The root republishes this whenever the active profile's service graph or
/// catalog operation changes, so full-screen surfaces (Library, Catalog) keep
/// reading current services instead of holding references captured at push
/// time. A null value means no profile is active — surfaces close themselves.
final class HomeSurfaceContext {
  const HomeSurfaceContext({
    required this.localProfileId,
    required this.consumerApiClient,
    required this.playServices,
    this.catalogOperation,
  });

  final String localProfileId;
  final ConsumerApiClient consumerApiClient;
  final PlayServices playServices;
  final CatalogOperationContext? catalogOperation;

  bool hasSameContext(HomeSurfaceContext other) =>
      localProfileId == other.localProfileId &&
      identical(consumerApiClient, other.consumerApiClient) &&
      identical(playServices, other.playServices) &&
      switch ((catalogOperation, other.catalogOperation)) {
        (null, null) => true,
        (final CatalogOperationContext a, final CatalogOperationContext b) =>
          a.hasSameOperation(b),
        _ => false,
      };
}

/// Opens the profile's Library as a full-screen surface.
void pushLibrarySurface(
  BuildContext context, {
  required ValueListenable<HomeSurfaceContext?> surfaceContext,
  required VoidCallback onConnect,
}) {
  Navigator.of(context).push<void>(
    MaterialPageRoute<void>(
      builder: (context) => _LibrarySurfaceScreen(
        surfaceContext: surfaceContext,
        onConnect: onConnect,
      ),
    ),
  );
}

/// Opens the Catalog as a full-screen surface.
void pushCatalogSurface(
  BuildContext context, {
  required ValueListenable<HomeSurfaceContext?> surfaceContext,
  required VoidCallback onConnect,
}) {
  Navigator.of(context).push<void>(
    MaterialPageRoute<void>(
      builder: (context) => _CatalogSurfaceScreen(
        surfaceContext: surfaceContext,
        onConnect: onConnect,
      ),
    ),
  );
}

final class _LibrarySurfaceScreen extends StatefulWidget {
  const _LibrarySurfaceScreen({
    required this.surfaceContext,
    required this.onConnect,
  });

  final ValueListenable<HomeSurfaceContext?> surfaceContext;
  final VoidCallback onConnect;

  @override
  State<_LibrarySurfaceScreen> createState() => _LibrarySurfaceScreenState();
}

final class _LibrarySurfaceScreenState extends State<_LibrarySurfaceScreen> {
  final GlobalKey<LibraryLauncherDestinationState> _destinationKey =
      GlobalKey<LibraryLauncherDestinationState>();

  @override
  Widget build(BuildContext context) => _SurfaceContextGate(
    surfaceContext: widget.surfaceContext,
    builder: (context, current) => _LauncherSurfaceScaffold(
      title: 'Library',
      contentBuilder: (headerFocusNode) => LibraryLauncherDestination(
        key: _destinationKey,
        consumerApiClient: current.consumerApiClient,
        operation: current.catalogOperation,
        playServices: current.playServices,
        active: true,
        autofocusContent: true,
        headerFocusNode: headerFocusNode,
        onConnect: widget.onConnect,
        onOpenGame: (game, heroTag) => pushGameDetail(
          context,
          game: game,
          heroTag: heroTag,
          consumerApiClient: current.consumerApiClient,
          operation: current.catalogOperation,
          playServices: current.playServices,
          localProfileId: current.localProfileId,
        ),
        onMoveToUtilities: _noop,
      ),
    ),
  );

  static void _noop() {}
}

final class _CatalogSurfaceScreen extends StatefulWidget {
  const _CatalogSurfaceScreen({
    required this.surfaceContext,
    required this.onConnect,
  });

  final ValueListenable<HomeSurfaceContext?> surfaceContext;
  final VoidCallback onConnect;

  @override
  State<_CatalogSurfaceScreen> createState() => _CatalogSurfaceScreenState();
}

final class _CatalogSurfaceScreenState extends State<_CatalogSurfaceScreen> {
  @override
  Widget build(BuildContext context) => _SurfaceContextGate(
    surfaceContext: widget.surfaceContext,
    builder: (context, current) => CatalogLauncherDestination(
      consumerApiClient: current.consumerApiClient,
      playServices: current.playServices,
      operation: current.catalogOperation,
      active: true,
      autofocusNavigation: true,
      onBack: () => Navigator.of(context).maybePop(),
      onConnect: widget.onConnect,
      onOpenGame: (game, heroTag) => _openGame(context, current, game, heroTag),
      onSearch: (session) => _openSearch(context, current, session),
    ),
  );

  void _openSearch(
    BuildContext context,
    HomeSurfaceContext current,
    DiscoverCatalogSession session,
  ) {
    final operation = current.catalogOperation;
    if (operation == null || !operation.isRequestCurrent) return;
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (context) => DiscoverSearchRoute(
          session: session,
          onOpenGame: (game, heroTag) {
            final live = widget.surfaceContext.value;
            if (live != null) _openGame(context, live, game, heroTag);
          },
        ),
      ),
    );
  }

  void _openGame(
    BuildContext context,
    HomeSurfaceContext current,
    ConsoleGame game,
    String heroTag,
  ) {
    final operation = current.catalogOperation;
    if (operation == null || !operation.isRequestCurrent) return;
    pushGameDetail(
      context,
      game: game,
      heroTag: heroTag,
      consumerApiClient: current.consumerApiClient,
      operation: operation,
      playServices: current.playServices,
      localProfileId: current.localProfileId,
    );
  }
}

/// Rebuilds a surface against the live [HomeSurfaceContext]; closes every
/// route above home once the context is gone (profile switched or services
/// cleared) so no surface outlives its authority.
final class _SurfaceContextGate extends StatefulWidget {
  const _SurfaceContextGate({
    required this.surfaceContext,
    required this.builder,
  });

  final ValueListenable<HomeSurfaceContext?> surfaceContext;
  final Widget Function(BuildContext context, HomeSurfaceContext current)
  builder;

  @override
  State<_SurfaceContextGate> createState() => _SurfaceContextGateState();
}

final class _SurfaceContextGateState extends State<_SurfaceContextGate> {
  bool _closing = false;

  @override
  Widget build(BuildContext context) =>
      ValueListenableBuilder<HomeSurfaceContext?>(
        valueListenable: widget.surfaceContext,
        builder: (context, current, _) {
          if (current == null) {
            _scheduleClose();
            return Scaffold(
              backgroundColor: context.theme.scaffoldBackgroundColor,
              body: const SizedBox.expand(),
            );
          }
          return widget.builder(context, current);
        },
      );

  void _scheduleClose() {
    if (_closing) return;
    _closing = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        Navigator.of(context).popUntil((route) => route.isFirst);
      }
    });
  }
}

/// Shared full-screen chrome for dock-launched surfaces: the compact page
/// band, the destination's slivers below, and the reserved footer hint row.
/// The band carries no back affordance — Esc/B dismiss, and the footer's
/// Back hint is tappable for pointer users.
final class _LauncherSurfaceScaffold extends StatefulWidget {
  const _LauncherSurfaceScaffold({
    required this.title,
    required this.contentBuilder,
  });

  final String title;
  final Widget Function(FocusNode headerFocusNode) contentBuilder;

  @override
  State<_LauncherSurfaceScaffold> createState() =>
      _LauncherSurfaceScaffoldState();
}

final class _LauncherSurfaceScaffoldState
    extends State<_LauncherSurfaceScaffold> {
  /// Handed to content as its "up from the top row" target. The band has no
  /// focusable chrome, so the node refuses focus and the content simply
  /// stays put — matching the system rule that Back lives in the footer.
  final FocusNode _headerNode = FocusNode(
    debugLabel: 'surface-header',
    canRequestFocus: false,
    skipTraversal: true,
  );
  final ScrollController _scroll = ScrollController(
    debugLabel: 'surface-scroll',
  );
  bool _scrolled = false;

  @override
  void initState() {
    super.initState();
    _scroll.addListener(_handleScroll);
  }

  @override
  void dispose() {
    _scroll.removeListener(_handleScroll);
    _scroll.dispose();
    _headerNode.dispose();
    super.dispose();
  }

  void _handleScroll() {
    final scrolled = _scroll.hasClients && _scroll.offset > 0;
    if (scrolled != _scrolled) setState(() => _scrolled = scrolled);
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Scaffold(
      backgroundColor: Colors.transparent,
      // Actions must live inside the Scaffold (it registers its own
      // DismissIntent action that would otherwise intercept ours).
      body: Actions(
        actions: <Type, Action<Intent>>{
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              Navigator.of(context).maybePop();
              return null;
            },
          ),
        },
        child: Stack(
          children: <Widget>[
            const Positioned.fill(
              child: ConsoleAmbientBackground(dimmed: true),
            ),
            SafeArea(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  ConsolePageBand(title: widget.title, anchored: _scrolled),
                  Expanded(
                    child: CustomScrollView(
                      key: const ValueKey<String>('surface-scroll-view'),
                      controller: _scroll,
                      slivers: <Widget>[
                        SliverToBoxAdapter(child: SizedBox(height: layout.md)),
                        widget.contentBuilder(_headerNode),
                      ],
                    ),
                  ),
                  ConsoleFooterBar(
                    hints: <ConsoleHint>[
                      const ConsoleHint(
                        glyph: ConsoleHintGlyphs.navigate,
                        gamepadGlyph: 'D-PAD',
                        label: 'Navigate',
                      ),
                      const ConsoleHint(
                        glyph: ConsoleHintGlyphs.confirm,
                        gamepadGlyph: 'A',
                        label: 'Open',
                      ),
                      ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: 'B',
                        label: 'Back',
                        onPressed: () => Navigator.of(context).maybePop(),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
