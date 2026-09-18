import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../data/consumer_api_client.dart';
import '../domain/consumer_account.dart';
import '../domain/device_authorization.dart';
import '../domain/local_profile.dart';
import 'theme/console_theme_context.dart';
import 'widgets/console_action_button.dart';
import 'widgets/console_clock.dart';
import 'widgets/console_hint_bar.dart';
import 'widgets/profile_avatar.dart';

enum _DeviceFlowStatus { requesting, awaitingApproval, failed }

// Structural constraint: this controls the login composition, not its skin.
const double _kLoginContentMaxWidth = 520;

final class RomdLoginScreen extends StatefulWidget {
  const RomdLoginScreen({
    required this.localProfile,
    required this.consumerApiClient,
    required this.onAuthenticated,
    required this.onBack,
    super.key,
  });

  final LocalProfile localProfile;
  final ConsumerApiClient consumerApiClient;
  final ValueChanged<ConsumerLoginSession> onAuthenticated;

  /// Invoked when the user backs out (Esc) to return to profile selection.
  final VoidCallback onBack;

  @override
  State<RomdLoginScreen> createState() => _RomdLoginScreenState();
}

final class _RomdLoginScreenState extends State<RomdLoginScreen> {
  _DeviceFlowStatus _status = _DeviceFlowStatus.requesting;
  DeviceAuthorization? _authorization;
  String? _errorMessage;

  @override
  void initState() {
    super.initState();
    _startDeviceFlow();
  }

