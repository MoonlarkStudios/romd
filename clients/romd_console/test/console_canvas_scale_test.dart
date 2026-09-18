import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/app/console_canvas_scale.dart';

void main() {
  Future<BoxConstraints> pumpAndProbe(WidgetTester tester, Size surface) async {
    await tester.binding.setSurfaceSize(surface);
    addTearDown(() => tester.binding.setSurfaceSize(null));

    late BoxConstraints seen;
    await tester.pumpWidget(
      MediaQuery(
        data: MediaQueryData(size: surface),
        child: ConsoleCanvasScale(
          child: LayoutBuilder(
            builder: (context, constraints) {
              seen = constraints;
              return const SizedBox.expand();
            },
          ),
        ),
      ),
    );
    return seen;
  }

  testWidgets('oversized windows lay out on the 720p design canvas', (
    tester,
  ) async {
    final constraints = await pumpAndProbe(tester, const Size(1920, 1080));

    // 1.5× scale: the child sees 1280×720 and is scaled up to fill.
    expect(constraints.maxWidth, moreOrLessEquals(1280));
    expect(constraints.maxHeight, moreOrLessEquals(720));
  });

  testWidgets('ultrawide windows pin the shortest side to the canvas', (
    tester,
  ) async {
    final constraints = await pumpAndProbe(tester, const Size(2560, 1080));

    // Scale is min(2560/1280, 1080/720) = 1.5 → canvas 1706.7×720.
    expect(constraints.maxHeight, moreOrLessEquals(720));
    expect(constraints.maxWidth, moreOrLessEquals(2560 / 1.5, epsilon: 0.1));
  });

  testWidgets('windows at or below the design size pass through unscaled', (
    tester,
  ) async {
    final constraints = await pumpAndProbe(tester, const Size(800, 600));

    expect(constraints.maxWidth, moreOrLessEquals(800));
    expect(constraints.maxHeight, moreOrLessEquals(600));
  });

  testWidgets('devicePixelRatio is multiplied by the canvas scale', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1920, 1080));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    late double seenRatio;
    await tester.pumpWidget(
      MediaQuery(
        data: const MediaQueryData(size: Size(1920, 1080), devicePixelRatio: 2),
        child: ConsoleCanvasScale(
          child: Builder(
            builder: (context) {
              seenRatio = MediaQuery.devicePixelRatioOf(context);
              return const SizedBox.expand();
            },
          ),
        ),
      ),
    );

    // 2.0 native × 1.5 canvas scale — image decode sizes track screen pixels.
    expect(seenRatio, moreOrLessEquals(3));
  });
}
