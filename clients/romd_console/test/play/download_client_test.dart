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
}
