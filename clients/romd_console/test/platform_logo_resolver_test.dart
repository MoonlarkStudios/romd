import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/widgets/platform_logo_resolver.dart';

void main() {
  testWidgets(
    'without server metadata even a familiar name has no invented logo',
    (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          home: Builder(
            builder: (context) {
              final source = PlatformLogoResolverScope.of(context).resolve(
                platformId: 'snes',
                platformName: 'Super Nintendo',
                shortCode: 'SNES',
              );
              expect(source, isNull);
              return const SizedBox();
            },
          ),
        ),
      );
    },
  );
}
