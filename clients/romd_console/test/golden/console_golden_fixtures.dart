import 'package:flutter/material.dart';
import 'package:romd_console/src/data/local_profiles/local_profile_repository.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/connection_status_indicator.dart';
import 'package:romd_console/src/presentation/widgets/console_action_button.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';
import 'package:romd_console/src/presentation/widgets/console_clock.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';
import 'package:romd_console/src/presentation/widgets/console_page_band.dart';
import 'package:romd_console/src/presentation/widgets/console_status_state.dart';
import 'package:romd_console/src/presentation/widgets/cover_art.dart';
import 'package:romd_console/src/presentation/widgets/game_tile.dart';
import 'package:romd_console/src/presentation/widgets/meta_chip.dart';
import 'package:romd_console/src/presentation/widgets/platform_identity_plate.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

import 'console_golden_harness.dart';

/// Deterministic board for the shared experience-system primitives: the
/// Waterline page band (rest and anchored), the three-role action button,
/// the unified status states, and the font-verified footer keycaps. Shared
/// by both production skins' baseline goldens.
final class GoldenSystemComponentsBoard extends StatelessWidget {
  const GoldenSystemComponentsBoard({super.key});

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;

    Widget statusPanel(Widget child) => Expanded(
      child: Container(
        decoration: BoxDecoration(
          color: colors.panelSurface,
          borderRadius: BorderRadius.circular(layout.panelRadius),
          border: Border.all(color: colors.panelBorder),
        ),
        child: child,
      ),
    );

