import Foundation

enum ProbeCommand: Equatable {
  case inventory(durationSeconds: Double)
  case seize(candidateID: String, durationSeconds: Double)
  case playerIndex(controllerOrder: Int, setRawValue: Int?, durationSeconds: Double)
}

enum ProbeArgumentError: Error, Equatable, CustomStringConvertible {
  case usage(String)

  var description: String {
    switch self {
    case .usage(let message): message
    }
  }
}

enum ProbeArguments {
  static let usage = """
    Usage:
      romd-macos-controller-probe inventory [--duration-seconds <1...300>]
      romd-macos-controller-probe seize --candidate-id <id> [--duration-seconds <1...300>]
      romd-macos-controller-probe player-index [--controller-order <0...7>] \
    [--set <-1...3>] [--duration-seconds <1...300>]

    The seize command is explicit and development-only. It never creates a
    virtual device and never classifies isolation.

    The player-index command observes one GameController's playerIndex and can
    explicitly set it. Raw values 0...3 correspond to GCControllerPlayerIndex
    1...4; -1 is indexUnset. It never classifies propagation.
    """

  static func parse(_ arguments: [String]) throws -> ProbeCommand {
    guard let verb = arguments.first else {
      throw ProbeArgumentError.usage(usage)
    }

    var candidateID: String?
    var controllerOrder: Int?
    var setRawValue: Int?
    var duration = 5.0
    var index = 1
    while index < arguments.count {
      let option = arguments[index]
      guard index + 1 < arguments.count else {
        throw ProbeArgumentError.usage("Missing value for \(option).\n\n\(usage)")
      }
      let value = arguments[index + 1]
      switch option {
      case "--candidate-id":
        guard candidateID == nil, !value.isEmpty else {
          throw ProbeArgumentError.usage("Invalid or repeated candidate id.\n\n\(usage)")
        }
        candidateID = value
      case "--controller-order":
        guard controllerOrder == nil, let parsed = Int(value), (0...7).contains(parsed) else {
          throw ProbeArgumentError.usage(
            "Controller order must be between 0 and 7.\n\n\(usage)")
        }
        controllerOrder = parsed
      case "--set":
        guard setRawValue == nil, let parsed = Int(value), (-1...3).contains(parsed) else {
          throw ProbeArgumentError.usage(
            "Player index must be between -1 (unset) and 3.\n\n\(usage)")
        }
        setRawValue = parsed
      case "--duration-seconds":
        guard let parsed = Double(value), (1...300).contains(parsed) else {
          throw ProbeArgumentError.usage("Duration must be between 1 and 300 seconds.\n\n\(usage)")
        }
        duration = parsed
      default:
        throw ProbeArgumentError.usage("Unknown option \(option).\n\n\(usage)")
      }
      index += 2
    }

    switch verb {
    case "inventory":
      guard candidateID == nil, controllerOrder == nil, setRawValue == nil else {
        throw ProbeArgumentError.usage(
          "inventory does not accept --candidate-id, --controller-order, or --set.\n\n\(usage)")
      }
      return .inventory(durationSeconds: duration)
    case "seize":
      guard controllerOrder == nil, setRawValue == nil else {
        throw ProbeArgumentError.usage(
          "seize does not accept --controller-order or --set.\n\n\(usage)")
      }
      guard let candidateID else {
        throw ProbeArgumentError.usage("seize requires --candidate-id.\n\n\(usage)")
      }
      return .seize(candidateID: candidateID, durationSeconds: duration)
    case "player-index":
      guard candidateID == nil else {
        throw ProbeArgumentError.usage("player-index does not accept --candidate-id.\n\n\(usage)")
      }
      return .playerIndex(
        controllerOrder: controllerOrder ?? 0, setRawValue: setRawValue, durationSeconds: duration)
    default:
      throw ProbeArgumentError.usage("Unknown command \(verb).\n\n\(usage)")
    }
  }
}
