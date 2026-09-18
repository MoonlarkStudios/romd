import Foundation
import GameController
import Testing

@testable import ControllerIsolationProbe

@Test func parsesInventoryDefaults() throws {
  #expect(try ProbeArguments.parse(["inventory"]) == .inventory(durationSeconds: 5))
}

@Test func parsesExplicitSeize() throws {
  #expect(
    try ProbeArguments.parse(["seize", "--candidate-id", "hid-1", "--duration-seconds", "2"])
      == .seize(candidateID: "hid-1", durationSeconds: 2)
  )
}

@Test func parsesPlayerIndexDefaults() throws {
  #expect(
    try ProbeArguments.parse(["player-index"])
      == .playerIndex(controllerOrder: 0, setRawValue: nil, durationSeconds: 5)
  )
}

@Test func parsesExplicitPlayerIndexSet() throws {
  #expect(
    try ProbeArguments.parse([
      "player-index", "--controller-order", "1", "--set", "2", "--duration-seconds", "120",
    ]) == .playerIndex(controllerOrder: 1, setRawValue: 2, durationSeconds: 120)
  )
}

@Test func parsesPlayerIndexUnsetValue() throws {
  #expect(
    try ProbeArguments.parse(["player-index", "--set", "-1"])
      == .playerIndex(controllerOrder: 0, setRawValue: -1, durationSeconds: 5)
  )
}

@Test(arguments: [
  [String](),
  ["unknown"],
  ["inventory", "--candidate-id", "hid-1"],
  ["inventory", "--duration-seconds", "0"],
  ["inventory", "--duration-seconds", "301"],
  ["inventory", "--set", "1"],
  ["inventory", "--controller-order", "0"],
  ["seize"],
  ["seize", "--candidate-id"],
  ["seize", "--candidate-id", "hid-1", "--controller-order", "0"],
  ["seize", "--candidate-id", "hid-1", "--set", "1"],
  ["player-index", "--candidate-id", "hid-1"],
  ["player-index", "--set"],
  ["player-index", "--set", "4"],
  ["player-index", "--set", "-2"],
  ["player-index", "--set", "notANumber"],
  ["player-index", "--controller-order", "8"],
  ["player-index", "--controller-order", "-1"],
  ["player-index", "--set", "1", "--set", "2"],
])
func rejectsMalformedArguments(arguments: [String]) {
  #expect(throws: ProbeArgumentError.self) {
    try ProbeArguments.parse(arguments)
  }
}

@Test func fingerprintsSensitiveIdentity() {
  let raw = "host-or-device-secret"
  let redacted = EvidenceRedactor.fingerprint(raw)
  #expect(redacted != .string(raw))
  #expect(redacted == EvidenceRedactor.fingerprint(raw))
  #expect(EvidenceRedactor.token(raw).count == 16)
  #expect(!EvidenceRedactor.token(raw).contains(raw))
  #expect(EvidenceRedactor.fingerprint(nil) == .missing)
}

@Test func filtersIORegistryToControllerRepresentations() {
  #expect(IORegistryInventory.isControllerUsage(usagePage: 0x01, usage: 0x04, synthetic: false))
  #expect(IORegistryInventory.isControllerUsage(usagePage: 0x01, usage: 0x05, synthetic: false))
  #expect(IORegistryInventory.isControllerUsage(usagePage: 0x01, usage: 0x08, synthetic: false))
  #expect(!IORegistryInventory.isControllerUsage(usagePage: 0x01, usage: 0x06, synthetic: false))
  #expect(IORegistryInventory.isControllerUsage(usagePage: nil, usage: nil, synthetic: true))
}

@Test func countsAttributedInputWithoutControlValues() {
  let counter = InputObservationCounter()
  counter.record(result: kIOReturnSuccess, timestamp: 100)
  counter.record(result: kIOReturnSuccess, timestamp: 140)
  counter.record(result: kIOReturnError, timestamp: 180)

  let observation = counter.snapshot()
  #expect(observation.eventCount == 2)
  #expect(observation.firstTimestamp == 100)
  #expect(observation.lastTimestamp == 140)
  #expect(observation.callbackErrorCount == 1)
  #expect(observation.evidenceFields["rawControlValuesCaptured"] == .bool(false))
}

