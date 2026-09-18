import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../domain/local_profile.dart';
import 'theme/console_theme_context.dart';
import 'widgets/console_action_button.dart';
import 'widgets/console_ambient_background.dart';
import 'widgets/console_blinking_caret.dart';
import 'widgets/console_circle_button.dart';
import 'widgets/console_hint_bar.dart';
import 'widgets/on_screen_keyboard.dart';

final class RomdServerOriginScreen extends StatefulWidget {
  const RomdServerOriginScreen({required this.initialOrigin, super.key});

  final Uri initialOrigin;

  static Future<Uri?> show(
    BuildContext context, {
    required Uri initialOrigin,
  }) => Navigator.of(context).push<Uri>(
    MaterialPageRoute<Uri>(
      builder: (_) => RomdServerOriginScreen(initialOrigin: initialOrigin),
    ),
  );

  @override
  State<RomdServerOriginScreen> createState() => _RomdServerOriginScreenState();
}

const int _kMaxOriginLength = 128;

final class _RomdServerOriginScreenState extends State<RomdServerOriginScreen> {
  static const double _contentMaxWidth = 560;

  final FocusNode _keyboardFocus = FocusNode(debugLabel: 'server-keyboard-a');
  final FocusNode _saveFocus = FocusNode(debugLabel: 'server-save');
  late String _originText = widget.initialOrigin.toString();

  Uri? get _origin => RomdServerOrigins.tryParse(_originText);

  String? get _error {
    if (_originText.trim().isEmpty) {
      return 'Server origin is required.';
    }
    if (_origin == null) {
      return 'Use an http(s) origin without a path.';
    }
    return null;
  }

  bool get _canSave => _origin != null;

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
    _saveFocus.dispose();
    super.dispose();
  }

  void _append(String char) {
    if (char == ' ') {
      return;
    }
    _setOriginText(_originText + char.toLowerCase());
  }

  void _appendRaw(String char) {
    if (char != ' ') {
      _setOriginText(_originText + char);
    }
  }

  void _backspace() {
    if (_originText.isNotEmpty) {
      _setOriginText(_originText.substring(0, _originText.length - 1));
    }
  }

  void _clear() => _setOriginText('');

  void _setOriginText(String value) {
    if (value.length > _kMaxOriginLength) {
      return;
    }
    setState(() => _originText = value);
  }

  void _submit() {
    final origin = _origin;
    if (origin != null) {
      Navigator.of(context).pop(origin);
    }
  }

  void _cancel() => Navigator.of(context).pop();

  KeyEventResult _onKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent && event is! KeyRepeatEvent) {
      return KeyEventResult.ignored;
    }
    if (event.logicalKey == LogicalKeyboardKey.backspace) {
      _backspace();
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.space) {
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

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    return CallbackShortcuts(
      bindings: <ShortcutActivator, VoidCallback>{
        const SingleActivator(LogicalKeyboardKey.escape): _cancel,
      },
      child: Scaffold(
        backgroundColor: context.theme.scaffoldBackgroundColor,
        body: Actions(
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
            child: Stack(
              children: <Widget>[
                const Positioned.fill(
                  child: ConsoleAmbientBackground(dimmed: true),
                ),
                SafeArea(
                  child: Padding(
                    padding: EdgeInsets.fromLTRB(
                      layout.screenGutter,
                      layout.lg,
                      layout.screenGutter,
                      layout.screenChrome.bottom,
                    ),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        _Header(onBack: _cancel),
                        Expanded(
                          child: SingleChildScrollView(
                            child: Center(
                              child: ConstrainedBox(
                                constraints: const BoxConstraints(
                                  maxWidth: _contentMaxWidth,
                                ),
                                child: Column(
                                  mainAxisSize: MainAxisSize.min,
                                  crossAxisAlignment:
                                      CrossAxisAlignment.stretch,
                                  children: <Widget>[
                                    _OriginField(
                                      value: _originText,
                                      error: _error,
                                    ),
                                    SizedBox(height: layout.lg),
                                    Align(
                                      alignment: Alignment.centerLeft,
                                      child: OnScreenKeyboard(
                                        layout: OnScreenKeyboardLayout.url,
                                        initialFocusNode: _keyboardFocus,
                                        onMoveBelow: _saveFocus.requestFocus,
                                        onChar: _append,
                                        onBackspace: _backspace,
                                        onClear: _clear,
                                      ),
                                    ),
                                    SizedBox(height: layout.lg),
                                    SizedBox(
                                      height:
                                          layout.settings.compactActionHeight,
                                      child: ConsoleActionButton(
                                        label: 'Save',
                                        kind: ConsoleActionKind.primary,
                                        icon: Icons.check,
                                        focusNode: _saveFocus,
                                        onPressed: _canSave ? _submit : null,
                                      ),
                                    ),
                                    SizedBox(height: layout.sm),
                                    SizedBox(
                                      height:
                                          layout.settings.compactActionHeight,
                                      child: ConsoleActionButton(
                                        label: 'Cancel',
                                        kind: ConsoleActionKind.quiet,
                                        icon: Icons.close,
                                        onPressed: _cancel,
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                            ),
                          ),
                        ),
                        const Center(
                          child: ConsoleHintBar(
                            muted: true,
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
    );
  }
}

final class _Header extends StatelessWidget {
  const _Header({required this.onBack});

  final VoidCallback onBack;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;
    return Row(
      children: <Widget>[
        ConsoleCircleButton(onTap: onBack),
        SizedBox(width: layout.lg),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Text(
                'ROMD server',
                style: text.sectionHeading.copyWith(color: colors.textStrong),
              ),
              SizedBox(height: layout.xxs),
              Text(
                'ORIGIN ONLY',
                style: text.metadata.copyWith(color: colors.textFaint),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

final class _OriginField extends StatelessWidget {
  const _OriginField({required this.value, required this.error});

  final String value;
  final String? error;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final text = context.text;
    final empty = value.isEmpty;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        Container(
          padding: layout.panelPadding,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(layout.panelRadius),
            color: colors.panelSurface,
            border: Border.all(
              color: error == null ? colors.borderStrong : context.colors.error,
              width: layout.hairlineStroke,
            ),
          ),
          child: Row(
            children: <Widget>[
              Flexible(
                child: Text(
                  empty ? RomdServerOrigins.defaultValue : value,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: text.sectionLabel.copyWith(
                    color: empty ? colors.textFaint : colors.textStrong,
                  ),
                ),
              ),
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
            error!,
            style: text.metadata.copyWith(color: context.colors.error),
          ),
        ],
      ],
    );
  }
}
