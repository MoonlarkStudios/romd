import Foundation
import GameController
import IOKit
import IOKit.hid

enum IORegistryInventory {
  static func records() -> [[String: EvidenceValue]] {
    var iterator: io_iterator_t = 0
    guard
      IOServiceGetMatchingServices(
        kIOMainPortDefault,
        IOServiceMatching(kIOHIDDeviceKey),
        &iterator
      ) == KERN_SUCCESS
    else {
      return []
    }
    defer { IOObjectRelease(iterator) }

    var rawRecords: [(registryID: UInt64, fields: [String: EvidenceValue])] = []
    while true {
      let service = IOIteratorNext(iterator)
      guard service != 0 else { break }
      defer { IOObjectRelease(service) }

      var registryID: UInt64 = 0
      guard IORegistryEntryGetRegistryEntryID(service, &registryID) == KERN_SUCCESS else {
        continue
      }
      guard isControllerCandidate(service) else { continue }
      rawRecords.append((registryID, fields(service: service, registryID: registryID)))
    }

    return rawRecords.sorted { $0.registryID < $1.registryID }.enumerated().map {
      order, record in
      var fields = record.fields
      fields["observationOrder"] = .integer(Int64(order))
      return fields
    }
  }

  private static func fields(
    service: io_service_t,
    registryID: UInt64
  ) -> [String: EvidenceValue] {
    [
      "registryEntryId": EvidenceRedactor.fingerprint(String(registryID)),
      "product": EvidenceRedactor.plain(stringProperty(service, kIOHIDProductKey)),
      "manufacturer": EvidenceRedactor.plain(stringProperty(service, kIOHIDManufacturerKey)),
      "vendorId": integerProperty(service, kIOHIDVendorIDKey),
      "productId": integerProperty(service, kIOHIDProductIDKey),
      "serial": EvidenceRedactor.fingerprint(stringProperty(service, kIOHIDSerialNumberKey)),
      "uniqueId": EvidenceRedactor.fingerprint(stringProperty(service, kIOHIDUniqueIDKey)),
      "location": EvidenceRedactor.fingerprint(stringProperty(service, kIOHIDLocationIDKey)),
      "transport": EvidenceRedactor.plain(stringProperty(service, kIOHIDTransportKey)),
      "primaryUsagePage": integerProperty(service, kIOHIDPrimaryUsagePageKey),
      "primaryUsage": integerProperty(service, kIOHIDPrimaryUsageKey),
      "syntheticGameController": boolProperty(service, kIOHIDGCSyntheticDeviceKey),
      "classification": .string(
        bool(service, kIOHIDGCSyntheticDeviceKey) == true ? "syntheticCompatibility" : "physical"
      ),
    ]
  }

  private static func property(_ service: io_service_t, _ key: String) -> CFTypeRef? {
    IORegistryEntryCreateCFProperty(
      service,
      key as CFString,
      kCFAllocatorDefault,
      0
    )?.takeRetainedValue()
  }

  private static func stringProperty(_ service: io_service_t, _ key: String) -> String? {
    guard let value = property(service, key) else { return nil }
    if let string = value as? String { return string }
    if let number = value as? NSNumber { return number.stringValue }
    return nil
  }

  private static func integerProperty(_ service: io_service_t, _ key: String) -> EvidenceValue {
    guard let number = property(service, key) as? NSNumber else { return .missing }
    return .integer(number.int64Value)
  }

  private static func boolProperty(_ service: io_service_t, _ key: String) -> EvidenceValue {
    guard let number = property(service, key) as? NSNumber else { return .missing }
    return .bool(number.boolValue)
  }

  private static func bool(_ service: io_service_t, _ key: String) -> Bool? {
    (property(service, key) as? NSNumber)?.boolValue
  }

  private static func integer(_ service: io_service_t, _ key: String) -> Int? {
    (property(service, key) as? NSNumber)?.intValue
  }

  private static func isControllerCandidate(_ service: io_service_t) -> Bool {
    isControllerUsage(
      usagePage: integer(service, kIOHIDPrimaryUsagePageKey),
      usage: integer(service, kIOHIDPrimaryUsageKey),
      synthetic: bool(service, kIOHIDGCSyntheticDeviceKey) == true
    )
  }

  static func isControllerUsage(usagePage: Int?, usage: Int?, synthetic: Bool) -> Bool {
    synthetic || (usagePage == 0x01 && [0x04, 0x05, 0x08].contains(usage))
  }
}
