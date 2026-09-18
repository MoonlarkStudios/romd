import 'canonical_controller_mapping.dart';

final class SdlGamepadMappingDocument {
  const SdlGamepadMappingDocument({
    required this.sdlGuid,
    required this.displayName,
    required this.sdlPlatform,
    required this.mapping,
  });

  final String sdlGuid;
  final String displayName;
  final String sdlPlatform;
  final CanonicalControllerMapping mapping;
}

/// Strict codec for the SDL3 mapping-string subset ROMD owns.
///
/// Strict decoding is intentional: a future SDL token or ROMD format version
/// fails closed instead of being guessed, normalized, or passively rewritten.
final class SdlGamepadMappingCodec {
  const SdlGamepadMappingCodec();

  static const int currentFormatVersion = 1;

  static final RegExp _guidPattern = RegExp(r'^[0-9a-fA-F]{32}$');
  static final RegExp _sourcePattern = RegExp(r'^([+-]?)([ab])(\d+)(~?)$');
  static final RegExp _hatPattern = RegExp(r'^h(\d+)\.([1248])$');

  String encode(SdlGamepadMappingDocument document) {
    final guid = normalizeGuid(document.sdlGuid);
    final name = _validatedField(document.displayName, 'displayName');
    final platform = normalizePlatform(document.sdlPlatform);
    final fields = <String>[guid, name];
    for (final control in CanonicalGamepadControl.values) {
      final input = document.mapping.inputFor(control);
      if (input != null) {
        fields.add('${_controlNames[control]}:${_encodeInput(input)}');
      }
    }
    fields.add('platform:$platform');
    return '${fields.join(',')},';
  }

  SdlGamepadMappingDocument decode(String value) {
    if (!value.endsWith(',')) {
      throw const FormatException('SDL mapping must end with a comma');
    }
    final fields = value.substring(0, value.length - 1).split(',');
    if (fields.length < 3) {
      throw const FormatException('SDL mapping is incomplete');
    }
    final guid = normalizeGuid(fields[0]);
    final name = _validatedField(fields[1], 'displayName');
    String? platform;
    final bindings = <CanonicalGamepadControl, RawGamepadInput>{};
    for (final field in fields.skip(2)) {
      final separator = field.indexOf(':');
      if (separator <= 0 || separator == field.length - 1) {
        throw FormatException('Malformed SDL mapping field: $field');
      }
      final key = field.substring(0, separator);
      final source = field.substring(separator + 1);
      if (key == 'platform') {
        if (platform != null) {
          throw const FormatException('Duplicate SDL platform field');
        }
        platform = normalizePlatform(source);
        continue;
      }
      final control = _controlsByName[key];
      if (control == null || bindings.containsKey(control)) {
        throw FormatException('Unsupported or duplicate SDL target: $key');
      }
      bindings[control] = _decodeInput(source);
    }
    if (platform == null) {
      throw const FormatException('SDL platform field is required');
    }
    return SdlGamepadMappingDocument(
      sdlGuid: guid,
      displayName: name,
      sdlPlatform: platform,
      mapping: CanonicalControllerMapping(bindings),
    );
  }

  /// Imports the broader mapping strings returned by SDL for detected pads.
  ///
  /// SDL may append metadata and controls ROMD does not model. Those fields,
  /// and individual canonical bindings whose source syntax ROMD cannot safely
  /// represent, are ignored. Identity remains validated and persisted rows
  /// continue to use the strict [decode] path above.
  SdlGamepadMappingDocument decodeDetected(
    String value, {
    String? fallbackPlatform,
  }) {
    final fields =
        (value.endsWith(',') ? value.substring(0, value.length - 1) : value)
            .split(',');
    if (fields.length < 2) {
      throw const FormatException('Detected SDL mapping is incomplete');
    }
    final guid = normalizeGuid(fields[0]);
    final name = _validatedField(fields[1], 'displayName');
    String? platform;
    final bindings = <CanonicalGamepadControl, RawGamepadInput>{};
    for (final field in fields.skip(2)) {
      final separator = field.indexOf(':');
      if (separator <= 0 || separator == field.length - 1) {
        continue;
      }
      final key = field.substring(0, separator);
      final source = field.substring(separator + 1);
      if (key == 'platform') {
        if (platform == null) {
          try {
            platform = normalizePlatform(source);
          } on FormatException {
            // The provider platform below can still establish identity.
          }
        }
        continue;
      }
      if (_detectedMetadata.contains(key)) {
        continue;
      }
      final control = _controlsByName[key];
      if (control == null || bindings.containsKey(control)) {
        continue;
      }
      try {
        final input = _decodeInput(source);
        // Validate this source in isolation so an out-of-range SDL extension
        // drops one binding rather than invalidating the detected controller.
        CanonicalControllerMapping(<CanonicalGamepadControl, RawGamepadInput>{
          control: input,
        });
        if (bindings.values.any((existing) => existing.conflictsWith(input))) {
          continue;
        }
        bindings[control] = input;
      } on FormatException {
        continue;
      } on ArgumentError {
        continue;
      }
    }
    final effectivePlatform =
        platform ??
        (fallbackPlatform == null
            ? throw const FormatException(
                'Detected SDL mapping platform is required',
              )
            : normalizePlatform(fallbackPlatform));
    return SdlGamepadMappingDocument(
      sdlGuid: guid,
      displayName: name,
      sdlPlatform: effectivePlatform,
      mapping: CanonicalControllerMapping(bindings),
    );
  }

