import 'package:flutter/material.dart';

import '../../domain/console_game.dart';
import '../theme/console_theme_context.dart';
import 'byte_format.dart';
import 'console_ambient_background.dart';
import 'console_circle_button.dart';
import 'console_hint_bar.dart';
import 'platform_presentation.dart';

/// The preservation layer — reached via Details, kept off the cover-forward Game
/// View so the console doesn't read as a database browser. Renders the catalog
/// record in the mono "archive" aesthetic.
final class GameRecordScreen extends StatelessWidget {
  const GameRecordScreen({
    required this.game,
    required this.detailFuture,
    super.key,
  });

  final ConsoleGame game;
  final Future<ConsoleGameDetail> detailFuture;

  @override
  Widget build(BuildContext context) => Scaffold(
    backgroundColor: context.theme.scaffoldBackgroundColor,
    // Actions must live inside the Scaffold (it registers a DismissIntent action
    // of its own that would otherwise intercept ours).
    body: Actions(
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
      },
      child: Focus(
        autofocus: true,
        child: Stack(
          children: <Widget>[
            const Positioned.fill(
              child: ConsoleAmbientBackground(dimmed: true),
            ),
            SafeArea(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Expanded(
                    child: Padding(
                      padding: EdgeInsets.fromLTRB(
                        context.layout.screenGutter,
                        context.layout.xl,
                        context.layout.screenGutter,
                        context.layout.xs,
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: <Widget>[
                          _RecordHeader(game: game),
                          SizedBox(height: context.layout.lg),
                          Expanded(
                            child: FutureBuilder<ConsoleGameDetail>(
                              future: detailFuture,
                              builder: (context, snapshot) {
                                if (snapshot.connectionState !=
                                    ConnectionState.done) {
                                  return const _RecordLoading();
                                }
                                if (snapshot.hasError ||
                                    snapshot.data == null) {
                                  return const _RecordError();
                                }
                                return SingleChildScrollView(
                                  child: _RecordBody(
                                    game: game,
                                    detail: snapshot.data!,
                                  ),
                                );
                              },
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                  const ConsoleFooterBar(
                    context: 'PRESERVATION LAYER · RECORD',
                    hints: <ConsoleHint>[
                      ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: 'B',
                        label: 'Back to game',
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

final class _RecordHeader extends StatelessWidget {
  const _RecordHeader({required this.game});

  final ConsoleGame game;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final shortCode = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    ).shortCode;

    return Row(
      children: <Widget>[
        ConsoleCircleButton(
          selected: true,
          onTap: () => Navigator.of(context).maybePop(),
        ),
        SizedBox(width: layout.lg),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Text(
                'RECORD',
                style: context.text.eyebrow.copyWith(color: colors.textFaint),
              ),
              SizedBox(height: layout.xxs),
              Text(
                game.title,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: context.text.cardTitle.copyWith(
                  color: colors.textStrong,
                ),
              ),
            ],
          ),
        ),
        _CatalogIdBadge(label: '$shortCode·${game.id}'),
      ],
    );
  }
}

final class _CatalogIdBadge extends StatelessWidget {
  const _CatalogIdBadge({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(layout.chipRadius),
        border: Border.all(
          color: colors.catalogAccent.withValues(
            alpha: layout.tint.borderOpacity,
          ),
          width: layout.hairlineStroke,
        ),
        color: colors.catalogAccent.withValues(alpha: layout.tint.fillOpacity),
      ),
      child: Padding(
        padding: EdgeInsets.symmetric(
          horizontal: layout.sm,
          vertical: layout.xs,
        ),
        child: Text(
          label,
          style: context.text.metadataStrong.copyWith(
            color: colors.catalogAccent,
          ),
        ),
      ),
    );
  }
}

final class _RecordLoading extends StatelessWidget {
  const _RecordLoading();

  @override
  Widget build(BuildContext context) => Center(
    child: SizedBox(
      width: context.layout.status.loadingIndicatorWidth,
      child: LinearProgressIndicator(
        minHeight: context.layout.hairlineStroke * 2,
      ),
    ),
  );
}

final class _RecordError extends StatelessWidget {
  const _RecordError();

  @override
  Widget build(BuildContext context) => Center(
    child: Text(
      'Record unavailable',
      style: context.text.cardTitle.copyWith(
        color: context.consoleColors.textMuted,
      ),
    ),
  );
}

final class _RecordBody extends StatelessWidget {
  const _RecordBody({required this.game, required this.detail});

  final ConsoleGame game;
  final ConsoleGameDetail detail;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final release = _preferredRelease(detail);
    final fields = <_RecordField>[
      _RecordField('Catalog ID', game.id),
      _RecordField('Platform', detail.platformName),
      _RecordField(
        'Release',
        _yearLabel(detail.releaseDate ?? game.releaseDate),
      ),
      if (detail.publisher case final publisher?)
        _RecordField('Publisher', publisher),
      if (detail.developer case final developer?)
        _RecordField('Developer', developer),
      if (detail.players case final players?)
        _RecordField('Players', players.toString()),
      if (release != null) ...<_RecordField>[
        _RecordField('Default release', release.name),
        _RecordField('Revision', release.revision ?? 'Original'),
        _RecordField('Region', _joinOrUnknown(release.regions)),
        _RecordField('Languages', _joinOrUnknown(release.languages)),
        _RecordField('Size', formatBytes(release.sizeBytes)),
        _RecordField(
          'Completeness',
          release.isComplete ? 'Complete' : 'Partial',
        ),
      ],
      _RecordField(
        'Status',
        game.canLaunch ? 'PLAYABLE' : 'NOT YET PLAYABLE',
        accent: game.canLaunch ? colors.connected : null,
      ),
    ];

    return Align(
      alignment: Alignment.topCenter,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 1180),
        child: DecoratedBox(
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(layout.panelRadius),
            border: Border.all(
              color: colors.hairline,
              width: layout.hairlineStroke,
            ),
            color: colors.panelSurface,
          ),
          child: Padding(
            padding: EdgeInsets.symmetric(
              horizontal: layout.xl,
              vertical: layout.xs,
            ),
            child: _RecordGrid(fields: fields),
          ),
        ),
      ),
    );
  }
}

/// Two-column paired record grid: fields fill left-to-right, top-to-bottom, with
/// a hairline under each row and a vertical hairline between the columns. Falls
/// back to a single column when the panel is narrow.
final class _RecordGrid extends StatelessWidget {
  const _RecordGrid({required this.fields});

  final List<_RecordField> fields;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final twoColumns = constraints.maxWidth >= 720;
      final rowCount = twoColumns ? (fields.length + 1) ~/ 2 : fields.length;

      return Column(
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          for (var row = 0; row < rowCount; row++)
            _RecordRow(
              left: fields[twoColumns ? row * 2 : row],
              right: twoColumns && row * 2 + 1 < fields.length
                  ? fields[row * 2 + 1]
                  : null,
              isLast: row == rowCount - 1,
            ),
        ],
      );
    },
  );
}

final class _RecordRow extends StatelessWidget {
  const _RecordRow({
    required this.left,
    required this.right,
    required this.isLast,
  });

