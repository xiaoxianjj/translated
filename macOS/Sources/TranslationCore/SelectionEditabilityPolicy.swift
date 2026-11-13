import Foundation

/// A selection setter is not evidence that a web page can accept replacement.
/// Resolve the selected control before deciding whether any write is allowed.
public enum SelectionEditabilityPolicy {
    public static func classify(role: String?, explicitlyEditable: Bool?, enabled: Bool?,
                                selectedTextSettable: Bool?, valueSettable: Bool?) -> SelectionEditability {
        if enabled == false || explicitlyEditable == false { return .readOnly }
        if explicitlyEditable == true { return .editable }
        if ["AXStaticText", "AXLink", "AXWebArea", "AXHeading", "AXParagraph"].contains(role ?? "") {
            return .readOnly
        }
        if ["AXTextField", "AXTextArea", "AXComboBox"].contains(role ?? "") {
            if valueSettable == true || selectedTextSettable == true { return .editable }
            if valueSettable == false { return .readOnly }
        }
        return .unknown
    }
}
