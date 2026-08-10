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
    dependencies: [
        // Apple's own, and the only dependencies this package has. They earn it: the generated client
        // makes a drift between this and `src/api/openapi/` a compile error rather than a decoding
        // failure at runtime — which is how a `surfaceVersion` declared as a number instead of a string
        // shipped once already.
        .package(url: "https://github.com/apple/swift-openapi-generator", from: "1.10.0"),
        .package(url: "https://github.com/apple/swift-openapi-runtime", from: "1.10.0"),
        .package(url: "https://github.com/apple/swift-openapi-urlsession", from: "1.2.0"),
    ],
    targets: [
        // Generated from the committed OpenAPI document, and the generated files are committed too.
        //
        // The command plugin rather than the build plugin: this repository already generates, commits
        // and then diffs in CI — the OpenAPI document itself and the docs index both work that way — and
        // a build plugin would instead add a trust prompt in Xcode and a code-generation step to every
        // clean build. Regenerate with `scripts/generate-apple-client.sh`; CI fails if the result
        // differs from what is committed.
        .target(
            name: "MediaServerAPI",
            dependencies: [
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "OpenAPIURLSession", package: "swift-openapi-urlsession"),
            ]
        ),
        .target(name: "MediaKit", dependencies: ["MediaServerAPI"]),
        .testTarget(name: "MediaKitTests", dependencies: ["MediaKit"]),
    ]
)
