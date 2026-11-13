import Foundation

public enum SelectionIntent: String, CaseIterable, Sendable { case automatic, copy }
public enum SelectionEditability: Sendable { case editable, readOnly, unknown }
public enum SelectionDelivery: Sendable { case replace, copy, preview }

public enum SelectionDeliveryPolicy {
    public static func delivery(intent: SelectionIntent, editability: SelectionEditability,
                                replacementEnabled: Bool) -> SelectionDelivery {
        // An opaque control is not permission to paste into the foreground app.
        if intent == .copy || editability != .editable { return .copy }
        return replacementEnabled ? .replace : .preview
    }

    public static func mayCopy(translation: String, expectedClipboardChangeCount: Int,
                               currentClipboardChangeCount: Int) -> Bool {
        !translation.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
            expectedClipboardChangeCount == currentClipboardChangeCount
    }
}