  @override
  Widget build(BuildContext context) {
    final accent = Color(widget.localProfile.accentColor);
    final layout = context.layout;

    return CallbackShortcuts(
      bindings: <ShortcutActivator, VoidCallback>{
        const SingleActivator(LogicalKeyboardKey.escape): widget.onBack,
      },
      child: Scaffold(
        backgroundColor: Colors.transparent,
        body: Actions(
          actions: <Type, Action<Intent>>{
            DismissIntent: CallbackAction<DismissIntent>(
              onInvoke: (_) {
                widget.onBack();
                return null;
              },
            ),
          },
          child: Focus(
            autofocus: true,
            child: SafeArea(
              child: Stack(
                children: <Widget>[
                  Positioned(
                    top: layout.screenChrome.top,
                    right: layout.screenChrome.side,
                    child: const ConsoleClock(),
                  ),
                  Center(
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(
                        maxWidth: _kLoginContentMaxWidth,
                      ),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: <Widget>[
                          Center(
                            child: ProfileAvatar(
                              avatarKey: widget.localProfile.avatarKey,
                              accentColor: accent,
                              size: layout.avatarMd,
                            ),
                          ),
                          SizedBox(height: layout.md),
                          Text(
                            widget.localProfile.displayName,
                            textAlign: TextAlign.center,
                            style: context.text.sectionHeading,
                          ),
                          SizedBox(height: layout.xl),
                          ..._buildBody(context),
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
                            glyph: 'Esc',
                            gamepadGlyph: 'B',
                            label: 'Back',
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

  List<Widget> _buildBody(BuildContext context) {
    final layout = context.layout;
    switch (_status) {
      case _DeviceFlowStatus.requesting:
        return <Widget>[
          const Center(child: CircularProgressIndicator()),
          SizedBox(height: layout.md),
          Text(
            'Preparing secure sign-in…',
            textAlign: TextAlign.center,
            style: context.text.bodyCompact,
          ),
        ];
      case _DeviceFlowStatus.awaitingApproval:
        return _buildAwaitingApproval(context, _authorization!);
      case _DeviceFlowStatus.failed:
        return <Widget>[
          Icon(
            Icons.error_outline,
            size: layout.status.feedbackIconSize,
            color: context.colors.error,
          ),
          SizedBox(height: layout.sm),
          Text(
            _errorMessage ?? 'Sign-in failed.',
            textAlign: TextAlign.center,
            style: DefaultTextStyle.of(
              context,
            ).style.copyWith(color: context.colors.error),
          ),
          SizedBox(height: layout.lg),
          Center(
            child: ConsoleActionButton(
              label: 'Try again',
              kind: ConsoleActionKind.primary,
              icon: Icons.refresh,
              onPressed: _startDeviceFlow,
            ),
          ),
        ];
    }
  }

  List<Widget> _buildAwaitingApproval(
    BuildContext context,
    DeviceAuthorization authorization,
  ) {
    final colors = context.consoleColors;
    final layout = context.layout;

    return <Widget>[
      Text(
        'Link this device to your ROMD account',
        textAlign: TextAlign.center,
        style: context.text.action,
      ),
      SizedBox(height: layout.lg),
      _CodeCard(authorization: authorization),
      SizedBox(height: layout.lg),
      Row(
        mainAxisAlignment: MainAxisAlignment.center,
        children: <Widget>[
          SizedBox.square(
            dimension: layout.progressIndicatorSm,
            child: CircularProgressIndicator(strokeWidth: layout.focusStroke),
          ),
          SizedBox(width: layout.sm),
          Text(
            'Waiting for approval…',
            style: _loginBodyStyle(context, color: colors.textMuted),
          ),
        ],
      ),
    ];
  }

  Future<void> _startDeviceFlow() async {
    setState(() {
      _status = _DeviceFlowStatus.requesting;
      _errorMessage = null;
      _authorization = null;
    });

    final result = await widget.consumerApiClient.requestDeviceAuthorization();
    if (!mounted) {
      return;
    }

    switch (result) {
      case DeviceAuthorizationSuccess(:final authorization):
        setState(() {
          _authorization = authorization;
          _status = _DeviceFlowStatus.awaitingApproval;
        });
        unawaited(_pollForApproval(authorization));
      case DeviceAuthorizationFailure(:final message):
        setState(() {
          _status = _DeviceFlowStatus.failed;
          _errorMessage = message;
        });
    }
  }

  Future<void> _pollForApproval(DeviceAuthorization authorization) async {
    var interval = authorization.interval;

    while (mounted &&
        _status == _DeviceFlowStatus.awaitingApproval &&
        identical(_authorization, authorization)) {
      await Future<void>.delayed(interval);
      if (!mounted ||
          _status != _DeviceFlowStatus.awaitingApproval ||
          !identical(_authorization, authorization)) {
        return;
      }

      if (DateTime.now().isAfter(authorization.expiresAt)) {
        setState(() {
          _status = _DeviceFlowStatus.failed;
          _errorMessage = 'The sign-in code expired. Try again.';
        });
        return;
      }

      final result = await widget.consumerApiClient.redeemDeviceCode(
        deviceCode: authorization.deviceCode,
      );
      if (!mounted || !identical(_authorization, authorization)) {
        return;
      }

      switch (result) {
        case DeviceTokenPending():
          break;
        case DeviceTokenSlowDown():
          interval += const Duration(seconds: 5);
        case DeviceTokenSuccess(:final session):
          widget.onAuthenticated(session);
          return;
        case DeviceTokenFailure(:final message):
          setState(() {
            _status = _DeviceFlowStatus.failed;
            _errorMessage = message;
          });
          return;
      }
    }
  }
}

final class _CodeCard extends StatelessWidget {
  const _CodeCard({required this.authorization});

  final DeviceAuthorization authorization;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;

    return Container(
      key: const ValueKey<String>('login-code-card'),
      padding: EdgeInsets.all(layout.lg),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(layout.dialogRadius),
        color: colors.panelSurface,
        border: Border.all(
          color: colors.panelBorder,
          width: layout.hairlineStroke,
        ),
        boxShadow: context.elevation.panel,
      ),
      child: Column(
        children: <Widget>[
          Text(
            'On a phone or computer, open',
            style: _loginBodyStyle(context, color: colors.textMuted),
          ),
          SizedBox(height: layout.xs),
          SelectableText(
            authorization.verificationUri.toString(),
            textAlign: TextAlign.center,
            style: context.text.action,
          ),
          SizedBox(height: layout.lg),
          Text(
            'and enter this code',
            style: _loginBodyStyle(context, color: colors.textMuted),
          ),
          SizedBox(height: layout.sm),
          SelectableText(
            authorization.userCode,
            key: const ValueKey<String>('login-user-code'),
            textAlign: TextAlign.center,
            style: context.artwork.verificationCodeStyle,
          ),
        ],
      ),
    );
  }
}

TextStyle _loginBodyStyle(BuildContext context, {required Color color}) =>
    context.text.body.copyWith(color: color);
