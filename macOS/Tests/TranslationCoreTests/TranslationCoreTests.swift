import Foundation
import CoreGraphics
import Testing
@testable import TranslationCore

@Test("English selections go to Chinese", arguments: ["Hello, world!", "Please send me the meeting notes.", "selected words", "translation"])
func englishRouting(_ text: String) {
    let route = LanguageRouter.route(for: text)
    #expect(route.sourceCode == "en")
    #expect(route.targetCode == "zh-Hans")
}

@Test("Ambiguous short English selections still go to Chinese", arguments: [
    "words", "word", "application", "cancel", "record", "email", "open", "test", "update",
    "English", "button", "settings", "  words!\n"
])
func shortEnglishRouting(_ text: String) {
    let route = LanguageRouter.route(for: text)
    #expect(route.sourceCode == "en")
    #expect(route.targetCode == "zh-Hans")
    #expect(route.automaticChineseTarget == .simplified)
    #expect(LanguageRouter.route(for: text, chinese: .traditional).targetCode == "zh-Hant")
}

@Test("Short foreign words retain the English target", arguments: [
    "bonjour", "merci", "hola", "gracias", "ciao", "hallo", "schnell", "amigo",
    "hola mundo", "merci beaucoup", "guten morgen", "こんにちは", "안녕하세요", "你好"
])
func shortForeignRouting(_ text: String) {
    let route = LanguageRouter.route(for: text)
    #expect(route.sourceCode != "en")
    #expect(route.targetCode == "en")
}

@Test("Chinese selections go to English", arguments: ["今天天气真好。", "请帮我翻译这段文字。", "請幫我翻譯這段文字。", "你好"])
func chineseRouting(_ text: String) {
    let route = LanguageRouter.route(for: text)
    #expect(route.sourceCode?.hasPrefix("zh") == true)
    #expect(route.targetCode == "en")
}

@Test("Other detected languages go to English", arguments: ["Bonjour, comment allez-vous aujourd’hui ?", "今日はとてもいい天気ですね。", "¿Dónde está la estación de tren?"])
func otherLanguages(_ text: String) {
    let route = LanguageRouter.route(for: text)
    #expect(route.sourceCode != "en")
    #expect(route.targetCode == "en")
}

@Test("Chinese variant follows the preference")
func traditionalChinese() {
    #expect(LanguageRouter.route(for: "Hello, world!", chinese: .traditional).targetCode == "zh-Hant")
}

@Test("Reject empty, nonlinguistic, and oversized selections without truncation")
func selectionValidation() throws {
    #expect(throws: SelectionValidationError.empty) { try LanguageRouter.validatedText(" \n\t ") }
    #expect(throws: SelectionValidationError.noWords) { try LanguageRouter.validatedText("1234 🎉 —") }
    #expect(throws: SelectionValidationError.tooLong) { try LanguageRouter.validatedText(String(repeating: "a", count: LanguageRouter.maximumLength + 1)) }
    #expect(try LanguageRouter.validatedText("  你好，世界！\n") == "你好，世界！")
    #expect(try LanguageRouter.validatedText(String(repeating: "a", count: 50_000)).count == 50_000)
}

@Test("Popup stays inside each display, including displays with negative coordinates")
func placement() {
    let screens = [CGRect(x: 0, y: 30, width: 1440, height: 850), CGRect(x: -1920, y: -100, width: 1920, height: 1080)]
    for screen in screens {
        for anchor in [CGPoint(x: screen.minX, y: screen.minY), CGPoint(x: screen.maxX, y: screen.maxY), CGPoint(x: screen.midX, y: screen.midY)] {
            let frame = PopupPlacement.frame(size: CGSize(width: 440, height: 360), anchor: anchor, screen: screen)
            #expect(screen.contains(frame))
        }
    }
}

@Test("Popup flips above a selection near the screen bottom")
func flipsAbove() {
    let anchor = CGPoint(x: 300, y: 50)
    let frame = PopupPlacement.frame(size: CGSize(width: 184, height: 46), anchor: anchor, screen: CGRect(x: 0, y: 0, width: 1000, height: 700))
    #expect(frame.minY > anchor.y)
}

@Test("Translation status is centered above the Dock on the selection's display", arguments: [
    CGRect(x: 0, y: 72, width: 1440, height: 790),
    CGRect(x: -1850, y: -220, width: 1850, height: 1056),
    CGRect(x: 80, y: 900, width: 1000, height: 760)
])
func statusPlacement(_ visibleFrame: CGRect) {
    let size = CGSize(width: 184, height: 48)
    let frame = PopupPlacement.statusFrame(size: size, visibleFrame: visibleFrame)
    #expect(frame.midX == visibleFrame.midX)
    #expect(frame.minY == visibleFrame.minY + 12)
    #expect(frame.size == size)
    #expect(visibleFrame.contains(frame))
}

@Test("Replacement is rejected after focus, selection text, or range changes")
func staleSelectionProtection() {
    let range = NSRange(location: 6, length: 5)
    #expect(ReplacementPolicy.mayReplace(originalText: "hello", currentText: "hello", originalRange: range,
                                         currentRange: range, sameApplication: true, sameElement: true))
    #expect(!ReplacementPolicy.mayReplace(originalText: "hello", currentText: "world", originalRange: range,
                                          currentRange: range, sameApplication: true, sameElement: true))
    #expect(!ReplacementPolicy.mayReplace(originalText: "hello", currentText: "hello", originalRange: range,
                                          currentRange: NSRange(location: 20, length: 5), sameApplication: true, sameElement: true))
    #expect(!ReplacementPolicy.mayReplace(originalText: "hello", currentText: "hello", originalRange: range,
                                          currentRange: range, sameApplication: false, sameElement: true))
    #expect(!ReplacementPolicy.mayReplace(originalText: "hello", currentText: "hello", originalRange: range,
                                          currentRange: range, sameApplication: true, sameElement: false))
    #expect(!ReplacementPolicy.mayReplace(originalText: "hello", currentText: "hello", originalRange: nil,
                                          currentRange: nil, sameApplication: true, sameElement: true))
}

@Test("Dismissed requests cannot perform a delayed replacement")
func cancelledReplacement() {
    let permit = ReplacementPermit()
    permit.cancel()
    var didWrite = false
    let result = permit.performIfActive { didWrite = true; return "written" }
    #expect(result == nil)
    #expect(!didWrite)
}

@Test("Replacing words preserves selected surrounding whitespace")
func replacementWhitespace() {
    #expect(ReplacementPolicy.preservingWhitespace(original: "  hello\n", translation: "你好") == "  你好\n")
    #expect(ReplacementPolicy.preservingWhitespace(original: "你好", translation: "Hello") == "Hello")
}
