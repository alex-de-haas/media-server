// swift-tools-version: 6.2

import PackageDescription

// Everything the clients share and nothing that draws: models, the capability profile, and the API
// client. Kept a package rather than a framework target so it builds and tests on its own — the logic
// worth testing is the logic that has no screen.
let package = Package(
    name: "MediaKit",
    platforms: [.tvOS(.v18), .iOS(.v18), .macOS(.v15)],
    products: [
        .library(name: "MediaKit", targets: ["MediaKit"]),
    ],
    targets: [
        .target(name: "MediaKit"),
        .testTarget(name: "MediaKitTests", dependencies: ["MediaKit"]),
    ]
)
