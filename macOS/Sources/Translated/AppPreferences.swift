import AppKit
@preconcurrency import ApplicationServices
import Combine
import TranslationCore

@MainActor
final class AppPreferences: ObservableObject {
    @Published private(set) var shortcut: ShortcutConfiguration
    @Published private(set) var provider: TranslationProvider
    @Published var progress: String?
    @Published var highQuality: Bool {
        didSet { UserDefaults.standard.set(highQuality, forKey: "highQuality") }
    }
    var registerShortcut: ((ShortcutConfiguration) -> Bool)?
    @Published var replaceSelection: Bool {
        didSet { UserDefaults.standard.set(replaceSelection, forKey: "replaceSelection") }
    }
    @Published var automaticButton: Bool {
        didSet { UserDefaults.standard.set(automaticButton, forKey: "automaticButton") }
    }
    @Published var verifyWeChatSelection: Bool {
        didSet { UserDefaults.standard.set(verifyWeChatSelection, forKey: "verifyWeChatSelection") }
    }
    @Published var chinese: ChineseVariant {
        didSet { UserDefaults.standard.set(chinese.rawValue, forKey: "chineseVariant") }
    }
    @Published var accessibilityGranted = AXIsProcessTrusted()
    @Published var shortcutAvailable = true

    init() {
        let defaults = UserDefaults.standard
        shortcut = defaults.data(forKey: "shortcut").flatMap { try? JSONDecoder().decode(ShortcutConfiguration.self, from: $0) } ?? .standard
        provider = TranslationProvider(rawValue: defaults.string(forKey: "provider") ?? "") ?? .apple
        highQuality = defaults.object(forKey: "highQuality") as? Bool ?? true
        replaceSelection = defaults.object(forKey: "replaceSelection") as? Bool ?? true
        automaticButton = defaults.object(forKey: "automaticButton") as? Bool ?? true
        verifyWeChatSelection = defaults.bool(forKey: "verifyWeChatSelection")
        chinese = ChineseVariant(rawValue: defaults.string(forKey: "chineseVariant") ?? "") ?? .simplified
    }

    func changeShortcut(_ candidate: ShortcutConfiguration) throws {
        if let message = candidate.validationMessage { throw SettingsError(message: message) }
        guard registerShortcut?(candidate) == true else {
            throw SettingsError(message: "That combination is unavailable. Your previous shortcut is still active.")
        }
        shortcut = candidate
        shortcutAvailable = true
        UserDefaults.standard.set(try JSONEncoder().encode(candidate), forKey: "shortcut")
    }

    func configuration(for provider: TranslationProvider? = nil) -> LLMConfiguration {
        let chosen = provider ?? self.provider
        return UserDefaults.standard.data(forKey: "apiConfiguration.\(chosen.rawValue)")
            .flatMap { try? JSONDecoder().decode(LLMConfiguration.self, from: $0) } ?? LLMConfiguration(provider: chosen)
    }

    func saveProvider(_ configuration: LLMConfiguration, newKey: String) throws {
        if configuration.provider != .apple {
            try configuration.validate()
            guard let account = configuration.credentialAccount else { throw TranslationServiceError.invalidEndpoint }
            let key = newKey.trimmingCharacters(in: .whitespacesAndNewlines)
            guard configuration.isLocal || !key.isEmpty || APIKeyStore.contains(account: account) else { throw TranslationServiceError.missingKey }
            if !key.isEmpty { try APIKeyStore.save(key, account: account) }
            UserDefaults.standard.set(try JSONEncoder().encode(configuration), forKey: "apiConfiguration.\(configuration.provider.rawValue)")
        }
        provider = configuration.provider
        UserDefaults.standard.set(provider.rawValue, forKey: "provider")
    }

    struct SettingsError: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    func requestAccessibility() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        accessibilityGranted = AXIsProcessTrustedWithOptions(options)
        if !accessibilityGranted,
           let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }
}
