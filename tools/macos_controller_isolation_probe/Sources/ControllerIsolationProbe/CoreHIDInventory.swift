import CoreHID
import Foundation

@available(macOS 15, *)
private actor CoreHIDRecordStore {
  private var recordsByDeviceID: [UInt64: [String: EvidenceValue]] = [:]

  func record(_ reference: HIDDeviceClient.DeviceReference) async {
    guard let client = HIDDeviceClient(deviceReference: reference) else { return }
    let transport = await client.transport
    recordsByDeviceID[reference.deviceID] = [
      "providerDeviceId": EvidenceRedactor.fingerprint(String(reference.deviceID)),
      "product": EvidenceRedactor.plain(await client.product),
      "manufacturer": EvidenceRedactor.plain(await client.manufacturer),
      "modelNumber": EvidenceRedactor.plain(await client.modelNumber),
      "vendorId": .integer(Int64(await client.vendorID)),
      "productId": .integer(Int64(await client.productID)),
      "serial": EvidenceRedactor.fingerprint(await client.serialNumber),
      "uniqueId": EvidenceRedactor.fingerprint(await client.uniqueID),
      "location": locationValue(await client.locationID),
      "transport": EvidenceRedactor.plain(transportName(transport)),
      "primaryUsagePage": .integer(Int64(await client.primaryUsage.page)),
      "primaryUsage": usageValue(await client.primaryUsage.usage),
      "isBuiltIn": .bool(await client.isBuiltIn),
      "syntheticGameController": .missing,
      "classification": .string(transport == .virtual ? "virtual" : "physical"),
    ]
  }

  func snapshot() -> [[String: EvidenceValue]] {
    recordsByDeviceID.sorted { $0.key < $1.key }.enumerated().map { order, item in
      var fields = item.value
      fields["observationOrder"] = .integer(Int64(order))
      return fields
    }
  }

  private func locationValue(_ value: UInt64?) -> EvidenceValue {
    guard let value else { return .missing }
    return EvidenceRedactor.fingerprint(String(value))
  }

  private func usageValue(_ value: UInt16?) -> EvidenceValue {
    guard let value else { return .missing }
    return .integer(Int64(value))
  }

  private func transportName(_ value: HIDDeviceTransport?) -> String? {
    switch value {
    case .usb: "USB"
    case .bluetooth: "Bluetooth"
    case .bluetoothLowEnergy: "BluetoothLowEnergy"
    case .bluetoothAACP: "BluetoothAACP"
    case .aid: "AID"
    case .i2c: "I2C"
    case .spi: "SPI"
    case .serial: "Serial"
    case .iap: "iAP"
    case .airPlay: "AirPlay"
    case .spu: "SPU"
    case .fifo: "FIFO"
    case .inductiveInBand: "InductiveInBand"
    case .virtual: "Virtual"
    case .unknown(let name): "Unknown:\(name)"
    case nil: nil
    @unknown default: "UnknownFutureTransport"
    }
  }
}

@available(macOS 15, *)
enum CoreHIDInventory {
  static func records(observationSeconds: Double = 0.25) async throws
    -> [[String: EvidenceValue]]
  {
    let manager = HIDDeviceManager()
    let usages: [HIDUsage] = [
      .genericDesktop(.joystick),
      .genericDesktop(.gamepad),
      .genericDesktop(.multiAxisController),
    ]
    let criteria = usages.map {
      HIDDeviceManager.DeviceMatchingCriteria(primaryUsage: $0)
    }
    let stream = await manager.monitorNotifications(matchingCriteria: criteria)
    let store = CoreHIDRecordStore()
    let monitor = Task {
      for try await notification in stream {
        guard case .deviceMatched(let reference) = notification else { continue }
        await store.record(reference)
      }
    }
    try await Task.sleep(for: .seconds(observationSeconds))
    monitor.cancel()
    return await store.snapshot()
  }
}
