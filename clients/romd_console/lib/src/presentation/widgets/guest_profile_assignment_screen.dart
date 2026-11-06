import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/players_projection.dart';

import '../theme/console_theme_context.dart';
import 'profile_avatar.dart';

Future<GuestProfileChoice?> showWhoIsUsingController(
  BuildContext context, {
  required int playerNumber,
  required List<LocalProfile> profiles,
  required String controllerDisplayName,
  String? selectedProfileId,
}) => showDialog<GuestProfileChoice>(
  context: context,
  barrierColor: context.consoleColors.scrim,
  builder: (context) => _GuestProfilePicker(
    playerNumber: playerNumber,
    profiles: profiles,
    controllerDisplayName: controllerDisplayName,
    selectedProfileId: selectedProfileId,
  ),
);

/// Session-only local-profile assignment for P2-P4.
///
/// This surface reads local profile names but never changes the active profile,
/// unlocks a library, or writes controller affinity. Anonymous guest is always
/// available.
final class GuestProfileAssignmentScreen extends StatelessWidget {
  const GuestProfileAssignmentScreen({
    required this.projectionListenable,
    required this.readProjection,
    required this.refreshProjection,
    required this.slotClaims,
    required this.profiles,
    required this.activeProfile,
    super.key,
  });

  final Listenable projectionListenable;
  final PlayersProjection Function() readProjection;
  final Future<void> Function() refreshProjection;
  final SessionControllerSlotClaims slotClaims;
  final List<LocalProfile> profiles;
  final LocalProfile activeProfile;

  Future<void> _chooseProfile(
    BuildContext context,
    PlayerSlotProjection slot,
  ) async {
    final assignedElsewhere = <String>{};
    for (final other in readProjection().slots) {
      final profileId = other.claim?.localProfileId;
      if (other.playerSlot != slot.playerSlot && profileId != null) {
        assignedElsewhere.add(profileId);
      }
    }
    final choice = await showWhoIsUsingController(
      context,
      playerNumber: slot.playerSlot + 1,
      profiles: profiles
          .where(
            (profile) =>
                profile.id != activeProfile.id &&
                !assignedElsewhere.contains(profile.id),
          )
          .toList(growable: false),
      controllerDisplayName:
          slot.controller?.name ?? slot.claim?.displayName ?? 'Controller',
      selectedProfileId: slot.claim?.localProfileId,
    );
    if (choice == null || !context.mounted) {
      return;
    }
    final claim =
        slot.claim ??
        (slot.controller == null
            ? null
            : ControllerSlotClaim.fromGamepad(slot.controller!));
    if (claim == null) {
      return;
    }
    await slotClaims.assignGuestProfile(
      playerSlot: slot.playerSlot,
      claim: claim,
      localProfileId: choice.localProfileId,
    );
    await refreshProjection();
  }

