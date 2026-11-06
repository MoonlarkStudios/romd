import 'package:flutter/widgets.dart';

/// A resolved platform logo plus the rendering contract for that source.
///
/// Artwork and tintability come from the effective server catalog. The client
/// chooses layout, color, and a generic fallback when no source is available.
@immutable
final class PlatformLogoSource {
  const PlatformLogoSource({
    required this.image,
    required this.cacheKey,
    required this.tintable,
  });

  final ImageProvider<Object> image;
  final String cacheKey;
  final bool tintable;
}

abstract interface class PlatformLogoResolver {
  PlatformLogoSource? resolve({
    required String platformId,
    required String platformName,
    required String shortCode,
  });
}

/// No catalog means a generic text/icon fallback, never a competing local mark.
final class NoPlatformLogoResolver implements PlatformLogoResolver {
  const NoPlatformLogoResolver();
  @override
  PlatformLogoSource? resolve({
    required String platformId,
    required String platformName,
    required String shortCode,
  }) => null;
}

/// Supplies platform-logo metadata without coupling cards to its origin.
final class PlatformLogoResolverScope extends InheritedWidget {
  const PlatformLogoResolverScope({
    required this.resolver,
    required super.child,
    this.revision,
    super.key,
  });

  final PlatformLogoResolver resolver;
  final String? revision;

  static PlatformLogoResolver of(BuildContext context) =>
      context
          .dependOnInheritedWidgetOfExactType<PlatformLogoResolverScope>()
          ?.resolver ??
      const NoPlatformLogoResolver();

  @override
  bool updateShouldNotify(PlatformLogoResolverScope oldWidget) =>
      resolver != oldWidget.resolver || revision != oldWidget.revision;
}
