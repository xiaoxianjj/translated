import Foundation
import NaturalLanguage

public struct TranslationChunk: Sendable, Equatable {
    public let text: String
    public let prefix: String
    public let suffix: String
    public let contextBefore: String
    public let contextAfter: String
}

public struct TranslationPlan: Sendable {
    public let chunks: [TranslationChunk]

    public init(text: String, limit: Int) {
        let limit = max(100, limit)
        var ranges: [Range<String.Index>] = []
        var start = text.startIndex
        let tokenizer = NLTokenizer(unit: .sentence)
        tokenizer.string = text
        let sentences = tokenizer.tokens(for: text.startIndex..<text.endIndex)
        while start < text.endIndex {
            let hardEnd = text.index(start, offsetBy: limit, limitedBy: text.endIndex) ?? text.endIndex
            var end = hardEnd
            if hardEnd < text.endIndex {
                // Prefer a paragraph boundary, then a whole sentence. Only exceptionally
                // long sentences fall back to a word/grapheme boundary.
                let window = text[start..<hardEnd]
                let threshold = text.index(start, offsetBy: max(1, window.count / 3))
                if let newline = window.lastIndex(where: { $0.isNewline }), newline >= threshold {
                    end = text.index(after: newline)
                } else if let sentence = sentences.last(where: { $0.upperBound <= hardEnd && $0.upperBound > threshold }) {
                    end = sentence.upperBound
                } else if let space = window.lastIndex(where: { $0.isWhitespace }), space > start {
                    end = text.index(after: space)
                }
            }
            ranges.append(start..<end)
            start = end
        }
        chunks = ranges.map { range in
            let raw = String(text[range])
            let prefix = String(raw.prefix(while: \.isWhitespace))
            let suffix = String(raw.reversed().prefix(while: \.isWhitespace).reversed())
            return TranslationChunk(text: raw.trimmingCharacters(in: .whitespacesAndNewlines),
                                    prefix: prefix, suffix: suffix,
                                    contextBefore: String(text[..<range.lowerBound].suffix(600)),
                                    contextAfter: String(text[range.upperBound...].prefix(600)))
        }
    }

    public func assemble(_ translations: [String], targetCode: String) throws -> String {
        guard translations.count == chunks.count else { throw TranslationServiceError.incomplete }
        var output = ""
        for (index, chunk) in chunks.enumerated() {
            let translated = translations[index].trimmingCharacters(in: .whitespacesAndNewlines)
            guard chunk.text.isEmpty || !translated.isEmpty else { throw TranslationServiceError.emptyResponse }
            if index > 0 && targetCode == "en" && chunk.prefix.isEmpty && chunks[index - 1].suffix.isEmpty &&
                output.last?.isWhitespace != true { output += " " }
            if chunk.text.isEmpty {
                output += chunk.prefix
            } else {
                output += chunk.prefix + translated + chunk.suffix
            }
        }
        return output
    }
}
