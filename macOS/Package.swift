// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Translated",
    platforms: [.macOS(.v15)],
    products: [.executable(name: "Translated", targets: ["Translated"])],
    targets: [
        .target(name: "TranslationCore"),
        .executableTarget(name: "Translated", dependencies: ["TranslationCore"]),
        .testTarget(name: "TranslationCoreTests", dependencies: ["TranslationCore"])
    ]
)
