import Foundation
import GameController

struct PlayerIndexSample: Equatable {
  let controllerPresent: Bool
  let playerIndexRawValue: Int?
  let controllerCount: Int
}

struct PlayerIndexTransition: Equatable {
  enum Kind: String {
    case playerIndexChanged
    case presenceChanged
    case controllerCountChanged
  }

  let kind: Kind
  let previousPlayerIndex: Int?
  let currentPlayerIndex: Int?
  let previousPresent: Bool
  let currentPresent: Bool
  let previousCount: Int
  let currentCount: Int

  var evidenceFields: [String: EvidenceValue] {
    [
      "kind": .string(kind.rawValue),
      "previousPlayerIndex": integerOrMissing(previousPlayerIndex),
      "currentPlayerIndex": integerOrMissing(currentPlayerIndex),
      "previousPresent": .bool(previousPresent),
      "currentPresent": .bool(currentPresent),
      "previousControllerCount": .integer(Int64(previousCount)),
      "currentControllerCount": .integer(Int64(currentCount)),
    ]
  }

  private func integerOrMissing(_ value: Int?) -> EvidenceValue {
    guard let value else { return .missing }
    return .integer(Int64(value))
  }
}

final class PlayerIndexTransitionTracker {
  private var last: PlayerIndexSample?

  func ingest(_ sample: PlayerIndexSample) -> [PlayerIndexTransition] {
    defer { last = sample }
    guard let previous = last, previous != sample else { return [] }
    var transitions: [PlayerIndexTransition] = []
    if previous.controllerPresent != sample.controllerPresent {
      transitions.append(transition(kind: .presenceChanged, from: previous, to: sample))
    } else if previous.playerIndexRawValue != sample.playerIndexRawValue {
      transitions.append(transition(kind: .playerIndexChanged, from: previous, to: sample))
    }
    if previous.controllerCount != sample.controllerCount {
      transitions.append(transition(kind: .controllerCountChanged, from: previous, to: sample))
    }
    return transitions
  }

  private func transition(
    kind: PlayerIndexTransition.Kind,
    from previous: PlayerIndexSample,
    to current: PlayerIndexSample
  ) -> PlayerIndexTransition {
    PlayerIndexTransition(
      kind: kind,
      previousPlayerIndex: previous.playerIndexRawValue,
      currentPlayerIndex: current.playerIndexRawValue,
      previousPresent: previous.controllerPresent,
      currentPresent: current.controllerPresent,
      previousCount: previous.controllerCount,
      currentCount: current.controllerCount
    )
  }
}

enum GameControllerPlayerIndexProbe {
  static let settleSeconds = 10.0
  private static let pollInterval: Duration = .milliseconds(100)

  static func playerIndex(fromRawValue rawValue: Int) -> GCControllerPlayerIndex? {
    switch rawValue {
    case -1: .indexUnset
    case 0: .index1
    case 1: .index2
    case 2: .index3
    case 3: .index4
    default: nil
    }
  }

  @MainActor
  static func run(
    controllerOrder: Int,
    setRawValue: Int?,
    durationSeconds: Double,
    writer: EvidenceWriter
  ) async throws {
    let clock = ContinuousClock()
    let settleDeadline = clock.now.advanced(by: .seconds(settleSeconds))
    while GCController.controllers().count <= controllerOrder, clock.now < settleDeadline {
      try await Task.sleep(for: pollInterval)
    }
    let controllers = GCController.controllers()
    guard controllers.count > controllerOrder else {
      try writer.write(
        observer: "gameController", event: "controllerNotObserved",
        fields: [
          "controllerOrder": .integer(Int64(controllerOrder)),
          "settleSeconds": .number(settleSeconds),
          "controllerCount": .integer(Int64(controllers.count)),
        ])
      throw ProbeArgumentError.usage(
        "Controller order \(controllerOrder) was not observed within the settle window.")
    }
    let controller = controllers[controllerOrder]
    try writer.write(
      observer: "gameController", event: "playerIndexBaseline",
      fields: [
        "controllerOrder": .integer(Int64(controllerOrder)),
        "vendorName": EvidenceRedactor.plain(controller.vendorName),
        "productCategory": EvidenceRedactor.plain(controller.productCategory),
        "playerIndex": .integer(Int64(controller.playerIndex.rawValue)),
        "controllerCount": .integer(Int64(controllers.count)),
        "extendedGamepad": .bool(controller.extendedGamepad != nil),
      ])

    if let setRawValue {
      guard let target = playerIndex(fromRawValue: setRawValue) else {
        throw ProbeArgumentError.usage("Player index \(setRawValue) is not representable.")
      }
      controller.playerIndex = target
      let readBack = controller.playerIndex.rawValue
      try writer.write(
        observer: "gameController", event: "playerIndexSet",
        fields: [
          "controllerOrder": .integer(Int64(controllerOrder)),
          "requestedPlayerIndex": .integer(Int64(setRawValue)),
          "readBackPlayerIndex": .integer(Int64(readBack)),
          "readBackMatchesRequest": .bool(readBack == setRawValue),
        ])
    }

    let start = clock.now
    let deadline = start.advanced(by: .seconds(durationSeconds))
    let tracker = PlayerIndexTransitionTracker()
    _ = tracker.ingest(sample(for: controller))
    var transitionCount = 0
    while clock.now < deadline {
      try await Task.sleep(for: pollInterval)
      for transition in tracker.ingest(sample(for: controller)) {
        transitionCount += 1
        let elapsed = start.duration(to: clock.now)
        try writer.write(
          observer: "gameController", event: "playerIndexTransition",
          fields: transition.evidenceFields.merging([
            "controllerOrder": .integer(Int64(controllerOrder)),
            "elapsedMilliseconds": .integer(Int64((elapsed / .milliseconds(1)).rounded())),
          ]) { _, new in new })
      }
    }
    let finalSample = sample(for: controller)
    try writer.write(
      observer: "gameController", event: "playerIndexObservation",
      fields: [
        "controllerOrder": .integer(Int64(controllerOrder)),
        "transitionCount": .integer(Int64(transitionCount)),
        "finalPresent": .bool(finalSample.controllerPresent),
        "finalPlayerIndex": finalSample.playerIndexRawValue.map { EvidenceValue.integer(Int64($0)) }
          ?? .missing,
        "finalControllerCount": .integer(Int64(finalSample.controllerCount)),
        "propagationClassified": .bool(false),
      ])
  }

  @MainActor
  private static func sample(for controller: GCController) -> PlayerIndexSample {
    let controllers = GCController.controllers()
    let present = controllers.contains(controller)
    return PlayerIndexSample(
      controllerPresent: present,
      playerIndexRawValue: present ? controller.playerIndex.rawValue : nil,
      controllerCount: controllers.count
    )
  }
}
