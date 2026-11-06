import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';

void main() {
  late Directory dir;
  late HttpDownloadClient client;

  setUp(() {
    dir = Directory.systemTemp.createTempSync('romd_dl_test');
    client = HttpDownloadClient();
  });

  tearDown(() {
    client.close();
    if (dir.existsSync()) {
      dir.deleteSync(recursive: true);
    }
  });

  Uri urlFor(HttpServer server, String path) =>
      Uri.parse('http://${server.address.host}:${server.port}$path');

  List<Uri> useResponses(
    List<_Response> responses, {
    DateTime Function()? now,
    Timer Function(Duration, void Function())? createRetryTimer,
  }) {
    client.close();
    final opened = <Uri>[];
    client = HttpDownloadClient(
      httpClient: _Client((url) async {
        final response = responses[opened.length];
        opened.add(url);
        return _Request()..response.complete(response);
      }),
      now: now,
      createRetryTimer: createRetryTimer,
    );
    return opened;
  }

  test('size overflow cancels an unfinished response before writing its excess chunk', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client.close();
    final http = _Client((_) async => request);
    client = HttpDownloadClient(httpClient: http);
    final destination = _TrackedFile(File('${dir.path}/bounded.bin'));
    final progress = <int>[];
    final done = client
        .download(url: _grant, destination: destination, maximumBytes: 3)
        .forEach(progress.add);
    final rejected = expectLater(done, throwsA(isA<DownloadSizeExceeded>()));
    response.chunks.add([1, 2]);
    await destination.writeStarted.future;
    response.chunks.add([3, 4]);
    // No EOF is sent: overflow must terminate a stalled/endless response.
    await rejected.timeout(const Duration(seconds: 2));

    expect(progress, [2]);
    expect(destination.events.where((event) => event == 'write'), hasLength(1));
    expect(await destination.exists(), isFalse);
    expect(response.cancelled.isCompleted, isTrue);
    expect(request.aborts, 1);
    expect(http.closeCalls, 0);
    await response.chunks.close();
  });

  test(
    'an exact byte budget completes and retains the downloaded file',
    () async {
      final response = _Response()..chunks.add([1, 2, 3]);
      unawaited(response.chunks.close());
      useResponses([response]);
      final destination = File('${dir.path}/exact.bin');

      expect(
        await client
            .download(url: _grant, destination: destination, maximumBytes: 3)
            .toList(),
        [3],
      );
      expect(await destination.readAsBytes(), [1, 2, 3]);
    },
  );

  test(
    'a zero byte budget rejects the first body chunk without writing it',
    () async {
      final response = _Response()..chunks.add([1]);
      useResponses([response]);
      final destination = _TrackedFile(File('${dir.path}/zero.bin'));
      await expectLater(
        client
            .download(url: _grant, destination: destination, maximumBytes: 0)
            .drain<void>(),
        throwsA(isA<DownloadSizeExceeded>()),
      );
      expect(destination.events, isNot(contains('write')));
      expect(await destination.exists(), isFalse);
      await response.chunks.close();
    },
  );

  for (final header in <String?>[
    '15',
    'Sun, 01 Nov 2026 12:00:20 GMT',
    null,
    'invalid',
    '-1',
    '0',
    'Sun, 01 Nov 2026 11:59:00 GMT',
  ]) {
    test(
      '503 honors Retry-After $header before retrying the same grant',
      () async {
        final busy = _Response(statusCode: 503, retryAfter: header);
        final ok = _Response()..chunks.add(<int>[1, 2, 3]);
        unawaited(ok.chunks.close());
        final delays = <Duration>[];
        final opened = useResponses(
          [busy, ok],
          now: () => DateTime.utc(2026, 11, 1, 12),
          createRetryTimer: (delay, callback) {
            expect(busy.cancelled.isCompleted, isTrue);
            delays.add(delay);
            final timer = _RetryTimer(callback);
            scheduleMicrotask(timer.fire);
            return timer;
          },
        );
        final destination = File('${dir.path}/retried.bin');

        final progress = await client
            .download(url: _grant, destination: destination)
            .toList();

        final seconds = header == 'Sun, 01 Nov 2026 12:00:20 GMT'
            ? 20
            : header == '0' || header == 'Sun, 01 Nov 2026 11:59:00 GMT'
            ? 0
            : 15;
        expect(delays, [Duration(seconds: seconds)]);
        expect(opened, [_grant, _grant]);
        expect(progress, [3]);
        expect(await destination.readAsBytes(), <int>[1, 2, 3]);
      },
    );
  }

  test('duplicate Retry-After fields use the bounded default wait', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    addTearDown(() => server.close(force: true));
    var calls = 0;
    server.listen((request) async {
      calls++;
      if (calls == 1) {
        request.response
          ..statusCode = HttpStatus.serviceUnavailable
          ..headers.add(HttpHeaders.retryAfterHeader, '15')
          ..headers.add(HttpHeaders.retryAfterHeader, '20');
      } else {
        request.response.add(<int>[7]);
      }
      await request.response.close();
    });
    client.close();
    final delays = <Duration>[];
    client = HttpDownloadClient(
      createRetryTimer: (delay, callback) {
        delays.add(delay);
        return Timer(Duration.zero, callback);
      },
    );
    final destination = File('${dir.path}/duplicate-header.bin');

    await client
        .download(url: urlFor(server, '/grant'), destination: destination)
        .drain<void>();

    expect(calls, 2);
    expect(delays, [const Duration(seconds: 15)]);
    expect(await destination.readAsBytes(), <int>[7]);
  });

  for (final header in <String>['0', '30', '61', '999999999999999999999']) {
    test('persistent 503 bounds retries and total waits for $header', () async {
      final responses = List.generate(
        4,
        (_) => _Response(statusCode: 503, retryAfter: header),
      );
      final delays = <Duration>[];
      final opened = useResponses(
        responses,
        createRetryTimer: (delay, callback) {
          delays.add(delay);
          final timer = _RetryTimer(callback);
          scheduleMicrotask(timer.fire);
          return timer;
        },
      );
      final destination = File('${dir.path}/busy.bin');
      // A refusal occurs before opening/truncating an existing destination.
      await destination.writeAsString('existing');

      await expectLater(
        client.download(url: _grant, destination: destination).drain<void>(),
        throwsA(
          isA<DownloadHttpError>().having((e) => e.statusCode, 'status', 503),
        ),
      );

      final retries = header == '0'
          ? 3
          : header == '30'
          ? 2
          : 0;
      expect(opened, hasLength(retries + 1));
      expect(delays, hasLength(retries));
      expect(
        delays.fold(Duration.zero, (sum, delay) => sum + delay),
        lessThanOrEqualTo(const Duration(minutes: 1)),
      );
      expect(await destination.readAsString(), 'existing');
      for (final response in responses.take(opened.length)) {
        expect(response.cancelled.isCompleted, isTrue);
      }
    });
  }

  for (final status in <int>[401, 404, 500]) {
    test(
      '503 followed by $status stops retrying and preserves its typed error',
      () async {
        final opened = useResponses(
          [
            _Response(statusCode: 503, retryAfter: '0'),
            _Response(statusCode: status),
          ],
          createRetryTimer: (delay, callback) {
            final timer = _RetryTimer(callback);
            scheduleMicrotask(timer.fire);
            return timer;
          },
        );
        await expectLater(
          client
              .download(
                url: _grant,
                destination: File('${dir.path}/expired.bin'),
              )
              .drain<void>(),
          throwsA(
            status == 500
                ? isA<DownloadHttpError>()
                : isA<DownloadGrantExpired>(),
          ),
        );
        expect(opened, hasLength(2));
      },
    );
  }

  test(
    'cancelling a 503 wait cancels its timer and issues no further request',
    () async {
      final waiting = Completer<void>();
      late _RetryTimer timer;
      final opened = useResponses(
        [_Response(statusCode: 503, retryAfter: '15')],
        createRetryTimer: (delay, callback) {
          timer = _RetryTimer(callback);
          waiting.complete();
          return timer;
        },
      );
      final destination = File('${dir.path}/cancelled.bin');
      final subscription = client
          .download(url: _grant, destination: destination)
          .listen((_) => fail('unexpected progress'));
      await waiting.future;

      await subscription.cancel().timeout(const Duration(seconds: 1));
      timer.fire();

      expect(timer.isActive, isFalse);
      expect(opened, [_grant]);
      expect(await destination.exists(), isFalse);
    },
  );

  test('streams bytes to destination and yields cumulative count', () async {
    final payload = List<int>.generate(2048, (i) => i % 256);
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async {
      request.response
        ..statusCode = HttpStatus.ok
        ..add(payload);
      await request.response.close();
    });
    addTearDown(() => server.close(force: true));

    final dest = File('${dir.path}/out.bin');
    final counts = await client
        .download(url: urlFor(server, '/file'), destination: dest)
        .toList();

    expect(await dest.readAsBytes(), payload);
    expect(counts.last, payload.length);
    expect(counts, counts.toList()..sort()); // monotonically increasing
  });

  test('downloads an absolute same-origin-shaped content grant URL', () async {
    Uri? requestedUrl;
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async {
      requestedUrl = request.requestedUri;
      request.response
        ..statusCode = HttpStatus.ok
        ..add(<int>[4, 5, 6]);
      await request.response.close();
    });
    addTearDown(() => server.close(force: true));
    final grantUrl = urlFor(server, '/delivery/content/payload.signature');
    final destination = File('${dir.path}/grant.bin');

    await client
        .download(url: grantUrl, destination: destination)
        .drain<void>();

    expect(requestedUrl, grantUrl);
    expect(await destination.readAsBytes(), <int>[4, 5, 6]);
  });

  test('maps a relative grant URL to a typed failure', () async {
    final destination = File('${dir.path}/relative.bin');

    await expectLater(
      client
          .download(
            url: Uri.parse('/delivery/content/payload.signature'),
            destination: destination,
          )
          .drain<void>(),
      throwsA(isA<DownloadNetworkError>()),
    );
    expect(await destination.exists(), isFalse);
  });

  test('does not send an Authorization header', () async {
    String? seenAuth = 'unset';
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async {
      seenAuth = request.headers.value(HttpHeaders.authorizationHeader);
      request.response
        ..statusCode = HttpStatus.ok
        ..add(<int>[1, 2, 3]);
      await request.response.close();
    });
    addTearDown(() => server.close(force: true));

    await client
        .download(
          url: urlFor(server, '/file'),
          destination: File('${dir.path}/o.bin'),
        )
        .drain<void>();

    expect(seenAuth, isNull);
  });

  test('follows a redirect without re-adding auth', () async {
    String? authOnFinal = 'unset';
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async {
      if (request.uri.path == '/redirect') {
        request.response
          ..statusCode = HttpStatus.found
          ..headers.set(
            HttpHeaders.locationHeader,
            urlFor(server, '/final').toString(),
          );
        await request.response.close();
        return;
      }
      authOnFinal = request.headers.value(HttpHeaders.authorizationHeader);
      request.response
        ..statusCode = HttpStatus.ok
        ..add(<int>[9, 9, 9]);
      await request.response.close();
    });
    addTearDown(() => server.close(force: true));

    final dest = File('${dir.path}/redir.bin');
    await client
        .download(url: urlFor(server, '/redirect'), destination: dest)
        .drain<void>();

    expect(await dest.readAsBytes(), <int>[9, 9, 9]);
    expect(authOnFinal, isNull);
  });

  test('maps 401 and 404 to DownloadGrantExpired', () async {
    for (final status in <int>[HttpStatus.unauthorized, HttpStatus.notFound]) {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      server.listen((request) async {
        request.response.statusCode = status;
        await request.response.close();
      });

      await expectLater(
        client
            .download(
              url: urlFor(server, '/file'),
              destination: File('${dir.path}/x.bin'),
            )
            .drain<void>(),
        throwsA(isA<DownloadGrantExpired>()),
      );
      await server.close(force: true);
    }
  });

  test('maps other non-2xx to DownloadHttpError with the status', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) async {
      request.response.statusCode = HttpStatus.internalServerError;
      await request.response.close();
    });
    addTearDown(() => server.close(force: true));

    await expectLater(
      client
          .download(
            url: urlFor(server, '/file'),
            destination: File('${dir.path}/x.bin'),
          )
          .drain<void>(),
      throwsA(
        isA<DownloadHttpError>().having((e) => e.statusCode, 'statusCode', 500),
      ),
    );
  });

  test(
    'cancelling at first progress closes before deleting the file',
    () async {
      final response = _Response();
      final request = _Request()..response.complete(response);
      final http = _Client((_) async => request);
      client = HttpDownloadClient(httpClient: http);
      final destination = _TrackedFile(File('${dir.path}/cancelled.bin'));
      response.chunks.add(<int>[1, 2, 3]);

      final progress = StreamIterator(
        client.download(url: _grant, destination: destination),
      );
      expect(await progress.moveNext(), isTrue);
      expect(progress.current, 3);
      await progress.cancel().timeout(const Duration(seconds: 2));

      expect(destination.events, <String>['open', 'write', 'close', 'delete']);
      expect(await destination.exists(), isFalse);
      expect(response.cancelled.isCompleted, isTrue);
      expect(http.closeCalls, 0);
    },
  );

  test(
    'cancels before headers without interrupting another download',
    () async {
      final stalled = _Request();
      final healthyResponse = _Response();
      final healthy = _Request()..response.complete(healthyResponse);
      final http = _Client((url) async {
        return url.path == '/stalled' ? stalled : healthy;
      });
      client = HttpDownloadClient(httpClient: http);
      final cancelledFile = File('${dir.path}/stalled.bin');
      final stalledSubscription = client
          .download(
            url: _grant.replace(path: '/stalled'),
            destination: cancelledFile,
          )
          .listen((_) => fail('The stalled request must not yield progress'));
      final healthyFile = File('${dir.path}/healthy.bin');
      final healthyDownload = client
          .download(url: _grant, destination: healthyFile)
          .toList();
      await stalled.started.future;
      await healthy.started.future;

      await stalledSubscription.cancel().timeout(const Duration(seconds: 2));
      healthyResponse.chunks.add(<int>[4, 5, 6]);
      await healthyResponse.chunks.close();

      expect(await healthyDownload, <int>[3]);
      expect(await healthyFile.readAsBytes(), <int>[4, 5, 6]);
      expect(stalled.aborts, 1);
      expect(healthy.aborts, 0);
      expect(await cancelledFile.exists(), isFalse);
      expect(http.closeCalls, 0);
    },
  );

  test('cancels promptly while opening and aborts a late request', () async {
    final opening = Completer<HttpClientRequest>();
    final started = Completer<void>();
    final http = _Client((_) {
      started.complete();
      return opening.future;
    });
    client = HttpDownloadClient(httpClient: http);
    final destination = File('${dir.path}/opening.bin');
    final subscription = client
        .download(url: _grant, destination: destination)
        .listen((_) => fail('A cancelled request must not yield progress'));
    await started.future;

    await subscription.cancel().timeout(const Duration(seconds: 2));
    final lateRequest = _Request();
    opening.complete(lateRequest);
    await Future<void>.delayed(Duration.zero);

    expect(lateRequest.aborts, 1);
    expect(lateRequest.started.isCompleted, isFalse);
    expect(await destination.exists(), isFalse);
  });

  test('cancels promptly while waiting for another network chunk', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client = HttpDownloadClient(httpClient: _Client((_) async => request));
    final destination = _TrackedFile(File('${dir.path}/waiting.bin'));
    final firstProgress = Completer<void>();
    final subscription = client
        .download(url: _grant, destination: destination)
        .listen((_) => firstProgress.complete());
    response.chunks.add(<int>[1, 2, 3]);
    await firstProgress.future;
    await Future<void>.delayed(Duration.zero);

    await subscription.cancel().timeout(const Duration(seconds: 2));

    expect(response.cancelled.isCompleted, isTrue);
    expect(destination.events, <String>['open', 'write', 'close', 'delete']);
    expect(await destination.exists(), isFalse);
  });

  test('waits for disk writes before consuming more network chunks', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client = HttpDownloadClient(httpClient: _Client((_) async => request));
    final writeGate = Completer<void>();
    final destination = _TrackedFile(
      File('${dir.path}/backpressure.bin'),
      writeGate: writeGate.future,
    );
    final progress = <int>[];
    final finished = client
        .download(url: _grant, destination: destination)
        .forEach(progress.add);
    response.chunks
      ..add(<int>[1, 2])
      ..add(<int>[3, 4])
      ..add(<int>[5, 6]);
    unawaited(response.chunks.close());
    await destination.writeStarted.future;
    await Future<void>.delayed(Duration.zero);

    expect(response.delivered, 1);
    expect(destination.events, <String>['open', 'write']);
    expect(progress, isEmpty);

    writeGate.complete();
    await finished;
    expect(progress, <int>[2, 4, 6]);
    expect(destination.events.last, 'close');
    expect(await destination.actual.readAsBytes(), <int>[1, 2, 3, 4, 5, 6]);
  });

  test(
    'cancelling during a disk write waits for cleanup without another chunk',
    () async {
      final response = _Response();
      final request = _Request()..response.complete(response);
      client = HttpDownloadClient(httpClient: _Client((_) async => request));
      final writeGate = Completer<void>();
      final destination = _TrackedFile(
        File('${dir.path}/writing-cancelled.bin'),
        writeGate: writeGate.future,
      );
      final progress = <int>[];
      final subscription = client
          .download(url: _grant, destination: destination)
          .listen(progress.add);
      response.chunks
        ..add(<int>[1, 2])
        ..add(<int>[3, 4]);
      unawaited(response.chunks.close());
      await destination.writeStarted.future;

      var cancellationCompleted = false;
      final cancelled = subscription.cancel().then<void>((_) {
        cancellationCompleted = true;
      });
      try {
        await Future<void>.delayed(Duration.zero);
        expect(cancellationCompleted, isFalse);
        expect(destination.events, <String>['open', 'write']);
        expect(await destination.exists(), isTrue);
        expect(response.delivered, 1);
        expect(progress, isEmpty);
      } finally {
        writeGate.complete();
        await cancelled.timeout(const Duration(seconds: 2));
      }

      expect(destination.events, <String>['open', 'write', 'close', 'delete']);
      expect(await destination.exists(), isFalse);
      expect(response.delivered, 1);
      expect(progress, isEmpty);
      expect(response.cancelled.isCompleted, isTrue);
    },
  );

  test('pausing progress also pauses file writes', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client = HttpDownloadClient(httpClient: _Client((_) async => request));
    final destination = _TrackedFile(File('${dir.path}/paused.bin'));
    final firstProgress = Completer<void>();
    final finished = Completer<void>();
    late StreamSubscription<int> subscription;
    subscription = client
        .download(url: _grant, destination: destination)
        .listen((_) {
          if (!firstProgress.isCompleted) {
            subscription.pause();
            firstProgress.complete();
          }
        }, onDone: finished.complete);
    response.chunks
      ..add(<int>[1, 2])
      ..add(<int>[3, 4]);
    unawaited(response.chunks.close());
    await firstProgress.future;
    await Future<void>.delayed(Duration.zero);

    expect(destination.events, <String>['open', 'write']);
    expect(response.delivered, 1);

    subscription.resume();
    await finished.future;
    expect(await destination.actual.readAsBytes(), <int>[1, 2, 3, 4]);
    expect(destination.events.last, 'close');
  });

  test(
    'transport errors close and delete partial output without grant URLs',
    () async {
      final response = _Response();
      final request = _Request()..response.complete(response);
      client = HttpDownloadClient(httpClient: _Client((_) async => request));
      final destination = _TrackedFile(File('${dir.path}/transport.bin'));
      final failed = expectLater(
        client.download(url: _grant, destination: destination).drain<void>(),
        throwsA(
          isA<DownloadNetworkError>().having(
            (error) => error.toString(),
            'safe error',
            isNot(contains('secret-grant')),
          ),
        ),
      );
      response.chunks.add(<int>[1, 2]);
      await destination.writeStarted.future;
      response.chunks.addError(HttpException('Connection lost at $_grant'));
      await failed;

      expect(destination.events, <String>['open', 'write', 'close', 'delete']);
      expect(await destination.exists(), isFalse);
      expect(response.cancelled.isCompleted, isTrue);
    },
  );

  test('write errors close and delete partial output', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client = HttpDownloadClient(httpClient: _Client((_) async => request));
    final destination = _TrackedFile(
      File('${dir.path}/disk-error.bin'),
      failWrite: true,
    );
    response.chunks.add(<int>[1, 2]);

    await expectLater(
      client.download(url: _grant, destination: destination).drain<void>(),
      throwsA(isA<DownloadNetworkError>()),
    );

    expect(destination.events, <String>['open', 'write', 'close', 'delete']);
    expect(await destination.exists(), isFalse);
    expect(response.cancelled.isCompleted, isTrue);
  });

  test('cancelling during file open closes the returned handle', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client = HttpDownloadClient(httpClient: _Client((_) async => request));
    final openGate = Completer<void>();
    final destination = _TrackedFile(
      File('${dir.path}/opening-file.bin'),
      openGate: openGate.future,
    );
    final subscription = client
        .download(url: _grant, destination: destination)
        .listen((_) => fail('A cancelled download must not yield progress'));
    await destination.openStarted.future;

    final cancelled = subscription.cancel();
    openGate.complete();
    await cancelled.timeout(const Duration(seconds: 2));

    expect(destination.events, <String>['open', 'close', 'delete']);
    expect(await destination.exists(), isFalse);
    expect(response.cancelled.isCompleted, isTrue);
  });

  test('failed file open preserves an existing destination', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client = HttpDownloadClient(httpClient: _Client((_) async => request));
    final actual = File('${dir.path}/existing.bin');
    await actual.writeAsBytes(<int>[9, 8, 7]);
    final destination = _TrackedFile(actual, failOpen: true);

    await expectLater(
      client.download(url: _grant, destination: destination).drain<void>(),
      throwsA(isA<DownloadNetworkError>()),
    );

    expect(await actual.readAsBytes(), <int>[9, 8, 7]);
    expect(destination.events, isEmpty);
    expect(response.cancelled.isCompleted, isTrue);
  });

  test('close errors do not leave a completed-looking file', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client = HttpDownloadClient(httpClient: _Client((_) async => request));
    final destination = _TrackedFile(
      File('${dir.path}/close-error.bin'),
      failClose: true,
    );
    response.chunks.add(<int>[1, 2]);
    unawaited(response.chunks.close());

    await expectLater(
      client.download(url: _grant, destination: destination).drain<void>(),
      throwsA(isA<DownloadNetworkError>()),
    );

    expect(destination.events.last, 'delete');
    expect(await destination.exists(), isFalse);
  });

  test('cancelling during final close waits before deleting output', () async {
    final response = _Response();
    final request = _Request()..response.complete(response);
    client = HttpDownloadClient(httpClient: _Client((_) async => request));
    final closeGate = Completer<void>();
    final destination = _TrackedFile(
      File('${dir.path}/closing-cancelled.bin'),
      closeGate: closeGate.future,
    );
    final progress = <int>[];
    final subscription = client
        .download(url: _grant, destination: destination)
        .listen(progress.add);
    response.chunks.add(<int>[1, 2, 3]);
    unawaited(response.chunks.close());
    await destination.closeStarted.future;

    var cancellationCompleted = false;
    final cancelled = subscription.cancel().then<void>((_) {
      cancellationCompleted = true;
    });
    try {
      await Future<void>.delayed(Duration.zero);
      expect(cancellationCompleted, isFalse);
      expect(destination.events, <String>['open', 'write', 'close']);
      expect(await destination.exists(), isTrue);
      expect(progress, <int>[3]);
    } finally {
      closeGate.complete();
      await cancelled.timeout(const Duration(seconds: 2));
    }

    expect(destination.events, <String>['open', 'write', 'close', 'delete']);
    expect(await destination.exists(), isFalse);
    expect(progress, <int>[3]);
    expect(response.cancelled.isCompleted, isTrue);
  });

  test('status errors cancel an unconsumed response body', () async {
    for (final status in <int>[401, 404, 500]) {
      final response = _Response(statusCode: status);
      final request = _Request()..response.complete(response);
      client.close();
      client = HttpDownloadClient(httpClient: _Client((_) async => request));
      final destination = File('${dir.path}/status-$status.bin');

      await expectLater(
        client.download(url: _grant, destination: destination).drain<void>(),
        throwsA(
          status == 500
              ? isA<DownloadHttpError>()
              : isA<DownloadGrantExpired>(),
        ),
      );

      expect(response.cancelled.isCompleted, isTrue);
      expect(await destination.exists(), isFalse);
    }
  });

  for (final completeBeforeCancel in <bool>[true, false]) {
    test(
      'releases a response completed ${completeBeforeCancel ? "immediately before" : "after"} cancellation',
      () async {
        final request = _Request();
        client = HttpDownloadClient(httpClient: _Client((_) async => request));
        final destination = File('${dir.path}/late-response.bin');
        final subscription = client
            .download(url: _grant, destination: destination)
            .listen((_) => fail('A cancelled request must not yield progress'));
        await request.started.future;
        final response = _Response();

        if (completeBeforeCancel) request.response.complete(response);
        await subscription.cancel().timeout(const Duration(seconds: 2));
        if (!completeBeforeCancel) request.response.complete(response);
        await response.cancelled.future.timeout(const Duration(seconds: 2));

        expect(await destination.exists(), isFalse);
      },
    );
  }

  test(
    'cancels the final request after a redirect without closing a shared client',
    () async {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      addTearDown(() => server.close(force: true));
      final storage = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      addTearDown(() => storage.close(force: true));
      final stalled = Completer<Socket>();
      final disconnected = Completer<void>();
      Future<void> handle(HttpRequest request) async {
        switch (request.uri.path) {
          case '/redirect-stalled':
            request.response
              ..statusCode = HttpStatus.found
              ..headers.set(
                HttpHeaders.locationHeader,
                urlFor(storage, '/stalled-final').toString(),
              );
            await request.response.close();
          case '/stalled-final':
            final socket = await request.response.detachSocket(
              writeHeaders: false,
            );
            addTearDown(socket.destroy);
            socket.listen((_) {}, onDone: disconnected.complete);
            stalled.complete(socket);
          default:
            request.response.add(<int>[7, 8, 9]);
            await request.response.close();
        }
      }

      server.listen(handle);
      storage.listen(handle);
      final destination = File('${dir.path}/redirect-stalled.bin');
      final subscription = client
          .download(
            url: urlFor(server, '/redirect-stalled'),
            destination: destination,
          )
          .listen((_) => fail('Stalled redirect must not yield progress'));
      await stalled.future;
      final healthy = File('${dir.path}/after-redirect.bin');
      final overlapping = client
          .download(url: urlFor(server, '/healthy'), destination: healthy)
          .toList();

      await subscription.cancel().timeout(const Duration(seconds: 2));
      await disconnected.future.timeout(const Duration(seconds: 2));

      expect(await overlapping, <int>[3]);
      expect(await healthy.readAsBytes(), <int>[7, 8, 9]);
      expect(await destination.exists(), isFalse);
    },
  );

  for (final hops in <int>[5, 6]) {
    test(
      'bounds relative redirects with fragments to five hops ($hops requested)',
      () async {
        final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
        addTearDown(() => server.close(force: true));
        var requests = 0;
        server.listen((request) async {
          requests++;
          final current = int.parse(request.uri.pathSegments.last);
          if (current < hops) {
            request.response
              ..statusCode = HttpStatus.temporaryRedirect
              ..headers.set(
                HttpHeaders.locationHeader,
                '${current + 1}#section',
              );
          } else {
            request.response.add(<int>[9, 8, 7]);
          }
          await request.response.close();
        });
        final destination = File('${dir.path}/redirect-limit.bin');
        final download = client
            .download(url: urlFor(server, '/hop/0'), destination: destination)
            .toList();

        if (hops == 5) {
          expect(await download, <int>[3]);
          expect(await destination.readAsBytes(), <int>[9, 8, 7]);
        } else {
          await expectLater(download, throwsA(isA<DownloadNetworkError>()));
          expect(await destination.exists(), isFalse);
        }
        expect(requests, 6);
      },
    );
  }
}

