import Foundation

public enum SelectionAccessPolicy {
    public static let weChat = "com.tencent.xinWeChat"
    /// These custom composers may expose selected text while their AX write is a
    /// no-op, or hide the text field altogether. Use their native Copy/Paste path.
    public static func prefersClipboard(bundleIdentifier: String?) -> Bool {
        bundleIdentifier == "com.openai.codex" || bundleIdentifier == weChat
    }

    public static func offersSelectionGestureFallback(bundleIdentifier: String?, selectedRange: NSRange?) -> Bool {
        guard let selectedRange, selectedRange.location >= 0, selectedRange.location != NSNotFound,
              selectedRange.length > 0 else { return false }
        return prefersClipboard(bundleIdentifier: bundleIdentifier)
            || bundleIdentifier == "com.google.Chrome" || bundleIdentifier == "com.bytedance.macos.feishu"
    }

    public static func insideWorkArea(_ point: CGPoint, workAreas: [CGRect]) -> Bool {
        workAreas.contains { $0.contains(point) }
    }
}

/// WeChat can expose only a window to AX. Its native Edit menu reports
/// whether the current selection is copyable and whether it can also be cut.
public struct WeChatEditCommands {
    public let copyEnabled: Bool?
    public let cutEnabled: Bool?

    public init(copyEnabled: Bool?, cutEnabled: Bool?) {
        self.copyEnabled = copyEnabled
        self.cutEnabled = cutEnabled
    }

    public var selection: SelectionEditability? {
        guard copyEnabled == true else { return nil }
        switch cutEnabled {
        case true: return .editable
        case false: return .readOnly
        default: return .unknown
        }
    }
}

public enum PointerSelectionTarget: Sendable {
    case excluded, text, unconfirmed

    /// Roles run from the hit element up to its window. An icon or a label can
    /// be a child of a button, so the leaf role alone is insufficient.
    public static func classify(roles: [String]) -> Self {
        let controls: Set<String> = ["AXMenuBar", "AXMenuBarItem", "AXMenu", "AXMenuItem",
            "AXMenuButton", "AXStatusItem", "AXDockItem", "AXButton", "AXPopUpButton",
            "AXCheckBox", "AXRadioButton", "AXSwitch", "AXTab", "AXImage", "AXIcon",
            "AXSlider", "AXScrollBar", "AXDisclosureTriangle"]
        let editors: Set<String> = ["AXTextField", "AXTextArea", "AXComboBox"]
        var text = false
        var editor = false
        for role in roles {
            if controls.contains(role) { return .excluded }
            if editors.contains(role) { editor = true }
            if ["AXStaticText", "AXHeading", "AXParagraph", "AXLink"].contains(role) { text = true }
            if ["AXToolbar", "AXTitleBar"].contains(role) && !editor {
                return .excluded
            }
            if role == "AXWebArea" { break }
        }
        return editor || text ? .text : .unconfirmed
    }
}
