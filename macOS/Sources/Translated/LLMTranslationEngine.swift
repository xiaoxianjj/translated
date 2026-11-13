import Foundation
import TranslationCore

@MainActor
enum LLMTranslationEngine {
    static func translate(text: String, route: TranslationRoute, configuration: LLMConfiguration,
                          progress: @escaping @Sendable (Int, Int) async -> Void = { _, _ in }) async throws -> String {
        try configuration.validate()
        guard let account = configuration.credentialAccount else { throw TranslationServiceError.invalidEndpoint }
        let key = try APIKeyStore.load(account: account) ?? ""
        return try await LLMClient().translate(text: text, route: route, configuration: configuration, apiKey: key, progress: progress)
    }
}
