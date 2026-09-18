import Foundation
import GameController
import IOKit
import IOKit.hid

private let gamepadUsagePage: Int = 0x01
private let joystickUsage: Int = 0x04
private let gamepadUsage: Int = 0x05
private let multiAxisUsage: Int = 0x08

struct HIDCandidate {
  let id: String
  let device: IOHIDDevice
  let order: Int
}

final class IOHIDInventory {
  private let manager: IOHIDManager

  init?() {
    manager = IOHIDManagerCreate(kCFAllocatorDefault, 0)
    let criteria: [[String: Any]] = [joystickUsage, gamepadUsage, multiAxisUsage].map {
      [
        kIOHIDDeviceUsagePageKey as String: gamepadUsagePage,
        kIOHIDDeviceUsageKey as String: $0,
      ]
    }
    IOHIDManagerSetDeviceMatchingMultiple(manager, criteria as CFArray)
    guard IOHIDManagerOpen(manager, 0) == kIOReturnSuccess else {
      return nil
    }
  }

  deinit {
    IOHIDManagerClose(manager, 0)
  }

  func candidates() -> [HIDCandidate] {
    let devices = (IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice>) ?? []
    return devices.map { device in
      (device, registryID(device))
    }.sorted { left, right in
      if left.1 != right.1 { return left.1 < right.1 }
      return propertyString(left.0, kIOHIDProductKey) < propertyString(right.0, kIOHIDProductKey)
    }.enumerated().map { order, item in
      let candidateID = "hid-" + EvidenceRedactor.token("registry:\(item.1)")
      return HIDCandidate(id: candidateID, device: item.0, order: order)
    }
  }

  func fields(for candidate: HIDCandidate) -> [String: EvidenceValue] {
    let device = candidate.device
    return [
      "candidateId": .string(candidate.id),
      "observationOrder": .integer(Int64(candidate.order)),
      "registryEntryId": EvidenceRedactor.fingerprint(String(registryID(device))),
      "product": EvidenceRedactor.plain(propertyString(device, kIOHIDProductKey)),
      "manufacturer": EvidenceRedactor.plain(propertyString(device, kIOHIDManufacturerKey)),
      "vendorId": propertyIntegerValue(device, kIOHIDVendorIDKey),
      "productId": propertyIntegerValue(device, kIOHIDProductIDKey),
      "serial": EvidenceRedactor.fingerprint(propertyString(device, kIOHIDSerialNumberKey)),
      "uniqueId": EvidenceRedactor.fingerprint(propertyString(device, kIOHIDUniqueIDKey)),
      "location": EvidenceRedactor.fingerprint(propertyString(device, kIOHIDLocationIDKey)),
      "transport": EvidenceRedactor.plain(propertyString(device, kIOHIDTransportKey)),
      "primaryUsagePage": propertyIntegerValue(device, kIOHIDPrimaryUsagePageKey),
      "primaryUsage": propertyIntegerValue(device, kIOHIDPrimaryUsageKey),
      "syntheticGameController": propertyBoolValue(device, kIOHIDGCSyntheticDeviceKey),
      "classification": .string(
        propertyBool(device, kIOHIDGCSyntheticDeviceKey) == true
          ? "syntheticCompatibility" : "physical"
      ),
    ]
  }

  private func registryID(_ device: IOHIDDevice) -> UInt64 {
    var value: UInt64 = 0
    let service = IOHIDDeviceGetService(device)
    guard service != 0, IORegistryEntryGetRegistryEntryID(service, &value) == KERN_SUCCESS else {
      return 0
    }
    return value
  }

  private func property(_ device: IOHIDDevice, _ key: String) -> Any? {
    IOHIDDeviceGetProperty(device, key as CFString)
  }

  private func propertyString(_ device: IOHIDDevice, _ key: String) -> String {
    guard let value = property(device, key) else { return "" }
    if let string = value as? String { return string }
    if let number = value as? NSNumber { return number.stringValue }
    return ""
  }

  private func propertyIntegerValue(_ device: IOHIDDevice, _ key: String) -> EvidenceValue {
    guard let number = property(device, key) as? NSNumber else { return .missing }
    return .integer(number.int64Value)
  }

  private func propertyBoolValue(_ device: IOHIDDevice, _ key: String) -> EvidenceValue {
    guard let number = property(device, key) as? NSNumber else { return .missing }
    return .bool(number.boolValue)
  }

  private func propertyBool(_ device: IOHIDDevice, _ key: String) -> Bool? {
    (property(device, key) as? NSNumber)?.boolValue
  }
}

enum GameControllerInventory {
  static func records() -> [[String: EvidenceValue]] {
    GCController.controllers().enumerated().map { order, controller in
      [
        "observationOrder": .integer(Int64(order)),
        "vendorName": EvidenceRedactor.plain(controller.vendorName),
        "productCategory": EvidenceRedactor.plain(controller.productCategory),
        "playerIndex": .integer(Int64(controller.playerIndex.rawValue)),
        "extendedGamepad": .bool(controller.extendedGamepad != nil),
        "microGamepad": .bool(controller.microGamepad != nil),
      ]
    }
  }
}
