import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/config/romd_environment.dart';

void main() {
  test('uses the dev consumer host by default', () {
    final environment = RomdEnvironment.fromDefines();

    expect(
      environment.consumerApiOrigin.toString(),
      RomdEnvironment.defaultConsumerApiOrigin,
    );
  });

  test('prefers the console API origin define', () {
    final origin = RomdEnvironment.resolveConsumerApiOrigin(
      consumerApiOrigin: 'http://localhost:5002',
      viteConsumerApiOrigin: 'http://localhost:11338',
    );

    expect(origin.toString(), 'http://localhost:5002');
  });

  test('falls back to the Vite consumer API origin define', () {
    final origin = RomdEnvironment.resolveConsumerApiOrigin(
      consumerApiOrigin: '',
      viteConsumerApiOrigin: 'http://localhost:11338',
    );

    expect(origin.toString(), 'http://localhost:11338');
  });
}
