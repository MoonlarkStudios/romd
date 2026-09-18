import Foundation
import IOKit.hid

struct InputObservation: Equatable {
  let eventCount: Int64
  let firstTimestamp: UInt64?
  let lastTimestamp: UInt64?
  let callbackErrorCount: Int64

  var evidenceFields: [String: EvidenceValue] {
    [
      "physicalEventCount": .integer(eventCount),
      "firstMonotonicTimestamp": timestampValue(firstTimestamp),
      "lastMonotonicTimestamp": timestampValue(lastTimestamp),
      "callbackErrorCount": .integer(callbackErrorCount),
      "rawControlValuesCaptured": .bool(false),
    ]
  }

  private func timestampValue(_ value: UInt64?) -> EvidenceValue {
    guard let value else { return .missing }
    return .string(String(value))
  }
}

final class InputObservationCounter: @unchecked Sendable {
  private let lock = NSLock()
  private var eventCount: Int64 = 0
  private var firstTimestamp: UInt64?
  private var lastTimestamp: UInt64?
  private var callbackErrorCount: Int64 = 0

  func record(result: IOReturn, timestamp: UInt64) {
    lock.lock()
    defer { lock.unlock() }
    guard result == kIOReturnSuccess else {
      callbackErrorCount += 1
      return
    }
    eventCount += 1
    firstTimestamp = firstTimestamp ?? timestamp
    lastTimestamp = timestamp
  }

  func snapshot() -> InputObservation {
    lock.lock()
    defer { lock.unlock() }
    return InputObservation(
      eventCount: eventCount,
      firstTimestamp: firstTimestamp,
      lastTimestamp: lastTimestamp,
      callbackErrorCount: callbackErrorCount
    )
  }
}

final class IOHIDInputMonitor {
  private let device: IOHIDDevice
  private let counter = InputObservationCounter()
  private var runLoop: CFRunLoop?

  init(device: IOHIDDevice) {
    self.device = device
  }

  func start() -> Bool {
    guard let currentRunLoop = CFRunLoopGetCurrent() else { return false }
    runLoop = currentRunLoop
    IOHIDDeviceRegisterInputValueCallback(
      device,
      { context, result, _, value in
        guard let context else { return }
        let monitor = Unmanaged<IOHIDInputMonitor>.fromOpaque(context).takeUnretainedValue()
        monitor.counter.record(result: result, timestamp: IOHIDValueGetTimeStamp(value))
      },
      Unmanaged.passUnretained(self).toOpaque()
    )
    IOHIDDeviceScheduleWithRunLoop(device, currentRunLoop, CFRunLoopMode.defaultMode.rawValue)
    return true
  }

  func observe(for durationSeconds: Double) {
    let deadline = Date().addingTimeInterval(durationSeconds)
    while Date() < deadline {
      _ = RunLoop.current.run(
        mode: .default,
        before: min(deadline, Date().addingTimeInterval(0.05))
      )
    }
  }

  func stop() {
    IOHIDDeviceRegisterInputValueCallback(device, nil, nil)
    if let runLoop {
      IOHIDDeviceUnscheduleFromRunLoop(device, runLoop, CFRunLoopMode.defaultMode.rawValue)
    }
    runLoop = nil
  }

  func snapshot() -> InputObservation {
    counter.snapshot()
  }
}
