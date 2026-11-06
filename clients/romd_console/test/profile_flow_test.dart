import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/local_profiles/local_profile_repository.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/consumer_host_health.dart';
import 'package:romd_console/src/domain/device_authorization.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/presentation/local_profile_selection_screen.dart';
import 'package:romd_console/src/presentation/profile_identity_palette.dart';
import 'package:romd_console/src/presentation/profile_settings_screen.dart';
import 'package:romd_console/src/presentation/romd_login_screen.dart';
import 'package:romd_console/src/presentation/romd_server_origin_screen.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_blinking_caret.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';
import 'package:romd_console/src/presentation/widgets/create_profile_screen.dart';
import 'package:romd_console/src/presentation/widgets/profile_avatar.dart';

void main() {
  test(
    'profile identity accents provide contrast-safe derived foregrounds',
    () {
      for (final accent in ProfileIdentityPalette.accents) {
        final foreground = ProfileIdentityPalette.foregroundFor(accent);
        final lighter = accent.computeLuminance() + 0.05;
        final darker = foreground.computeLuminance() + 0.05;
        final contrast = lighter > darker ? lighter / darker : darker / lighter;
        expect(contrast, greaterThanOrEqualTo(4.5), reason: '$accent');
      }
    },
  );

  testWidgets('profile selection remains overflow-free at 2x text scale', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(textScaler: TextScaler.linear(2)),
          child: LocalProfileSelectionScreen(
            repository: _FakeRepository(profiles: _twoProfiles),
            defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
            onSelected: (_) {},
            onBack: () {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
    expect(find.text("Who's playing?"), findsOneWidget);
    expect(find.text('Local'), findsNWidgets(2));
    expect(find.text('Not linked'), findsNothing);
    expect(find.byIcon(Icons.sports_esports), findsNothing);
    final aliceTile = find.byKey(const ValueKey<String>('profile-tile-a'));
    _expectRectContained(tester, find.text('Alice'), aliceTile);
    _expectRectContained(
      tester,
      find.descendant(of: aliceTile, matching: find.text('Local')),
      aliceTile,
    );
    _expectRectContained(
      tester,
      find.byKey(const ValueKey<String>('profile-avatar-a')),
      aliceTile,
    );
    final tileRect = tester.getRect(aliceTile);
    expect(tileRect.top, greaterThanOrEqualTo(0));
    expect(tileRect.bottom, lessThanOrEqualTo(720));
  });

  testWidgets('profile carousel uses the approved 1280 by 720 geometry', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: LocalProfileSelectionScreen(
          repository: _FakeRepository(profiles: _twoProfiles),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (_) {},
          onBack: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(
      tester.getSize(find.byKey(const ValueKey<String>('profile-tile-a'))),
      const Size(158, 276),
    );
    expect(
      tester.getSize(find.byKey(const ValueKey<String>('add-profile-tile'))),
      const Size(158, 276),
    );
    for (final avatar in tester.widgetList<ProfileAvatar>(
      find.byType(ProfileAvatar),
    )) {
      expect(avatar.size, 118);
    }
    expect(
      tester
              .getCenter(find.byKey(const ValueKey<String>('profile-tile-a')))
              .dx -
          tester
              .getCenter(find.byKey(const ValueKey<String>('add-profile-tile')))
              .dx,
      closeTo(182, 0.1),
    );
    final focusedTile = tester.getRect(
      find.byKey(const ValueKey<String>('profile-tile-a')),
    );
    expect(focusedTile.left, greaterThanOrEqualTo(0));
    expect(focusedTile.top, greaterThanOrEqualTo(0));
    expect(focusedTile.right, lessThanOrEqualTo(1280));
    expect(focusedTile.bottom, lessThanOrEqualTo(720));
    expect(
      tester
          .getCenter(find.byKey(const ValueKey<String>('profile-avatar-a')))
          .dx,
      closeTo(640, 1),
    );
    expect(
      tester
          .getCenter(find.byKey(const ValueKey<String>('profile-avatar-a')))
          .dy,
      closeTo(360, 1),
    );
    expect(
      find.byWidgetPredicate(
        (widget) =>
            widget is Semantics &&
            widget.properties.label == 'Alice, Local profile.' &&
            widget.excludeSemantics,
      ),
      findsOneWidget,
    );
    expect(find.byIcon(Icons.sports_esports), findsNothing);
  });

  testWidgets('reduced motion uses immediate carousel traversal', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    LocalProfile? selected;

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: LocalProfileSelectionScreen(
            repository: _FakeRepository(profiles: _twoProfiles),
            defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
            onSelected: (profile) => selected = profile,
            onBack: () {},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(
      tester
          .getCenter(find.byKey(const ValueKey<String>('profile-avatar-b')))
          .dx,
      closeTo(640, 1),
    );
    final bobTile = tester.getRect(
      find.byKey(const ValueKey<String>('profile-tile-b')),
    );
    expect(bobTile.left, greaterThanOrEqualTo(0));
    expect(bobTile.right, lessThanOrEqualTo(1280));

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(selected?.displayName, 'Bob');
  });

  testWidgets(
    'mounted profile selection re-resolves artwork painter and intro motion',
    (tester) async {
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      addTearDown(mode.dispose);
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        ValueListenableBuilder<ThemeMode>(
          valueListenable: mode,
          builder: (context, themeMode, child) => MaterialApp(
            theme: RomdSkins.seamTestTheme(),
            darkTheme: RomdSkins.baselineDark(),
            themeMode: themeMode,
            themeAnimationDuration: Duration.zero,
            home: child,
          ),
          child: LocalProfileSelectionScreen(
            repository: _FakeRepository(profiles: _twoProfiles),
            defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
            onSelected: (_) {},
            onBack: () {},
          ),
        ),
      );
      await tester.pump();
      await tester.pump();

      final screen = find.byType(LocalProfileSelectionScreen);
      final intro = find.byKey(
        const ValueKey<String>('profile-selection-intro'),
      );
      final addAvatar = find.byKey(
        const ValueKey<String>('add-profile-avatar'),
      );
      final aliceAvatar = find.descendant(
        of: find.byKey(const ValueKey<String>('profile-avatar-a')),
        matching: find.byType(ProfileAvatar),
      );
      final elementBefore = tester.element(screen);
      final introBefore = tester.widget<AnimatedBuilder>(intro);
      final controllerBefore = introBefore.animation as AnimationController;
      final durationBefore = controllerBefore.duration;
      final promptBefore = tester
          .widget<Text>(find.text("Who's playing?"))
          .style!;
      final painterBefore = tester.widget<CustomPaint>(addAvatar).painter!;
      final identityAccentBefore = tester
          .widget<ProfileAvatar>(aliceAvatar)
          .accentColor;

      mode.value = ThemeMode.light;
      await tester.pump();

      final introAfter = tester.widget<AnimatedBuilder>(intro);
      final controllerAfter = introAfter.animation as AnimationController;
      final promptAfter = tester
          .widget<Text>(find.text("Who's playing?"))
          .style!;
      final painterAfter = tester.widget<CustomPaint>(addAvatar).painter!;
      final identityAccentAfter = tester
          .widget<ProfileAvatar>(aliceAvatar)
          .accentColor;

      expect(identical(elementBefore, tester.element(screen)), isTrue);
      expect(identical(controllerBefore, controllerAfter), isTrue);
      expect(controllerAfter.duration, isNot(durationBefore));
      expect(promptAfter.fontFamily, isNot(promptBefore.fontFamily));
      expect(promptAfter.fontSize, isNot(promptBefore.fontSize));
      expect(painterAfter.shouldRepaint(painterBefore), isTrue);
      expect(identityAccentAfter, identityAccentBefore);
    },
  );

  testWidgets(
    'mounted profile creator re-resolves controls and caret motion at runtime',
    (tester) async {
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      addTearDown(mode.dispose);
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        ValueListenableBuilder<ThemeMode>(
          valueListenable: mode,
          builder: (context, themeMode, child) => MaterialApp(
            theme: RomdSkins.seamTestTheme(),
            darkTheme: RomdSkins.baselineDark(),
            themeMode: themeMode,
            themeAnimationDuration: Duration.zero,
            home: child,
          ),
          child: CreateProfileScreen(
            initialRomdServerOrigin: RomdServerOrigins.defaultUri,
          ),
        ),
      );
      await tester.pump();

      final screen = find.byKey(
        const ValueKey<String>('create-profile-screen'),
      );
      final nameField = find.byKey(
        const ValueKey<String>('create-profile-Profile name-field'),
      );
      final createButton = find.byKey(
        const ValueKey<String>('create-profile-create-button'),
      );
      final caret = find.byType(ConsoleBlinkingCaret);
      final previewAvatar = find.byType(ProfileAvatar).first;
      final elementBefore = tester.element(screen);
      final fieldBefore = tester.widget<AnimatedContainer>(nameField);
      final fieldDecorationBefore = fieldBefore.decoration! as BoxDecoration;
      final headerBefore = tester
          .widget<Text>(find.text('Create profile'))
          .style!;
      final buttonSizeBefore = tester.getSize(createButton);
      final caretStateBefore = tester.state<ConsoleBlinkingCaretState>(caret);
      final caretDurationBefore = caretStateBefore.debugDuration;
      final identityAccentBefore = tester
          .widget<ProfileAvatar>(previewAvatar)
          .accentColor;

      mode.value = ThemeMode.light;
      await tester.pump();

      final fieldAfter = tester.widget<AnimatedContainer>(nameField);
      final fieldDecorationAfter = fieldAfter.decoration! as BoxDecoration;
      final headerAfter = tester
          .widget<Text>(find.text('Create profile'))
          .style!;
      final caretStateAfter = tester.state<ConsoleBlinkingCaretState>(caret);
      final identityAccentAfter = tester
          .widget<ProfileAvatar>(previewAvatar)
          .accentColor;

      expect(identical(elementBefore, tester.element(screen)), isTrue);
      expect(fieldAfter.duration, isNot(fieldBefore.duration));
      expect(
        fieldDecorationAfter.borderRadius,
        isNot(fieldDecorationBefore.borderRadius),
      );
      expect(fieldDecorationAfter.color, isNot(fieldDecorationBefore.color));
      expect(headerAfter.fontFamily, isNot(headerBefore.fontFamily));
      expect(tester.getSize(createButton), isNot(buttonSizeBefore));
      expect(identical(caretStateBefore, caretStateAfter), isTrue);
      expect(caretStateAfter.debugDuration, isNot(caretDurationBefore));
      expect(identityAccentAfter, identityAccentBefore);
    },
  );

  testWidgets('profile semantics distinguish local and linked identity', (
    tester,
  ) async {
    final linked = _profile(
      'linked',
      'Dana',
      romdServerOrigin: Uri.parse('https://romd.example:11338'),
      romdAccountLink: RomdAccountLink(
        romdUserId: 'user-1',
        username: 'dana',
        email: 'dana@example.com',
        linkedAt: DateTime(2026),
      ),
    );
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: LocalProfileSelectionScreen(
          repository: _FakeRepository(
            profiles: <LocalProfile>[_twoProfiles.first, linked],
          ),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (_) {},
          onBack: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(
      find.byWidgetPredicate(
        (widget) =>
            widget is Semantics &&
            widget.properties.label == 'Alice, Local profile.' &&
            widget.excludeSemantics,
      ),
      findsOneWidget,
    );
    expect(
      find.byWidgetPredicate(
        (widget) =>
            widget is Semantics &&
            widget.properties.label ==
                'Dana, Linked profile as dana on romd.example:11338.' &&
            widget.excludeSemantics,
      ),
      findsOneWidget,
    );
  });

  testWidgets('arrow keys move focus between profiles and Enter selects', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    LocalProfile? selected;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: LocalProfileSelectionScreen(
          repository: _FakeRepository(profiles: _twoProfiles),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (profile) => selected = profile,
          onBack: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    // The first tile autofocuses; arrow-right must move focus to the second.
    expect(find.text('Alice'), findsOneWidget);
    expect(
      tester.getCenter(find.text('Add player')).dx,
      lessThan(tester.getCenter(find.text('Alice')).dx),
    );
    final screenCenter = tester.getCenter(
      find.byType(LocalProfileSelectionScreen),
    );
    final selectedAvatarCenter = tester.getCenter(
      find.byKey(const ValueKey<String>('profile-avatar-a')),
    );
    expect(selectedAvatarCenter.dx, closeTo(screenCenter.dx, 1));
    expect(selectedAvatarCenter.dy, closeTo(screenCenter.dy, 1));
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pumpAndSettle();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    expect(selected?.displayName, 'Bob');
  });

  testWidgets(
    'footer verb tracks focus: Select on a profile, Add player on the add tile',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1280, 800));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(
        MaterialApp(
          theme: RomdSkins.baselineDark(),
          home: LocalProfileSelectionScreen(
            repository: _FakeRepository(profiles: _twoProfiles),
            defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
            onSelected: (_) {},
            onBack: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      // The tile label and the footer verb now share the "Add player" copy,
      // so hint assertions scope to the hint bar.
      Finder hintText(String label) => find.descendant(
        of: find.byType(ConsoleHintBar),
        matching: find.text(label),
      );

      // A profile autofocuses, so the primary action reads "Select" — and the
      // calmer welcome row drops the "Move" hint entirely.
      expect(hintText('Select'), findsOneWidget);
      expect(hintText('Add player'), findsNothing);
      expect(hintText('Move'), findsNothing);

      // Focus the add tile (left of the first profile): the verb crossfades.
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
      await tester.pumpAndSettle();
      expect(hintText('Add player'), findsOneWidget);
      expect(hintText('Select'), findsNothing);
    },
  );

  testWidgets('Esc on profile selection invokes onBack', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1280, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    var backCalled = false;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: LocalProfileSelectionScreen(
          repository: _FakeRepository(profiles: _twoProfiles),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (_) {},
          onBack: () => backCalled = true,
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();

    expect(backCalled, isTrue);
  });

  testWidgets(
    'Add player opens the create screen; on-screen keyboard creates a profile',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1280, 800));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      LocalProfile? selected;
      final repository = _FakeRepository(profiles: _twoProfiles);
      await tester.pumpWidget(
        MaterialApp(
          theme: RomdSkins.baselineDark(),
          home: LocalProfileSelectionScreen(
            repository: repository,
            defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
            onSelected: (profile) => selected = profile,
            onBack: () {},
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Add player'));
      // The create screen runs the ambient background ticker, which never
      // settles — pump fixed frames instead of pumpAndSettle.
      for (var i = 0; i < 12; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }
      expect(find.text('Create profile'), findsOneWidget);

      // Type "Ada Lin" entirely via the on-screen keyboard (no TextField, no
      // hardware keys) — the controller-only path. Exercises word
      // capitalization and the SPACE key end-to-end.
      for (final key in <String>['A', 'D', 'A', 'SPACE', 'L', 'I', 'N']) {
        await tester.tap(find.text(key));
        await tester.pump();
      }

      await tester.tap(find.text('Create'));
      // Fixed pumps, not pumpAndSettle: on success the Add tile shows a spinner
      // (the real app unmounts the screen here), which never settles.
      for (var i = 0; i < 12; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }

      expect(repository.lastCreated?.displayName, 'Ada Lin');
      expect(selected?.displayName, 'Ada Lin');
    },
  );

  testWidgets('create screen is completable with the keyboard/controller only', (
    tester,
  ) async {
    // TV size — the whole reason the create step is a full screen, not a modal.
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    FocusManager.instance.highlightStrategy =
        FocusHighlightStrategy.alwaysTraditional;
    addTearDown(
      () => FocusManager.instance.highlightStrategy =
          FocusHighlightStrategy.automatic,
    );

    LocalProfile? selected;
    final repository = _FakeRepository(profiles: _twoProfiles);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: LocalProfileSelectionScreen(
          repository: repository,
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (profile) => selected = profile,
          onBack: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Add player'));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text('Create profile'), findsOneWidget);

    // Focus autofocuses on the 'A' key; activate it to type a name — entirely
    // through the focus system, no direct taps.
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    // Down out of the key grid saturates at the bottom-most control (Cancel);
    // one Up lands on Create — proving Create is reachable by D-pad alone.
    for (var i = 0; i < 10; i++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
    }
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(repository.lastCreated?.displayName, 'A');
    expect(selected?.displayName, 'A');
  });

  testWidgets('create screen persists a custom ROMD server origin', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final repository = _FakeRepository(profiles: _twoProfiles);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: LocalProfileSelectionScreen(
          repository: repository,
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (_) {},
          onBack: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Add player'));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    await tester.tap(find.text('A'));
    await tester.pump();
    await tester.tap(find.text(RomdServerOrigins.defaultValue));
    await tester.pump();
    await tester.tap(find.text('CLEAR'));
    await tester.pump();

    for (final key in <String>[
      'L',
      'O',
      'C',
      'A',
      'L',
      'H',
      'O',
      'S',
      'T',
      ':',
      '5',
      '0',
      '0',
      '2',
    ]) {
      await tester.tap(find.text(key).last);
      await tester.pump();
    }

    await tester.tap(find.text('Create'));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(
      repository.lastCreated?.romdServerOrigin?.toString(),
      'http://localhost:5002',
    );
  });

  testWidgets('create screen allows a purely local profile (blank server)', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final repository = _FakeRepository(profiles: _twoProfiles);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: LocalProfileSelectionScreen(
          repository: repository,
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (_) {},
          onBack: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Add player'));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    // Name the profile, then clear the prefilled server so it stays local.
    await tester.tap(find.text('A'));
    await tester.pump();
    await tester.tap(find.text(RomdServerOrigins.defaultValue));
    await tester.pump();
    await tester.tap(find.text('CLEAR'));
    await tester.pump();

    await tester.tap(find.text('Create'));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(repository.lastCreated?.displayName, 'A');
    expect(repository.lastCreated?.romdServerOrigin, isNull);
  });

  testWidgets('Esc on the login screen invokes onBack', (tester) async {
    var backCalled = false;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: RomdLoginScreen(
          localProfile: _twoProfiles.first,
          consumerApiClient: _NeverApiClient(),
          onAuthenticated: (_) {},
          onBack: () => backCalled = true,
        ),
      ),
    );
    await tester.pump();

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();

    expect(backCalled, isTrue);
  });

  testWidgets('login remains overflow-free at 2x text scale', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(textScaler: TextScaler.linear(2)),
          child: RomdLoginScreen(
            localProfile: _twoProfiles.first,
            consumerApiClient: _NeverApiClient(),
            onAuthenticated: (_) {},
            onBack: () {},
          ),
        ),
      ),
    );
    await tester.pump();

    expect(tester.takeException(), isNull);
    expect(find.text('Preparing secure sign-in…'), findsOneWidget);
  });

  testWidgets(
    'login re-resolves code-card skin while preserving profile identity color',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      addTearDown(mode.dispose);
      final authorization = DeviceAuthorization(
        deviceCode: 'device-code',
        userCode: 'ABCD-1234',
        verificationUri: Uri.parse('https://romd.test/link'),
        interval: const Duration(milliseconds: 1),
        expiresAt: DateTime(2100),
      );

      await tester.pumpWidget(
        ValueListenableBuilder<ThemeMode>(
          valueListenable: mode,
          builder: (context, themeMode, _) => MaterialApp(
            theme: RomdSkins.seamTestTheme(),
            darkTheme: RomdSkins.baselineDark(),
            themeMode: themeMode,
            themeAnimationDuration: Duration.zero,
            home: RomdLoginScreen(
              localProfile: _twoProfiles.first,
              consumerApiClient: _NeverApiClient(authorization: authorization),
              onAuthenticated: (_) {},
              onBack: () {},
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.pump();

      final cardFinder = find.byKey(const ValueKey<String>('login-code-card'));
      final codeFinder = find.byKey(const ValueKey<String>('login-user-code'));
      final cardElement = tester.element(cardFinder);
      final darkCard = tester.widget<Container>(cardFinder);
      final darkCode = tester.widget<SelectableText>(codeFinder);
      final darkAvatar = tester.widget<ProfileAvatar>(
        find.byType(ProfileAvatar),
      );
      final avatarPaintFinder = find.descendant(
        of: find.byType(ProfileAvatar),
        matching: find.byType(CustomPaint),
      );
      final darkAvatarPaint = tester.widget<CustomPaint>(avatarPaintFinder);

      mode.value = ThemeMode.light;
      await tester.pump();

      final testCard = tester.widget<Container>(cardFinder);
      final testCode = tester.widget<SelectableText>(codeFinder);
      final testAvatar = tester.widget<ProfileAvatar>(
        find.byType(ProfileAvatar),
      );
      final testAvatarPaint = tester.widget<CustomPaint>(avatarPaintFinder);
      expect(identical(cardElement, tester.element(cardFinder)), isTrue);
      expect(testCard.decoration, isNot(darkCard.decoration));
      expect(testCode.style, isNot(darkCode.style));
      expect(testAvatar.accentColor, darkAvatar.accentColor);
      expect(
        testAvatarPaint.painter!.shouldRepaint(darkAvatarPaint.painter!),
        isTrue,
      );
      await tester.pump(const Duration(milliseconds: 1));
    },
  );

  testWidgets('profile settings remains usable at 2x text scale', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(textScaler: TextScaler.linear(2)),
          child: ProfileSettingsScreen(
            localProfile: _twoProfiles.first,
            connected: false,
            onConnect: () {},
            onChangeServer: () {},
            onRemoveServer: () {},
            onSignOut: () {},
            onStorage: () {},
            onControllers: () {},
            onEmulation: () {},
            onSwitchProfile: () {},
            onBack: () {},
          ),
        ),
      ),
    );
    await tester.pump();

    expect(tester.takeException(), isNull);
    expect(find.text('Profile & Account'), findsOneWidget);
    expect(find.text('Storage'), findsOneWidget);
    expect(find.text('Controllers'), findsOneWidget);
    expect(find.text('Emulation'), findsOneWidget);
  });

  testWidgets('profile settings exposes the Emulation entry', (tester) async {
    var opened = false;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ProfileSettingsScreen(
          localProfile: _twoProfiles.first,
          connected: false,
          onConnect: () {},
          onChangeServer: () {},
          onRemoveServer: () {},
          onSignOut: () {},
          onStorage: () {},
          onControllers: () {},
          onEmulation: () => opened = true,
          onSwitchProfile: () {},
          onBack: () {},
        ),
      ),
    );
    await tester.pump();

    await tester.ensureVisible(find.text('Emulation'));
    await tester.pump();
    await tester.tap(find.text('Emulation'));
    expect(opened, isTrue);
  });

  testWidgets('profile settings exposes profile-scoped Storage', (
    tester,
  ) async {
    var opened = false;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ProfileSettingsScreen(
          localProfile: _twoProfiles.first,
          connected: false,
          onConnect: () {},
          onChangeServer: () {},
          onRemoveServer: () {},
          onSignOut: () {},
          onStorage: () => opened = true,
          onControllers: () {},
          onEmulation: () {},
          onSwitchProfile: () {},
          onBack: () {},
        ),
      ),
    );
    await tester.pump();

    await tester.tap(
      find.byKey(const ValueKey<String>('settings-storage-row')),
    );
    expect(opened, isTrue);
  });

  testWidgets('Profile & Account groups local, server, and account settings', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ProfileSettingsScreen(
          localProfile: _twoProfiles.first,
          connected: false,
          onConnect: () {},
          onChangeServer: () {},
          onRemoveServer: () {},
          onSignOut: () {},
          onStorage: () {},
          onControllers: () {},
          onEmulation: () {},
          onSwitchProfile: () {},
          onBack: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(
      find.byKey(const ValueKey<String>('settings-account-row')),
    );
    await tester.pumpAndSettle();

    expect(find.text('Local Profile'), findsOneWidget);
    expect(find.text('ROMD Account'), findsOneWidget);
    expect(find.text('Switch profile'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey<Object>('account')));
    await tester.pumpAndSettle();
    expect(find.text('Change server'), findsOneWidget);
    expect(find.text('Remove server'), findsOneWidget);
    expect(find.text('Server address'), findsOneWidget);
  });

  testWidgets('profile settings focus does not move row content', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ProfileSettingsScreen(
          localProfile: _twoProfiles.first,
          connected: false,
          onConnect: () {},
          onChangeServer: () {},
          onRemoveServer: () {},
          onSignOut: () {},
          onStorage: () {},
          onControllers: () {},
          onEmulation: () {},
          onSwitchProfile: () {},
          onBack: () {},
        ),
      ),
    );
    await tester.pumpAndSettle();

    final account = find.text('Profile & Account');
    final controllers = find.text('Controllers');
    final accountPosition = tester.getTopLeft(account);
    final controllersPosition = tester.getTopLeft(controllers);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pumpAndSettle();

    expect(tester.getTopLeft(account), accountPosition);
    expect(tester.getTopLeft(controllers), controllersPosition);
  });

  testWidgets('profile settings re-resolves shared skin roles at runtime', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(mode.dispose);

    await tester.pumpWidget(
      ValueListenableBuilder<ThemeMode>(
        valueListenable: mode,
        builder: (context, themeMode, _) => MaterialApp(
          theme: RomdSkins.seamTestTheme(),
          darkTheme: RomdSkins.baselineDark(),
          themeMode: themeMode,
          themeAnimationDuration: Duration.zero,
          home: ProfileSettingsScreen(
            localProfile: _twoProfiles.first,
            connected: false,
            onConnect: () {},
            onChangeServer: () {},
            onRemoveServer: () {},
            onSignOut: () {},
            onStorage: () {},
            onControllers: () {},
            onEmulation: () {},
            onSwitchProfile: () {},
            onBack: () {},
          ),
        ),
      ),
    );
    await tester.pump();

    final action = find.ancestor(
      of: find.text('Profile & Account'),
      matching: find.byType(AnimatedContainer),
    );
    final elementBefore = tester.element(action);
    final sizeBefore = tester.getSize(action);
    final containerBefore = tester.widget<AnimatedContainer>(action);
    final decorationBefore = containerBefore.decoration! as BoxDecoration;
    final textBefore = tester.widget<Text>(find.text('Profile & Account'));

    mode.value = ThemeMode.light;
    await tester.pump();

    final containerAfter = tester.widget<AnimatedContainer>(action);
    final decorationAfter = containerAfter.decoration! as BoxDecoration;
    final textAfter = tester.widget<Text>(find.text('Profile & Account'));
    await tester.pumpAndSettle();
    final sizeAfter = tester.getSize(action);
    expect(identical(elementBefore, tester.element(action)), isTrue);
    expect(sizeAfter, isNot(sizeBefore));
    expect(containerAfter.duration, isNot(containerBefore.duration));
    expect(decorationAfter.borderRadius, isNot(decorationBefore.borderRadius));
    expect(decorationAfter.color, isNot(decorationBefore.color));
    expect(textAfter.style!.fontSize, isNot(textBefore.style!.fontSize));
  });

  testWidgets('server origin editor remains usable at 1.5x text scale', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(textScaler: TextScaler.linear(1.5)),
          child: RomdServerOriginScreen(
            initialOrigin: RomdServerOrigins.defaultUri,
          ),
        ),
      ),
    );
    await tester.pump();

    expect(tester.takeException(), isNull);
    expect(find.text('ROMD server'), findsOneWidget);
    expect(find.text('Save'), findsOneWidget);
  });

  testWidgets('mounted server origin editor re-resolves shared skin roles', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(mode.dispose);
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      ValueListenableBuilder<ThemeMode>(
        valueListenable: mode,
        builder: (context, themeMode, child) => MaterialApp(
          theme: RomdSkins.seamTestTheme(),
          darkTheme: RomdSkins.baselineDark(),
          themeMode: themeMode,
          themeAnimationDuration: Duration.zero,
          home: child,
        ),
        child: RomdServerOriginScreen(
          initialOrigin: RomdServerOrigins.defaultUri,
        ),
      ),
    );
    await tester.pump();

    final screen = find.byType(RomdServerOriginScreen);
    final save = find.byType(FilledButton);
    final caret = find.byType(ConsoleBlinkingCaret);
    final caretContainer = find.descendant(
      of: caret,
      matching: find.byType(Container),
    );
    final elementBefore = tester.element(screen);
    final headerBefore = tester.widget<Text>(find.text('ROMD server')).style!;
    final saveSizeBefore = tester.getSize(save);
    final caretSizeBefore = tester.getSize(caret);
    final caretBefore = tester.widget<Container>(caretContainer);

    mode.value = ThemeMode.light;
    await tester.pump();

    final headerAfter = tester.widget<Text>(find.text('ROMD server')).style!;
    final caretAfter = tester.widget<Container>(caretContainer);
    expect(identical(elementBefore, tester.element(screen)), isTrue);
    expect(headerAfter.fontFamily, isNot(headerBefore.fontFamily));
    expect(tester.getSize(save), isNot(saveSizeBefore));
    expect(tester.getSize(caret), isNot(caretSizeBefore));
    expect(caretAfter.color, isNot(caretBefore.color));
  });

  testWidgets('DismissIntent on the login screen invokes onBack', (
    tester,
  ) async {
    var backCalled = false;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: RomdLoginScreen(
          localProfile: _twoProfiles.first,
          consumerApiClient: _NeverApiClient(),
          onAuthenticated: (_) {},
          onBack: () => backCalled = true,
        ),
      ),
    );
    await tester.pump();

    final context = tester.binding.focusManager.primaryFocus?.context;
    expect(context, isNotNull);
    Actions.maybeInvoke(context!, const DismissIntent());
    await tester.pump();

    expect(backCalled, isTrue);
  });

  testWidgets('DismissIntent cancels the server origin editor', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => RomdServerOriginScreen.show(
              context,
              initialOrigin: RomdServerOrigins.defaultUri,
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.byType(RomdServerOriginScreen), findsOneWidget);

    final context = tester.binding.focusManager.primaryFocus?.context;
    expect(context, isNotNull);
    Actions.maybeInvoke(context!, const DismissIntent());
    for (var i = 0; i < 40; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.byType(RomdServerOriginScreen), findsNothing);
  });
}

