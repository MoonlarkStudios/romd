import 'dart:async';

import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_activity.dart';

final class PlayActivitySyncService {
  PlayActivitySyncService({
    required PlayActivityRepository repository,
    required PlayActivityApiClient apiClient,
    required AuthenticatedRequestExecutor authenticatedSession,
    DateTime Function()? now,
  }) : _repository = repository,
       _apiClient = apiClient,
       _authenticatedSession = authenticatedSession,
       _now = now ?? DateTime.now;

  final PlayActivityRepository _repository;
  final PlayActivityApiClient _apiClient;
  final AuthenticatedRequestExecutor _authenticatedSession;
  final DateTime Function() _now;
  Future<void>? _syncInFlight;
  bool _syncRequested = false;

  Future<void> syncPending() {
    final current = _syncInFlight;
    if (current != null) {
      _syncRequested = true;
      return current;
    }
    final run = _run();
    _syncInFlight = run;
    return run.whenComplete(() {
      if (identical(_syncInFlight, run)) _syncInFlight = null;
    });
  }

  Future<void> _run() async {
    do {
      _syncRequested = false;
      if (!await _repository.isSyncEnabled()) return;
      while (true) {
        final snapshots = await _repository.pendingSnapshots();
        if (snapshots.isEmpty) break;
        for (final snapshot in snapshots) {
          if (!await _repository.isSyncEnabled()) return;
          try {
            await _authenticatedSession.execute<void>(
              request: (accessToken) => _apiClient.upsertPlaySession(
                accessToken: accessToken,
                session: snapshot,
              ),
              replayPolicy: AuthenticatedReplayPolicy.onceAfterInvalidToken,
              isInvalidTokenError: (error) =>
                  error is ConsumerApiException && error.isInvalidToken,
            );
            await _repository.markSynced(snapshot);
          } on Object {
            await _repository.markAttemptFailed(
              snapshot,
              _now().toUtc().add(const Duration(seconds: 30)),
            );
          }
        }
      }
    } while (_syncRequested);
  }
}