  final _RecordField left;
  final _RecordField? right;
  final bool isLast;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return DecoratedBox(
      decoration: BoxDecoration(
        border: isLast
            ? null
            : Border(
                bottom: BorderSide(
                  color: colors.hairline,
                  width: layout.hairlineStroke,
                ),
              ),
      ),
      child: IntrinsicHeight(
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: <Widget>[
            Expanded(child: _RecordCell(field: left)),
            if (right != null) ...<Widget>[
              VerticalDivider(
                width: layout.xxl,
                color: colors.hairline,
                thickness: layout.hairlineStroke,
              ),
              Expanded(child: _RecordCell(field: right!)),
            ] else
              const Spacer(),
          ],
        ),
      ),
    );
  }
}

final class _RecordCell extends StatelessWidget {
  const _RecordCell({required this.field});

  final _RecordField field;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Padding(
      padding: EdgeInsets.symmetric(vertical: layout.md),
      child: Row(
        children: <Widget>[
          Text(
            field.label.toUpperCase(),
            style: context.text.eyebrow.copyWith(color: colors.textFaint),
          ),
          SizedBox(width: layout.md),
          Expanded(
            child: Text(
              field.value,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              textAlign: TextAlign.right,
              style: context.text.metadata.copyWith(
                color: field.accent ?? colors.iconStrong,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

final class _RecordField {
  const _RecordField(this.label, this.value, {this.accent});

  final String label;
  final String value;
  final Color? accent;
}

ConsoleRelease? _preferredRelease(ConsoleGameDetail detail) {
  if (detail.releases.isEmpty) {
    return null;
  }

  final defaultReleaseId = detail.defaultReleaseId;
  if (defaultReleaseId == null) {
    return detail.releases.first;
  }

  return detail.releases.firstWhere(
    (release) => release.id == defaultReleaseId,
    orElse: () => detail.releases.first,
  );
}

String _yearLabel(DateTime? value) => value?.year.toString() ?? 'Unknown';

String _joinOrUnknown(List<String> values) =>
    values.isEmpty ? 'Unknown' : values.join(', ');
