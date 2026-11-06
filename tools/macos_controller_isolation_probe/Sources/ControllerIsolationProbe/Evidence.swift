import CryptoKit
import Foundation

struct EvidenceRecord: Encodable {
  let schemaVersion = 1
  let observedAt: String
  let observer: String
  let event: String
  let fields: [String: EvidenceValue]
}

enum EvidenceValue: Encodable, Equatable {
  case bool(Bool)
  case integer(Int64)
  case number(Double)
  case string(String)
  case missing

  func encode(to encoder: Encoder) throws {
    var container = encoder.singleValueContainer()
    switch self {
    case .bool(let value): try container.encode(value)
    case .integer(let value): try container.encode(value)
    case .number(let value): try container.encode(value)
    case .string(let value): try container.encode(value)
    case .missing: try container.encodeNil()
    }
  }
}

enum EvidenceRedactor {
  static func token(_ value: String) -> String {
    let digest = SHA256.hash(data: Data(value.utf8))
    return digest.prefix(8).map { String(format: "%02x", $0) }.joined()
  }

  static func fingerprint(_ value: String?) -> EvidenceValue {
    guard let value, !value.isEmpty else { return .missing }
    return .string("sha256:" + token(value))
  }

  static func plain(_ value: String?) -> EvidenceValue {
    guard let value, !value.isEmpty else { return .missing }
    return .string(value)
  }
}

final class EvidenceWriter: @unchecked Sendable {
  private let encoder: JSONEncoder
  private let handle: FileHandle
  private let lock = NSLock()

  init(handle: FileHandle = .standardOutput) {
    self.handle = handle
    encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
  }

  func write(observer: String, event: String, fields: [String: EvidenceValue]) throws {
    let record = EvidenceRecord(
      observedAt: ISO8601DateFormatter().string(from: Date()),
      observer: observer,
      event: event,
      fields: fields
    )
    var data = try encoder.encode(record)
    data.append(0x0A)
    lock.lock()
    defer { lock.unlock() }
    try handle.write(contentsOf: data)
  }
}
