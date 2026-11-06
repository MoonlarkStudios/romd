import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/players_projection.dart';

import '../../input/claim_ceremony.dart';
import '../theme/console_theme_context.dart';
import 'console_hint_bar.dart';
import 'controller_display_names.dart';

/// Switch-style "Change Order" ceremony: hold **L + R** on each controller in
/// player order — the first chord seats P1, the second P2, and so on. The
/// seating is working state only; **A**/Enter commits it as the whole slot
/// assignment (dormant reservations do not survive a re-run), **B**/Esc
/// cancels without touching stored claims.
///
/// Known v1 simplification: [ActivateIntent] arrives without device
/// attribution (`GamepadNavigator` doesn't say which pad pressed A), so any
/// pad — seated or not — can confirm.
final class ChangeOrderScreen extends StatefulWidget {
  const ChangeOrderScreen({
    required this.slotClaims,
    this.controllerInputProvider = const GamepadsControllerInputProvider(),
    this.profileNamesById = const <String, String>{},
    super.key,
  });

  final SessionControllerSlotClaims slotClaims;
  final ControllerInputProvider controllerInputProvider;
  final Map<String, String> profileNamesById;

  static Future<void> show(
    BuildContext context, {
    required SessionControllerSlotClaims slotClaims,
    required ControllerInputProvider controllerInputProvider,
    Map<String, String> profileNamesById = const <String, String>{},
  }) {
    final motion = context.motion;
    return Navigator.of(context).push<void>(
      PageRouteBuilder<void>(
        opaque: false,
        transitionDuration: motion.resolve(context, motion.routeEnter),
        reverseTransitionDuration: motion.resolve(context, motion.routeExit),
        pageBuilder: (context, animation, secondaryAnimation) =>
            ChangeOrderScreen(
              slotClaims: slotClaims,
              controllerInputProvider: controllerInputProvider,
              profileNamesById: profileNamesById,
            ),
        transitionsBuilder: (context, animation, secondaryAnimation, child) {
          final background = CurvedAnimation(
            parent: animation,
            curve: Interval(0, 0.52, curve: motion.standardCurve),
          );
          final content = CurvedAnimation(
            parent: animation,
            curve: Interval(0.22, 1, curve: motion.emphasizedCurve),
          );
          return Stack(
            fit: StackFit.expand,
            children: <Widget>[
              FadeTransition(
                key: const ValueKey<String>(
                  'change-order-transition-background',
                ),
                opacity: background,
                child: Builder(
                  builder: (context) =>
                      ColoredBox(color: context.consoleColors.dialogSurface),
                ),
              ),
              FadeTransition(
                key: const ValueKey<String>('change-order-transition-content'),
                opacity: content,
                child: ScaleTransition(
                  scale: Tween<double>(
                    begin: motion.interaction.subtleEnterScale,
                    end: 1,
                  ).animate(content),
                  child: child,
                ),
              ),
            ],
          );
        },
      ),
    );
  }

  @override
  State<ChangeOrderScreen> createState() => _ChangeOrderScreenState();
}

final class _ChangeOrderScreenState extends State<ChangeOrderScreen> {
  static const Duration _pollInterval = Duration(seconds: 1);
  static const double _contentMaxWidth = 900;

  final ClaimCeremony _ceremony = ClaimCeremony();
  List<ConnectedGamepad> _pads = const <ConnectedGamepad>[];
  Timer? _pollTimer;
  StreamSubscription<NormalizedGamepadEvent>? _events;
  String? _alreadySeatedId;
  final Map<String, Set<GamepadButton>> _seatedChordButtons = {};
  final Map<String, ControllerSlotClaim> _seatedClaims = {};
  bool _confirming = false;

  @override
  void initState() {
    super.initState();
    _refreshPads();
    _pollTimer = Timer.periodic(_pollInterval, (_) => _refreshPads());
    try {
      _events = widget.controllerInputProvider.events().listen(
        _onEvent,
        onError: (_) {},
      );
    } on Object {
      // No gamepad backend — the ceremony renders but nothing can seat.
    }
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    _events?.cancel();
    super.dispose();
  }

