import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../domain/local_profile.dart';
import '../profile_identity_palette.dart';
import '../theme/console_theme_context.dart';
import 'console_action_button.dart';
import 'console_ambient_background.dart';
import 'console_blinking_caret.dart';
import 'console_circle_button.dart';
import 'console_hint_bar.dart';
import 'on_screen_keyboard.dart';
import 'profile_avatar.dart';

/// Full-screen, controller-first profile creation: a display name, a ROMD
/// server origin, a procedural avatar, and an accent color. Returns a
/// [CreateLocalProfileRequest] on confirm (pop), or null on cancel.
///
/// Name entry drives an on-screen keyboard so a gamepad-only player can finish
/// first-run without a hardware keyboard — raw keystrokes still type, mirroring
/// [SearchScreen]. We avoid a [TextField] for the same reason it does: it would
/// swallow the D-pad. It's a full screen rather than a modal because the keyboard
/// plus pickers don't fit a dialog at TV (720p) sizes.
final class CreateProfileScreen extends StatefulWidget {
  const CreateProfileScreen({required this.initialRomdServerOrigin, super.key});

  final Uri initialRomdServerOrigin;

  static Future<CreateLocalProfileRequest?> show(
    BuildContext context, {
    required Uri initialRomdServerOrigin,
  }) => Navigator.of(context).push<CreateLocalProfileRequest>(
    MaterialPageRoute<CreateLocalProfileRequest>(
      builder: (_) =>
          CreateProfileScreen(initialRomdServerOrigin: initialRomdServerOrigin),
    ),
  );

  @override
  State<CreateProfileScreen> createState() => _CreateProfileScreenState();
}

enum _ProfileInputField { name, serverOrigin }

const int _kMaxNameLength = 24;
const int _kMaxServerOriginLength = 128;
const double _kPreviewColumnWidth = 340;

final class _CreateProfileScreenState extends State<CreateProfileScreen> {
  final FocusNode _keyboardFocus = FocusNode(debugLabel: 'profile-keyboard-a');
  final FocusNode _createButtonFocus = FocusNode(debugLabel: 'profile-create');
  String _name = '';
  late String _serverOriginText = widget.initialRomdServerOrigin.toString();
  String _avatarKey = ProfileAvatarStyle.values.first.name;
  Color _accent = ProfileIdentityPalette.accents.first;
  _ProfileInputField _activeField = _ProfileInputField.name;

