import 'dart:convert';

import 'package:crypto/crypto.dart';

/// romd-reference-json-v1: UTF-16 member ordering, preserved arrays, safe integer
/// numbers, short JSON control escapes and otherwise literal valid Unicode.
String canonicalReferenceJson(Object? value) {
  final result = StringBuffer();
  void string(String text) {
    result.write('"');
    for (var index = 0; index < text.length; index++) {
      final unit = text.codeUnitAt(index);
      switch (unit) {
        case 0x22:
          result.write(r'\"');
        case 0x5c:
          result.write(r'\\');
        case 0x08:
          result.write(r'\b');
        case 0x09:
          result.write(r'\t');
        case 0x0a:
          result.write(r'\n');
        case 0x0c:
          result.write(r'\f');
        case 0x0d:
          result.write(r'\r');
        default:
          if (unit < 0x20) {
            result.write('\\u${unit.toRadixString(16).padLeft(4, '0')}');
          } else if (unit >= 0xd800 && unit <= 0xdbff) {
            if (index + 1 >= text.length ||
                text.codeUnitAt(index + 1) < 0xdc00 ||
                text.codeUnitAt(index + 1) > 0xdfff) {
              throw const FormatException('Unpaired reference JSON surrogate');
            }
            result.write(text.substring(index, index + 2));
            index++;
          } else if (unit >= 0xdc00 && unit <= 0xdfff) {
            throw const FormatException('Unpaired reference JSON surrogate');
          } else {
            result.writeCharCode(unit);
          }
      }
    }
    result.write('"');
  }

  void encode(Object? item) {
    switch (item) {
      case null:
        result.write('null');
      case bool():
        result.write(item ? 'true' : 'false');
      case num():
        if (!item.isFinite ||
            (item < -9007199254740991 || item > 9007199254740991) ||
            item != item.truncate()) {
          throw const FormatException('Invalid reference JSON integer');
        }
        result.write(item.toInt());
      case String():
        string(item);
      case List<Object?>():
        result.write('[');
        for (var index = 0; index < item.length; index++) {
          if (index != 0) result.write(',');
          encode(item[index]);
        }
        result.write(']');
      case Map<String, dynamic>():
        result.write('{');
        final keys = item.keys.toList()..sort();
        for (var index = 0; index < keys.length; index++) {
          if (index != 0) result.write(',');
          string(keys[index]);
          result.write(':');
          encode(item[keys[index]]);
        }
        result.write('}');
      default:
        throw const FormatException('Unsupported reference JSON value');
    }
  }

  encode(value);
  return result.toString();
}

String referenceCatalogContentHash(Object? value) =>
    sha256.convert(utf8.encode(canonicalReferenceJson(value))).toString();
