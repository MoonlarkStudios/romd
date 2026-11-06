import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../data/local_profiles/local_profile_repository.dart';
import '../domain/local_profile.dart';
import 'theme/console_theme_context.dart';
import 'widgets/console_clock.dart';
import 'widgets/console_hint_bar.dart';
import 'widgets/create_profile_screen.dart';
import 'widgets/profile_avatar.dart';

final class LocalProfileSelectionScreen extends StatefulWidget {
  const LocalProfileSelectionScreen({
    required this.repository,
    required this.onSelected,
    required this.onBack,
    required this.defaultRomdServerOrigin,
    this.showLockup = true,
    this.showClock = true,
    this.animateIntro = true,
    super.key,
  });

  final LocalProfileRepository repository;
  final ValueChanged<LocalProfile> onSelected;
  final Uri defaultRomdServerOrigin;

  /// Invoked when the user backs out (Esc) to return to the attract screen.
  final VoidCallback onBack;

  /// Whether to render the profile-entry prompt above the carousel.
  /// Suppressed when embedded in [EntryStage], which coordinates the prompt
  /// with the approved attract → selection handoff.
  final bool showLockup;

  /// Whether to render the corner clock. Suppressed when embedded so the host
  /// can render a single persistent clock across the transition.
  final bool showClock;

  /// Whether to play the intro fade/slide on first build. Disabled when the
  /// entrance is driven by an enclosing animation.
  final bool animateIntro;

  @override
  State<LocalProfileSelectionScreen> createState() =>
      _LocalProfileSelectionScreenState();
}

