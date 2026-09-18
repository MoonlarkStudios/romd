// swift-tools-version: 6.0

import PackageDescription

let package = Package(
  name: "MacOSControllerIsolationProbe",
  platforms: [.macOS(.v15)],
  products: [
    .executable(name: "romd-macos-controller-probe", targets: ["ControllerIsolationProbe"])
  ],
  targets: [
    .executableTarget(
      name: "ControllerIsolationProbe",
      swiftSettings: [.unsafeFlags(["-parse-as-library"])]
    ),
    .testTarget(
      name: "ControllerIsolationProbeTests",
      dependencies: ["ControllerIsolationProbe"]
    ),
  ]
)
