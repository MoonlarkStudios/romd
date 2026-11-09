// Raw consumer-host manifest bodies. Names, casing and null emission follow
// ConsumerReleaseManifest/ConsumerReleaseManifestFormat under the
// consumer host's default web JSON options: ByteCount values are decimal
// strings, `fileExtension`/`filename`/`sizeBytes`/`sha256`/`md5`/`sha1` are
// omitted when null (Original has no `fileExtension`), while `revision`, item
// `sha256`/`contentGrant` and format `runtime`/`contentGrant` are always
// written (null when absent). A single-file release whose platform registers
// a whole-file stored delivery (GameCube, Wii and PS2 today) carries a
// per-format runtime on every available format
// (ConsumerReleaseManifestRepository.ToManifestFormat); every other release
// writes `"runtime": null` on each format.

String serverWireSingleDiscPs2Manifest({
  required String serverInstanceId,
  required String releaseId,
  required String titleId,
  required String logicalSha256,
  int logicalSizeBytes = 12,
  String chdSha256 =
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
  int chdSizeBytes = 5,
  String expiresAt = '2030-01-01T00:15:00.1234567+00:00',
}) =>
    '''
{
  "serverInstanceId": "$serverInstanceId",
  "releaseId": "$releaseId",
  "titleId": "$titleId",
  "systemKey": "ps2",
  "name": "Synthetic PS2 Disc",
  "revision": null,
  "isComplete": true,
  "isDeliveryComplete": true,
  "runtime": {
    "contentType": "single_rom",
    "launch": {"type": "file", "relativePath": "disc/game.iso"},
    "packaging": "direct_files",
    "minimumInstallBytes": "$logicalSizeBytes"
  },
  "items": [
    {
      "relativePath": "disc/game.iso",
      "role": "rom",
      "provenance": "Original",
      "sizeBytes": "$logicalSizeBytes",
      "sha256": "$logicalSha256",
      "isAvailable": true,
      "contentGrant": {
        "downloadUrl": "/delivery/content/item.sig",
        "expiresAt": "$expiresAt"
      },
      "formats": [
        {
          "relativePath": "disc/game.iso",
          "runtime": {
            "contentType": "single_rom",
            "launch": {"type": "file", "relativePath": "disc/game.iso"},
            "packaging": "direct_files",
            "minimumInstallBytes": "$logicalSizeBytes"
          },
          "contentGrant": {
            "downloadUrl": "/delivery/content/original.sig",
            "expiresAt": "$expiresAt"
          },
          "format": "original",
          "label": "Original",
          "state": "available",
          "filename": "game.iso",
          "sizeBytes": "$logicalSizeBytes",
          "sha256": "$logicalSha256",
          "md5": "${'e' * 32}",
          "sha1": "${'f' * 40}",
          "profileVersion": "identity/v1",
          "runtimeApplicability": []
        },
        {
          "relativePath": "disc/game.rvz",
          "runtime": null,
          "contentGrant": null,
          "format": "rvz",
          "label": "RVZ",
          "fileExtension": ".rvz",
          "state": "unsupported",
          "profileVersion": "unknown",
          "runtimeApplicability": []
        },
        {
          "relativePath": "disc/game.chd",
          "runtime": {
            "contentType": "single_rom",
            "launch": {"type": "file", "relativePath": "disc/game.chd"},
            "packaging": "direct_files",
            "minimumInstallBytes": "$chdSizeBytes"
          },
          "contentGrant": {
            "downloadUrl": "/delivery/content/chd.sig",
            "expiresAt": "$expiresAt"
          },
          "format": "chd",
          "label": "CHD",
          "fileExtension": ".chd",
          "state": "available",
          "filename": "game.chd",
          "sizeBytes": "$chdSizeBytes",
          "sha256": "$chdSha256",
          "md5": "${'c' * 32}",
          "sha1": "${'d' * 40}",
          "profileVersion": "chd-observed/v1",
          "runtimeApplicability": ["pcsx2"]
        }
      ]
    }
  ]
}
''';

String serverWireSnesManifestWithFormats({
  required String serverInstanceId,
  required String releaseId,
  required String titleId,
  required String logicalSha256,
}) =>
    '''
{
  "serverInstanceId": "$serverInstanceId",
  "releaseId": "$releaseId",
  "titleId": "$titleId",
  "systemKey": "snes",
  "name": "Chrono Trigger",
  "revision": null,
  "isComplete": true,
  "isDeliveryComplete": true,
  "runtime": {
    "contentType": "single_rom",
    "launch": {"type": "file", "relativePath": "chrono/game.sfc"},
    "packaging": "direct_files",
    "minimumInstallBytes": "12"
  },
  "items": [
    {
      "relativePath": "chrono/game.sfc",
      "role": "rom",
      "provenance": "Original",
      "sizeBytes": "12",
      "sha256": "$logicalSha256",
      "isAvailable": true,
      "contentGrant": {
        "downloadUrl": "/delivery/content/item.sig",
        "expiresAt": "2030-01-01T00:15:00+00:00"
      },
      "formats": [
        {
          "relativePath": "chrono/game.sfc",
          "runtime": null,
          "contentGrant": {
            "downloadUrl": "/delivery/content/original.sig",
            "expiresAt": "2030-01-01T00:15:00+00:00"
          },
          "format": "original",
          "label": "Original",
          "state": "available",
          "filename": "game.sfc",
          "sizeBytes": "12",
          "sha256": "$logicalSha256",
          "profileVersion": "identity/v1",
          "runtimeApplicability": []
        },
        {
          "relativePath": "chrono/game.rvz",
          "runtime": null,
          "contentGrant": null,
          "format": "rvz",
          "label": "RVZ",
          "fileExtension": ".rvz",
          "state": "unsupported",
          "profileVersion": "unknown",
          "runtimeApplicability": []
        }
      ]
    }
  ]
}
''';
