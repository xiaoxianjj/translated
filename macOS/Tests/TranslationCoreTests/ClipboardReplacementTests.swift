import Testing
import Foundation
@testable import TranslationCore

@Test("Clipboard replacement requires the same selection, app and interaction generation")
func clipboardSelectionGuard() {
    func allows(_ text: String = "words", process: Int32 = 42, generation: UInt64 = 9) -> Bool {
        ClipboardReplacementPolicy.mayReplace(originalText: "words", currentText: text,
                                              originalProcess: 42, currentProcess: process,
                                              originalGeneration: 9, currentGeneration: generation)
    }
    #expect(allows())
    #expect(!allows(""))
    #expect(!allows("other words"))
    #expect(!allows(process: 43))
    // Even selecting identical words elsewhere must cancel the pending replacement.
    #expect(!allows(generation: 10))
}

@Test("Clipboard restoration never overwrites a newer clipboard owner")
func clipboardOwnership() {
    #expect(ClipboardReplacementPolicy.mayRestore(ownedChangeCount: 5, currentChangeCount: 5))
    #expect(!ClipboardReplacementPolicy.mayRestore(ownedChangeCount: 5, currentChangeCount: 6))
    #expect(!ClipboardReplacementPolicy.mayRestore(ownedChangeCount: nil, currentChangeCount: 5))
}

@Test("Custom Codex and WeChat composers use clipboard compatibility directly")
func customComposerAccess() {
    #expect(SelectionAccessPolicy.prefersClipboard(bundleIdentifier: "com.openai.codex"))
    #expect(SelectionAccessPolicy.prefersClipboard(bundleIdentifier: "com.tencent.xinWeChat"))
    #expect(!SelectionAccessPolicy.prefersClipboard(bundleIdentifier: "com.apple.TextEdit"))
    #expect(!SelectionAccessPolicy.prefersClipboard(bundleIdentifier: nil))
}

@Test("Chrome supports selection-gesture fallback without forcing its editor clipboard path")
func browserGestureFallback() {
    let selection = NSRange(location: 2, length: 5)
    #expect(SelectionAccessPolicy.offersSelectionGestureFallback(bundleIdentifier: "com.google.Chrome", selectedRange: selection))
    #expect(SelectionAccessPolicy.offersSelectionGestureFallback(bundleIdentifier: "com.bytedance.macos.feishu", selectedRange: selection))
    #expect(!SelectionAccessPolicy.prefersClipboard(bundleIdentifier: "com.google.Chrome"))
    #expect(SelectionAccessPolicy.offersSelectionGestureFallback(bundleIdentifier: "com.openai.codex", selectedRange: selection))
    #expect(SelectionAccessPolicy.offersSelectionGestureFallback(bundleIdentifier: "com.tencent.xinWeChat", selectedRange: selection))
    #expect(!SelectionAccessPolicy.offersSelectionGestureFallback(bundleIdentifier: nil, selectedRange: selection))
}

@Test("A double-click in a supported editor cannot offer Translate for an empty or missing selection")
func emptySelectionGesture() {
    for app in ["com.openai.codex", "com.tencent.xinWeChat", "com.google.Chrome", "com.bytedance.macos.feishu"] {
        for range in [nil, NSRange(location: 0, length: 0), NSRange(location: 9, length: 0),
                      NSRange(location: NSNotFound, length: 1)] as [NSRange?] {
            #expect(!SelectionAccessPolicy.offersSelectionGestureFallback(bundleIdentifier: app, selectedRange: range))
        }
    }
}

@Test("Menu bars and the Dock are outside the usable area on either display")
func displayWorkAreas() {
    let workAreas = [CGRect(x: 0, y: 65, width: 1440, height: 810),
                     CGRect(x: -1280, y: 20, width: 1280, height: 750)]
    func accepts(_ x: CGFloat, _ y: CGFloat) -> Bool {
        SelectionAccessPolicy.insideWorkArea(CGPoint(x: x, y: y), workAreas: workAreas)
    }
    #expect(!accepts(300, 890))
    #expect(!accepts(-300, 780))
    #expect(!accepts(100, 5))
    #expect(accepts(-300, 500))
    #expect(accepts(100, 200))
}

@Test("Control labels and icons do not qualify as text, including inside a web page")
func pointerRoles() {
    #expect(PointerSelectionTarget.classify(roles: ["AXStaticText", "AXMenuBarItem", "AXMenuBar"]) == .excluded)
    #expect(PointerSelectionTarget.classify(roles: ["AXStaticText", "AXButton", "AXWebArea"]) == .excluded)
    #expect(PointerSelectionTarget.classify(roles: ["AXImage", "AXLink", "AXWebArea"]) == .excluded)
    #expect(PointerSelectionTarget.classify(roles: ["AXStaticText", "AXToolbar"]) == .excluded)
    #expect(PointerSelectionTarget.classify(roles: ["AXGroup", "AXWebArea"]) == .unconfirmed)
    #expect(PointerSelectionTarget.classify(roles: ["AXStaticText", "AXParagraph", "AXWebArea"]) == .text)
    #expect(PointerSelectionTarget.classify(roles: ["AXTextField", "AXToolbar"]) == .text)
}