  Uri? get _serverOrigin => RomdServerOrigins.tryParse(_serverOriginText);

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) {
        _keyboardFocus.requestFocus();
      }
    });
  }

  @override
  void dispose() {
    _keyboardFocus.dispose();
    _createButtonFocus.dispose();
    super.dispose();
  }

  // The server is optional: a blank origin makes a purely local profile that
  // can attach a ROMD server later. Only a non-blank, malformed origin errors.
  String? get _serverOriginError {
    if (_serverOriginText.trim().isEmpty) {
      return null;
    }
    if (_serverOrigin == null) {
      return 'Use an http(s) origin without a path.';
    }
    return null;
  }

  bool get _canCreate => _name.trim().isNotEmpty && _serverOriginError == null;

  // On-screen keys are caseless; mirror TextCapitalization.words so the name
  // reads "Ada Lin", not "ada lin" or "ADA LIN".
  void _appendKey(String char) {
    if (_activeField == _ProfileInputField.serverOrigin) {
      _appendServerOriginKey(char);
      return;
    }
    _appendNameKey(char);
  }

  void _appendNameKey(String char) {
    if (char == ' ') {
      if (_name.isEmpty || _name.endsWith(' ')) {
        return; // no leading or doubled spaces
      }
      _setName('$_name ');
      return;
    }
    final atWordStart = _name.isEmpty || _name.endsWith(' ');
    _setName(_name + (atWordStart ? char.toUpperCase() : char.toLowerCase()));
  }

  void _appendServerOriginKey(String char) {
    if (char == ' ') {
      return;
    }
    _setServerOriginText(_serverOriginText + char.toLowerCase());
  }

  // Hardware typing preserves the user's own shift choices verbatim.
  void _appendRaw(String char) {
    switch (_activeField) {
      case _ProfileInputField.name:
        _setName(_name + char);
      case _ProfileInputField.serverOrigin:
        if (char != ' ') {
          _setServerOriginText(_serverOriginText + char);
        }
    }
  }

  void _backspace() {
    switch (_activeField) {
      case _ProfileInputField.name:
        if (_name.isNotEmpty) {
          _setName(_name.substring(0, _name.length - 1));
        }
      case _ProfileInputField.serverOrigin:
        if (_serverOriginText.isNotEmpty) {
          _setServerOriginText(
            _serverOriginText.substring(0, _serverOriginText.length - 1),
          );
        }
    }
  }

  void _clear() {
    switch (_activeField) {
      case _ProfileInputField.name:
        _setName('');
      case _ProfileInputField.serverOrigin:
        _setServerOriginText('');
    }
  }

  void _setName(String name) {
    if (name.length > _kMaxNameLength) {
      return;
    }
    setState(() => _name = name);
  }

  void _setServerOriginText(String value) {
    if (value.length > _kMaxServerOriginLength) {
      return;
    }
    setState(() => _serverOriginText = value);
  }

  void _setActiveField(_ProfileInputField field) {
    if (_activeField != field) {
      setState(() => _activeField = field);
    }
  }

  // Printable hardware characters type into the name; control keys (arrows,
  // Enter, Esc, Tab) fall through so focus traversal, activation, and dismiss
  // still work. Enter/Space stay free to activate the focused control.
  KeyEventResult _onKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent && event is! KeyRepeatEvent) {
      return KeyEventResult.ignored;
    }
    if (event.logicalKey == LogicalKeyboardKey.backspace) {
      _backspace();
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.space) {
      // Consume Space so the global Space→Activate shortcut can't fire the
      // focused button; type it (guarded against leading/doubled spaces).
      _appendKey(' ');
      return KeyEventResult.handled;
    }
    final char = event.character;
    if (char != null && char.length == 1) {
      final code = char.codeUnitAt(0);
      if (code > 0x20 && code != 0x7f) {
        _appendRaw(char);
        return KeyEventResult.handled;
      }
    }
    return KeyEventResult.ignored;
  }

  void _submit() {
    if (!_canCreate) {
      return;
    }
    Navigator.of(context).pop(
      CreateLocalProfileRequest(
        displayName: _name.trim(),
        avatarKey: _avatarKey,
        accentColor: _accent.toARGB32(),
        romdServerOrigin: _serverOriginText.trim().isEmpty
            ? null
            : _serverOrigin,
      ),
    );
  }

  void _cancel() => Navigator.of(context).pop();

  @override
  Widget build(BuildContext context) => CallbackShortcuts(
    bindings: <ShortcutActivator, VoidCallback>{
      const SingleActivator(LogicalKeyboardKey.escape): _cancel,
    },
    child: Actions(
      actions: <Type, Action<Intent>>{
        DismissIntent: CallbackAction<DismissIntent>(
          onInvoke: (_) {
            _cancel();
            return null;
          },
        ),
      },
      child: Focus(
        canRequestFocus: false,
        onKeyEvent: _onKey,
        child: Scaffold(
          key: const ValueKey<String>('create-profile-screen'),
          backgroundColor: context.theme.scaffoldBackgroundColor,
          body: Stack(
            children: <Widget>[
              const Positioned.fill(
                child: ConsoleAmbientBackground(dimmed: true),
              ),
              SafeArea(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: <Widget>[
                    Padding(
                      padding: EdgeInsets.fromLTRB(
                        context.layout.screenGutter,
                        context.layout.lg,
                        context.layout.screenGutter,
                        context.layout.md,
                      ),
                      child: _Header(onBack: _cancel),
                    ),
                    Expanded(
                      child: Padding(
                        padding: EdgeInsets.symmetric(
                          horizontal: context.layout.screenGutter,
                        ),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: <Widget>[
                            // Left: live preview, ROMD server, and look
                            // choices.
                            SizedBox(
                              width: _kPreviewColumnWidth,
                              child: FocusTraversalGroup(
                                policy: ReadingOrderTraversalPolicy(),
                                child: SingleChildScrollView(
                                  child: _PreviewColumn(
                                    avatarKey: _avatarKey,
                                    serverOriginText: _serverOriginText,
                                    serverOriginError: _serverOriginError,
                                    activeField: _activeField,
                                    accent: _accent,
                                    onActiveFieldChanged: _setActiveField,
                                    onAvatar: (key) =>
                                        setState(() => _avatarKey = key),
                                    onAccent: (color) =>
                                        setState(() => _accent = color),
                                  ),
                                ),
                              ),
                            ),
                            SizedBox(width: context.layout.xxxl),
                            // Right: the name + keyboard, with Create / Cancel
                            // directly beneath so Down-traversal from the keys
                            // always reaches them (no cross-column gamble).
                            Expanded(
                              child: FocusTraversalGroup(
                                policy: ReadingOrderTraversalPolicy(),
                                child: SingleChildScrollView(
                                  child: _ComposeColumn(
                                    name: _name,
                                    activeField: _activeField,
                                    canCreate: _canCreate,
                                    onChar: _appendKey,
                                    onBackspace: _backspace,
                                    onClear: _clear,
                                    keyboardFocusNode: _keyboardFocus,
                                    createButtonFocusNode: _createButtonFocus,
                                    onActiveFieldChanged: _setActiveField,
                                    onCreate: _submit,
                                    onCancel: _cancel,
                                  ),
                                ),
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                    const ConsoleFooterBar(
                      hints: <ConsoleHint>[
                        ConsoleHint.adaptive(
                          keyboardGlyph: 'Type',
                          gamepadGlyph: 'A',
                          label: 'Type',
                        ),
                        ConsoleHint(
                          glyph: ConsoleHintGlyphs.navigate,
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

final class _Header extends StatelessWidget {
  const _Header({required this.onBack});

  final VoidCallback onBack;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final text = context.text;
    final colors = context.consoleColors;

    return Row(
      children: <Widget>[
        ConsoleCircleButton(onTap: onBack),
        SizedBox(width: layout.lg),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Text('Create profile', style: text.sectionHeading),
              SizedBox(height: layout.xxs),
              Text(
                'NAME IT · PICK A LOOK',
                style: text.utilityLabel.copyWith(color: colors.textFaint),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// Left column: a live identity preview plus the avatar and colour pickers — the
/// "look" choices, kept beside the preview that reflects them.
final class _PreviewColumn extends StatelessWidget {
  const _PreviewColumn({
    required this.avatarKey,
    required this.serverOriginText,
    required this.serverOriginError,
    required this.activeField,
    required this.accent,
    required this.onActiveFieldChanged,
    required this.onAvatar,
    required this.onAccent,
  });

  final String avatarKey;
  final String serverOriginText;
  final String? serverOriginError;
  final _ProfileInputField activeField;
  final Color accent;
  final ValueChanged<_ProfileInputField> onActiveFieldChanged;
  final ValueChanged<String> onAvatar;
  final ValueChanged<Color> onAccent;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        SizedBox(height: layout.xs),
        Center(
          child: ProfileAvatar(
            avatarKey: avatarKey,
            accentColor: accent,
            size: layout.avatarLg,
          ),
        ),
        SizedBox(height: layout.sectionGap),
        const _SectionLabel('Avatar'),
        SizedBox(height: layout.sm),
        Wrap(
          spacing: layout.sm,
          runSpacing: layout.sm,
          children: <Widget>[
            for (final style in ProfileAvatarStyle.values)
              _AvatarOption(
                avatarKey: style.name,
                accent: accent,
                selected: avatarKey == style.name,
                onSelected: () => onAvatar(style.name),
              ),
          ],
        ),
        SizedBox(height: layout.lg),
        const _SectionLabel('Color'),
        SizedBox(height: layout.sm),
        Wrap(
          spacing: layout.sm,
          runSpacing: layout.sm,
          children: <Widget>[
            for (final color in ProfileIdentityPalette.accents)
              _ColorOption(
                color: color,
                selected: accent.toARGB32() == color.toARGB32(),
                onSelected: () => onAccent(color),
              ),
          ],
        ),
        SizedBox(height: layout.lg),
        _InputField(
          label: 'ROMD server (optional)',
          value: serverOriginText,
          placeholder: RomdServerOrigins.defaultValue,
          error: serverOriginError,
          monospace: true,
          active: activeField == _ProfileInputField.serverOrigin,
          onFocused: () =>
              onActiveFieldChanged(_ProfileInputField.serverOrigin),
        ),
        SizedBox(height: layout.xs),
      ],
    );
  }
}

/// Right column: the name display, the on-screen keyboard, and Create / Cancel
/// stacked directly beneath it. Confirm sits below the keyboard so a controller
/// reaches it by pressing Down out of the key grid — never a cross-column jump.
final class _ComposeColumn extends StatelessWidget {
  const _ComposeColumn({
    required this.name,
    required this.activeField,
    required this.canCreate,
    required this.onChar,
    required this.onBackspace,
    required this.onClear,
    required this.keyboardFocusNode,
    required this.createButtonFocusNode,
    required this.onActiveFieldChanged,
    required this.onCreate,
    required this.onCancel,
  });

  final String name;
  final _ProfileInputField activeField;
  final bool canCreate;
  final ValueChanged<String> onChar;
  final VoidCallback onBackspace;
  final VoidCallback onClear;
  final FocusNode keyboardFocusNode;
  final FocusNode createButtonFocusNode;
  final ValueChanged<_ProfileInputField> onActiveFieldChanged;
  final VoidCallback onCreate;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    mainAxisSize: MainAxisSize.min,
    children: <Widget>[
      SizedBox(height: context.layout.xs),
      _InputField(
        label: 'Profile name',
        value: name,
        placeholder: 'Enter a name',
        active: activeField == _ProfileInputField.name,
        onFocused: () => onActiveFieldChanged(_ProfileInputField.name),
      ),
      SizedBox(height: context.layout.md),
      Align(
        alignment: Alignment.centerLeft,
        child: OnScreenKeyboard(
          layout: activeField == _ProfileInputField.serverOrigin
              ? OnScreenKeyboardLayout.url
              : OnScreenKeyboardLayout.text,
          initialFocusNode: keyboardFocusNode,
          onMoveBelow: createButtonFocusNode.requestFocus,
          onChar: onChar,
          onBackspace: onBackspace,
          onClear: onClear,
        ),
      ),
      SizedBox(height: context.layout.md),
      SizedBox(
        height: context.layout.settings.compactActionHeight,
        child: ConsoleActionButton(
          key: const ValueKey<String>('create-profile-create-button'),
          label: 'Create',
          kind: ConsoleActionKind.primary,
          icon: Icons.check,
          focusNode: createButtonFocusNode,
          onPressed: canCreate ? onCreate : null,
        ),
      ),
      SizedBox(height: context.layout.sm),
      SizedBox(
        height: context.layout.settings.compactActionHeight,
        child: ConsoleActionButton(
          label: 'Cancel',
          kind: ConsoleActionKind.quiet,
          icon: Icons.close,
          onPressed: onCancel,
        ),
      ),
    ],
  );
}

/// A styled display (not a [TextField]) with a placeholder and blinking caret,
/// fed by the on-screen keyboard and raw keystrokes.
final class _InputField extends StatefulWidget {
  const _InputField({
    required this.label,
    required this.value,
    required this.placeholder,
    required this.active,
    required this.onFocused,
    this.error,
    this.monospace = false,
  });

  final String label;
  final String value;
  final String placeholder;
  final String? error;
  final bool active;
  final bool monospace;
  final VoidCallback onFocused;

  @override
  State<_InputField> createState() => _InputFieldState();
}

final class _InputFieldState extends State<_InputField> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;
    final motion = context.motion;
    final empty = widget.value.isEmpty;
    final highlighted = widget.active || _focused;
    final error = widget.error;

    return Semantics(
      label: widget.label,
      value: empty ? 'empty' : widget.value,
      child: FocusableActionDetector(
        mouseCursor: SystemMouseCursors.text,
        onFocusChange: (focused) {
          setState(() => _focused = focused);
          if (focused) {
            widget.onFocused();
          }
        },
        child: GestureDetector(
          onTap: widget.onFocused,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Padding(
                padding: EdgeInsets.only(left: layout.xxs, bottom: layout.xs),
                child: Text(
                  widget.label.toUpperCase(),
                  style: text.utilityLabel.copyWith(color: colors.textFaint),
                ),
              ),
              AnimatedContainer(
                key: ValueKey<String>('create-profile-${widget.label}-field'),
                duration: motion.resolve(context, motion.focus),
                curve: motion.standardCurve,
                padding: EdgeInsets.symmetric(
                  horizontal: layout.md,
                  vertical: layout.sm,
                ),
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(layout.controlRadius),
                  color: colors.controlRestFill,
                  border: Border.all(
                    color: error != null
                        ? context.colors.error
                        : highlighted
                        ? colors.focusBorder
                        : colors.borderStrong,
                    width: highlighted
                        ? layout.focusStroke
                        : layout.hairlineStroke,
                  ),
                ),
                child: Row(
                  children: <Widget>[
                    Flexible(
                      child: Text(
                        empty ? widget.placeholder : widget.value,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style:
                            (widget.monospace
                                    ? text.sectionLabel
                                    : text.cardTitle)
                                .copyWith(
                                  color: empty
                                      ? colors.textFaint
                                      : colors.textStrong,
                                ),
                      ),
                    ),
                    if (widget.active)
                      ConsoleBlinkingCaret(
                        height: layout.iconMd,
                        margin: EdgeInsets.only(left: layout.xxs),
                      ),
                  ],
                ),
              ),
              if (error != null) ...<Widget>[
                SizedBox(height: layout.xs),
                Text(
                  error,
                  style: text.metadata.copyWith(color: context.colors.error),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

final class _SectionLabel extends StatelessWidget {
  const _SectionLabel(this.text);

  final String text;

  @override
  Widget build(BuildContext context) =>
      Text(text.toUpperCase(), style: context.text.eyebrow);
}

final class _AvatarOption extends StatelessWidget {
  const _AvatarOption({
    required this.avatarKey,
    required this.accent,
    required this.selected,
    required this.onSelected,
  });

  final String avatarKey;
  final Color accent;
  final bool selected;
  final VoidCallback onSelected;

  @override
  Widget build(BuildContext context) => _SelectableRing(
    selected: selected,
    accent: accent,
    onSelected: onSelected,
    child: ProfileAvatar(
      avatarKey: avatarKey,
      accentColor: accent,
      size: context.layout.avatarSm,
    ),
  );
}

final class _ColorOption extends StatelessWidget {
  const _ColorOption({
    required this.color,
    required this.selected,
    required this.onSelected,
  });

  final Color color;
  final bool selected;
  final VoidCallback onSelected;

  @override
  Widget build(BuildContext context) => _SelectableRing(
    selected: selected,
    accent: color,
    onSelected: onSelected,
    child: Container(
      width: context.layout.avatarSm,
      height: context.layout.avatarSm,
      decoration: BoxDecoration(shape: BoxShape.circle, color: color),
      child: selected
          ? Icon(
              Icons.check,
              color: ProfileIdentityPalette.foregroundFor(color),
              size: context.layout.iconMd,
            )
          : null,
    ),
  );
}

/// A focusable circular option with a selection/focus ring. Activates on tap or
/// Enter / select / gamepad A when focused.
final class _SelectableRing extends StatefulWidget {
  const _SelectableRing({
    required this.selected,
    required this.accent,
    required this.onSelected,
    required this.child,
  });

  final bool selected;
  final Color accent;
  final VoidCallback onSelected;
  final Widget child;

  @override
  State<_SelectableRing> createState() => _SelectableRingState();
}

final class _SelectableRingState extends State<_SelectableRing> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final highlight = widget.selected || _focused;

    return Shortcuts(
      shortcuts: const <ShortcutActivator, Intent>{
        SingleActivator(LogicalKeyboardKey.enter): ActivateIntent(),
        SingleActivator(LogicalKeyboardKey.select): ActivateIntent(),
        SingleActivator(LogicalKeyboardKey.gameButtonA): ActivateIntent(),
      },
      child: FocusableActionDetector(
        mouseCursor: SystemMouseCursors.click,
        actions: <Type, Action<Intent>>{
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              widget.onSelected();
              return null;
            },
          ),
        },
        onFocusChange: (focused) => setState(() => _focused = focused),
        child: GestureDetector(
          onTap: widget.onSelected,
          child: AnimatedContainer(
            duration: motion.resolve(context, motion.focus),
            curve: motion.standardCurve,
            padding: EdgeInsets.all(layout.xxs),
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              border: Border.all(
                color: _focused
                    ? colors.focusBorder
                    : widget.selected
                    ? widget.accent
                    : colors.hairline,
                width: highlight
                    ? layout.focusRingStroke
                    : layout.hairlineStroke,
              ),
            ),
            child: widget.child,
          ),
        ),
      ),
    );
  }
}