  @override
  Widget build(BuildContext context) => CallbackShortcuts(
    bindings: <ShortcutActivator, VoidCallback>{
      const SingleActivator(LogicalKeyboardKey.escape): () =>
          Navigator.of(context).maybePop(),
    },
    child: Actions(
      actions: <Type, Action<Intent>>{
        DismissIntent: CallbackAction<DismissIntent>(
          onInvoke: (_) {
            Navigator.of(context).maybePop();
            return null;
          },
        ),
      },
      child: Scaffold(
        backgroundColor: context.theme.scaffoldBackgroundColor,
        appBar: AppBar(
          title: const Text('Guest profiles'),
          backgroundColor: Colors.transparent,
        ),
        body: SafeArea(
          minimum: EdgeInsets.all(context.layout.lg),
          child: AnimatedBuilder(
            animation: projectionListenable,
            builder: (context, _) {
              final guests = readProjection().slots
                  .where(
                    (slot) =>
                        slot.playerSlot > 0 &&
                        (slot.controller != null || slot.claim != null),
                  )
                  .toList(growable: false);
              if (guests.isEmpty) {
                return Center(
                  child: Text(
                    'Add or seat another controller first.',
                    style: context.text.action.copyWith(
                      color: context.consoleColors.textMuted,
                    ),
                  ),
                );
              }
              final names = <String, String>{
                for (final profile in profiles) profile.id: profile.displayName,
              };
              return SingleChildScrollView(
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 760),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: <Widget>[
                        Text(
                          'Choose who is using each guest controller. This lasts '
                          'only for this session and does not change the library.',
                          textAlign: TextAlign.center,
                          style: context.text.action.copyWith(
                            color: context.consoleColors.textMuted,
                          ),
                        ),
                        SizedBox(height: context.layout.lg),
                        for (final (index, slot) in guests.indexed) ...<Widget>[
                          _GuestSeatAction(
                            autofocus: index == 0,
                            playerNumber: slot.playerSlot + 1,
                            controllerName:
                                slot.controller?.name ??
                                slot.claim?.displayName ??
                                'Controller',
                            profileName:
                                names[slot.claim?.localProfileId] ??
                                'Anonymous guest',
                            onPressed: () => _chooseProfile(context, slot),
                          ),
                          if (index < guests.length - 1)
                            SizedBox(height: context.layout.sm),
                        ],
                      ],
                    ),
                  ),
                ),
              );
            },
          ),
        ),
      ),
    ),
  );
}

final class GuestProfileChoice {
  const GuestProfileChoice(this.localProfileId);

  final String? localProfileId;
}

final class _GuestProfilePicker extends StatelessWidget {
  const _GuestProfilePicker({
    required this.playerNumber,
    required this.profiles,
    required this.selectedProfileId,
    required this.controllerDisplayName,
  });

