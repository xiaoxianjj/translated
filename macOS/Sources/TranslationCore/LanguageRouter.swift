import Foundation
import NaturalLanguage

public enum ChineseVariant: String, CaseIterable, Sendable {
    case simplified = "zh-Hans"
    case traditional = "zh-Hant"

    public var title: String {
        self == .simplified ? "简体中文" : "繁體中文"
    }
}

public struct TranslationRoute: Equatable, Sendable {
    public let sourceCode: String?
    public let targetCode: String
    /// Present for automatic routing. Explicit source-language overrides leave this nil.
    public let automaticChineseTarget: ChineseVariant?

    public init(sourceCode: String?, targetCode: String, automaticChineseTarget: ChineseVariant? = nil) {
        self.sourceCode = sourceCode
        self.targetCode = targetCode
        self.automaticChineseTarget = automaticChineseTarget
    }

    public var sourceName: String {
        guard let sourceCode else { return "Auto-detect" }
        return Locale(identifier: "en").localizedString(forIdentifier: sourceCode) ?? sourceCode
    }

    public var targetName: String {
        targetCode.hasPrefix("zh") ? "Chinese" : "English"
    }

    public var targetNativeName: String {
        targetCode == "zh-Hans" ? "简体中文" : targetCode == "zh-Hant" ? "繁體中文" : "English"
    }
}

public enum SelectionValidationError: Error, LocalizedError, Equatable {
    case empty
    case noWords
    case tooLong

    public var errorDescription: String? {
        switch self {
        case .empty: "Select or paste some text first."
        case .noWords: "Choose text that contains words."
        case .tooLong: "Choose a shorter passage (up to 50,000 characters)."
        }
    }
}

public enum LanguageRouter {
    public static let maximumLength = 50_000

    public static func validatedText(_ raw: String) throws -> String {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { throw SelectionValidationError.empty }
        guard text.count <= maximumLength else { throw SelectionValidationError.tooLong }
        guard text.unicodeScalars.contains(where: CharacterSet.letters.contains) else {
            throw SelectionValidationError.noWords
        }
        return text
    }

    public static func route(for text: String, chinese: ChineseVariant = .simplified) -> TranslationRoute {
        let recognizer = NLLanguageRecognizer()
        recognizer.processString(text)
        let hypotheses = recognizer.languageHypotheses(withMaximum: 10)
        var source = recognizer.dominantLanguage?.rawValue

        // Single words share spellings across languages. The top result alone labels
        // English "words" as Dutch and "application" as French. For short Latin
        // selections, prefer English when it is a plausible alternative and no other
        // language has strong evidence. Preserve confident foreign-language results.
        let letters = text.unicodeScalars.filter(CharacterSet.letters.contains)
        let isASCIILatin = !letters.isEmpty && letters.allSatisfy { $0.isASCII }
        let wordCount = text.split(whereSeparator: { $0.isWhitespace }).count
        if isASCIILatin && letters.count <= 80 && wordCount <= 6 {
            let strongest = hypotheses.values.max() ?? 0
            let english = hypotheses[.english] ?? 0
            if source == nil || (strongest < 0.60 && english >= 0.06 && english >= strongest * 0.125) {
                source = "en"
            }
        }
        return TranslationRoute(sourceCode: source, targetCode: source == "en" ? chinese.rawValue : "en",
                                automaticChineseTarget: chinese)
    }
}