void _expectRectContained(WidgetTester tester, Finder child, Finder parent) {
  final childRect = tester.getRect(child);
  final parentRect = tester.getRect(parent);
  expect(childRect.left, greaterThanOrEqualTo(parentRect.left));
  expect(childRect.top, greaterThanOrEqualTo(parentRect.top));
  expect(childRect.right, lessThanOrEqualTo(parentRect.right));
  expect(childRect.bottom, lessThanOrEqualTo(parentRect.bottom));
}

LocalProfile _profile(
  String id,
  String name, {
  Uri? romdServerOrigin,
  RomdAccountLink? romdAccountLink,
}) => LocalProfile(
  id: id,
  displayName: name,
  avatarKey: 'orbit',
  accentColor: 0xff1fbf8f,
  romdServerOrigin: romdServerOrigin ?? RomdServerOrigins.defaultUri,
  entryMode: LocalProfileEntryMode.open,
  createdAt: DateTime(2026),
  updatedAt: DateTime(2026),
  romdAccountLink: romdAccountLink,
);

final List<LocalProfile> _twoProfiles = <LocalProfile>[
  _profile('a', 'Alice'),
  _profile('b', 'Bob'),
];

final class _FakeRepository implements LocalProfileRepository {
  _FakeRepository({required this.profiles});

