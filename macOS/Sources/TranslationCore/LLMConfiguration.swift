import Foundation

public enum TranslationProvider: String, Codable, CaseIterable, Sendable {
    case apple, deepSeek, openAI, custom
    public var title: String {
        switch self { case .apple: "Apple · On device"; case .deepSeek: "DeepSeek"; case .openAI: "OpenAI"; case .custom: "Custom / Local LLM" }
    }
    public var defaultBaseURL: String {
        switch self { case .apple: ""; case .deepSeek: "https://api.deepseek.com"; case .openAI: "https://api.openai.com/v1"; case .custom: "http://localhost:11434/v1" }
    }
    public var defaultModel: String {
        switch self { case .apple: ""; case .deepSeek: "deepseek-flash"; case .openAI: "gpt-6-luna"; case .custom: "" }
    }
}

public struct LLMConfiguration: Codable, Equatable, Sendable {
    public var provider: TranslationProvider
    public var baseURL: String
    public var model: String
    public var reasoning: Bool

    public init(provider: TranslationProvider, baseURL: String? = nil, model: String? = nil, reasoning: Bool = false) {
        self.provider = provider
        self.baseURL = baseURL ?? provider.defaultBaseURL
        self.model = model ?? provider.defaultModel
        self.reasoning = reasoning
    }

    public func endpoint() throws -> URL {
        guard provider != .apple, var parts = URLComponents(string: baseURL.trimmingCharacters(in: .whitespacesAndNewlines)),
              let host = parts.host?.lowercased(), !host.isEmpty, parts.user == nil, parts.password == nil,
              parts.query == nil, parts.fragment == nil else { throw TranslationServiceError.invalidEndpoint }
        let isLocal = ["localhost", "127.0.0.1", "[::1]", "::1"].contains(host)
        guard parts.scheme == "https" || (parts.scheme == "http" && isLocal) else { throw TranslationServiceError.invalidEndpoint }
        if provider == .deepSeek && (host != "api.deepseek.com" || parts.scheme != "https") { throw TranslationServiceError.invalidEndpoint }
        if provider == .openAI && (host != "api.openai.com" || parts.scheme != "https") { throw TranslationServiceError.invalidEndpoint }
        parts.host = host
        var path = parts.path
        while path.hasSuffix("/") { path.removeLast() }
        if !path.hasSuffix("/chat/completions") { path += "/chat/completions" }
        parts.path = path
        guard let url = parts.url else { throw TranslationServiceError.invalidEndpoint }
        return url
    }

    public var credentialAccount: String? {
        guard let url = try? endpoint() else { return nil }
        return provider.rawValue + "|" + url.absoluteString
    }
    public var isLocal: Bool {
        guard let host = try? endpoint().host?.lowercased() else { return false }
        return ["localhost", "127.0.0.1", "[::1]", "::1"].contains(host)
    }
    public func validate() throws {
        _ = try endpoint()
        guard !model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw TranslationServiceError.missingModel }
    }
}

public enum TranslationServiceError: Error, LocalizedError, Equatable {
    case invalidEndpoint, missingModel, missingKey, emptyResponse, incomplete, invalidResponse, rejected, redirected
    case http(Int)
    public var errorDescription: String? {
        switch self {
        case .invalidEndpoint: "Use a valid HTTPS API base URL, or HTTP on localhost for a local model."
        case .missingModel: "Enter the model name supplied by your provider."
        case .missingKey: "Save an API key for this provider in Settings first."
        case .emptyResponse: "The provider returned no translation. Your original text is unchanged."
        case .incomplete: "The translation was cut short. Your original text is unchanged. Try a shorter passage or another model."
        case .invalidResponse: "The provider returned an unexpected response. Check the API URL and model."
        case .rejected: "The provider declined this translation. Your original text is unchanged."
        case .redirected: "The API redirected the request. Enter the provider’s final API URL in Settings."
        case .http(let code):
            switch code {
            case 401: "The API key was rejected. Update it in Settings."
            case 402: "The provider reports insufficient API balance."
            case 403: "This API key does not have access to the selected model."
            case 404: "The API endpoint or model was not found. Check Settings."
            case 429: "The provider’s rate or usage limit was reached. Try again later."
            case 500...599: "The provider is temporarily unavailable. Try again later."
            default: "The provider rejected the request (HTTP \(code)). Check your model and API settings."
            }
        }
    }
}