  final int playerNumber;
  final List<LocalProfile> profiles;
  final String? selectedProfileId;
  final String controllerDisplayName;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return CallbackShortcuts(
      bindings: <ShortcutActivator, VoidCallback>{
        const SingleActivator(LogicalKeyboardKey.escape): () =>
            Navigator.of(context).maybePop(),
      },
      child: Actions(
        actions: <Type, Action<Intent>>{
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              Navigator.of(context).maybePop();
              return null;
            },
          ),
        },
        child: Dialog(
          backgroundColor: Colors.transparent,
          insetPadding: EdgeInsets.all(layout.lg),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 680, maxHeight: 620),
            child: DecoratedBox(
              decoration: BoxDecoration(
                color: colors.dialogSurface,
                borderRadius: BorderRadius.circular(layout.dialogRadius),
                border: Border.all(
                  color: colors.panelBorder,
                  width: layout.hairlineStroke,
                ),
                boxShadow: context.elevation.dialog,
              ),
              child: Padding(
                padding: EdgeInsets.all(layout.xl),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: <Widget>[
                    Text(
                      "Who's using this controller?",
                      style: context.text.pageHeading.copyWith(
                        color: colors.textStrong,
                      ),
                    ),
                    SizedBox(height: layout.xs),
                    Row(
                      children: <Widget>[
                        Icon(
                          Icons.sports_esports_outlined,
                          color: colors.iconStrong,
                        ),
                        SizedBox(width: layout.xs),
                        Expanded(
                          child: Text(
                            controllerDisplayName,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: context.text.action.copyWith(
                              color: colors.textStrong,
                            ),
                          ),
                        ),
                      ],
                    ),
                    SizedBox(height: layout.sm),
                    Text(
                      'Joining as Player $playerNumber · This choice lasts for this '
                      'session and affects identity and controls. It does not change '
                      'the current library.',
                      style: context.text.body.copyWith(
                        color: colors.textMuted,
                      ),
                    ),
                    SizedBox(height: layout.lg),
                    Flexible(
                      child: SingleChildScrollView(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: <Widget>[
                            _ProfileChoiceAction(
                              label: 'Guest',
                              detail:
                                  'Uses the primary profile’s controls for this session.',
                              selected: selectedProfileId == null,
                              autofocus: true,
                              onPressed: () => Navigator.of(
                                context,
                              ).pop(const GuestProfileChoice(null)),
                            ),
                            for (final profile in profiles) ...<Widget>[
                              SizedBox(height: layout.xs),
                              _ProfileChoiceAction(
                                label: profile.displayName,
                                detail:
                                    'Uses ${profile.displayName}’s saved controls for this session.',
                                profile: profile,
                                selected: selectedProfileId == profile.id,
                                onPressed: () => Navigator.of(
                                  context,
                                ).pop(GuestProfileChoice(profile.id)),
                              ),
                            ],
                          ],
                        ),
                      ),
                    ),
                    SizedBox(height: layout.md),
                    Text(
                      'A  Select     B  Cancel',
                      style: context.text.metadata.copyWith(
                        color: colors.textMuted,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

final class _GuestSeatAction extends StatelessWidget {
  const _GuestSeatAction({
    required this.playerNumber,
    required this.controllerName,
    required this.profileName,
    required this.onPressed,
    this.autofocus = false,
  });

  final int playerNumber;
  final String controllerName;
  final String profileName;
  final VoidCallback onPressed;
  final bool autofocus;

  @override
  Widget build(BuildContext context) => _FocusableAction(
    autofocus: autofocus,
    onPressed: onPressed,
    child: ListTile(
      leading: CircleAvatar(child: Text('$playerNumber')),
      title: Text(
        'P$playerNumber · $controllerName',
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      subtitle: Text(profileName, maxLines: 1, overflow: TextOverflow.ellipsis),
      trailing: const Icon(Icons.chevron_right),
    ),
  );
}

final class _ProfileChoiceAction extends StatelessWidget {
  const _ProfileChoiceAction({
    required this.label,
    required this.selected,
    required this.detail,
    required this.onPressed,
    this.profile,
    this.autofocus = false,
  });

  final String label;
  final bool selected;
  final String detail;
  final VoidCallback onPressed;
  final LocalProfile? profile;
  final bool autofocus;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return _FocusableAction(
      autofocus: autofocus,
      onPressed: onPressed,
      child: ListTile(
        leading: profile == null
            ? CircleAvatar(
                backgroundColor: colors.controlRestFill,
                child: Icon(Icons.person_outline, color: colors.iconStrong),
              )
            : ProfileAvatar(
                avatarKey: profile!.avatarKey,
                accentColor: Color(profile!.accentColor),
                size: layout.avatarSm,
              ),
        title: Text(label),
        subtitle: Text(detail),
        trailing: selected
            ? Icon(Icons.check, color: context.colors.primary)
            : null,
      ),
    );
  }
}

final class _FocusableAction extends StatefulWidget {
  const _FocusableAction({
    required this.child,
    required this.onPressed,
    required this.autofocus,
  });

  final Widget child;
  final VoidCallback onPressed;
  final bool autofocus;

  @override
  State<_FocusableAction> createState() => _FocusableActionState();
}

final class _FocusableActionState extends State<_FocusableAction> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    return Shortcuts(
      shortcuts: const <ShortcutActivator, Intent>{
        SingleActivator(LogicalKeyboardKey.enter): ActivateIntent(),
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
          child: GestureDetector(
            onTap: widget.onPressed,
            child: AnimatedContainer(
              duration: motion.resolve(context, motion.focus),
              curve: motion.standardCurve,
              decoration: BoxDecoration(
                color: _focused ? colors.focusFill : colors.controlRestFill,
                borderRadius: BorderRadius.circular(layout.controlRadius),
                border: Border.all(
                  color: _focused ? colors.focusBorder : colors.borderStrong,
                  width: _focused ? layout.focusStroke : layout.hairlineStroke,
                ),
              ),
              child: widget.child,
            ),
          ),
        ),
      ),
    );
  }
}