  final List<LocalProfile> profiles;
  CreateLocalProfileRequest? lastCreated;

  @override
  Future<List<LocalProfile>> listProfiles() async => profiles;

  @override
  Stream<List<LocalProfile>> watchProfiles() => Stream.value(profiles);

  @override
  Future<LocalProfile> createProfile(CreateLocalProfileRequest request) async {
    lastCreated = request;
    return _profile(
      'new',
      request.displayName,
      romdServerOrigin: request.romdServerOrigin,
    );
  }

  @override
  Future<LocalProfile> linkRomdAccount({
    required String localProfileId,
    required RomdAccountLink accountLink,
  }) async => profiles.first;

  @override
  Future<LocalProfile> unlinkRomdAccount({
    required String localProfileId,
  }) async => profiles.first;

  @override
  Future<LocalProfile> updateRomdServerOrigin({
    required String localProfileId,
    required Uri? serverOrigin,
  }) async => _profile(
    localProfileId,
    profiles.first.displayName,
    romdServerOrigin: serverOrigin,
  );

  @override
  Future<void> markLastUsed(String profileId) async {}
}

// Never resolves its requests, so the login screen stays on the initial spinner
// (no poll timers) while we exercise the Esc shortcut.
final class _NeverApiClient implements ConsumerApiClient {
  @override
  Future<void> revokeSession({required String refreshToken}) async {}

