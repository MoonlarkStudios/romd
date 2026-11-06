import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/data/release_access_api_client.dart';
import 'package:romd_console/src/data/release_manifest_api_client.dart';
import 'package:romd_console/src/domain/content_file_provenance.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/rom_delivery_format.dart';
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
import 'package:romd_console/src/play/content/domain/release_install_plan.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';

import '../../../domain/system_key.dart';
import 'acquisition_checkpoint.dart';
import 'content_file_store.dart';
import 'drift_local_install_repository.dart';
import 'install_mutation_serializer.dart';
import 'profile_game_cleanup_service.dart';
import 'release_acquisition_committer.dart';

export 'acquisition_checkpoint.dart';

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
    RuntimeResolver? runtimeResolver,
    DateTime Function()? now,
    Future<void> Function(AcquisitionCheckpoint checkpoint)? checkpoint,
  }) : _access = releaseAccessApiClient,
       _manifests = releaseManifestApiClient,
       _database = database,
       _authority = authority,
       _authenticatedSession = authenticatedSession,
       _runtimeResolver = runtimeResolver,
       _downloads = downloadClient,
       _verifier = verifier,
       _fileStore = fileStore,
       _installs = installs,
       _profileLocalLibrary = profileLocalLibrary,
       _mutationSerializer = mutationSerializer,
       _cleanupService = cleanupService,
       _now = now ?? DateTime.now,
       _checkpoint = checkpoint,
       _acquisitions = ReleaseAcquisitionCommitter(
         database: database,
         installs: installs,
         fileStore: fileStore,
         now: now ?? DateTime.now,
         checkpoint: checkpoint,
       );

  final ReleaseAccessApiClient _access;
  final ReleaseManifestApiClient _manifests;
  final AppDatabase _database;
  final InstallAuthorityContext? _authority;
  final AuthenticatedRequestExecutor? _authenticatedSession;

  /// The resolver the launch path uses; null composes Original-only installs.
  final RuntimeResolver? _runtimeResolver;
  final DownloadClient _downloads;
  final ContentVerifier _verifier;
  final ContentFileStore _fileStore;
  final LocalInstallRepository _installs;
  final ProfileLocalLibraryRepository _profileLocalLibrary;
  final InstallMutationSerializer _mutationSerializer;
  final ProfileGameCleanupService _cleanupService;
  final DateTime Function() _now;
  final Future<void> Function(AcquisitionCheckpoint checkpoint)? _checkpoint;
  final ReleaseAcquisitionCommitter _acquisitions;

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
  static const _deliveryChanged = InstallFailed(
    InstallFailureKind.contentUnavailable,
    'This release changed while installing. Try again.',
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
  }) {
    final current = _InstallOperation(operation);
    late StreamController<InstallProgress> progress;
    late StreamSubscription<InstallProgress> subscription;
    progress = StreamController<InstallProgress>(
      sync: true,
      onListen: () {
        subscription =
            _installWithLease(
              target,
              operation: current,
              accessToken: accessToken,
            ).listen(
              progress.add,
              onError: progress.addError,
              onDone: () => unawaited(progress.close()),
            );
      },
      onPause: () => subscription.pause(),
      onResume: () => subscription.resume(),
      onCancel: () async {
        current.cancelled = true;
        final stopped = subscription.cancel();
        try {
          // An async* generator waiting for an event does not immediately cancel
          // its inner stream. Wake that wait before awaiting generator cleanup.
          await current.download?.cancel();
        } finally {
          await stopped;
        }
      },
    );
    return progress.stream;
  }

  Stream<InstallProgress> _installWithLease(
    PlayTarget target, {
    required _InstallOperation operation,
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
    required _InstallOperation operation,
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

    // The runtime is resolved once and the format chosen before any download;
    // a grant refresh re-plans the same format and never substitutes another.
    final runtime = await _installRuntime(manifestResult.manifest);
    if (!_operationIsCurrent(operation)) {
      yield _authorityChanged;
      return;
    }
    var plan = ReleaseInstallPlan.select(
      manifestResult.manifest,
      runtime: runtime,
    );

    var manifestRefreshed = false;

    while (true) {
      final manifest = plan.delivered;
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
      final resolved = _resolve(target, plan);

      LocalInstall? existing;
      try {
        existing = await _installs.findByReleaseId(manifest.releaseId.value);
      } on LocalInstallProjectionException {
        // A corrupt projection is never usable proof. Acquisition may replace
        // it only after current authoritative verification succeeds.
      }
      // Prefer reuse over a swap: until anything is downloaded, a verified
      // Original already on this device is kept instead of fetching a
      // delivered alternative.
      final originalAlternative = manifestRefreshed
          ? null
          : _originalAlternative(plan);
      if (existing != null && existing.state == InstallState.installed) {
        for (final candidate in <ReleaseInstallPlan>[
          plan,
          if (originalAlternative != null) originalAlternative,
        ]) {
          final candidateResolved = _resolve(target, candidate);
          final candidateFingerprint = _verifier.manifestFingerprint(
            _fingerprintItems(candidate.delivered),
          );
          if (!_matchesCurrentManifest(
                existing: existing,
                target: target,
                plan: candidate,
                resolved: candidateResolved,
                fingerprint: candidateFingerprint,
              ) ||
              !await _filesVerifiedAgainstManifest(
                candidate.delivered,
                Directory(candidateResolved.contentRoot),
              )) {
            continue;
          }
          final canonical = _toInstall(
            target,
            candidate,
            candidateResolved,
            candidateFingerprint,
            previous: existing,
          );
          if (!await _acquisitions.commit(
            canonical,
            authority: authority,
            operation: operation,
          )) {
            yield _authorityChanged;
            return;
          }
          yield InstallCompleted(candidateResolved);
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
            if (operation.cancelled) return;
            final destination = _fileStore.resolveWithin(
              staging,
              item.relativePath,
            );
            final download = _InstallDownload(
              _downloads.download(
                url: item.contentGrant!.downloadUrl,
                destination: destination,
                maximumBytes: item.sizeBytes,
              ),
            );
            operation.download = download;
            try {
              if (operation.cancelled) return;
              while (await download.moveNext()) {
                if (operation.cancelled) return;
                if (download.current > item.sizeBytes) {
                  throw const DownloadSizeExceeded();
                }
                yield InstallDownloading(
                  receivedBytes: completedBytes + download.current,
                  totalBytes: total,
                );
              }
            } finally {
              operation.download = null;
              await download.cancel();
            }
            if (operation.cancelled) return;
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
            final refreshed = ReleaseInstallPlan.forFormat(
              manifestResult.manifest,
              plan.format,
              runtime: runtime,
            );
            if (refreshed == null) {
              yield _deliveryChanged;
              return;
            }
            // A captured generated file keeps its exact bytes and provenance on renewal.
            // An arriving original requires a new explicit install attempt.
            final pinsDerived =
                plan.delivered.items.any(
                  (item) => item.provenance == ContentFileProvenance.derived,
                ) ||
                refreshed.delivered.items.any(
                  (item) => item.provenance == ContentFileProvenance.derived,
                );
            if (pinsDerived &&
                (_verifier.manifestFingerprint(
                          _fingerprintItems(plan.delivered),
                        ) !=
                        _verifier.manifestFingerprint(
                          _fingerprintItems(refreshed.delivered),
                        ) ||
                    plan.delivered.runtime.launch?.relativePath !=
                        refreshed.delivered.runtime.launch?.relativePath ||
                    plan.delivered.runtime.packaging !=
                        refreshed.delivered.runtime.packaging)) {
              yield _deliveryChanged;
              return;
            }
            plan = refreshed;
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
          plan,
          resolved,
          fingerprint,
          previous: _canPreserveTimestamps(existing, manifest)
              ? existing
              : null,
        );
        final outcome = await _acquisitions.finalize(
          staging: staging,
          resolved: resolved,
          manifest: manifest,
          install: install,
          previous: existing,
          authority: authority,
          operation: operation,
        );
        switch (outcome) {
          case ReleaseAcquisitionOutcome.completed:
            yield InstallCompleted(resolved);
          case ReleaseAcquisitionOutcome.authorityChanged:
            yield _authorityChanged;
          case ReleaseAcquisitionOutcome.ioError:
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

  /// The served Original for a delivered [plan], when it is itself
  /// installable, so Original content already on the device can be kept.
  /// Selecting without a runtime always yields the Original plan.
  ReleaseInstallPlan? _originalAlternative(ReleaseInstallPlan plan) {
    if (plan.format == RomDeliveryFormat.original) return null;
    final original = ReleaseInstallPlan.select(plan.original, runtime: null);
    return _validate(original.delivered) == null ? original : null;
  }

  /// Asks the launch path's resolver, with the launch identity, only when the
  /// manifest offers an available CHD; otherwise the install is Original. A
  /// resolver failure also leaves the runtime-neutral Original.
  Future<RuntimeProfile?> _installRuntime(
    ServerBoundReleaseManifest manifest,
  ) async {
    final resolver = _runtimeResolver;
    final offersChd = manifest.items.any(
      (item) => item.formats.any(
        (option) =>
            option.format == RomDeliveryFormat.chd &&
            option.state == RomDeliveryState.available,
      ),
    );
    if (resolver == null || !offersChd) return null;
    try {
      final candidates = await resolver.resolve(
        PlayIdentity(
          platformShortName: manifest.systemKey,
          titleId: manifest.titleId.value,
          releaseId: manifest.releaseId.value,
        ),
      );
      return candidates.preferred;
    } on Object {
      return null;
    }
  }

  InstallFailed? _validate(ServerBoundReleaseManifest m) {
    if (m.runtime.packaging != 'direct_files' || m.runtime.launch == null) {
      return const InstallFailed(
        InstallFailureKind.unsupportedManifest,
        "This title isn't supported on this device yet.",
      );
    }
    final fullyAvailable =
        m.isDeliveryComplete &&
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
          provenance: i.provenance,
        ),
      );

  ResolvedPlayTarget _resolve(PlayTarget target, ReleaseInstallPlan plan) {
    final m = plan.delivered;
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
      deliveryFormat: plan.format,
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
      deliveryFormat: install.deliveryFormat,
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
    required ReleaseInstallPlan plan,
    required ResolvedPlayTarget resolved,
    required String fingerprint,
  }) {
    final manifest = plan.delivered;
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
          provenance: item.provenance,
        ),
      ),
    );
    final currentItems = <String, (int, String?, ContentFileProvenance)>{
      for (final item in manifest.items)
        item.relativePath: (item.sizeBytes, item.sha256, item.provenance),
    };
    final storedItems = <String, (int, String?, ContentFileProvenance)>{
      for (final item in existing.items)
        item.relativePath: (item.sizeBytes, item.sha256, item.provenance),
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
        _sameItemSet(storedItems, currentItems) &&
        existing.deliveryFormat == plan.format &&
        _sameOriginalIdentity(existing, plan.original);
  }

  /// A reused install must have been materialized from the same logical
  /// Original that is served now, whichever format was delivered.
  bool _sameOriginalIdentity(
    LocalInstall existing,
    ServerBoundReleaseManifest original,
  ) =>
      existing.originalLaunchRelativePath ==
          original.runtime.launch?.relativePath &&
      _sameItemSet(
        <String, (int, String?, ContentFileProvenance)>{
          for (final item in existing.originalItems)
            item.relativePath: (item.sizeBytes, item.sha256, item.provenance),
        },
        <String, (int, String?, ContentFileProvenance)>{
          for (final item in original.items)
            item.relativePath: (item.sizeBytes, item.sha256, item.provenance),
        },
      );

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
    Map<String, (int, String?, ContentFileProvenance)> left,
    Map<String, (int, String?, ContentFileProvenance)> right,
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
    ReleaseInstallPlan plan,
    ResolvedPlayTarget resolved,
    String fingerprint, {
    LocalInstall? previous,
  }) {
    final m = plan.delivered;
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
              provenance: i.provenance,
            ),
          )
          .toList(),
      installedAt: previous?.installedAt ?? _now(),
      lastPlayedAt: previous?.lastPlayedAt,
      deliveryFormat: plan.format,
      originalIdentity: plan.format == RomDeliveryFormat.original
          ? null
          : OriginalReleaseIdentity(
              items: plan.original.items
                  .map(
                    (item) => InstalledItem(
                      relativePath: item.relativePath,
                      sizeBytes: item.sizeBytes,
                      sha256: item.sha256,
                      provenance: item.provenance,
                    ),
                  )
                  .toList(growable: false),
              launchRelativePath: plan.original.runtime.launch!.relativePath,
            ),
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

/// Per-subscription cancellation also invalidates the existing acquisition guards.
final class _InstallOperation implements InstallOperationLease {
  _InstallOperation(this.original);

  final InstallOperationLease original;
  bool cancelled = false;
  _InstallDownload? download;

  @override
  bool get isCurrent => !cancelled && original.isCurrent;
}

/// All cleanup paths join the same upstream cancellation before releasing staging.
final class _InstallDownload {
  _InstallDownload(Stream<int> progress) : _iterator = StreamIterator(progress);

  final StreamIterator<int> _iterator;
  Completer<void>? _cancelled;

  int get current => _iterator.current;
  Future<bool> moveNext() => _iterator.moveNext();

  Future<void> cancel() {
    final previous = _cancelled;
    if (previous != null) return previous.future;
    final cancelled = Completer<void>();
    _cancelled = cancelled;
    unawaited(
      Future<void>.sync(_iterator.cancel).then<void>(
        (_) => cancelled.complete(),
        onError: (Object error, StackTrace stack) =>
            cancelled.completeError(error, stack),
      ),
    );
    return cancelled.future;
  }
}
