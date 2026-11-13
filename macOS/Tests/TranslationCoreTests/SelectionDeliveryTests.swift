import Testing
@testable import TranslationCore

@Test("Read-only selections copy while editable selections retain replacement")
func automaticDelivery() {
    #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: .readOnly, replacementEnabled: true) == .copy)
    #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: .readOnly, replacementEnabled: false) == .copy)
    #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: .editable, replacementEnabled: true) == .replace)
    #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: .unknown, replacementEnabled: true) == .copy)
    #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: .editable, replacementEnabled: false) == .preview)
}

@Test("A writable selection on read-only web content must never enable replacement")
func webContentEditability() {
    for role in ["AXWebArea", "AXStaticText", "AXLink", "AXHeading", "AXParagraph"] {
        let access = SelectionEditabilityPolicy.classify(role: role, explicitlyEditable: nil, enabled: true,
                                                        selectedTextSettable: true, valueSettable: true)
        #expect(access == .readOnly)
        #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: access, replacementEnabled: true) == .copy)
    }
    #expect(SelectionEditabilityPolicy.classify(role: "AXGroup", explicitlyEditable: nil, enabled: true,
                                               selectedTextSettable: true, valueSettable: true) == .unknown)
}

@Test("Editable inputs replace while read-only and disabled inputs copy")
func textControlEditability() {
    for role in ["AXTextField", "AXTextArea", "AXComboBox"] {
        let editable = SelectionEditabilityPolicy.classify(role: role, explicitlyEditable: nil, enabled: true,
                                                          selectedTextSettable: false, valueSettable: true)
        #expect(editable == .editable)
        #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: editable, replacementEnabled: true) == .replace)
        #expect(SelectionEditabilityPolicy.classify(role: role, explicitlyEditable: false, enabled: true,
                                                   selectedTextSettable: true, valueSettable: true) == .readOnly)
        #expect(SelectionEditabilityPolicy.classify(role: role, explicitlyEditable: true, enabled: false,
                                                   selectedTextSettable: true, valueSettable: true) == .readOnly)
        #expect(SelectionEditabilityPolicy.classify(role: role, explicitlyEditable: nil, enabled: true,
                                                   selectedTextSettable: false, valueSettable: false) == .readOnly)
    }
    #expect(SelectionEditabilityPolicy.classify(role: "AXGroup", explicitlyEditable: true, enabled: true,
                                               selectedTextSettable: nil, valueSettable: nil) == .editable)
}

@Test("Explicit copy cannot write into editable or opaque source applications")
func explicitCopyDelivery() {
    for editability in [SelectionEditability.editable, .readOnly, .unknown] {
        for replacement in [true, false] {
            #expect(SelectionDeliveryPolicy.delivery(intent: .copy, editability: editability, replacementEnabled: replacement) == .copy)
        }
    }
}

@Test("Automatic copy does not replace newer clipboard contents or copy empty output")
func copyCompletionGuard() {
    #expect(SelectionDeliveryPolicy.mayCopy(translation: "你好", expectedClipboardChangeCount: 10, currentClipboardChangeCount: 10))
    #expect(!SelectionDeliveryPolicy.mayCopy(translation: "你好", expectedClipboardChangeCount: 10, currentClipboardChangeCount: 11))
    #expect(!SelectionDeliveryPolicy.mayCopy(translation: " \n ", expectedClipboardChangeCount: 10, currentClipboardChangeCount: 10))
    let permit = ReplacementPermit()
    permit.cancel()
    var didCopy = false
    let result = permit.performIfActive { didCopy = true; return true }
    #expect(result == nil)
    #expect(!didCopy)
}

@Test("Opaque WeChat drafts, messages and empty clicks use native Copy and Cut availability")
func weChatOpaqueSelection() {
    let draft = WeChatEditCommands(copyEnabled: true, cutEnabled: true)
    let message = WeChatEditCommands(copyEnabled: true, cutEnabled: false)
    #expect(draft.selection == .editable)
    #expect(message.selection == .readOnly)
    #expect(WeChatEditCommands(copyEnabled: true, cutEnabled: nil).selection == .unknown)
    for copy in [false, nil] as [Bool?] {
        for cut in [true, false, nil] as [Bool?] {
            #expect(WeChatEditCommands(copyEnabled: copy, cutEnabled: cut).selection == nil)
        }
    }
}

@Test("A WeChat selection that loses Cut while translation is running no longer permits replacement")
func weChatRechecksEditability() {
    let before = WeChatEditCommands(copyEnabled: true, cutEnabled: true)
    let after = WeChatEditCommands(copyEnabled: true, cutEnabled: false)
    #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: before.selection ?? .unknown,
                                             replacementEnabled: true) == .replace)
    #expect(SelectionDeliveryPolicy.delivery(intent: .automatic, editability: after.selection ?? .unknown,
                                             replacementEnabled: true) == .copy)
}
