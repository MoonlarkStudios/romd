import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../domain/local_profile.dart';
import '../../play/controllers/domain/players_projection.dart';
import '../theme/console_theme_context.dart';
import 'available_controllers.dart';
import 'console_hint_bar.dart';
import 'profile_avatar.dart';

/// Pure presentation for the session-scoped Players review/setup surface.
///
/// Projection polling, slot mutations, navigation, and launch approval stay in
/// the Players status shell. This component owns only the reusable panel contract
/// and receives already-resolved actions from its container.
final class PlayersPanel extends StatelessWidget {
  const PlayersPanel({
    required this.projection,
    required this.profileNamesBySlot,
    required this.actions,
    this.activeProfile,
    this.issueCopy,
    super.key,
  });

  final PlayersProjection projection;
  final Map<int, String?> profileNamesBySlot;
  final List<Widget> actions;
  final LocalProfile? activeProfile;
  final String? issueCopy;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;

    return SafeArea(
      minimum: EdgeInsets.all(layout.lg),
      child: Center(
        child: Container(
          key: const ValueKey<String>('players-panel'),
          constraints: const BoxConstraints(maxWidth: 900, maxHeight: 660),
          padding: EdgeInsets.fromLTRB(
            layout.lg,
            layout.lg,
            layout.lg,
            layout.md,
          ),
          decoration: BoxDecoration(
            color: colors.dialogSurface,
            borderRadius: BorderRadius.circular(layout.dialogRadius),
            border: Border.all(
              color: colors.panelBorder,
              width: layout.hairlineStroke,
            ),
            boxShadow: context.elevation.dialog,
          ),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: <Widget>[
                Text(
                  'PLAYERS',
                  textAlign: TextAlign.center,
                  style: text.eyebrow,
                ),
                if (activeProfile case final profile?) ...<Widget>[
                  SizedBox(height: layout.xs),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: <Widget>[
                      ProfileAvatar(
                        avatarKey: profile.avatarKey,
                        accentColor: Color(profile.accentColor),
                        size: layout.avatarSm,
                      ),
                      SizedBox(width: layout.xs),
                      Flexible(
                        child: Text(
                          'Playing as ${profile.displayName}',
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: text.sectionLabel.copyWith(
                            color: colors.textStrong,
                          ),
                        ),
                      ),
                    ],
                  ),
                ],
                SizedBox(height: layout.xs),
                Text(
                  'Player order is for this session only.',
                  textAlign: TextAlign.center,
                  style: text.metadata.copyWith(color: colors.textMuted),
                ),
                SizedBox(height: layout.md),
                Row(
                  children: <Widget>[
                    for (final slot in projection.slots) ...<Widget>[
                      if (slot.playerSlot > 0) SizedBox(width: layout.xs),
                      Expanded(
                        child: _PlayerSummaryTile(
                          slot: slot,
                          profileName: profileNamesBySlot[slot.playerSlot],
                        ),
                      ),
                    ],
                  ],
                ),
                if (projection.availableControllers.isNotEmpty) ...<Widget>[
                  SizedBox(height: layout.md),
                  AvailableControllersPanel(
                    controllers: projection.availableControllers,
                  ),
                ],
                if (issueCopy case final copy?) ...<Widget>[
                  SizedBox(height: layout.xs),
                  Text(
                    copy,
                    textAlign: TextAlign.center,
                    style: text.metadata.copyWith(color: colors.warning),
                  ),
                ],
                SizedBox(height: layout.md),
                for (
                  var index = 0;
                  index < actions.length;
                  index++
                ) ...<Widget>[
                  if (index > 0) SizedBox(height: layout.xs),
                  actions[index],
                ],
                SizedBox(height: layout.md),
                const Center(
                  child: ConsoleHintBar(
                    muted: true,
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
                        label: 'Back',
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

final class _PlayerSummaryTile extends StatelessWidget {
  const _PlayerSummaryTile({required this.slot, this.profileName});

  final PlayerSlotProjection slot;
  final String? profileName;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;
    final (name, status, color) = switch (slot.state) {
      PlayersSlotState.connected => (
        slot.controller!.name,
        profileName ?? 'Connected',
        colors.connected,
      ),
      PlayersSlotState.reservedExact => (
        slot.claim?.displayName ?? 'Controller',
        slot.playerSlot == 0 || profileName == null
            ? 'Waiting'
            : '$profileName · Waiting',
        colors.textMuted,
      ),
      PlayersSlotState.blockedAmbiguous => (
        slot.claim?.displayName ?? 'Controller',
        profileName == null ? 'Check order' : '$profileName · Check order',
        colors.warning,
      ),
      PlayersSlotState.open => ('—', 'Open', colors.textFaint),
    };
    final textScale = MediaQuery.textScalerOf(context).scale(1);
    final scaleGrowth = (textScale - 1).clamp(0, 1).toDouble();
    final tileHeight =
        layout.playersPanel.seatHeight +
        layout.playersPanel.seatTextScaleGrowth * scaleGrowth;

    return Container(
      key: ValueKey<String>('player-seat-${slot.playerSlot}'),
      height: tileHeight,
      padding: EdgeInsets.all(layout.sm),
      decoration: BoxDecoration(
        color: colors.controlRestFill,
        borderRadius: BorderRadius.circular(layout.controlRadius),
        border: Border.all(
          color: color.withValues(alpha: layout.tint.borderOpacity),
          width: layout.hairlineStroke,
        ),
      ),
      child: Column(
        children: <Widget>[
          Text(
            'P${slot.playerSlot + 1}',
            key: ValueKey<String>('player-seat-number-${slot.playerSlot}'),
            style: text.sectionLabel.copyWith(color: color),
          ),
          SizedBox(height: layout.xxs),
          Text(
            name,
            key: ValueKey<String>('player-seat-name-${slot.playerSlot}'),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: text.sectionLabel.copyWith(color: colors.textStrong),
          ),
          SizedBox(height: layout.xxs),
          Text(
            status,
            key: ValueKey<String>('player-seat-status-${slot.playerSlot}'),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: text.metadata.copyWith(color: colors.textFaint),
          ),
        ],
      ),
    );
  }
}

/// Focusable action row composed into a [PlayersPanel] by its container.
final class PlayersPanelAction extends StatefulWidget {
  const PlayersPanelAction({
    required this.icon,
    required this.label,
    required this.detail,
    required this.onPressed,
    this.autofocus = false,
    super.key,
  });

  final IconData icon;
  final String label;
  final String detail;
  final VoidCallback onPressed;
  final bool autofocus;

  @override
  State<PlayersPanelAction> createState() => _PlayersPanelActionState();
}

final class _PlayersPanelActionState extends State<PlayersPanelAction> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final text = context.text;
    final textScale = MediaQuery.textScalerOf(context).scale(1);
    final scaleGrowth = (textScale - 1).clamp(0, 1).toDouble();
    final actionHeight =
        layout.settings.actionHeight +
        layout.playersPanel.actionTextScaleGrowth * scaleGrowth;

    return Shortcuts(
      shortcuts: const <ShortcutActivator, Intent>{
        SingleActivator(LogicalKeyboardKey.enter): ActivateIntent(),
        SingleActivator(LogicalKeyboardKey.numpadEnter): ActivateIntent(),
        SingleActivator(LogicalKeyboardKey.select): ActivateIntent(),
        SingleActivator(LogicalKeyboardKey.gameButtonA): ActivateIntent(),
      },
      child: Actions(
        actions: <Type, Action<Intent>>{
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              widget.onPressed();
              return null;
            },
          ),
        },
        child: FocusableActionDetector(
          autofocus: widget.autofocus,
          onFocusChange: (focused) => setState(() => _focused = focused),
          mouseCursor: SystemMouseCursors.click,
          child: GestureDetector(
            onTap: widget.onPressed,
            child: AnimatedContainer(
              duration: motion.resolve(context, motion.focus),
              curve: motion.standardCurve,
              height: actionHeight,
              padding: EdgeInsets.symmetric(horizontal: layout.md),
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(layout.controlRadius),
                color: _focused ? colors.focusFill : Colors.transparent,
                border: Border.all(
                  color: _focused ? colors.focusBorder : colors.borderStrong,
                  width: _focused ? layout.focusStroke : layout.hairlineStroke,
                ),
                boxShadow: _focused
                    ? context.elevation.focusGlow
                    : context.elevation.none,
              ),
              child: Row(
                children: <Widget>[
                  Icon(
                    widget.icon,
                    size: layout.iconMd,
                    color: _focused ? colors.focusBorder : colors.textMuted,
                  ),
                  SizedBox(width: layout.sm),
                  Expanded(
                    child: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        Text(
                          widget.label,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: text.sectionLabel.copyWith(
                            color: colors.textStrong,
                          ),
                        ),
                        Text(
                          widget.detail,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: text.metadata.copyWith(
                            color: colors.textMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  Icon(
                    Icons.chevron_right,
                    size: layout.iconMd,
                    color: colors.textFaint,
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