  Future<List<ConnectedGamepad>> _refreshPads() async {
    try {
      final pads = await widget.controllerInputProvider.listGamepads();
      if (mounted) {
        setState(() => _pads = pads);
      }
      return pads;
    } on Object {
      // Keep the last known list.
      return _pads;
    }
  }

  void _onEvent(NormalizedGamepadEvent event) {
    final isBumper =
        event.button == GamepadButton.leftBumper ||
        event.button == GamepadButton.rightBumper;
    final alreadySeated = _ceremony.seatedIds.contains(event.gamepadId);
    if (isBumper && alreadySeated) {
      final pressed = _seatedChordButtons.putIfAbsent(
        event.gamepadId,
        () => <GamepadButton>{},
      );
      if (event.value >= 0.5) {
        pressed.add(event.button!);
        if (pressed.contains(GamepadButton.leftBumper) &&
            pressed.contains(GamepadButton.rightBumper)) {
          setState(() => _alreadySeatedId = event.gamepadId);
        }
      } else {
        pressed.remove(event.button);
      }
    }
    if (!_ceremony.onEvent(event)) {
      return;
    }
    final connectedPad = _padById(event.gamepadId);
    var reconnectedExistingSeat = false;
    if (connectedPad != null) {
      reconnectedExistingSeat = _recordSeatedClaim(
        event.gamepadId,
        connectedPad,
      );
    } else {
      // Chord from a pad that connected since the last poll — resolve its
      // name now instead of dropping the seat behind a stale device list.
      unawaited(_refreshAndSnapshot(event.gamepadId));
    }
    setState(
      () => _alreadySeatedId = reconnectedExistingSeat ? event.gamepadId : null,
    );
  }

  Future<void> _confirm() async {
    if (_confirming) {
      return;
    }
    setState(() => _confirming = true);
    var completed = false;
    try {
      final missingSeatedIds = _ceremony.seatedIds.where(
        (id) => !_pads.any((pad) => pad.id == id),
      );
      if (missingSeatedIds.isNotEmpty) {
        await _refreshPads();
      }
      final byId = <String, ControllerSlotClaim>{
        for (final pad in _pads) pad.id: ControllerSlotClaim.fromGamepad(pad),
      };
      await widget.slotClaims.replaceAllClaims(<ControllerSlotClaim?>[
        for (final id in _ceremony.seatedIds) _seatedClaims[id] ?? byId[id],
      ]);
      if (mounted) {
        await Navigator.of(context).maybePop();
        completed = true;
      }
    } finally {
      if (mounted && !completed) {
        setState(() => _confirming = false);
      }
    }
  }

  ConnectedGamepad? _padById(String id) {
    for (final pad in _pads) {
      if (pad.id == id) {
        return pad;
      }
    }
    return null;
  }

  Future<void> _refreshAndSnapshot(String id) async {
    await _refreshPads();
    final pad = _padById(id);
    if (pad != null) {
      final reconnectedExistingSeat = _recordSeatedClaim(id, pad);
      if (mounted) {
        setState(() => _alreadySeatedId = reconnectedExistingSeat ? id : null);
      }
    }
  }

  bool _recordSeatedClaim(String id, ConnectedGamepad pad) {
    final claim = ControllerSlotClaim.fromGamepad(
      pad,
      localProfileId: _sessionProfileId(pad),
    );
    String? previousId;
    if (claim.hasExactIdentity) {
      for (final entry in _seatedClaims.entries) {
        if (entry.key != id && entry.value.matchesExact(pad.identity)) {
          previousId = entry.key;
          break;
        }
      }
    }
    if (previousId == null) {
      _seatedClaims[id] = claim;
      return false;
    }

    _ceremony.replaceReconnectedId(previousId: previousId, newId: id);
    _seatedClaims.remove(previousId);
    _seatedClaims[id] = claim;
    _seatedChordButtons.remove(previousId);
    return true;
  }

  void _cancel() => Navigator.of(context).maybePop();

  /// Device name for a seated pad, with a `#n` ordinal when the same model
  /// is connected more than once.
  String _displayName(String gamepadId) {
    final names = controllerDisplayNamesById(_pads);
    return names[gamepadId] ?? 'Controller';
  }

  String? _profileName(String gamepadId, int playerSlot) {
    if (playerSlot == 0) {
      return null;
    }
    final pad = _padById(gamepadId);
    if (pad == null) {
      return null;
    }
    return widget.profileNamesById[_sessionProfileId(pad)];
  }