  static String normalizeGuid(String value) {
    final guid = value.trim();
    if (!_guidPattern.hasMatch(guid)) {
      throw FormatException('Invalid SDL GUID: $value');
    }
    return guid.toLowerCase();
  }

  static String normalizePlatform(String value) =>
      _validatedField(value.trim(), 'sdlPlatform');

  static String _validatedField(String value, String field) {
    if (value.isEmpty ||
        value.contains(',') ||
        value.contains('\n') ||
        value.contains('\r')) {
      throw FormatException(
        '$field must be non-empty and contain no separators',
      );
    }
    return value;
  }

  static String _encodeInput(RawGamepadInput input) => switch (input) {
    RawButtonInput(:final button) => 'b$button',
    RawAxisInput(:final axis, :final direction, :final inverted) =>
      '${switch (direction) {
        RawAxisDirection.full => '',
        RawAxisDirection.negative => '-',
        RawAxisDirection.positive => '+',
      }}a$axis${inverted ? '~' : ''}',
    RawHatInput(:final hat, :final mask) => 'h$hat.$mask',
  };

  static RawGamepadInput _decodeInput(String source) {
    final hat = _hatPattern.firstMatch(source);
    if (hat != null) {
      return RawHatInput(int.parse(hat[1]!), int.parse(hat[2]!));
    }
    final input = _sourcePattern.firstMatch(source);
    if (input == null) {
      throw FormatException('Unsupported SDL input: $source');
    }
    final kind = input[2]!;
    final index = int.parse(input[3]!);
    final inverted = input[4]!.isNotEmpty;
    if (kind == 'b') {
      if (input[1]!.isNotEmpty || inverted) {
        throw FormatException('Button modifiers are unsupported: $source');
      }
      return RawButtonInput(index);
    }
    return RawAxisInput(
      index,
      direction: switch (input[1]!) {
        '-' => RawAxisDirection.negative,
        '+' => RawAxisDirection.positive,
        _ => RawAxisDirection.full,
      },
      inverted: inverted,
    );
  }

  static const Map<CanonicalGamepadControl, String> _controlNames =
      <CanonicalGamepadControl, String>{
        CanonicalGamepadControl.faceSouth: 'a',
        CanonicalGamepadControl.faceEast: 'b',
        CanonicalGamepadControl.faceWest: 'x',
        CanonicalGamepadControl.faceNorth: 'y',
        CanonicalGamepadControl.dpadUp: 'dpup',
        CanonicalGamepadControl.dpadDown: 'dpdown',
        CanonicalGamepadControl.dpadLeft: 'dpleft',
        CanonicalGamepadControl.dpadRight: 'dpright',
        CanonicalGamepadControl.leftShoulder: 'leftshoulder',
        CanonicalGamepadControl.rightShoulder: 'rightshoulder',
        CanonicalGamepadControl.leftTrigger: 'lefttrigger',
        CanonicalGamepadControl.rightTrigger: 'righttrigger',
        CanonicalGamepadControl.select: 'back',
        CanonicalGamepadControl.start: 'start',
        CanonicalGamepadControl.guide: 'guide',
        CanonicalGamepadControl.leftStickPress: 'leftstick',
        CanonicalGamepadControl.rightStickPress: 'rightstick',
        CanonicalGamepadControl.leftStickX: 'leftx',
        CanonicalGamepadControl.leftStickY: 'lefty',
        CanonicalGamepadControl.rightStickX: 'rightx',
        CanonicalGamepadControl.rightStickY: 'righty',
      };

  static final Map<String, CanonicalGamepadControl> _controlsByName =
      <String, CanonicalGamepadControl>{
        for (final entry in _controlNames.entries) entry.value: entry.key,
      };

  static const Set<String> _detectedMetadata = <String>{'crc', 'hint', 'sdk'};
}
