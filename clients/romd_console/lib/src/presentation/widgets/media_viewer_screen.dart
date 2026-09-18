import 'package:flutter/material.dart';

import '../../domain/console_game.dart';
import '../theme/console_theme_context.dart';
import 'console_hint_bar.dart';

/// Full-screen viewer for a title's media. Opened by activating a media tile on
/// the Game View; browse with ◂ ▸ and dismiss with Enter / Esc / B.
final class MediaViewerScreen extends StatefulWidget {
  const MediaViewerScreen({
    required this.media,
    required this.initialIndex,
    this.imageProvider,
    super.key,
  });

  final List<ConsoleMediaRef> media;
  final int initialIndex;
  final ImageProvider<Object> Function(Uri uri)? imageProvider;

  @override
  State<MediaViewerScreen> createState() => _MediaViewerScreenState();
}

final class _MediaViewerScreenState extends State<MediaViewerScreen> {
  late int _index = widget.initialIndex;

  void _move(TraversalDirection direction) {
    switch (direction) {
      case TraversalDirection.left:
      case TraversalDirection.up:
        _step(-1);
      case TraversalDirection.right:
      case TraversalDirection.down:
        _step(1);
    }
  }

  void _step(int delta) {
    if (widget.media.length < 2) {
      return;
    }
    setState(() {
      _index = (_index + delta) % widget.media.length;
      if (_index < 0) {
        _index += widget.media.length;
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final item = widget.media[_index];
    final total = widget.media.length;

    return Scaffold(
      backgroundColor: colors.mediaBackdrop,
      // Actions must live inside the Scaffold (it registers its own DismissIntent
      // action that would otherwise intercept ours).
      body: Actions(
        actions: <Type, Action<Intent>>{
          DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
            onInvoke: (intent) {
              _move(intent.direction);
              return null;
            },
          ),
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              Navigator.of(context).maybePop();
              return null;
            },
          ),
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              Navigator.of(context).maybePop();
              return null;
            },
          ),
        },
        child: Focus(
          autofocus: true,
          child: GestureDetector(
            onTap: () => Navigator.of(context).maybePop(),
            child: Stack(
              fit: StackFit.expand,
              children: <Widget>[
                Center(
                  child: Padding(
                    padding: EdgeInsets.fromLTRB(
                      layout.xxl,
                      layout.xxl,
                      layout.xxl,
                      layout.screenGutter,
                    ),
                    child: Image(
                      image:
                          widget.imageProvider?.call(item.url) ??
                          NetworkImage(item.url.toString()),
                      fit: BoxFit.contain,
                      errorBuilder: (context, error, stackTrace) => Icon(
                        Icons.broken_image_outlined,
                        size: layout.xxxl,
                        color: colors.textFaint,
                      ),
                    ),
                  ),
                ),
                if (total > 1)
                  Positioned(
                    top: layout.lg + layout.xxs,
                    right: layout.xl,
                    child: Text(
                      '${_index + 1} / $total',
                      style: context.text.utilityLabel.copyWith(
                        color: colors.textMuted,
                      ),
                    ),
                  ),
                Positioned(
                  left: 0,
                  right: 0,
                  bottom: 0,
                  child: ConsoleFooterBar(
                    hints: <ConsoleHint>[
                      if (total > 1)
                        const ConsoleHint(
                          glyph: ConsoleHintGlyphs.navigateHorizontal,
                          gamepadGlyph: 'D-PAD',
                          label: 'Browse',
                        ),
                      const ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: 'B',
                        label: 'Close',
                      ),
                    ],
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