    return Scaffold(
      backgroundColor: context.theme.scaffoldBackgroundColor,
      body: ConsoleAmbientBackground(
        dimmed: true,
        child: Column(
          children: <Widget>[
            ConsolePageBand(
              title: 'Storage',
              eyebrow: 'SETTINGS',
              trailing: <Widget>[
                ConsoleBandStatusCluster(
                  connectionStatus: ConnectionStatus.connected,
                  serverHost: 'shelf.local',
                  clockNow: () => ConsoleGoldenHarness.fixedNow,
                ),
              ],
            ),
            ConsolePageBand(
              title: 'Catalog',
              anchored: true,
              switcher: ConsoleSegmentedSwitcher(
                segments: const <ConsoleSegment>[
                  ConsoleSegment(label: 'Featured'),
                  ConsoleSegment(label: 'Browse'),
                ],
                selectedIndex: 1,
                onSelected: (_) {},
              ),
              trailing: <Widget>[
                ConsoleBandStatusCluster(
                  connectionStatus: ConnectionStatus.offline,
                  serverHost: 'shelf.local',
                  clockNow: () => ConsoleGoldenHarness.fixedNow,
                ),
              ],
            ),
            Expanded(
              child: Padding(
                padding: EdgeInsets.symmetric(
                  horizontal: layout.screenGutter,
                  vertical: layout.lg,
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: <Widget>[
                    Text(
                      'ACTIONS',
                      style: context.text.sectionLabel.copyWith(
                        color: colors.textMuted,
                      ),
                    ),
                    SizedBox(height: layout.sm),
                    Row(
                      children: <Widget>[
                        ConsoleActionButton(
                          label: 'Play',
                          icon: Icons.play_arrow_rounded,
                          kind: ConsoleActionKind.primary,
                          autofocus: true,
                          onPressed: () {},
                        ),
                        SizedBox(width: layout.sm),
                        ConsoleActionButton(
                          label: 'Manage',
                          icon: Icons.tune_rounded,
                          onPressed: () {},
                        ),
                        SizedBox(width: layout.sm),
                        ConsoleActionButton(
                          label: 'Media',
                          icon: Icons.photo_library_outlined,
                          kind: ConsoleActionKind.quiet,
                          onPressed: () {},
                        ),
                        SizedBox(width: layout.sm),
                        ConsoleActionButton(
                          label: 'Remove',
                          icon: Icons.delete_outline_rounded,
                          destructive: true,
                          onPressed: () {},
                        ),
                        SizedBox(width: layout.sm),
                        const ConsoleActionButton(
                          label: 'Install',
                          kind: ConsoleActionKind.primary,
                          onPressed: null,
                        ),
                        SizedBox(width: layout.sm),
                        const ConsoleActionButton(
                          label: 'Preparing',
                          kind: ConsoleActionKind.primary,
                          busy: true,
                          onPressed: null,
                        ),
                      ],
                    ),
                    SizedBox(height: layout.lg),
                    Text(
                      'STATE CHIPS',
                      style: context.text.sectionLabel.copyWith(
                        color: colors.textMuted,
                      ),
                    ),
                    SizedBox(height: layout.sm),
                    // The detail state family on the colour contract: green
                    // marks READY only; awaiting-action states stay neutral.
                    Wrap(
                      spacing: layout.sm,
                      children: <Widget>[
                        MetaChip(
                          label: 'READY',
                          icon: Icons.circle,
                          color: colors.connected,
                        ),
                        const MetaChip(
                          label: 'ACCESS REQUIRED',
                          icon: Icons.lock_outline,
                        ),
                        const MetaChip(
                          label: 'NOT YET PLAYABLE',
                          icon: Icons.schedule,
                        ),
                        const MetaChip(label: 'SNES'),
                        const MetaChip(label: '1994', icon: Icons.event),
                      ],
                    ),
                    SizedBox(height: layout.lg),
                    Text(
                      'STATUS STATES',
                      style: context.text.sectionLabel.copyWith(
                        color: colors.textMuted,
                      ),
                    ),
                    SizedBox(height: layout.sm),
                    Expanded(
                      child: Row(
                        children: <Widget>[
                          statusPanel(
                            ConsoleStatusState(
                              icon: Icons.auto_awesome_rounded,
                              invitation: true,
                              title: 'Nothing here yet',
                              message:
                                  'Games you install appear here, ready '
                                  'to play.',
                              action: ConsoleActionButton(
                                label: 'Open Catalog',
                                icon: Icons.storefront_rounded,
                                onPressed: () {},
                              ),
                            ),
                          ),
                          SizedBox(width: layout.lg),
                          statusPanel(
                            const ConsoleStatusState(
                              loading: true,
                              title: 'Fetching your library',
                              message: 'Talking to shelf.local…',
                            ),
                          ),
                          SizedBox(width: layout.lg),
                          statusPanel(
                            ConsoleStatusState(
                              icon: Icons.error_outline_rounded,
                              title: 'Couldn\u2019t reach the catalog',
                              message:
                                  'Check the connection to shelf.local '
                                  'and try again.',
                              action: ConsoleActionButton(
                                label: 'Try again',
                                icon: Icons.refresh_rounded,
                                onPressed: () {},
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
            const ConsoleFooterBar(
              hints: <ConsoleHint>[
                ConsoleHint(
                  glyph: ConsoleHintGlyphs.navigate,
                  gamepadGlyph: 'D-PAD',
                  label: 'Navigate',
                ),
                ConsoleHint(
                  glyph: ConsoleHintGlyphs.confirm,
                  gamepadGlyph: 'A',
                  label: 'Open',
                ),
                ConsoleHint(glyph: 'Esc', gamepadGlyph: 'B', label: 'Back'),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// Deterministic Discover component board shared by every skin's baseline
/// goldens. All styling resolves through the active theme so the same board
/// screenshots each production skin faithfully.
final class GoldenCatalogComponentBoard extends StatelessWidget {
  const GoldenCatalogComponentBoard({
    required this.coverImage,
    required this.navNodes,
    super.key,
  });

  final ImageProvider<Object> coverImage;
  final List<FocusNode> navNodes;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    return Scaffold(
      backgroundColor: context.theme.scaffoldBackgroundColor,
      body: ConsoleAmbientBackground(
        dimmed: true,
        child: SafeArea(
          child: Padding(
            padding: EdgeInsets.symmetric(
              horizontal: layout.screenGutter,
              vertical: layout.lg,
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    Text('DISCOVER', style: context.text.pageHeading),
                    const Spacer(),
                    ConsoleClock(now: () => ConsoleGoldenHarness.fixedNow),
                  ],
                ),
                SizedBox(height: layout.lg),
                Row(
                  children: <Widget>[
                    LauncherNavChip(
                      label: 'FEATURED',
                      node: navNodes[0],
                      selected: true,
                      onActivate: () {},
                    ),
                    SizedBox(width: layout.xs),
                    LauncherNavChip(
                      label: 'ALL GAMES',
                      node: navNodes[1],
                      selected: false,
                      onActivate: () {},
                    ),
                    SizedBox(width: layout.xs),
                    LauncherNavChip(
                      label: 'SYSTEMS',
                      node: navNodes[2],
                      selected: false,
                      onActivate: () {},
                    ),
                  ],
                ),
                SizedBox(height: layout.xl),
                Expanded(
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: <Widget>[
                      SizedBox(
                        width: layout.gameRail.strip.tileWidth,
                        child: GameTile(
                          game: goldenGames[0],
                          heroTag: 'golden-chrono',
                          installed: true,
                          autofocus: true,
                          onPressed: () {},
                        ),
                      ),
                      SizedBox(width: layout.lg),
                      SizedBox(
                        width: layout.gameRail.strip.tileWidth,
                        child: GameTile(
                          game: goldenGames[1],
                          heroTag: 'golden-metroid',
                          supportingText: 'LAST PLAYED JUL 18 · SNES',
                          onPressed: () {},
                        ),
                      ),
                      SizedBox(width: layout.lg),
                      SizedBox(
                        width: layout.gameRail.strip.tileWidth,
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: <Widget>[
                            AspectRatio(
                              aspectRatio: kCoverAspectRatio,
                              child: CoverArt(
                                coverUrl: Uri.parse(
                                  'https://golden.invalid/fixture.png',
                                ),
                                imageProvider: coverImage,
                                platformId: 'ps1',
                                platformName: 'PlayStation',
                                title: 'Injected Cover',
                                borderRadius: layout.panelRadius,
                              ),
                            ),
                            SizedBox(height: layout.xs),
                            Text(
                              'DETERMINISTIC ART',
                              style: context.text.bodySmall!.copyWith(
                                color: colors.textMuted,
                              ),
                            ),
                          ],
                        ),
                      ),
                      const Spacer(),
                      const SizedBox(
                        width: 390,
                        child: Column(
                          children: <Widget>[
                            Expanded(
                              child: LauncherStatusPanel(
                                icon: Icons.cloud_off_rounded,
                                title: 'Library available offline',
                                message:
                                    'Installed titles stay playable while '
                                    'ROMD is unreachable.',
                              ),
                            ),
                          ],
                        ),
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
}

/// Representative platform marks with very wide, balanced, and near-square
/// source proportions. This catches logo-safe-area regressions that a single
/// system card cannot reveal.
final class GoldenPlatformLogoBoard extends StatelessWidget {
  const GoldenPlatformLogoBoard({super.key});

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    const platforms = <({String id, String name})>[
      (id: 'gba', name: 'Game Boy Advance'),
      (id: 'snes', name: 'Super Nintendo'),
      (id: 'jaguar', name: 'Atari Jaguar'),
    ];

    return Scaffold(
      backgroundColor: context.theme.scaffoldBackgroundColor,
      body: ConsoleAmbientBackground(
        dimmed: true,
        child: SafeArea(
          child: Padding(
            padding: EdgeInsets.symmetric(
              horizontal: layout.screenGutter,
              vertical: layout.xl,
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Text('PLATFORM MARKS', style: context.text.pageHeading),
                SizedBox(height: layout.xl),
                Expanded(
                  child: Row(
                    children: <Widget>[
                      for (
                        var index = 0;
                        index < platforms.length;
                        index++
                      ) ...[
                        Expanded(
                          child: Column(
                            mainAxisAlignment: MainAxisAlignment.center,
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: <Widget>[
                              AspectRatio(
                                aspectRatio: 1.7,
                                child: PlatformIdentityPlate(
                                  platformName: platforms[index].name,
                                  shortName: platforms[index].id,
                                  tone: platformPresentationFor(
                                    context,
                                    platformId: platforms[index].id,
                                    platformName: platforms[index].name,
                                  ).tone,
                                  focused: index == 1,
                                ),
                              ),
                              SizedBox(height: layout.sm),
                              Text(
                                platforms[index].name,
                                style: context.text.cardTitle,
                              ),
                            ],
                          ),
                        ),
                        if (index != platforms.length - 1)
                          SizedBox(width: layout.lg),
                      ],
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
}

/// Deterministic installs for the Storage baseline golden.
final class GoldenInstallService implements InstallService {
  GoldenInstallService(this.installs);

  final List<LocalInstall> installs;

  @override
  Future<LocalInstall?> findInstall(String releaseId) async {
    for (final install in installs) {
      if (install.releaseId == releaseId) return install;
    }
    return null;
  }

  @override
  Future<List<LocalInstall>> listInstalled() async => installs;
  @override
  Future<List<LocalInstall>> listInstalledForTitle(String titleId) async =>
      installs.where((install) => install.titleId == titleId).toList();
  @override
  Stream<List<LocalInstall>> watchInstalled() =>
      Stream<List<LocalInstall>>.value(installs);
  @override
  Stream<List<LocalInstall>> watchInstalledForTitle(String titleId) =>
      Stream<List<LocalInstall>>.value(
        installs.where((install) => install.titleId == titleId).toList(),
      );
  @override
  Stream<Set<String>> watchInstalledReleaseIds() => Stream<Set<String>>.value(
    installs.map((install) => install.releaseId).toSet(),
  );
  @override
  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  }) => const Stream<InstallProgress>.empty();
  @override
  Future<void> uninstall(
    String releaseId, {
    InstallOperationLease? operation,
  }) async {}
}

final List<LocalInstall> goldenInstalls = <LocalInstall>[
  _goldenInstall(
    id: 'chrono-trigger',
    title: 'Chrono Trigger',
    sizeBytes: 4194304,
    installedAt: DateTime.utc(2026, 6, 19),
  ),
  _goldenInstall(
    id: 'super-metroid',
    title: 'Super Metroid',
    sizeBytes: 3145728,
    installedAt: DateTime.utc(2026, 6, 12),
    lastPlayedAt: DateTime.utc(2026, 7, 18),
  ),
  _goldenInstall(
    id: 'legend-of-zelda',
    title: 'The Legend of Zelda',
    sizeBytes: 2097152,
    installedAt: DateTime.utc(2026, 5, 30),
  ),
];

LocalInstall _goldenInstall({
  required String id,
  required String title,
  required int sizeBytes,
  required DateTime installedAt,
  DateTime? lastPlayedAt,
}) => LocalInstall(
  serverInstanceId: '11111111-1111-4111-8111-111111111111',
  releaseId: '$id-r',
  titleId: id,
  titleName: title,
  platformId: 'snes',
  platformName: 'Super Nintendo',
  platformShortName: 'snes',
  coverUrl: null,
  releaseName: title,
  releaseRevision: null,
  contentRoot: '/content/$id',
  launchRelativePath: '$id.sfc',
  sizeBytes: sizeBytes,
  primarySha256: null,
  manifestFingerprint: 'fp-$id',
  state: InstallState.installed,
  installMode: 'permanent',
  items: <InstalledItem>[
    InstalledItem(relativePath: '$id.sfc', sizeBytes: sizeBytes, sha256: null),
  ],
  installedAt: installedAt,
  lastPlayedAt: lastPlayedAt,
);

final List<ConsoleGame> goldenGames = <ConsoleGame>[
  ConsoleGame(
    id: 'chrono-trigger',
    platformId: 'snes',
    platformName: 'Super Nintendo',
    title: 'Chrono Trigger',
    releaseDate: DateTime.utc(1995, 3, 11),
    coverUrl: null,
    genre: 'Role-playing',
    rating: 9.5,
    releaseCount: 1,
    defaultReleaseId: 'chrono-trigger-us',
  ),
  ConsoleGame(
    id: 'super-metroid',
    platformId: 'snes',
    platformName: 'Super Nintendo',
    title: 'Super Metroid',
    releaseDate: DateTime.utc(1994, 3, 19),
    coverUrl: null,
    genre: 'Action-adventure',
    rating: 9.2,
    releaseCount: 1,
    defaultReleaseId: 'super-metroid-us',
  ),
];

final List<LocalProfile> goldenProfiles = <LocalProfile>[
  LocalProfile(
    id: 'alice',
    displayName: 'Alice',
    avatarKey: 'orbit',
    accentColor: 0xff1fbf8f,
    romdServerOrigin: RomdServerOrigins.defaultUri,
    entryMode: LocalProfileEntryMode.open,
    createdAt: DateTime.utc(2026),
    updatedAt: DateTime.utc(2026),
  ),
  LocalProfile(
    id: 'bob',
    displayName: 'Bob',
    avatarKey: 'signal',
    accentColor: 0xffe6c074,
    romdServerOrigin: null,
    entryMode: LocalProfileEntryMode.open,
    createdAt: DateTime.utc(2026),
    updatedAt: DateTime.utc(2026),
  ),
];

final class GoldenProfileRepository implements LocalProfileRepository {
  const GoldenProfileRepository(this.profiles);

  final List<LocalProfile> profiles;

  @override
  Future<List<LocalProfile>> listProfiles() async => profiles;

  @override
  Stream<List<LocalProfile>> watchProfiles() => Stream.value(profiles);

  @override
  Future<LocalProfile> createProfile(CreateLocalProfileRequest request) async =>
      profiles.first;

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
  }) async => profiles.first;

  @override
  Future<void> markLastUsed(String profileId) async {}
}
