import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';

/// The console's one empty/loading/offline/error vocabulary: a centered
/// column of leading visual, short title, muted supporting line, and an
/// optional action. Replaces per-screen loading spinners and bespoke empty
/// panels so non-happy paths read as one product.
///
/// Two register rules:
/// - [invitation] (gold icon) is reserved for first-run empties on surfaces
///   with no other affordance in view — never for errors, never on Home,
///   whose empty rail speaks through still vessels instead of copy.
/// - [loading] replaces the icon with the themed progress ring; under reduced
///   motion the ring holds a static arc rather than spinning.
final class ConsoleStatusState extends StatelessWidget {
  const ConsoleStatusState({
    required this.title,
    this.message,
    this.icon,
    this.loading = false,
    this.invitation = false,
    this.action,
    super.key,
  }) : assert(
         !(loading && icon != null),
         'Loading replaces the icon; provide one or the other.',
       );

  final String title;
  final String? message;
  final IconData? icon;
  final bool loading;
  final bool invitation;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final motionless =
        motion.resolve(context, motion.contentTransition) == Duration.zero;

    return Center(
      child: ConstrainedBox(
        constraints: BoxConstraints(maxWidth: layout.status.panelMaxWidth),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            if (loading)
              SizedBox(
                width: layout.progressIndicatorSize,
                height: layout.progressIndicatorSize,
                child: CircularProgressIndicator(
                  // A quarter arc reads "in progress" without motion.
                  value: motionless ? 0.25 : null,
                ),
              )
            else if (icon case final icon?)
              Icon(
                icon,
                size: layout.status.feedbackIconSize,
                color: invitation ? colors.catalogAccent : colors.textMuted,
              ),
            if (loading || icon != null) SizedBox(height: layout.md),
            Text(
              title,
              style: context.text.supportingTitle,
              textAlign: TextAlign.center,
            ),
            if (message case final message?) ...<Widget>[
              SizedBox(height: layout.xs),
              Text(
                message,
                style: context.text.bodyCompact.copyWith(
                  color: colors.textMuted,
                ),
                textAlign: TextAlign.center,
              ),
            ],
            if (action case final action?) ...<Widget>[
              SizedBox(height: layout.lg),
              action,
            ],
          ],
        ),
      ),
    );
  }
}
