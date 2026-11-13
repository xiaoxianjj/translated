import Foundation
import TranslationCore
@preconcurrency import Translation

@MainActor
enum AppleTranslationEngine {
    static func availability(highQuality: Bool) -> LanguageAvailability {
        if #available(macOS 26.4, *) {
            return LanguageAvailability(preferredStrategy: highQuality ? .highFidelity : .lowLatency)
        }
        return LanguageAvailability()
    }

    static func configuration(route: TranslationRoute, highQuality: Bool) -> TranslationSession.Configuration {
        let source = route.sourceCode.map { Locale.Language(identifier: $0) }
        let target = Locale.Language(identifier: route.targetCode)
        if #available(macOS 26.4, *) {
            return .init(source: source, target: target, preferredStrategy: highQuality ? .highFidelity : .lowLatency)
        }
        return .init(source: source, target: target)
    }

    @available(macOS 26.0, *)
    static func installedSession(source: Locale.Language, target: Locale.Language, highQuality: Bool) -> TranslationSession {
        if #available(macOS 26.4, *) {
            return TranslationSession(installedSource: source, target: target, preferredStrategy: highQuality ? .highFidelity : .lowLatency)
        }
        return TranslationSession(installedSource: source, target: target)
    }

    static func translate(text: String, route: TranslationRoute, session: TranslationSession,
                          progress: (Int, Int) -> Void = { _, _ in }) async throws -> String {
        let plan = TranslationPlan(text: text, limit: 2_400)
        var translations: [String] = []
        for (index, chunk) in plan.chunks.enumerated() {
            try Task.checkCancellation()
            progress(index + 1, plan.chunks.count)
            if chunk.text.isEmpty { translations.append(""); continue }
            let response = try await session.translate(chunk.text)
            translations.append(response.targetText)
        }
        try Task.checkCancellation()
        return try plan.assemble(translations, targetCode: route.targetCode)
    }
}
