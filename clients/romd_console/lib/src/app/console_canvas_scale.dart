import 'dart:math' as math;

import 'package:flutter/widgets.dart';

/// Pins the console UI to its 10-ft design canvas (1280×720 logical).
///
/// The whole presentation layer — type ramp, gutters, tile sizes, focus rings —
/// is authored against a 1280×720 canvas (see `main.dart`'s window size and
/// the active console layout theme). Without normalization, going fullscreen on a
/// 1080p/4K TV lays the app out at 1920×1080+ logical pixels, silently shrinking
/// every font and hit target by a third relative to the design.
///
/// This widget scales *up only*: when the window is larger than the design
/// canvas it lays the child out at a proportionally reduced logical size (same
/// aspect as the window, shortest side pinned near 720) and uniformly scales it
/// to fill — text and vectors stay crisp because the transform happens at paint
/// time. At or below the design size it is a no-op, so development windows and
/// widget tests are unaffected.
final class ConsoleCanvasScale extends StatelessWidget {
  const ConsoleCanvasScale({required this.child, super.key});

  static const Size designSize = Size(1280, 720);

  final Widget child;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final size = constraints.biggest;
      if (!size.isFinite || size.isEmpty) {
        return child;
      }

      final scale = math.min(
        size.width / designSize.width,
        size.height / designSize.height,
      );
      if (scale <= 1) {
        return child;
      }

      final media = MediaQuery.of(context);
      final canvas = Size(size.width / scale, size.height / scale);
      // Both dimensions divide by the same factor, so FittedBox's fill is a
      // uniform scale — no distortion, no letterboxing. devicePixelRatio is
      // multiplied so image decode sizes keep matching on-screen pixels.
      return FittedBox(
        fit: BoxFit.fill,
        child: SizedBox.fromSize(
          size: canvas,
          child: MediaQuery(
            data: media.copyWith(
              size: canvas,
              devicePixelRatio: media.devicePixelRatio * scale,
            ),
            child: child,
          ),
        ),
      );
    },
  );
}
