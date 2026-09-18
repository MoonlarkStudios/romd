import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/connection_status_indicator.dart';
import 'package:romd_console/src/presentation/widgets/console_clock.dart';

void main() {
  testWidgets('shared clock keeps the entry clock format when compact', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ConsoleClock(
          now: () => DateTime(2026, 7, 10, 14, 51),
          compact: true,
        ),
      ),
    );

    expect(find.text('2:51 PM'), findsOneWidget);
  });

  test('shared clock formatter handles midnight', () {
    expect(formatConsoleTime(DateTime(2026, 7, 10, 0, 5)), '12:05 AM');
  });

  testWidgets('connected status names itself with the status dot', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: const Scaffold(
          body: ConnectionStatusIndicator(status: ConnectionStatus.connected),
        ),
      ),
    );

    expect(find.text('Connected'), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('connection-status-connected')),
      findsOneWidget,
    );
  });

  testWidgets('local status uses complete concise server copy', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: const Scaffold(
          body: ConnectionStatusIndicator(status: ConnectionStatus.local),
        ),
      ),
    );

    expect(find.text('No server'), findsOneWidget);
    expect(find.textContaining('No ROMD server'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('offline status keeps saved-access honesty in semantics', (
    tester,
  ) async {
    final semantics = tester.ensureSemantics();
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: const Scaffold(
          body: ConnectionStatusIndicator(
            status: ConnectionStatus.offline,
            serverHost: 'shelf.local',
          ),
        ),
      ),
    );

    expect(find.text('Offline'), findsOneWidget);
    expect(
      find.bySemanticsLabel('Offline — saved access applies'),
      findsOneWidget,
    );
    semantics.dispose();
  });

  testWidgets('indicator remains stable at 2x text', (tester) async {
    await tester.binding.setSurfaceSize(const Size(800, 480));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: const MediaQuery(
          data: MediaQueryData(textScaler: TextScaler.linear(2)),
          child: Scaffold(
            body: ConnectionStatusIndicator(
              status: ConnectionStatus.unavailable,
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    expect(tester.takeException(), isNull);
    expect(find.text('Server unavailable'), findsOneWidget);
  });
}