  String? _sessionProfileId(ConnectedGamepad pad) {
    final projection = PlayersProjection.resolve(
      claims: widget.slotClaims.claimDetails,
      devices: _pads,
    );
    for (final slot in projection.slots) {
      if (slot.controller?.id != pad.id) {
        continue;
      }
      return slot.claim?.localProfileId;
    }
    return null;
  }

  String get _progressText {
    final seated = _ceremony.seatedIds;
    final alreadySeatedId = _alreadySeatedId;
    if (alreadySeatedId != null) {
      final player = seated.indexOf(alreadySeatedId) + 1;
      return '${_displayName(alreadySeatedId)} is already Player $player.';
    }
    if (seated.isEmpty) {
      return 'Choose Player 1, or select Ready to clear the joined roster.';
    }
    final latestPlayer = seated.length;
    final latestName = _displayName(seated.last);
    if (latestPlayer == ControllerAssignments.maxSlots) {
      return '$latestName is Player $latestPlayer. All player spots are ready.';
    }
    return '$latestName is Player $latestPlayer. Next: Player ${latestPlayer + 1}.';
  }

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;
    final seated = _ceremony.seatedIds;
    final unseatedPads = _pads
        .where((pad) => !seated.contains(pad.id))
        .toList(growable: false);
    return CallbackShortcuts(
      bindings: <ShortcutActivator, VoidCallback>{
        const SingleActivator(LogicalKeyboardKey.escape): _cancel,
        const SingleActivator(LogicalKeyboardKey.enter): _confirm,
        const SingleActivator(LogicalKeyboardKey.numpadEnter): _confirm,
      },
      child: Scaffold(
        backgroundColor: Colors.transparent,
        body: Actions(
          actions: <Type, Action<Intent>>{
            DismissIntent: CallbackAction<DismissIntent>(
              onInvoke: (_) {
                _cancel();
                return null;
              },
            ),
            ActivateIntent: CallbackAction<ActivateIntent>(
              onInvoke: (_) {
                _confirm();
                return null;
              },
            ),
          },
          child: Focus(
            autofocus: true,
            child: SafeArea(
              child: Stack(
                children: <Widget>[
                  Center(
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(
                        maxWidth: _contentMaxWidth,
                      ),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: <Widget>[
                          Text(
                            'CHANGE PLAYER ORDER',
                            textAlign: TextAlign.center,
                            style: text.eyebrow.copyWith(
                              color: colors.textFaint,
                            ),
                          ),
                          SizedBox(height: layout.lg),
                          const _ChordInstruction(),
                          SizedBox(height: layout.sm),
                          Text(
                            _progressText,
                            textAlign: TextAlign.center,
                            style: text.utilityLabel.copyWith(
                              color: _alreadySeatedId == null
                                  ? colors.textFaint
                                  : colors.connected,
                            ),
                          ),
                          SizedBox(height: layout.lg),
                          Row(
                            children: <Widget>[
                              for (
                                var slot = 0;
                                slot < ControllerAssignments.maxSlots;
                                slot++
                              ) ...<Widget>[
                                if (slot > 0) SizedBox(width: layout.sm),
                                Expanded(
                                  child: _OrderTile(
                                    slot: slot,
                                    name: slot < seated.length
                                        ? _displayName(seated[slot])
                                        : null,
                                    profileName: slot < seated.length
                                        ? _profileName(seated[slot], slot)
                                        : null,
                                  ),
                                ),
                              ],
                            ],
                          ),
                          SizedBox(height: layout.lg),
                          _UnseatedRoster(
                            names: <String>[
                              for (final pad in unseatedPads)
                                _displayName(pad.id),
                            ],
                            hasConnectedPads: _pads.isNotEmpty,
                          ),
                        ],
                      ),
                    ),
                  ),
                  Positioned(
                    left: 0,
                    right: 0,
                    bottom: layout.screenChrome.bottom,
                    child: const Center(
                      child: ConsoleHintBar(
                        muted: true,
                        hints: <ConsoleHint>[
                          ConsoleHint(
                            glyph: ConsoleHintGlyphs.confirm,
                            gamepadGlyph: 'A',
                            label: 'Ready',
                          ),
                          ConsoleHint(
                            glyph: 'Esc',
                            gamepadGlyph: 'B',
                            label: 'Cancel',
                          ),
                        ],
                      ),
                    ),
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

final class _UnseatedRoster extends StatelessWidget {
  const _UnseatedRoster({required this.names, required this.hasConnectedPads});

  final List<String> names;
  final bool hasConnectedPads;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;
    final summary = switch ((names.isEmpty, hasConnectedPads)) {
      (true, true) => 'All connected controllers are seated.',
      (true, false) => 'No controllers connected.',
      (false, _) => names.join('  •  '),
    };
    return Column(
      children: <Widget>[
        Text(
          'AVAILABLE CONTROLLERS',
          style: text.metadataStrong.copyWith(color: colors.textDisabled),
        ),
        SizedBox(height: layout.xs),
        Text(
          summary,
          maxLines: 2,
          textAlign: TextAlign.center,
          overflow: TextOverflow.ellipsis,
          style: text.utilityLabel.copyWith(color: colors.textFaint),
        ),
      ],
    );
  }
}

/// "Hold [L] + [R] on each controller in player order."
final class _ChordInstruction extends StatelessWidget {
  const _ChordInstruction();

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: <Widget>[
        const _InstructionText('Hold'),
        SizedBox(width: layout.xs),
        const _BumperCap('L'),
        SizedBox(width: layout.xs),
        const _InstructionText('+'),
        SizedBox(width: layout.xs),
        const _BumperCap('R'),
        SizedBox(width: layout.xs),
        const Flexible(
          child: _InstructionText('on each controller in player order.'),
        ),
      ],
    );
  }
}

final class _InstructionText extends StatelessWidget {
  const _InstructionText(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Text(
    text,
    style: context.text.action.copyWith(
      color: context.consoleColors.textStrong,
    ),
  );
}

final class _BumperCap extends StatelessWidget {
  const _BumperCap(this.label);

  final String label;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(layout.chipRadius),
        color: colors.keycapSurface,
        border: Border.all(
          color: colors.keycapBorder,
          width: layout.hairlineStroke,
        ),
      ),
      child: Padding(
        padding: EdgeInsets.symmetric(
          horizontal: layout.sm,
          vertical: layout.xxs,
        ),
        child: Text(
          label,
          style: context.text.sectionLabel.copyWith(
            color: colors.keycapForeground,
          ),
        ),
      ),
    );
  }
}

final class _OrderTile extends StatelessWidget {
  const _OrderTile({required this.slot, this.name, this.profileName});

  final int slot;
  final String? name;
  final String? profileName;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final text = context.text;
    final seated = name != null;
    return AnimatedContainer(
      duration: motion.resolve(context, motion.selection),
      curve: motion.standardCurve,
      height: context.layout.playersPanel.claimTileHeight,
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(layout.panelRadius),
        color: seated ? colors.selectionFill : colors.panelSurface,
        border: Border.all(
          color: seated ? colors.selectionBorder : colors.panelBorder,
          width: seated ? layout.focusStroke : layout.hairlineStroke,
        ),
      ),
      child: seated
          ? Padding(
              padding: layout.panelPadding,
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                children: <Widget>[
                  Text(
                    'P${slot + 1}',
                    style: text.sectionHeading.copyWith(
                      color: colors.connected,
                    ),
                  ),
                  SizedBox(height: layout.sm),
                  Text(
                    name!,
                    maxLines: profileName == null ? 2 : 1,
                    textAlign: TextAlign.center,
                    overflow: TextOverflow.ellipsis,
                    style: text.sectionLabel.copyWith(color: colors.textStrong),
                  ),
                  if (profileName case final profile?) ...<Widget>[
                    SizedBox(height: layout.xs),
                    Text(
                      profile,
                      maxLines: 1,
                      textAlign: TextAlign.center,
                      overflow: TextOverflow.ellipsis,
                      style: text.utilityLabel.copyWith(
                        color: colors.textMuted,
                      ),
                    ),
                  ],
                ],
              ),
            )
          : Center(
              child: Text(
                '${slot + 1}',
                style: text.hero.copyWith(color: colors.textDisabled),
              ),
            ),
    );
  }
}
