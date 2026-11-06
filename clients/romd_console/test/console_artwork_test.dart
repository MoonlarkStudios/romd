import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_artwork.dart';

void main() {
  final origin = Uri.parse('https://romd.example');
  Map<String, Object?> artwork({
    String role = 'Poster',
    String fit = 'Cover',
    String? url = '/artwork/abc/card/v1',
  }) => {
    'role': role,
    'fit': fit,
    'url': url,
    'assetId': 'abc',
    'contentVersion': 'v1',
    'width': 600,
    'height': 900,
    'originalWidth': 1200,
    'originalHeight': 1800,
    'fallbackReason': 'None',
    'variants': [
      {
        'name': 'card',
        'url': '/artwork/abc/card/v1',
        'contentVersion': 'v1',
        'contentType': 'image/png',
        'width': 600,
        'height': 900,
      },
    ],
  };
  test(
    'preserves resolved roles, measured variants and immutable identity',
    () {
      final result = ConsoleArtwork.readList([
        artwork(),
        artwork(role: 'Hero'),
      ], origin);
      expect(
        result.forRole('Poster')?.url,
        origin.resolve('/artwork/abc/card/v1'),
      );
      expect(result.forRole('Hero')?.contentVersion, 'v1');
      expect(result.first.originalHeight, 1800);
      expect(result.first.variants.single.width, 600);
      expect(result.first.contain, isFalse);
    },
  );
  test('box fallback remains contained and missing artwork remains absent', () {
    expect(
      ConsoleArtwork.read(
        artwork(fit: 'Contain', url: '/media/box'),
        origin,
      )?.contain,
      isTrue,
    );
    expect(ConsoleArtwork.read(artwork(url: null), origin)?.url, isNull);
  });
  test(
    'rejects unknown roles, invalid fit and remote or local injected URLs',
    () {
      expect(ConsoleArtwork.read(artwork(role: 'Box'), origin), isNull);
      expect(ConsoleArtwork.read(artwork(fit: 'Stretch'), origin), isNull);
      expect(
        ConsoleArtwork.read(
          artwork(url: 'https://other.example/image'),
          origin,
        )?.url,
        isNull,
      );
      expect(
        ConsoleArtwork.read(artwork(url: 'file:///private/image'), origin)?.url,
        isNull,
      );
    },
  );
}
