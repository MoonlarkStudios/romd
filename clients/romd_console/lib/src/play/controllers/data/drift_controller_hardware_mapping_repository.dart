import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/sdl_gamepad_mapping_codec.dart';

final class DriftControllerHardwareMappingRepository
    implements ControllerHardwareMappingRepository {
  DriftControllerHardwareMappingRepository({
    required AppDatabase database,
    DateTime Function()? now,
    SdlGamepadMappingCodec codec = const SdlGamepadMappingCodec(),
  }) : _db = database,
       _now = now ?? DateTime.now,
       _codec = codec;

  final AppDatabase _db;
  final DateTime Function() _now;
  final SdlGamepadMappingCodec _codec;

  @override
  Future<ControllerHardwareMapping?> find({
    required String sdlPlatform,
    required String sdlGuid,
  }) async {
    final identity = _normalizedOrNull(sdlPlatform, sdlGuid);
    if (identity == null) {
      return null;
    }
    final row =
        await (_db.select(_db.controllerHardwareMappings)..where(
              (t) =>
                  t.sdlPlatform.equals(identity.platform) &
                  t.sdlGuid.equals(identity.guid),
            ))
            .getSingleOrNull();
    if (row == null ||
        row.mappingFormatVersion !=
            SdlGamepadMappingCodec.currentFormatVersion) {
      return null;
    }
    try {
      final document = _codec.decode(row.mappingData);
      if (document.sdlPlatform != row.sdlPlatform ||
          document.sdlGuid != row.sdlGuid ||
          document.displayName != row.displayName) {
        return null;
      }
      return ControllerHardwareMapping(
        sdlPlatform: row.sdlPlatform,
        sdlGuid: row.sdlGuid,
        displayName: row.displayName,
        mapping: document.mapping,
        createdAt: row.createdAt,
        updatedAt: row.updatedAt,
      );
    } on FormatException {
      return null;
    } on ArgumentError {
      return null;
    }
  }

  @override
  Future<void> save({
    required String sdlPlatform,
    required String sdlGuid,
    required String displayName,
    required CanonicalControllerMapping mapping,
  }) async {
    final platform = SdlGamepadMappingCodec.normalizePlatform(sdlPlatform);
    final guid = SdlGamepadMappingCodec.normalizeGuid(sdlGuid);
    final document = SdlGamepadMappingDocument(
      sdlGuid: guid,
      displayName: displayName,
      sdlPlatform: platform,
      mapping: mapping,
    );
    final encoded = _codec.encode(document);
    // Decode before opening the write transaction. This makes codec validity
    // part of the repository boundary, not merely an encoder assumption.
    _codec.decode(encoded);

    await _db.transaction(() async {
      final existing =
          await (_db.select(_db.controllerHardwareMappings)..where(
                (t) => t.sdlPlatform.equals(platform) & t.sdlGuid.equals(guid),
              ))
              .getSingleOrNull();
      final timestamp = _now();
      await _db
          .into(_db.controllerHardwareMappings)
          .insertOnConflictUpdate(
            ControllerHardwareMappingsCompanion(
              sdlPlatform: Value(platform),
              sdlGuid: Value(guid),
              displayName: Value(displayName),
              mappingFormatVersion: const Value(
                SdlGamepadMappingCodec.currentFormatVersion,
              ),
              mappingData: Value(encoded),
              createdAt: Value(existing?.createdAt ?? timestamp),
              updatedAt: Value(timestamp),
            ),
          );
    });
  }

  @override
  Future<void> reset({
    required String sdlPlatform,
    required String sdlGuid,
  }) async {
    final identity = _normalizedOrNull(sdlPlatform, sdlGuid);
    if (identity == null) {
      return;
    }
    await _db.transaction(() async {
      await (_db.delete(_db.controllerHardwareMappings)..where(
            (t) =>
                t.sdlPlatform.equals(identity.platform) &
                t.sdlGuid.equals(identity.guid),
          ))
          .go();
    });
  }

  static ({String platform, String guid})? _normalizedOrNull(
    String platform,
    String guid,
  ) {
    try {
      return (
        platform: SdlGamepadMappingCodec.normalizePlatform(platform),
        guid: SdlGamepadMappingCodec.normalizeGuid(guid),
      );
    } on FormatException {
      return null;
    }
  }
}
