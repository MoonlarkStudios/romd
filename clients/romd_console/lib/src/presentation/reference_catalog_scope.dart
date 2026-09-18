import 'dart:async';
import 'dart:io';

import 'package:flutter/widgets.dart';

import '../data/reference_catalog.dart';
import 'widgets/platform_logo_resolver.dart';

final class ReferenceCatalogController extends ChangeNotifier
    implements PlatformLogoResolver {
  ReferenceCatalogController(this.root);
  final Directory? root;
  ReferenceCatalog? catalog;
  ReferenceCatalogCache? _cache;
  ReferenceCatalogApiClient? _client;
  int _generation = 0;
  bool _disposed = false;
  Future<void> _pending = Future<void>.value();

  void activate(String? instance, ReferenceCatalogApiClient? client) {
    if (_disposed) return;
    final generation = ++_generation;
    catalog = null;
    _client = client;
    _cache = instance != null && root != null
        ? ReferenceCatalogCache(root!, instance)
        : null;
    final cache = _cache;
    _pending = Future<void>.microtask(() async {
      final cached = await cache?.load();
      if (generation != _generation) return;
      catalog = cached;
      notifyListeners();
    });
  }

  Future<void> refresh(String token) {
    final generation = _generation;
    final client = _client;
    final cache = _cache;
    return _pending = _pending.then((_) async {
      if (generation != _generation || client == null) return;
      try {
        var next = await client.getReferenceCatalog(
          token,
          revision: catalog?.revision,
        );
        if (next == null || generation != _generation) return;
        try {
          await cache?.save(
            next,
            client,
            isCurrent: () => generation == _generation,
          );
        } on ReferenceAssetUnavailable {
          // Artwork may have expired while syncing an old revision. Retry the
          // current catalog once; never replace a complete offline cache on failure.
          if (generation != _generation) return;
          final current = await client.getReferenceCatalog(token);
          if (current == null ||
              current.revision == next.revision ||
              generation != _generation)
            return;
          await cache?.save(
            current,
            client,
            isCurrent: () => generation == _generation,
          );
          next = current;
        }
        if (generation != _generation) return;
        catalog = next;
        notifyListeners();
      } on Exception {
        // A failed refresh retains the last complete offline snapshot.
      }
    });
  }

  @override
  PlatformLogoSource? resolve({
    required String platformId,
    required String platformName,
    required String shortCode,
  }) {
    final row = catalog?.system(platformId);
    final icon = row?['icon'] as Map<String, dynamic>?;
    if (icon == null ||
        !const ['image/png', 'image/webp'].contains(icon['contentType']))
      return null;
    final file = _cache?.asset(icon['sha256'] as String);
    if (file == null || !file.existsSync()) return null;
    return PlatformLogoSource(
      image: FileImage(file),
      cacheKey: icon['sha256'] as String,
      tintable: icon['monochrome'] == true,
    );
  }

  @override
  void dispose() {
    _disposed = true;
    _generation++;
    super.dispose();
  }
}

/// Above the Navigator so every route uses the same effective snapshot.
final class ReferenceCatalogScope
    extends InheritedNotifier<ReferenceCatalogController> {
  const ReferenceCatalogScope({
    required ReferenceCatalogController controller,
    required super.child,
    super.key,
  }) : super(notifier: controller);
  static ReferenceCatalog? of(BuildContext context) => context
      .dependOnInheritedWidgetOfExactType<ReferenceCatalogScope>()
      ?.notifier
      ?.catalog;
}
