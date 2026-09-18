import 'dart:io';

import 'package:drift/drift.dart';
import 'package:flutter/foundation.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/data/release_access_api_client.dart';
import 'package:romd_console/src/data/release_manifest_api_client.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/server_bound_release_manifest.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/installed_play_target_resolver.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/local_install_repository.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

import '../../../domain/system_key.dart';
import 'content_file_store.dart';
import 'drift_local_install_repository.dart';
import 'install_mutation_serializer.dart';
import 'installed_artwork_store.dart';
import 'profile_game_cleanup_service.dart';

enum AcquisitionCheckpoint {
  beforeDeactivation,
  afterDeactivation,
  afterPromotion,
  beforeFinalCommit,
  beforeCorruptMark,
}

final class RomdInstallService
    implements InstallService, InstalledPlayTargetResolver {
  RomdInstallService({
    required ReleaseAccessApiClient releaseAccessApiClient,
    required ReleaseManifestApiClient releaseManifestApiClient,
    required AppDatabase database,
    required InstallAuthorityContext? authority,
    required DownloadClient downloadClient,
    required ContentVerifier verifier,
    required ContentFileStore fileStore,
    required LocalInstallRepository installs,
    required ProfileLocalLibraryRepository profileLocalLibrary,
    required InstallMutationSerializer mutationSerializer,
    required ProfileGameCleanupService cleanupService,
    AuthenticatedRequestExecutor? authenticatedSession,
    DateTime Function()? now,
    Future<void> Function(AcquisitionCheckpoint checkpoint)? checkpoint,
  }) : _access = releaseAccessApiClient,
       _manifests = releaseManifestApiClient,
       _database = database,
       _authority = authority,
       _authenticatedSession = authenticatedSession,
       _downloads = downloadClient,
       _verifier = verifier,
       _fileStore = fileStore,
       _installs = installs,
       _profileLocalLibrary = profileLocalLibrary,
       _mutationSerializer = mutationSerializer,
       _cleanupService = cleanupService,
       _now = now ?? DateTime.now,
       _checkpoint = checkpoint;

  final ReleaseAccessApiClient _access;
  final ReleaseManifestApiClient _manifests;
  final AppDatabase _database;
  final InstallAuthorityContext? _authority;
  final AuthenticatedRequestExecutor? _authenticatedSession;
  final DownloadClient _downloads;
  final ContentVerifier _verifier;
  final ContentFileStore _fileStore;
  final LocalInstallRepository _installs;
  final ProfileLocalLibraryRepository _profileLocalLibrary;
  final InstallMutationSerializer _mutationSerializer;
  final ProfileGameCleanupService _cleanupService;
  final DateTime Function() _now;
  final Future<void> Function(AcquisitionCheckpoint checkpoint)? _checkpoint;

  Future<T> _authenticated<T>(
    String? fallbackAccessToken,
    Future<T> Function(String accessToken) request, {
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  }) {
    final authenticatedSession = _authenticatedSession;
    if (authenticatedSession != null) {
      return authenticatedSession.execute<T>(
        request: request,
        // These calls are GETs or read-like POSTs. An invalid_token challenge
        // is emitted by authentication before the endpoint executes.
        replayPolicy: AuthenticatedReplayPolicy.onceAfterInvalidToken,
        isInvalidTokenError: isInvalidTokenError,
        isInvalidTokenResult: isInvalidTokenResult,
      );
    }
    if (fallbackAccessToken == null || fallbackAccessToken.isEmpty) {
      throw const SessionUnavailableException(
        SessionRefreshFailureKind.missingCredential,
      );
    }
    return request(fallbackAccessToken);
  }

  static const _manifestUnavailable = InstallFailed(
    InstallFailureKind.manifestUnavailable,
    "This release isn't available right now.",
  );
  static const _downloadFailed = InstallFailed(
    InstallFailureKind.downloadFailed,
    'Download failed. Check your connection and try again.',
  );
  static const _accessNotGranted = InstallFailed(
    InstallFailureKind.accessNotGranted,
    'Access is required from this ROMD Library.',
  );
  static const _authorityChanged = InstallFailed(
    InstallFailureKind.authorityChanged,
    'The selected ROMD server changed. Try again.',
  );

  @override
  Future<LocalInstall?> findInstall(String releaseId) async {
    final releases = await _profileLocalLibrary.readReleases();
    return switch (releases) {
      ProfileLocalReleasesReady(:final releases) =>
        releases
            .where(
              (release) =>
                  release.install.releaseId == releaseId &&
                  release.install.state == InstallState.installed,
            )
            .map((release) => release.install)
            .firstOrNull,
      ProfileLocalReleasesNoServer() ||
      ProfileLocalReleasesUnavailable() => null,
    };
  }

  @override
  Future<List<LocalInstall>> listInstalled() async =>
      _installedFrom(await _profileLocalLibrary.readReleases());

  @override
  Future<List<LocalInstall>> listInstalledForTitle(String titleId) async =>
      _installedFrom(
        await _profileLocalLibrary.readReleases(),
      ).where((install) => install.titleId == titleId).toList(growable: false);

  @override
  Stream<List<LocalInstall>> watchInstalled() =>
      _profileLocalLibrary.watchReleases().map(_installedFrom);

  @override
  Stream<List<LocalInstall>> watchInstalledForTitle(String titleId) =>
      watchInstalled().map(
        (installs) => installs
            .where((install) => install.titleId == titleId)
            .toList(growable: false),
      );

  @override
  Stream<Set<String>> watchInstalledReleaseIds() => watchInstalled().map(
    (installs) => installs.map((install) => install.releaseId).toSet(),
  );

  static List<LocalInstall> _installedFrom(ProfileLocalReleasesResult result) =>
      switch (result) {
        ProfileLocalReleasesReady(:final releases) =>
          releases
              .where(
                (release) => release.install.state == InstallState.installed,
              )
              .map((release) => release.install)
              .toList(growable: false),
        ProfileLocalReleasesNoServer() ||
        ProfileLocalReleasesUnavailable() => const <LocalInstall>[],
      };

  @override
  Future<ResolvedPlayTarget?> resolveInstalledForLaunch({
    required InstallAuthorityContext authority,
    required String releaseId,
    required String titleId,
    required String displayName,
  }) async {
    final composedAuthority = _authority;
    final parsedRelease = RomdPublicId.tryParse(releaseId);
    final parsedTitle = RomdPublicId.tryParse(titleId);
    if (composedAuthority == null ||
        !composedAuthority.hasSameAuthority(authority) ||
        parsedRelease == null ||
        parsedTitle == null) {
      return null;
    }
    if (!await _authorityIsCurrent(authority)) return null;
    return _resolveInstalledLocked(
      releaseId,
      titleId: titleId,
      displayName: displayName,
      localProfileId: authority.localProfileId,
    );
  }

  Future<ResolvedPlayTarget?> _resolveInstalledLocked(
    String releaseId, {
    required String titleId,
    required String displayName,
    required String localProfileId,
  }) async {
    final LocalInstall? install;
    try {
      install = await _installs.findByReleaseId(releaseId);
    } on LocalInstallProjectionException {
      return null;
    }
    if (install == null ||
        install.state != InstallState.installed ||
        install.titleId != titleId) {
      return null;
    }
    final canonicalRoot = _fileStore.contentRoot(
      platformShortName: install.platformShortName,
      titleId: install.titleId,
      releaseId: install.releaseId,
    );
    if (canonicalRoot.path != install.contentRoot) {
      return null;
    }
    if (!await _filesIntact(install)) {
      await _checkpoint?.call(AcquisitionCheckpoint.beforeCorruptMark);
      await _installs.updateState(releaseId, InstallState.corrupt);
      return null;
    }
    return _resolveInstalled(
      install,
      localProfileId: localProfileId,
      displayName: displayName,
    );
  }

  @override
  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  }) async* {
    if (!_operationIsCurrent(operation)) {
      yield _authorityChanged;
      return;
    }
    final authority = _authority;
    final releaseId = RomdPublicId.tryParse(target.releaseId);
    if (authority == null || releaseId == null) {
      yield* _installLocked(
        target,
        fallbackAccessToken: accessToken,
        operation: operation,
      );
      return;
    }

    final lease = await _mutationSerializer.acquire(
      InstallMutationKey(
        serverInstanceId: authority.connection.instanceId,
        releaseId: releaseId,
      ),
    );
    try {
      yield* _installLocked(
        target,
        fallbackAccessToken: accessToken,
        operation: operation,
      );
    } finally {
      lease.release();
    }
  }

  Stream<InstallProgress> _installLocked(
    PlayTarget target, {
    required String? fallbackAccessToken,
    required InstallOperationLease operation,
  }) async* {
    yield const InstallStarted();

    if (!_operationIsCurrent(operation)) {
      yield _authorityChanged;
      return;
    }

    final authority = _authority;
    final releaseId = RomdPublicId.tryParse(target.releaseId);
    final titleId = RomdPublicId.tryParse(target.titleId);
    final systemKey = target.platformId;
    if (authority == null ||
        target.localProfileId != authority.localProfileId ||
        releaseId == null ||
        titleId == null ||
        !isSystemKey(systemKey)) {
      yield _authorityChanged;
      return;
    }

    final ReleaseAccessResult access;
    try {
      access = await _authenticated(
        fallbackAccessToken,
        (accessToken) => _access.getReleaseAccess(
          accessToken: accessToken,
          expectedServerInstanceId: authority.connection.instanceId,
          releaseId: releaseId,
          expectedTitleId: titleId,
        ),
        isInvalidTokenResult: (result) =>
            result is ReleaseAccessFailure && result.isInvalidToken,
      );
    } on Object {
      yield _manifestUnavailable;
      return;
    }
    if (!_operationIsCurrent(operation)) {
      yield _authorityChanged;
      return;
    }
    switch (access) {
      case ReleaseAccessSuccess(decision: ReleaseAccessAllowed()):
        break;
      case ReleaseAccessSuccess(decision: ReleaseAccessRevoked()):
        yield _accessNotGranted;
        return;
      case ReleaseAccessFailure():
        yield _manifestUnavailable;
        return;
    }

    ReleaseManifestResult manifestResult;
    try {
      manifestResult = await _authenticated(
        fallbackAccessToken,
        (accessToken) => _manifests.getReleaseManifest(
          accessToken: accessToken,
          expectedServerInstanceId: authority.connection.instanceId,
          releaseId: releaseId,
          expectedTitleId: titleId,

          expectedSystemKey: systemKey!,
        ),
        isInvalidTokenResult: (result) =>
            result is ReleaseManifestFailure && result.isInvalidToken,
      );
    } on Object {
      yield _manifestUnavailable;
      return;
    }
    if (!_operationIsCurrent(operation)) {
      yield _authorityChanged;
      return;
    }
    if (manifestResult is! ReleaseManifestSuccess) {
      yield _manifestUnavailable;
      return;
    }
    var manifest = manifestResult.manifest;

    var manifestRefreshed = false;

    while (true) {
      final failure = _validate(manifest);
      if (failure != null) {
        if (kDebugMode) {
          debugPrint(
            '[play] manifest rejected: ${failure.kind} '
            '(packaging=${manifest.runtime.packaging}, '
            'hasLaunch=${manifest.runtime.launch != null}, '
            'isComplete=${manifest.isComplete}, items=${manifest.items.length})',
          );
        }
        yield failure;
        return;
      }

      final fingerprint = _verifier.manifestFingerprint(
        _fingerprintItems(manifest),
      );
      final resolved = _resolve(target, manifest);

      LocalInstall? existing;
      try {
        existing = await _installs.findByReleaseId(manifest.releaseId.value);
      } on LocalInstallProjectionException {
        // A corrupt projection is never usable proof. Acquisition may replace
        // it only after current authoritative verification succeeds.
      }
      if (existing != null && existing.state == InstallState.installed) {
        if (_matchesCurrentManifest(
              existing: existing,
              target: target,
              manifest: manifest,
              resolved: resolved,
              fingerprint: fingerprint,
            ) &&
            await _filesVerifiedAgainstManifest(
              manifest,
              Directory(resolved.contentRoot),
            )) {
          final canonical = _toInstall(
            target,
            manifest,
            resolved,
            fingerprint,
            previous: existing,
          );
          if (!await _commitAcquisition(
            canonical,
            authority: authority,
            operation: operation,
          )) {
            yield _authorityChanged;
            return;
          }
          yield InstallCompleted(resolved);
          return;
        }
      }

      final staging = _fileStore.stagingRoot(
        platformShortName: manifest.systemKey,
        titleId: manifest.titleId.value,
        releaseId: manifest.releaseId.value,
      );

      final pathsDistinct = await _fileStore.pathsAreDistinctOnTarget(
        scratch: _fileStore.preflightRoot(
          platformShortName: manifest.systemKey,
          titleId: manifest.titleId.value,
          releaseId: manifest.releaseId.value,
        ),
        relativePaths: manifest.items.map((item) => item.relativePath),
      );
      if (!pathsDistinct) {
        yield const InstallFailed(
          InstallFailureKind.unsupportedManifest,
          'These game files conflict on this device filesystem.',
        );
        return;
      }

      if (await _stageLegacyCandidate(manifest: manifest, staging: staging)) {
        yield const InstallVerifying();
        final install = _toInstall(
          target,
          manifest,
          resolved,
          fingerprint,
          previous: _canPreserveTimestamps(existing, manifest)
              ? existing
              : null,
        );
        final outcome = await _finalizeAcquisition(
          staging: staging,
          resolved: resolved,
          manifest: manifest,
          install: install,
          previous: existing,
          authority: authority,
          operation: operation,
        );
        switch (outcome) {
          case _FinalizeOutcome.completed:
            yield InstallCompleted(resolved);
          case _FinalizeOutcome.authorityChanged:
            yield _authorityChanged;
          case _FinalizeOutcome.ioError:
            yield const InstallFailed(
              InstallFailureKind.ioError,
              "Couldn't finalize the install.",
            );
        }
        return;
      }

      try {
        await _resetDir(staging);
      } on Object {
        yield const InstallFailed(
          InstallFailureKind.ioError,
          "Couldn't prepare local storage.",
        );
        return;
      }

      try {
        final total = manifest.items.fold<int>(
          0,
          (sum, i) => sum + i.sizeBytes,
        );
        var completedBytes = 0;
        var grantExpired = false;

        try {
          for (final item in manifest.items) {
            final destination = _fileStore.resolveWithin(
              staging,
              item.relativePath,
            );
            await for (final received in _downloads.download(
              url: item.contentGrant!.downloadUrl,
              destination: destination,
            )) {
              yield InstallDownloading(
                receivedBytes: completedBytes + received,
                totalBytes: total,
              );
            }
            completedBytes += item.sizeBytes;
          }
        } on DownloadGrantExpired {
          grantExpired = true;
        } on DownloadException {
          await _safeDelete(staging);
          yield _downloadFailed;
          return;
        } on Object {
          await _safeDelete(staging);
          yield const InstallFailed(
            InstallFailureKind.ioError,
            "Couldn't write game files.",
          );
          return;
        }

        if (grantExpired) {
          await _safeDelete(staging);
          if (manifestRefreshed) {
            yield _downloadFailed;
            return;
          }
          manifestRefreshed = true;
          try {
            manifestResult = await _authenticated(
              fallbackAccessToken,
              (accessToken) => _manifests.getReleaseManifest(
                accessToken: accessToken,
                expectedServerInstanceId: authority.connection.instanceId,
                releaseId: releaseId,
                expectedTitleId: titleId,

                expectedSystemKey: systemKey!,
              ),
              isInvalidTokenResult: (result) =>
                  result is ReleaseManifestFailure && result.isInvalidToken,
            );
            if (!_operationIsCurrent(operation)) {
              yield _authorityChanged;
              return;
            }
            if (manifestResult is! ReleaseManifestSuccess) {
              yield _manifestUnavailable;
              return;
            }
            manifest = manifestResult.manifest;
          } on Object {
            yield _manifestUnavailable;
            return;
          }
          continue; // revalidate + re-stage with fresh grants
        }

        yield const InstallVerifying();
        for (final item in manifest.items) {
          final staged = _fileStore.resolveWithin(staging, item.relativePath);
          final ok = await _verifier.verify(
            file: staged,
            expectedSize: item.sizeBytes,
            expectedSha256Hex: item.sha256!,
          );
          if (!ok) {
            await _safeDelete(staging);
            yield const InstallFailed(
              InstallFailureKind.hashMismatch,
              'Downloaded files were corrupt and have been discarded.',
            );
            return;
          }
        }

        final install = _toInstall(
          target,
          manifest,
          resolved,
          fingerprint,
          previous: _canPreserveTimestamps(existing, manifest)
              ? existing
              : null,
        );
        final outcome = await _finalizeAcquisition(
          staging: staging,
          resolved: resolved,
          manifest: manifest,
          install: install,
          previous: existing,
          authority: authority,
          operation: operation,
        );
        switch (outcome) {
          case _FinalizeOutcome.completed:
            yield InstallCompleted(resolved);
          case _FinalizeOutcome.authorityChanged:
            yield _authorityChanged;
          case _FinalizeOutcome.ioError:
            yield const InstallFailed(
              InstallFailureKind.ioError,
              "Couldn't finalize the install.",
            );
        }
        return;
      } finally {
        // Async-generator cancellation can occur at any progress yield. The
        // staging path is always safe to delete; after materialization it no
        // longer exists.
        await _safeDelete(staging);
      }
    }
  }

  @override
  Future<void> uninstall(
    String releaseId, {
    InstallOperationLease? operation,
  }) => _cleanupService.removeProfileGame(
    authority: _authority,
    releaseId: releaseId,
    operation: operation,
  );

  InstallFailed? _validate(ServerBoundReleaseManifest m) {
    if (m.runtime.packaging != 'direct_files' || m.runtime.launch == null) {
      return const InstallFailed(
        InstallFailureKind.unsupportedManifest,
        "This title isn't supported on this device yet.",
      );
    }
    final fullyAvailable =
        m.isComplete &&
        m.items.isNotEmpty &&
        m.items.every(
          (i) => i.isAvailable && i.sha256 != null && i.contentGrant != null,
        );
    if (!fullyAvailable) {
      return const InstallFailed(
        InstallFailureKind.contentUnavailable,
        "This release isn't fully available yet.",
      );
    }
    return null;
  }

  Iterable<FingerprintItem> _fingerprintItems(ServerBoundReleaseManifest m) =>
      m.items.map(
        (i) => (
          relativePath: i.relativePath,
          sizeBytes: i.sizeBytes,
          sha256: i.sha256,
        ),
      );

  ResolvedPlayTarget _resolve(PlayTarget target, ServerBoundReleaseManifest m) {
    final content = _fileStore.contentRoot(
      platformShortName: m.systemKey,
      titleId: m.titleId.value,
      releaseId: m.releaseId.value,
    );
    final launch = _fileStore.resolveWithin(
      content,
      m.runtime.launch!.relativePath,
    );
    return ResolvedPlayTarget(
      serverInstanceId: m.serverInstanceId.value,
      releaseId: m.releaseId.value,
      titleId: m.titleId.value,
      platformShortName: m.systemKey,
      displayName: target.displayName,
      localProfileId: target.localProfileId,
      contentRoot: content.path,
      launchAbsolutePath: launch.path,
      saveRoot: _fileStore
          .saveRoot(
            localProfileId: target.localProfileId,
            platformShortName: m.systemKey,
            titleId: m.titleId.value,
          )
          .path,
      stateRoot: _fileStore
          .stateRoot(
            localProfileId: target.localProfileId,
            platformShortName: m.systemKey,
            titleId: m.titleId.value,
          )
          .path,
      configRoot: _fileStore
          .configRoot(platformShortName: m.systemKey, titleId: m.titleId.value)
          .path,
    );
  }

  ResolvedPlayTarget _resolveInstalled(
    LocalInstall install, {
    required String localProfileId,
    required String displayName,
  }) {
    final content = Directory(install.contentRoot);
    final launch = _fileStore.resolveWithin(
      content,
      install.launchRelativePath,
    );
    return ResolvedPlayTarget(
      serverInstanceId: install.serverInstanceId,
      releaseId: install.releaseId,
      titleId: install.titleId,
      platformShortName: install.platformShortName,
      displayName: displayName,
      localProfileId: localProfileId,
      contentRoot: install.contentRoot,
      launchAbsolutePath: launch.path,
      saveRoot: _fileStore
          .saveRoot(
            localProfileId: localProfileId,
            platformShortName: install.platformShortName,
            titleId: install.titleId,
          )
          .path,
      stateRoot: _fileStore
          .stateRoot(
            localProfileId: localProfileId,
            platformShortName: install.platformShortName,
            titleId: install.titleId,
          )
          .path,
      configRoot: _fileStore
          .configRoot(
            platformShortName: install.platformShortName,
            titleId: install.titleId,
          )
          .path,
    );
  }

  Future<bool> _filesIntact(LocalInstall install) async {
    final root = Directory(install.contentRoot);
    final launchFile = _fileStore.resolveWithin(
      root,
      install.launchRelativePath,
    );
    if (!await launchFile.exists()) {
      return false;
    }
    for (final item in install.items) {
      final file = _fileStore.resolveWithin(root, item.relativePath);
      if (!await file.exists() || await file.length() != item.sizeBytes) {
        return false;
      }
    }
    return true;
  }

  bool _matchesCurrentManifest({
    required LocalInstall existing,
    required PlayTarget target,
    required ServerBoundReleaseManifest manifest,
    required ResolvedPlayTarget resolved,
    required String fingerprint,
  }) {
    final aggregateSize = manifest.items.fold<int>(
      0,
      (sum, item) => sum + item.sizeBytes,
    );
    final storedFingerprint = _verifier.manifestFingerprint(
      existing.items.map(
        (item) => (
          relativePath: item.relativePath,
          sizeBytes: item.sizeBytes,
          sha256: item.sha256,
        ),
      ),
    );
    final currentItems = <String, (int, String?)>{
      for (final item in manifest.items)
        item.relativePath: (item.sizeBytes, item.sha256),
    };
    final storedItems = <String, (int, String?)>{
      for (final item in existing.items)
        item.relativePath: (item.sizeBytes, item.sha256),
    };
    return existing.serverInstanceId == manifest.serverInstanceId.value &&
        existing.releaseId == manifest.releaseId.value &&
        existing.titleId == manifest.titleId.value &&
        target.platformId == manifest.systemKey &&
        existing.platformId == manifest.systemKey &&
        existing.platformShortName == manifest.systemKey &&
        existing.contentRoot == resolved.contentRoot &&
        existing.launchRelativePath == manifest.runtime.launch!.relativePath &&
        existing.installMode == 'permanent' &&
        existing.sizeBytes == aggregateSize &&
        existing.manifestFingerprint == storedFingerprint &&
        storedFingerprint == fingerprint &&
        _sameItemSet(storedItems, currentItems);
  }

  bool _canPreserveTimestamps(
    LocalInstall? existing,
    ServerBoundReleaseManifest manifest,
  ) =>
      existing != null &&
      existing.serverInstanceId == manifest.serverInstanceId.value &&
      existing.releaseId == manifest.releaseId.value &&
      existing.titleId == manifest.titleId.value &&
      existing.platformId == manifest.systemKey;

  bool _sameItemSet(
    Map<String, (int, String?)> left,
    Map<String, (int, String?)> right,
  ) =>
      left.length == right.length &&
      left.entries.every((entry) => right[entry.key] == entry.value);

  Future<bool> _filesVerifiedAgainstManifest(
    ServerBoundReleaseManifest manifest,
    Directory root,
  ) async {
    for (final item in manifest.items) {
      final sha256 = item.sha256;
      if (sha256 == null ||
          !await _verifier.verify(
            file: _fileStore.resolveWithin(root, item.relativePath),
            expectedSize: item.sizeBytes,
            expectedSha256Hex: sha256,
          )) {
        return false;
      }
    }
    return true;
  }

  Future<bool> _stageLegacyCandidate({
    required ServerBoundReleaseManifest manifest,
    required Directory staging,
  }) async {
    final List<LegacyLocalInstallRow> rows;
    try {
      rows = await (_database.select(
        _database.legacyLocalInstalls,
      )..where((row) => row.releaseId.equals(manifest.releaseId.value))).get();
    } on Object {
      return false;
    }
    if (rows.length != 1) return false;
    final row = rows.single;
    final legacyRoot = _fileStore.legacyContentRoot(
      platformShortName: manifest.systemKey,
      titleId: manifest.titleId.value,
      releaseId: manifest.releaseId.value,
    );
    // Migration preserves contentRoot as evidence, not as a trusted path. Only
    // the independently derived historical ROMD-managed canonical root may be
    // read. Noncanonical rows stay hidden and untouched for later explicit
    // administrator/orphan handling; never construct a Directory from the row.
    if (row.releaseId != manifest.releaseId.value ||
        row.titleId != manifest.titleId.value ||
        row.platformId != manifest.systemKey ||
        row.platformShortName != manifest.systemKey ||
        row.contentRoot != legacyRoot.path ||
        row.state != InstallState.installed.name ||
        row.installMode != 'permanent') {
      return false;
    }

    try {
      await _resetDir(staging);
      for (final item in manifest.items) {
        final source = _fileStore.resolveWithin(legacyRoot, item.relativePath);
        final destination = _fileStore.resolveWithin(
          staging,
          item.relativePath,
        );
        await destination.parent.create(recursive: true);
        await source.copy(destination.path);
      }
      if (await _filesVerifiedAgainstManifest(manifest, staging)) return true;
    } on Object {
      // Invalid legacy evidence falls through to a normal verified download.
    }
    await _safeDelete(staging);
    return false;
  }

  Future<_FinalizeOutcome> _finalizeAcquisition({
    required Directory staging,
    required ResolvedPlayTarget resolved,
    required ServerBoundReleaseManifest manifest,
    required LocalInstall install,
    required LocalInstall? previous,
    required InstallAuthorityContext authority,
    required InstallOperationLease operation,
  }) async {
    _MaterializationSwap? swap;
    var deactivated = false;
    try {
      await _checkpoint?.call(AcquisitionCheckpoint.beforeDeactivation);
      if (!_operationIsCurrent(operation)) {
        return _FinalizeOutcome.authorityChanged;
      }
      final deleted =
          await (_database.delete(_database.localInstalls)..where(
                (row) =>
                    row.serverInstanceId.equals(
                      authority.connection.instanceId.value,
                    ) &
                    row.releaseId.equals(manifest.releaseId.value),
              ))
              .go();
      deactivated = deleted > 0;
      await _checkpoint?.call(AcquisitionCheckpoint.afterDeactivation);

      swap = await _materialize(
        staging: staging,
        finalRoot: Directory(resolved.contentRoot),
        backup: _fileStore.backupRoot(
          platformShortName: manifest.systemKey,
          titleId: manifest.titleId.value,
          releaseId: manifest.releaseId.value,
        ),
      );
      await _checkpoint?.call(AcquisitionCheckpoint.afterPromotion);
      await _checkpoint?.call(AcquisitionCheckpoint.beforeFinalCommit);
      if (!await _commitAcquisition(
        install,
        authority: authority,
        operation: operation,
      )) {
        await swap.rollback();
        await _restorePrevious(previous, deactivated: deactivated);
        return _FinalizeOutcome.authorityChanged;
      }
      await swap.commit();
      return _FinalizeOutcome.completed;
    } on Object {
      await swap?.rollback();
      await _restorePrevious(previous, deactivated: deactivated);
      return _FinalizeOutcome.ioError;
    }
  }

  Future<void> _restorePrevious(
    LocalInstall? previous, {
    required bool deactivated,
  }) async {
    if (!deactivated || previous == null) return;
    try {
      await _installs.upsert(previous);
    } on Object {
      // No active row is safer than a stale row pointing at replaced files.
    }
  }

  Future<bool> _commitAcquisition(
    LocalInstall install, {
    required InstallAuthorityContext authority,
    required InstallOperationLease operation,
  }) async {
    try {
      final committed = await _database.transaction(() async {
        if (!_operationIsCurrent(operation)) {
          throw const _InstallOperationInvalidated();
        }
        final profiles = await (_database.select(
          _database.localProfiles,
        )..where((row) => row.id.equals(authority.localProfileId))).get();
        if (profiles.length != 1) {
          return false;
        }
        final profile = profiles.single;
        if (profile.selectedServerInstanceId !=
                authority.connection.instanceId.value ||
            profile.serverSelectionGeneration != authority.generation) {
          return false;
        }
        final connections =
            await (_database.select(_database.serverConnections)..where(
                  (row) => row.instanceId.equals(
                    authority.connection.instanceId.value,
                  ),
                ))
                .get();
        if (connections.length != 1 ||
            connections.single.lastKnownOrigin !=
                authority.connection.origin.toString()) {
          return false;
        }

        if (!_operationIsCurrent(operation)) {
          throw const _InstallOperationInvalidated();
        }

        final existingGames =
            await (_database.select(_database.profileLocalGames)..where(
                  (row) =>
                      row.localProfileId.equals(authority.localProfileId) &
                      row.serverInstanceId.equals(
                        authority.connection.instanceId.value,
                      ) &
                      row.releaseId.equals(install.releaseId),
                ))
                .get();
        if (existingGames.length > 1 ||
            (existingGames.isNotEmpty &&
                existingGames.single.titleId != install.titleId)) {
          return false;
        }

        await _installs.upsert(install);
        if (!_operationIsCurrent(operation)) {
          throw const _InstallOperationInvalidated();
        }
        final checkedAt = _now();
        final acquiredAt = existingGames.isEmpty
            ? checkedAt
            : existingGames.single.acquiredAt;
        await _database
            .into(_database.profileLocalGames)
            .insertOnConflictUpdate(
              ProfileLocalGamesCompanion.insert(
                localProfileId: authority.localProfileId,
                serverInstanceId: authority.connection.instanceId.value,
                releaseId: install.releaseId,
                titleId: install.titleId,
                authorizationState: ProfileGameAuthorization.authorized.name,
                acquiredAt: acquiredAt,
                lastCheckedAt: checkedAt,
              ),
            );
        if (!_operationIsCurrent(operation)) {
          throw const _InstallOperationInvalidated();
        }
        return true;
      });
      if (!committed) return false;
      // Only committed installs retain artwork. A cache failure after this point
      // must never trigger ROM rollback or undo the successful acquisition.
      try {
        if (install.artwork.isNotEmpty) {
          await InstalledArtworkStore(_fileStore.baseDir).retain(
            server: install.serverInstanceId,
            release: install.releaseId,
            origin: authority.connection.origin,
            artwork: install.artwork,
          );
          // Re-emit the unchanged projection so installed rails read the new
          // manifest. The caller still owns the release mutation lease.
          await _installs.upsert(install);
        }
      } on Object {
        // Verified installed content remains usable without artwork.
      }
      return true;
    } on Object {
      return false;
    }
  }

  static bool _operationIsCurrent(InstallOperationLease operation) =>
      operation.isCurrent;

  Future<bool> _authorityIsCurrent(InstallAuthorityContext authority) async {
    try {
      final profiles = await (_database.select(
        _database.localProfiles,
      )..where((row) => row.id.equals(authority.localProfileId))).get();
      if (profiles.length != 1) return false;
      final profile = profiles.single;
      if (profile.selectedServerInstanceId !=
              authority.connection.instanceId.value ||
          profile.serverSelectionGeneration != authority.generation) {
        return false;
      }
      final connections =
          await (_database.select(_database.serverConnections)..where(
                (row) => row.instanceId.equals(
                  authority.connection.instanceId.value,
                ),
              ))
              .get();
      return connections.length == 1 &&
          connections.single.lastKnownOrigin ==
              authority.connection.origin.toString();
    } on Object {
      return false;
    }
  }

  LocalInstall _toInstall(
    PlayTarget target,
    ServerBoundReleaseManifest m,
    ResolvedPlayTarget resolved,
    String fingerprint, {
    LocalInstall? previous,
  }) {
    return LocalInstall(
      artwork: target.artwork,
      serverInstanceId: m.serverInstanceId.value,
      releaseId: m.releaseId.value,
      titleId: m.titleId.value,
      titleName: target.displayName,
      platformId: target.platformId ?? m.systemKey,
      platformName: target.platformName,
      platformShortName: m.systemKey,
      coverUrl: target.coverUrl,
      releaseName: target.releaseName ?? m.name,
      releaseRevision: target.releaseRevision ?? m.revision,
      contentRoot: resolved.contentRoot,
      launchRelativePath: m.runtime.launch!.relativePath,
      sizeBytes: m.items.fold<int>(0, (sum, i) => sum + i.sizeBytes),
      primarySha256: m.items.length == 1 ? m.items.first.sha256 : null,
      manifestFingerprint: fingerprint,
      state: InstallState.installed,
      installMode: 'permanent',
      items: m.items
          .map(
            (i) => InstalledItem(
              relativePath: i.relativePath,
              sizeBytes: i.sizeBytes,
              sha256: i.sha256,
            ),
          )
          .toList(),
      installedAt: previous?.installedAt ?? _now(),
      lastPlayedAt: previous?.lastPlayedAt,
    );
  }

  /// Atomic replace with rollback: back up any existing final root, move staging
  /// into place, then drop the backup. On failure, restore the backup.
  Future<_MaterializationSwap> _materialize({
    required Directory staging,
    required Directory finalRoot,
    required Directory backup,
  }) async {
    await _safeDelete(backup);
    final finalExisted = await finalRoot.exists();
    if (finalExisted) {
      await finalRoot.rename(backup.path);
    }
    try {
      await staging.rename(finalRoot.path);
    } on Object {
      if (finalExisted) {
        await _safeDelete(finalRoot);
        await backup.rename(finalRoot.path);
      }
      rethrow;
    }
    return _MaterializationSwap(
      finalRoot: finalRoot,
      backup: backup,
      hadPrevious: finalExisted,
      safeDelete: _safeDelete,
    );
  }

  Future<void> _resetDir(Directory dir) async {
    if (await dir.exists()) {
      await dir.delete(recursive: true);
    }
    await dir.create(recursive: true);
  }

  Future<void> _safeDelete(FileSystemEntity entity) async {
    try {
      if (await entity.exists()) {
        await entity.delete(recursive: true);
      }
    } on Object {
      // Best-effort cleanup.
    }
  }
}

enum _FinalizeOutcome { completed, authorityChanged, ioError }

final class _InstallOperationInvalidated implements Exception {
  const _InstallOperationInvalidated();
}

final class _MaterializationSwap {
  const _MaterializationSwap({
    required this.finalRoot,
    required this.backup,
    required this.hadPrevious,
    required this.safeDelete,
  });

  final Directory finalRoot;
  final Directory backup;
  final bool hadPrevious;
  final Future<void> Function(FileSystemEntity) safeDelete;

  Future<void> commit() => safeDelete(backup);

  Future<void> rollback() async {
    await safeDelete(finalRoot);
    if (hadPrevious && await backup.exists()) {
      await backup.rename(finalRoot.path);
    }
  }
}
