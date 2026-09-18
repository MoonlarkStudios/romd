import '../domain/local_profile.dart';

final class RomdEnvironment {
  const RomdEnvironment({required this.consumerApiOrigin});

  static const defaultConsumerApiOrigin = RomdServerOrigins.defaultValue;
  static const consumerApiOriginDefine = 'ROMD_CONSUMER_API_ORIGIN';
  static const viteConsumerApiOriginDefine = 'VITE_ROMD_CONSUMER_API_ORIGIN';

  final Uri consumerApiOrigin;

  factory RomdEnvironment.fromDefines() {
    const consumerApiOrigin = String.fromEnvironment(consumerApiOriginDefine);
    const viteConsumerApiOrigin = String.fromEnvironment(
      viteConsumerApiOriginDefine,
    );

    return RomdEnvironment(
      consumerApiOrigin: resolveConsumerApiOrigin(
        consumerApiOrigin: consumerApiOrigin,
        viteConsumerApiOrigin: viteConsumerApiOrigin,
      ),
    );
  }

  static Uri resolveConsumerApiOrigin({
    required String consumerApiOrigin,
    required String viteConsumerApiOrigin,
  }) {
    final configuredOrigin = consumerApiOrigin.isNotEmpty
        ? consumerApiOrigin
        : viteConsumerApiOrigin;

    return Uri.parse(
      configuredOrigin.isNotEmpty ? configuredOrigin : defaultConsumerApiOrigin,
    );
  }
}