final class _LocalProfileSelectionScreenState
    extends State<LocalProfileSelectionScreen> {
  late final Future<List<LocalProfile>> _profilesFuture;
  bool _creating = false;

  /// True when the focused tile is "Add user" rather than a profile — drives
  /// the footer's contextual primary verb. Self-corrects on the first frame
  /// via the tiles' focus callbacks.
  final ValueNotifier<bool> _onAddTile = ValueNotifier<bool>(false);

  @override
  void initState() {
    super.initState();
    _profilesFuture = widget.repository.listProfiles();
  }

  @override
  void dispose() {
    _onAddTile.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    backgroundColor: Colors.transparent,
    // DismissIntent (keyboard Esc + gamepad B) lives inside the Scaffold, which
    // registers its own DismissIntent action that would otherwise intercept it.
    body: Actions(
      actions: <Type, Action<Intent>>{
        DismissIntent: CallbackAction<DismissIntent>(
          onInvoke: (_) {
            widget.onBack();
            return null;
          },
        ),
      },
      child: SafeArea(
        child: Stack(
          children: <Widget>[
            // The selection body fills the whole safe area so the focused avatar
            // can sit at the true vertical center of the screen. The clock and
            // hint bar float over it as chrome.
            Positioned.fill(child: _buildBody()),
            if (widget.showClock)
              Positioned(
                top: context.layout.screenChrome.top,
                right: context.layout.screenChrome.side,
                child: const ConsoleClock(),
              ),
            // Welcome-register footer: chrome-less and centered (no bar/border
            // over the wave), one notch fainter than the in-app footer, and the
            // primary verb tracks the focused tile. Sits at the same baseline a
            // flush ConsoleFooterBar's hints would occupy.
            Positioned(
              left: 0,
              right: 0,
              bottom: context.layout.screenChrome.bottom,
              child: Center(
                child: ValueListenableBuilder<bool>(
                  valueListenable: _onAddTile,
                  builder: (context, onAddTile, _) => ConsoleHintBar(
                    muted: true,
                    hints: <ConsoleHint>[
                      ConsoleHint(
                        glyph: ConsoleHintGlyphs.confirm,
                        gamepadGlyph: 'A',
                        label: onAddTile ? 'Add player' : 'Select',
                        reserveLabel: 'Add player',
                      ),
                      const ConsoleHint(
                        glyph: 'Esc',
                        gamepadGlyph: 'B',
                        label: 'Back',
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

  Widget _buildBody() => FutureBuilder<List<LocalProfile>>(
    future: _profilesFuture,
    builder: (context, snapshot) {
      if (snapshot.connectionState != ConnectionState.done) {
        return const Center(child: CircularProgressIndicator());
      }

      return _selectionBody(snapshot.data ?? const <LocalProfile>[]);
    },
  );

  _SelectionBody _selectionBody(List<LocalProfile> profiles) => _SelectionBody(
    profiles: profiles,
    creating: _creating,
    showLockup: widget.showLockup,
    animateIntro: widget.animateIntro,
    onSelected: widget.onSelected,
    onCreate: _createProfile,
    onAddTileFocused: (focused) => _onAddTile.value = focused,
  );

  Future<void> _createProfile() async {
    if (_creating) {
      return;
    }

    final request = await CreateProfileScreen.show(
      context,
      initialRomdServerOrigin: widget.defaultRomdServerOrigin,
    );
    if (request == null || !mounted) {
      return;
    }

    setState(() => _creating = true);

    try {
      final profile = await widget.repository.createProfile(request);
      widget.onSelected(profile);
    } on ArgumentError {
      if (mounted) {
        setState(() => _creating = false);
      }
    }
  }
}

final class _SelectionBody extends StatefulWidget {
  const _SelectionBody({
    required this.profiles,
    required this.creating,
    required this.showLockup,
    required this.animateIntro,
    required this.onSelected,
    required this.onCreate,
    required this.onAddTileFocused,
  });

  final List<LocalProfile> profiles;
  final bool creating;
  final bool showLockup;
  final bool animateIntro;
  final ValueChanged<LocalProfile> onSelected;
  final VoidCallback onCreate;
  final ValueChanged<bool> onAddTileFocused;

  @override
  State<_SelectionBody> createState() => _SelectionBodyState();
}

final class _SelectionBodyState extends State<_SelectionBody>
    with SingleTickerProviderStateMixin {
  late final AnimationController _intro = AnimationController(
    vsync: this,
    value: widget.animateIntro ? 0 : 1,
  );
  bool _introStarted = false;

  late final CurvedAnimation _lockupProgress = CurvedAnimation(
    parent: _intro,
    curve: Curves.linear,
  );

  late final CurvedAnimation _carouselProgress = CurvedAnimation(
    parent: _intro,
    curve: Curves.linear,
  );

  @override
  void initState() {
    super.initState();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final motion = context.motion;
    final duration = motion.resolve(context, motion.entryTransition);
    _lockupProgress.curve = Interval(0, 0.7, curve: motion.emphasizedCurve);
    _carouselProgress.curve = Interval(0.22, 1, curve: motion.emphasizedCurve);
    if (duration == Duration.zero) {
      _intro
        ..stop()
        ..value = 1;
    } else {
      _intro.duration = duration;
      if (widget.animateIntro && !_introStarted) {
        _introStarted = true;
        _intro.forward();
      }
    }
  }

  @override
  void dispose() {
    _lockupProgress.dispose();
    _carouselProgress.dispose();
    _intro.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    key: const ValueKey<String>('profile-selection-intro'),
    animation: _intro,
    builder: (context, _) => Stack(
      fit: StackFit.expand,
      children: <Widget>[
        // Title lockup, anchored a fixed distance above the centered avatar.
        if (widget.showLockup)
          Center(
            child: Opacity(
              opacity: _lockupProgress.value,
              child: Transform.translate(
                offset: Offset(
                  0,
                  -context.layout.profileSelectionLockupRise -
                      context.layout.md * (1 - _lockupProgress.value),
                ),
                child: _SelectionLockup(
                  hasProfiles: widget.profiles.isNotEmpty,
                ),
              ),
            ),
          ),
        // Carousel, centered so the focused avatar lands on the screen center.
        Center(
          child: Opacity(
            opacity: _carouselProgress.value,
            child: Transform.translate(
              offset: Offset(
                0,
                context.layout.entrySelectionTranslate *
                    (1 - _carouselProgress.value),
              ),
              child: _ProfileCarousel(
                profiles: widget.profiles,
                creating: widget.creating,
                onSelected: widget.onSelected,
                onCreate: widget.onCreate,
                onAddTileFocused: widget.onAddTileFocused,
              ),
            ),
          ),
        ),
      ],
    ),
  );
}

final class _SelectionLockup extends StatelessWidget {
  const _SelectionLockup({required this.hasProfiles});

  final bool hasProfiles;

  @override
  Widget build(BuildContext context) {
    return Text(
      hasProfiles ? "Who's playing?" : 'Add a profile to get started',
      key: const ValueKey<String>('profile-selection-prompt'),
      style: context.text.action.copyWith(
        color: context.consoleColors.textStrong,
      ),
    );
  }
}

const double _kTileHorizontalPadding = 12;
const double _kTileWidth = 158;
const double _kTileSlotWidth = _kTileWidth + _kTileHorizontalPadding * 2;

// At normal scale the approved 720p composition remains fixed. Enlarged text
// adds vertical room below and above the centered avatar so labels never clip.
const double _kTileHeight = 276;
const double _kLabelTop = 210;
const double _kEnlargedTextHeightAllowance = 56;

double _tileHeight(BuildContext context) {
  final scale = MediaQuery.textScalerOf(context).scale(1);
  return _kTileHeight + _kEnlargedTextHeightAllowance * (scale - 1).clamp(0, 1);
}

double _labelTop(BuildContext context) =>
    _tileHeight(context) / 2 + (_kLabelTop - _kTileHeight / 2);

/// Distance the profile prompt floats above the centered avatar.
final class _ProfileCarousel extends StatefulWidget {
  const _ProfileCarousel({
    required this.profiles,
    required this.creating,
    required this.onSelected,
    required this.onCreate,
    required this.onAddTileFocused,
  });

  final List<LocalProfile> profiles;
  final bool creating;
  final ValueChanged<LocalProfile> onSelected;
  final VoidCallback onCreate;
  final ValueChanged<bool> onAddTileFocused;

  @override
  State<_ProfileCarousel> createState() => _ProfileCarouselState();
}

final class _ProfileCarouselState extends State<_ProfileCarousel> {
  late final ScrollController _scrollController;

  int get _initialFocusedSlot => widget.profiles.isEmpty ? 0 : 1;

  @override
  void initState() {
    super.initState();
    _scrollController = ScrollController(
      initialScrollOffset: (_initialFocusedSlot * _kTileSlotWidth).toDouble(),
    );
  }

  @override
  void didUpdateWidget(covariant _ProfileCarousel oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.profiles.length != widget.profiles.length) {
      _centerSlot(_initialFocusedSlot, animated: false);
    }
  }

  @override
  void dispose() {
    _scrollController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final sidePadding = math.max(
        0.0,
        (constraints.maxWidth - _kTileSlotWidth) / 2,
      );

      return SizedBox(
        width: constraints.maxWidth,
        height: _tileHeight(context),
        child: FocusTraversalGroup(
          policy: ReadingOrderTraversalPolicy(),
          child: SingleChildScrollView(
            controller: _scrollController,
            scrollDirection: Axis.horizontal,
            padding: EdgeInsets.symmetric(horizontal: sidePadding),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                _CarouselSlot(
                  child: _AddProfileTile(
                    autofocus: widget.profiles.isEmpty,
                    busy: widget.creating,
                    onFocused: () {
                      _centerSlot(0);
                      widget.onAddTileFocused(true);
                    },
                    onSelected: widget.onCreate,
                  ),
                ),
                for (var i = 0; i < widget.profiles.length; i++)
                  _CarouselSlot(
                    child: _ProfileTile(
                      profile: widget.profiles[i],
                      autofocus: i == 0,
                      onFocused: () {
                        _centerSlot(i + 1);
                        widget.onAddTileFocused(false);
                      },
                      onSelected: () => widget.onSelected(widget.profiles[i]),
                    ),
                  ),
              ],
            ),
          ),
        ),
      );
    },
  );

  void _centerSlot(int slot, {bool animated = true}) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_scrollController.hasClients) {
        return;
      }

      final target = (slot * _kTileSlotWidth)
          .clamp(
            _scrollController.position.minScrollExtent,
            _scrollController.position.maxScrollExtent,
          )
          .toDouble();
      if (animated) {
        final motion = context.motion;
        final duration = motion.resolve(context, motion.contentTransition);
        if (duration == Duration.zero) {
          _scrollController.jumpTo(target);
          return;
        }
        unawaited(
          _scrollController.animateTo(
            target,
            duration: duration,
            curve: motion.spatialCurve,
          ),
        );
      } else {
        _scrollController.jumpTo(target);
      }
    });
  }
}

final class _CarouselSlot extends StatelessWidget {
  const _CarouselSlot({required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(horizontal: _kTileHorizontalPadding),
    child: child,
  );
}

final class _ProfileTile extends StatefulWidget {
  const _ProfileTile({
    required this.profile,
    required this.autofocus,
    required this.onFocused,
    required this.onSelected,
  });

  final LocalProfile profile;
  final bool autofocus;
  final VoidCallback onFocused;
  final VoidCallback onSelected;

  @override
  State<_ProfileTile> createState() => _ProfileTileState();
}

final class _ProfileTileState extends State<_ProfileTile> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;
    final accent = Color(widget.profile.accentColor);
    final linked = widget.profile.romdAccountLink != null;
    final semanticIdentity = linked
        ? '${widget.profile.displayName}, Linked profile as '
              '${widget.profile.romdAccountLink!.username} on '
              '${widget.profile.romdServerOrigin?.authority ?? 'ROMD server'}.'
        : '${widget.profile.displayName}, Local profile.';

    return FocusableActionDetector(
      autofocus: widget.autofocus,
      mouseCursor: SystemMouseCursors.click,
      actions: <Type, Action<Intent>>{
        ActivateIntent: CallbackAction<ActivateIntent>(
          onInvoke: (_) {
            widget.onSelected();
            return null;
          },
        ),
      },
      onFocusChange: (focused) {
        setState(() => _focused = focused);
        if (focused) {
          widget.onFocused();
        }
      },
      child: Semantics(
        button: true,
        excludeSemantics: true,
        label: semanticIdentity,
        child: GestureDetector(
          onTap: () {
            widget.onFocused();
            widget.onSelected();
          },
          child: _TileFrame(
            key: ValueKey<String>('profile-tile-${widget.profile.id}'),
            avatar: SizedBox(
              key: ValueKey<String>('profile-avatar-${widget.profile.id}'),
              child: _AvatarFrame(
                focused: _focused,
                accent: accent,
                child: ProfileAvatar(
                  avatarKey: widget.profile.avatarKey,
                  accentColor: accent,
                  size: layout.avatarXl,
                ),
              ),
            ),
            label: Column(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                Text(
                  widget.profile.displayName,
                  maxLines: 1,
                  textAlign: TextAlign.center,
                  overflow: TextOverflow.ellipsis,
                  style: text.action,
                ),
                SizedBox(height: layout.xs),
                Text(
                  linked ? 'Linked' : 'Local',
                  maxLines: 1,
                  textAlign: TextAlign.center,
                  overflow: TextOverflow.ellipsis,
                  style: text.metadata.copyWith(color: colors.textFaint),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

final class _AddProfileTile extends StatefulWidget {
  const _AddProfileTile({
    required this.autofocus,
    required this.busy,
    required this.onFocused,
    required this.onSelected,
  });

  final bool autofocus;
  final bool busy;
  final VoidCallback onFocused;
  final VoidCallback onSelected;

  @override
  State<_AddProfileTile> createState() => _AddProfileTileState();
}

final class _AddProfileTileState extends State<_AddProfileTile> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    return FocusableActionDetector(
      autofocus: widget.autofocus,
      mouseCursor: SystemMouseCursors.click,
      actions: <Type, Action<Intent>>{
        ActivateIntent: CallbackAction<ActivateIntent>(
          onInvoke: (_) {
            widget.onSelected();
            return null;
          },
        ),
      },
      onFocusChange: (focused) {
        setState(() => _focused = focused);
        if (focused) {
          widget.onFocused();
        }
      },
      child: GestureDetector(
        onTap: () {
          widget.onFocused();
          widget.onSelected();
        },
        child: _TileFrame(
          key: const ValueKey<String>('add-profile-tile'),
          avatar: AnimatedScale(
            duration: motion.resolve(context, motion.focus),
            curve: motion.emphasizedCurve,
            scale: _focused ? motion.interaction.focusedScale : 1,
            child: CustomPaint(
              key: const ValueKey<String>('add-profile-avatar'),
              painter: _DashedCirclePainter(
                color: _focused ? colors.focusBorder : colors.borderStrong,
                strokeWidth: layout.focusStroke,
              ),
              child: SizedBox(
                width: layout.avatarXl,
                height: layout.avatarXl,
                child: Center(
                  child: widget.busy
                      ? SizedBox.square(
                          dimension: layout.progressIndicatorSize,
                          child: CircularProgressIndicator(
                            strokeWidth: layout.focusStroke,
                          ),
                        )
                      : Icon(
                          Icons.add,
                          size: layout.status.feedbackIconSize,
                          color: _focused
                              ? colors.textStrong
                              : colors.textMuted,
                        ),
                ),
              ),
            ),
          ),
          label: Text(
            'Add player',
            textAlign: TextAlign.center,
            style: context.text.action,
          ),
        ),
      ),
    );
  }
}

/// Shared geometry for every carousel tile: a scale-aware box with the avatar
/// pinned dead-center and the label below. Keeping
/// the avatar at the box center makes the focused avatar land on the screen
/// center once the strip is vertically centered.
final class _TileFrame extends StatelessWidget {
  const _TileFrame({required this.avatar, required this.label, super.key});

  final Widget avatar;
  final Widget label;

  @override
  Widget build(BuildContext context) => SizedBox(
    width: _kTileWidth,
    height: _tileHeight(context),
    child: Stack(
      children: <Widget>[
        Align(child: avatar),
        Positioned(top: _labelTop(context), left: 0, right: 0, child: label),
      ],
    ),
  );
}

final class _AvatarFrame extends StatelessWidget {
  const _AvatarFrame({
    required this.focused,
    required this.accent,
    required this.child,
  });

  final bool focused;
  final Color accent;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    return AnimatedScale(
      duration: motion.resolve(context, motion.focus),
      curve: motion.emphasizedCurve,
      scale: focused ? motion.interaction.focusedScale : 1,
      child: AnimatedContainer(
        duration: motion.resolve(context, motion.selection),
        curve: motion.emphasizedCurve,
        padding: EdgeInsets.all(layout.xxs),
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          border: Border.all(
            color: focused ? accent : colors.hairline,
            width: focused ? layout.focusRingStroke : layout.hairlineStroke,
          ),
          boxShadow: focused
              ? <BoxShadow>[
                  BoxShadow(
                    color: accent.withValues(
                      alpha: context.elevation.identityFocusGlowAlpha,
                    ),
                    blurRadius: layout.focusGlowBlur,
                    spreadRadius: layout.focusGlowInflate,
                  ),
                ]
              : const <BoxShadow>[],
        ),
        child: child,
      ),
    );
  }
}

final class _DashedCirclePainter extends CustomPainter {
  const _DashedCirclePainter({required this.color, required this.strokeWidth});

  final Color color;
  final double strokeWidth;

  @override
  void paint(Canvas canvas, Size size) {
    final center = size.center(Offset.zero);
    final radius = size.shortestSide / 2 - 1;
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = strokeWidth
      ..strokeCap = StrokeCap.round
      ..color = color;

    const dashCount = 36;
    const gapRatio = 0.45;
    const sweep = 2 * math.pi / dashCount;
    for (var i = 0; i < dashCount; i++) {
      final start = i * sweep;
      canvas.drawArc(
        Rect.fromCircle(center: center, radius: radius),
        start,
        sweep * (1 - gapRatio),
        false,
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(_DashedCirclePainter oldDelegate) =>
      oldDelegate.color != color || oldDelegate.strokeWidth != strokeWidth;
}
