import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

void main() {
  testWidgets(
    'fallback derives a monogram without identifying a known system',
    (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          theme: RomdSkins.baselineDark(),
          home: Builder(
            builder: (context) {
              final presentation = platformPresentationFor(
                context,
                platformId: 'genesis',
                platformName: 'Genesis',
              );
              return Text(presentation.shortCode);
            },
          ),
        ),
      );

      expect(find.text('GENE'), findsOneWidget);
      expect(find.text('NES'), findsNothing);
    },
  );

  testWidgets('opaque platform ids fall back to the platform name', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Builder(
          builder: (context) {
            final presentation = platformPresentationFor(
              context,
              platformId: 'opaque-public-id',
              platformName: 'Nintendo 64',
            );
            return Text(presentation.shortCode);
          },
        ),
      ),
    );

    expect(find.text('N6'), findsOneWidget);
    expect(find.text('N64'), findsNothing);
  });
}
