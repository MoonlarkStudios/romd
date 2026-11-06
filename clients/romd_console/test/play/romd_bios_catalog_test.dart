import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/romd_bios_catalog.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';

final class _Api implements ConsumerApiClient {
  String? receivedAccessToken;
  String? receivedPlatformShortName;

  @override
  Future<List<BiosFileListing>> listPlatformBios({
    required String accessToken,
    required String platformShortName,
  }) async {
    receivedAccessToken = accessToken;
    receivedPlatformShortName = platformShortName;
    return const <BiosFileListing>[];
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  test('passes the current session token to the BIOS listing call', () async {
    final api = _Api();
    var token = 'token-1';
    final catalog = RomdBiosCatalog(
      apiClient: api,
      accessTokenProvider: () => token,
    );

    await catalog.biosForPlatform('psx');
    expect(api.receivedAccessToken, 'token-1');
    expect(api.receivedPlatformShortName, 'psx');

    // The provider is read per call, so a refreshed token is picked up.
    token = 'token-2';
    await catalog.biosForPlatform('psx');
    expect(api.receivedAccessToken, 'token-2');
  });

  test('throws ConsumerApiException when no session token exists', () async {
    final catalog = RomdBiosCatalog(
      apiClient: _Api(),
      accessTokenProvider: () => null,
    );

    expect(
      () => catalog.biosForPlatform('psx'),
      throwsA(isA<ConsumerApiException>()),
    );
  });

  test('throws ConsumerApiException when the session token is empty', () async {
    final catalog = RomdBiosCatalog(
      apiClient: _Api(),
      accessTokenProvider: () => '',
    );

    expect(
      () => catalog.biosForPlatform('psx'),
      throwsA(isA<ConsumerApiException>()),
    );
  });
}
