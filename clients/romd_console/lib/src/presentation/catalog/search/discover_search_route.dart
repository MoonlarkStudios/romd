import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/catalog/discover_catalog_session.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/search/discover_search_boundary.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';

/// Pushed Search route backed by the Catalog route's operation-scoped session.
final class DiscoverSearchRoute extends StatefulWidget {
  const DiscoverSearchRoute({
    required this.session,
    required this.onOpenGame,
    super.key,
  });

  final DiscoverCatalogSession session;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;

  @override
  State<DiscoverSearchRoute> createState() => _DiscoverSearchRouteState();
}

final class _DiscoverSearchRouteState extends State<DiscoverSearchRoute> {
  bool _rebuildScheduled = false;
  bool _closeScheduled = false;
  late final CatalogOperationContext? _openingOperation;

  @override
  void initState() {
    super.initState();
    _openingOperation = widget.session.operation;
    widget.session.addListener(_onSessionChanged);
    WidgetsBinding.instance.addPostFrameCallback((_) => _loadInitialState());
  }

  @override
  void didUpdateWidget(covariant DiscoverSearchRoute oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (identical(oldWidget.session, widget.session)) return;
    oldWidget.session.removeListener(_onSessionChanged);
    widget.session.addListener(_onSessionChanged);
  }

  @override
  void dispose() {
    widget.session.removeListener(_onSessionChanged);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final operation = widget.session.operation;
    final openingOperation = _openingOperation;
    if (operation == null ||
        openingOperation == null ||
        !_isOpeningOperationCurrent) {
      _scheduleClose();
      return Scaffold(
        backgroundColor: context.theme.scaffoldBackgroundColor,
        body: const CatalogPrivacyShield(),
      );
    }
    return CatalogPrivacyBoundary(
      operation: openingOperation,
      child: Scaffold(
        backgroundColor: context.theme.scaffoldBackgroundColor,
        body: Stack(
          children: <Widget>[
            const Positioned.fill(
              child: ConsoleAmbientBackground(dimmed: true),
            ),
            SafeArea(
              child: DiscoverSearchBoundary(
                input: DiscoverSearchInput(
                  results: widget.session.searchResults,
                  systems: widget.session.systems,
                  focus: widget.session.searchFocus,
                ),
                callbacks: DiscoverSearchCallbacks(
                  onSearch: (request) {
                    if (!_isOpeningOperationCurrent) return;
                    unawaited(
                      widget.session.search(
                        query: request.query,
                        platformId: request.platformId,
                        completeness: request.completeness,
                      ),
                    );
                  },
                  onRetry: (request) {
                    if (!_isOpeningOperationCurrent) return;
                    unawaited(
                      widget.session.search(
                        query: request.query,
                        platformId: request.platformId,
                        completeness: request.completeness,
                        force: true,
                      ),
                    );
                  },
                  onLoadMore: () {
                    if (_isOpeningOperationCurrent) {
                      unawaited(widget.session.loadMoreSearch());
                    }
                  },
                  onOpenGame: (game, heroTag) {
                    if (_isOpeningOperationCurrent) {
                      widget.onOpenGame(game, heroTag);
                    }
                  },
                  onBack: () => Navigator.of(context).maybePop(),
                  onRememberFocus: (focus) {
                    if (_isOpeningOperationCurrent) {
                      widget.session.rememberSearchFocus(focus);
                    }
                  },
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  bool get _isOpeningOperationCurrent {
    final opening = _openingOperation;
    final current = widget.session.operation;
    return opening != null &&
        current != null &&
        opening.hasSameOperation(current) &&
        opening.isRequestCurrent &&
        current.isRequestCurrent;
  }

  void _loadInitialState() {
    if (!mounted || !_isOpeningOperationCurrent) return;
    unawaited(widget.session.loadSystems());
    final query = widget.session.searchFocus.query.trim();
    if (query.isEmpty || !_isOpeningOperationCurrent) return;
    final request = widget.session.searchResults.requestKey;
    unawaited(
      widget.session.search(
        query: query,
        platformId: request?.platformId,
        completeness: request?.completeness ?? DiscoverReleaseCompleteness.any,
      ),
    );
  }

  void _scheduleClose() {
    final route = ModalRoute.of(context);
    if (_closeScheduled) return;
    _closeScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _closeScheduled = false;
      if (!mounted || route?.isCurrent != true) return;
      unawaited(Navigator.of(context).maybePop());
    });
  }

  void _onSessionChanged() {
    if (!mounted) return;
    if (SchedulerBinding.instance.schedulerPhase ==
        SchedulerPhase.persistentCallbacks) {
      if (_rebuildScheduled) return;
      _rebuildScheduled = true;
      SchedulerBinding.instance.addPostFrameCallback((_) {
        _rebuildScheduled = false;
        if (mounted) setState(() {});
      });
      return;
    }
    setState(() {});
  }
}