  _NeverApiClient({this.authorization});

  final DeviceAuthorization? authorization;

  @override
  Future<ConsumerHostHealth> getHealth() async =>
      const ConsumerHostHealth(status: 'Healthy');

  @override
  Future<ConsoleGamePage> searchCatalog({
    required String accessToken,
    String? query,
    String? platformId,
    String? completeness,
    String? sortBy,
    String? cursor,
    int limit = 48,
  }) => Completer<ConsoleGamePage>().future;

  @override
  Future<ConsolePlatformPage> listPlatforms({
    required String accessToken,
    String? cursor,
    int limit = 50,
  }) => Completer<ConsolePlatformPage>().future;

  @override
  Future<List<ConsoleCollection>> listCollections({
    required String accessToken,
    String? platformId,
  }) => Completer<List<ConsoleCollection>>().future;

  @override
  Future<ConsoleGamePage> listCollectionTitles({
    required String accessToken,
    required String collectionId,
    String? cursor,
    int limit = 24,
  }) => Completer<ConsoleGamePage>().future;

  @override
  Future<ConsoleGameDetail> getTitle({
    required String accessToken,
    required String titleId,
  }) => Completer<ConsoleGameDetail>().future;

  @override
  Future<ConsoleReleaseManifest> issueReleaseManifest({
    required String accessToken,
    required String releaseId,
  }) => Completer<ConsoleReleaseManifest>().future;

  @override
  Future<List<BiosFileListing>> listPlatformBios({
    required String accessToken,
    required String platformShortName,
  }) => Completer<List<BiosFileListing>>().future;

  @override
  Future<DeviceAuthorizationResult> requestDeviceAuthorization() {
    final authorization = this.authorization;
    return authorization == null
        ? Completer<DeviceAuthorizationResult>().future
        : Future<DeviceAuthorizationResult>.value(
            DeviceAuthorizationSuccess(authorization),
          );
  }

  @override
  Future<DeviceTokenResult> redeemDeviceCode({required String deviceCode}) =>
      Completer<DeviceTokenResult>().future;

  @override
  Future<ConsumerLoginResult> refreshSession({required String refreshToken}) =>
      Completer<ConsumerLoginResult>().future;

  @override
  void close() {}
}