final _grant = Uri.parse(
  'https://example.invalid/delivery/content/secret-grant',
);

final class _Client implements HttpClient {
  _Client(this.openRequest);
  final Future<HttpClientRequest> Function(Uri) openRequest;
  var closeCalls = 0;

  @override
  Future<HttpClientRequest> getUrl(Uri url) => openRequest(url);

  @override
  void close({bool force = false}) => closeCalls++;

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Request implements HttpClientRequest {
  @override
  bool followRedirects = true;
  final response = Completer<HttpClientResponse>();
  final started = Completer<void>();
  var aborts = 0;

  @override
  Future<HttpClientResponse> close() {
    started.complete();
    return response.future;
  }

  @override
  void abort([Object? exception, StackTrace? stackTrace]) => aborts++;

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Response extends Stream<List<int>> implements HttpClientResponse {
  _Response({this.statusCode = HttpStatus.ok, String? retryAfter})
    : headers = _Headers(retryAfter) {
    chunks = StreamController<List<int>>(
      sync: true,
      onCancel: () => cancelled.complete(),
    );
  }

  late final StreamController<List<int>> chunks;
  final cancelled = Completer<void>();
  var delivered = 0;

  @override
  final int statusCode;

  @override
  final HttpHeaders headers;

  @override
  StreamSubscription<List<int>> listen(
    void Function(List<int>)? onData, {
    Function? onError,
    void Function()? onDone,
    bool? cancelOnError,
  }) => chunks.stream
      .map((chunk) {
        delivered++;
        return chunk;
      })
      .listen(
        onData,
        onError: onError,
        onDone: onDone,
        cancelOnError: cancelOnError,
      );

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Headers implements HttpHeaders {
  _Headers(this.retryAfter);
  final String? retryAfter;

  @override
  String? value(String name) =>
      name == HttpHeaders.retryAfterHeader ? retryAfter : null;

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _RetryTimer implements Timer {
  _RetryTimer(this.callback);
  final void Function() callback;
  bool _active = true;
  int _tick = 0;

  void fire() {
    if (!_active) return;
    _active = false;
    _tick++;
    callback();
  }

  @override
  void cancel() => _active = false;

  @override
  bool get isActive => _active;

  @override
  int get tick => _tick;
}

// Wrap real files, with deterministic slow/failing writes and explicit handle
// lifecycle assertions instead of platform-specific open-descriptor inspection.
final class _TrackedFile implements File {
  _TrackedFile(
    this.actual, {
    this.openGate,
    this.writeGate,
    this.closeGate,
    this.failOpen = false,
    this.failWrite = false,
    this.failClose = false,
  });

  final File actual;
  final Future<void>? openGate;
  final Future<void>? writeGate;
  final Future<void>? closeGate;
  final bool failOpen;
  final bool failWrite;
  final bool failClose;
  final events = <String>[];
  final openStarted = Completer<void>();
  final writeStarted = Completer<void>();
  final closeStarted = Completer<void>();

  @override
  Directory get parent => actual.parent;

  @override
  Future<RandomAccessFile> open({FileMode mode = FileMode.read}) async {
    openStarted.complete();
    if (openGate != null) await openGate;
    if (failOpen) throw const FileSystemException('File open failed');
    final file = await actual.open(mode: mode);
    events.add('open');
    return _TrackedHandle(this, file);
  }

  @override
  Future<bool> exists() => actual.exists();

  @override
  Future<File> delete({bool recursive = false}) async {
    events.add('delete');
    await actual.delete(recursive: recursive);
    return this;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _TrackedHandle implements RandomAccessFile {
  _TrackedHandle(this.owner, this.actual);
  final _TrackedFile owner;
  final RandomAccessFile actual;

  @override
  Future<RandomAccessFile> writeFrom(
    List<int> buffer, [
    int start = 0,
    int? end,
  ]) async {
    owner.events.add('write');
    if (!owner.writeStarted.isCompleted) owner.writeStarted.complete();
    if (owner.writeGate != null) await owner.writeGate;
    if (owner.failWrite) throw const FileSystemException('Disk write failed');
    await actual.writeFrom(buffer, start, end);
    return this;
  }

  @override
  Future<void> close() async {
    owner.events.add('close');
    if (!owner.closeStarted.isCompleted) owner.closeStarted.complete();
    if (owner.closeGate != null) await owner.closeGate;
    await actual.close();
    if (owner.failClose) throw const FileSystemException('Disk close failed');
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