@Test func trackerEmitsNothingForStableSamples() {
  let tracker = PlayerIndexTransitionTracker()
  let sample = PlayerIndexSample(
    controllerPresent: true, playerIndexRawValue: -1, controllerCount: 1)
  #expect(tracker.ingest(sample).isEmpty)
  #expect(tracker.ingest(sample).isEmpty)
}

@Test func trackerRecordsPlayerIndexChange() {
  let tracker = PlayerIndexTransitionTracker()
  _ = tracker.ingest(
    PlayerIndexSample(controllerPresent: true, playerIndexRawValue: -1, controllerCount: 1))
  let transitions = tracker.ingest(
    PlayerIndexSample(controllerPresent: true, playerIndexRawValue: 0, controllerCount: 1))
  #expect(
    transitions == [
      PlayerIndexTransition(
        kind: .playerIndexChanged,
        previousPlayerIndex: -1,
        currentPlayerIndex: 0,
        previousPresent: true,
        currentPresent: true,
        previousCount: 1,
        currentCount: 1)
    ])
}

@Test func trackerRecordsDisconnectAsPresenceAndCountChange() {
  let tracker = PlayerIndexTransitionTracker()
  _ = tracker.ingest(
    PlayerIndexSample(controllerPresent: true, playerIndexRawValue: 2, controllerCount: 1))
  let transitions = tracker.ingest(
    PlayerIndexSample(controllerPresent: false, playerIndexRawValue: nil, controllerCount: 0))
  #expect(transitions.count == 2)
  #expect(transitions[0].kind == .presenceChanged)
  #expect(transitions[0].previousPlayerIndex == 2)
  #expect(transitions[0].currentPlayerIndex == nil)
  #expect(transitions[1].kind == .controllerCountChanged)
  #expect(transitions[1].previousCount == 1)
  #expect(transitions[1].currentCount == 0)
}

@Test func trackerDoesNotReportIndexChangeAcrossPresenceChange() {
  let tracker = PlayerIndexTransitionTracker()
  _ = tracker.ingest(
    PlayerIndexSample(controllerPresent: false, playerIndexRawValue: nil, controllerCount: 0))
  let transitions = tracker.ingest(
    PlayerIndexSample(controllerPresent: true, playerIndexRawValue: 1, controllerCount: 1))
  #expect(transitions.map(\.kind) == [.presenceChanged, .controllerCountChanged])
}

@Test func transitionEvidenceFieldsEncodeMissingIndices() {
  let transition = PlayerIndexTransition(
    kind: .presenceChanged,
    previousPlayerIndex: 3,
    currentPlayerIndex: nil,
    previousPresent: true,
    currentPresent: false,
    previousCount: 2,
    currentCount: 1)
  #expect(transition.evidenceFields["kind"] == .string("presenceChanged"))
  #expect(transition.evidenceFields["previousPlayerIndex"] == .integer(3))
  #expect(transition.evidenceFields["currentPlayerIndex"] == .missing)
  #expect(transition.evidenceFields["currentControllerCount"] == .integer(1))
}

@Test func mapsRawValuesToExactPlayerIndexCases() {
  #expect(GameControllerPlayerIndexProbe.playerIndex(fromRawValue: -1) == .indexUnset)
  #expect(GameControllerPlayerIndexProbe.playerIndex(fromRawValue: 0) == .index1)
  #expect(GameControllerPlayerIndexProbe.playerIndex(fromRawValue: 1) == .index2)
  #expect(GameControllerPlayerIndexProbe.playerIndex(fromRawValue: 2) == .index3)
  #expect(GameControllerPlayerIndexProbe.playerIndex(fromRawValue: 3) == .index4)
  #expect(GameControllerPlayerIndexProbe.playerIndex(fromRawValue: 4) == nil)
}
