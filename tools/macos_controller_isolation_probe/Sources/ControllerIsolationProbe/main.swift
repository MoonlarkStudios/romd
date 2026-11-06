import Darwin
import Foundation
import IOKit.hid

@main
enum ControllerIsolationProbeMain {
  static func main() async {
    do {
      let command = try ProbeArguments.parse(Array(CommandLine.arguments.dropFirst()))
      let writer = EvidenceWriter()
      try writer.write(
        observer: "probe", event: "started",
        fields: [
          "pid": .integer(Int64(ProcessInfo.processInfo.processIdentifier)),
          "virtualDeviceCreation": .bool(false),
          "automaticClassification": .bool(false),
        ])

      guard let inventory = IOHIDInventory() else {
        throw ProbeArgumentError.usage("IOHIDManager could not be opened.")
      }
      let candidates = inventory.candidates()
      try await emitInventories(inventory: inventory, candidates: candidates, writer: writer)

      switch command {
      case .inventory(let durationSeconds):
        try await observe(durationSeconds: durationSeconds)
        try await emitInventories(
          inventory: inventory, candidates: inventory.candidates(), writer: writer)
      case .seize(let candidateID, let durationSeconds):
        guard let candidate = candidates.first(where: { $0.id == candidateID }) else {
          throw ProbeArgumentError.usage(
            "Candidate \(candidateID) was not present in this inventory.")
        }
        let result = IOHIDDeviceOpen(candidate.device, IOOptionBits(kIOHIDOptionsTypeSeizeDevice))
        defer {
          if result == kIOReturnSuccess {
            let closeResult = IOHIDDeviceClose(
              candidate.device, IOOptionBits(kIOHIDOptionsTypeSeizeDevice))
            try? writer.write(
              observer: "iohid", event: "seizeClose",
              fields: [
                "candidateId": .string(candidate.id),
                "result": .integer(Int64(closeResult)),
                "succeeded": .bool(closeResult == kIOReturnSuccess),
              ])
          }
        }
        try writer.write(
          observer: "iohid", event: "seizeOpen",
          fields: [
            "candidateId": .string(candidate.id),
            "result": .integer(Int64(result)),
            "succeeded": .bool(result == kIOReturnSuccess),
            "isolationProven": .bool(false),
          ])
        guard result == kIOReturnSuccess else { break }
        let inputMonitor = IOHIDInputMonitor(device: candidate.device)
        guard inputMonitor.start() else {
          throw ProbeArgumentError.usage("Could not schedule the IOHID input observer.")
        }
        defer { inputMonitor.stop() }
        inputMonitor.observe(for: durationSeconds)
        try writer.write(
          observer: "iohid", event: "seizedPhysicalInputObservation",
          fields: ["candidateId": .string(candidate.id)]
            .merging(inputMonitor.snapshot().evidenceFields) { _, new in new }
        )
        try await emitInventories(
          inventory: inventory, candidates: inventory.candidates(), writer: writer)
      case .playerIndex(let controllerOrder, let setRawValue, let durationSeconds):
        try await GameControllerPlayerIndexProbe.run(
          controllerOrder: controllerOrder,
          setRawValue: setRawValue,
          durationSeconds: durationSeconds,
          writer: writer)
        try await emitInventories(
          inventory: inventory, candidates: inventory.candidates(), writer: writer)
      }
      try writer.write(observer: "probe", event: "finished", fields: [:])
    } catch {
      FileHandle.standardError.write(Data(("error: \(error)\n").utf8))
      exit(64)
    }
  }

  private static func observe(durationSeconds: Double) async throws {
    try await Task.sleep(for: .seconds(durationSeconds))
  }

  @MainActor
  private static func emitInventories(
    inventory: IOHIDInventory,
    candidates: [HIDCandidate],
    writer: EvidenceWriter
  ) async throws {
    let registryRecords = IORegistryInventory.records()
    for fields in registryRecords {
      try writer.write(observer: "ioRegistry", event: "inventory", fields: fields)
    }
    if registryRecords.isEmpty {
      try writer.write(observer: "ioRegistry", event: "inventoryEmpty", fields: [:])
    }

    if #available(macOS 15, *) {
      let coreHIDRecords = try await CoreHIDInventory.records()
      for fields in coreHIDRecords {
        try writer.write(observer: "coreHID", event: "inventory", fields: fields)
      }
      if coreHIDRecords.isEmpty {
        try writer.write(observer: "coreHID", event: "inventoryEmpty", fields: [:])
      }
    }

    for candidate in candidates {
      try writer.write(
        observer: "iohid", event: "inventory", fields: inventory.fields(for: candidate))
    }
    if candidates.isEmpty {
      try writer.write(observer: "iohid", event: "inventoryEmpty", fields: [:])
    }
    let controllers = GameControllerInventory.records()
    for fields in controllers {
      try writer.write(observer: "gameController", event: "inventory", fields: fields)
    }
    if controllers.isEmpty {
      try writer.write(observer: "gameController", event: "inventoryEmpty", fields: [:])
    }
  }
}
