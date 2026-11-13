import AppKit
import ApplicationServices
import Carbon
import OSLog
import TranslationCore

let weChatSelectionLog = Logger(subsystem: "app.translated.macos", category: "WeChatSelection")

struct CapturedSelection: Sendable {
    let text: String
    let anchor: CGPoint
    let processID: pid_t
    let rangeLocation: Int
    let replacement: SelectionHandle

    var signature: String { "\(processID):\(rangeLocation):\(text)" }
}

/// An immutable CF accessibility reference, used only for synchronous AX IPC off the UI thread.
final class SelectionHandle: @unchecked Sendable {
    let element: AXUIElement?
    let rawText: String
    let range: NSRange?
    let interactionGeneration: UInt64
    let editability: SelectionEditability

    init(element: AXUIElement?, rawText: String, range: NSRange?, interactionGeneration: UInt64 = 0,
         editability: SelectionEditability = .unknown) {
        self.element = element
        self.rawText = rawText
        self.range = range
        self.interactionGeneration = interactionGeneration
        self.editability = editability
    }
}

enum ReplacementResult: Sendable {
    case replaced
    case readOnly
    case selectionChanged
    case failed

    var label: String {
        switch self {
        case .replaced: "Selection replaced"
        case .readOnly: "Read-only text · Copy to use"
        case .selectionChanged: "Selection changed · Copy to use"
        case .failed: "Couldn’t replace · Copy to use"
        }
    }
}

enum SelectionReader {
    enum Probe: Sendable {
        case selected(CapturedSelection), candidate(SelectionEditability), weChatWindow(SelectionEditability?), none
    }

    static func probe(processID: pid_t, pointerStart: CGPoint?, mouse: CGPoint, primaryHeight: CGFloat,
                      interaction: UInt64, fallbackBundleID: String?) -> Probe {
        guard AXIsProcessTrusted(), !IsSecureEventInputEnabled() else { return .none }
        let app = AXUIElementCreateApplication(processID)
        AXUIElementSetMessagingTimeout(app, 0.2)
        let focused = elementAttribute(app, kAXFocusedUIElementAttribute)
        if let focused, isSecure(focused) { return .none }

        let gesture = pointerStart.map { SelectionGesture(start: $0, end: mouse) }
        var candidates: [AXUIElement] = []
        var textOwners: [AXUIElement] = []
        func add(_ chain: [AXUIElement]) {
            for item in chain where !candidates.contains(where: { CFEqual($0, item) }) {
                candidates.append(item)
            }
        }

        if let gesture {
            let points = gesture.start == gesture.end ? [gesture.start] : [gesture.start, gesture.end]
            var hitSource = false
            for (index, point) in points.enumerated() {
                guard let hit = hitElement(processID: processID, point: point, primaryHeight: primaryHeight) else { continue }
                let chain = selectionAncestors(hit)
                guard !chain.isEmpty else { return .none }
                let roles = chain.map { attribute($0, kAXRoleAttribute) as? String ?? "" }
                let target = PointerSelectionTarget.classify(roles: roles)
                if target == .excluded {
                    if index == 0 { return .none }
                    continue
                }
                hitSource = true
                if target == .text { textOwners.append(hit) }
                for (item, role) in zip(chain, roles) where [kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole,
                    kAXStaticTextRole, "AXHeading", "AXParagraph", "AXLink"].contains(role) {
                    textOwners.append(item)
                }
                // Custom views can own and expose a live selection directly,
                // even though their role is only AXGroup.
                if gesture.start != gesture.end, let focused, CFEqual(hit, focused) { textOwners.append(hit) }
                add(chain)
            }
            if !hitSource {
                guard fallbackBundleID == SelectionAccessPolicy.weChat,
                      points.allSatisfy({ inFrontWindow(processID: processID, point: $0, primaryHeight: primaryHeight) })
                else { return .none }
            }
        }
        // The focused editor or web document may own the selection while the
        // hit element is just a span. Verify its association with the gesture.
        if let focused { add(selectionAncestors(focused)) }
        if gesture == nil, let hit = hitElement(processID: processID, point: mouse, primaryHeight: primaryHeight) {
            add(selectionAncestors(hit))
        }

        var fallback: SelectionEditability?
        for element in candidates {
            if Task.isCancelled { return .none }
            let nativeRef = attribute(element, kAXSelectedTextRangeAttribute)
            let range = nativeRef.flatMap(validRange)
            let marker = attribute(element, "AXSelectedTextMarkerRange")
            let raw = selectedText(of: element, nativeRange: nativeRef, markerRange: marker)
            let text = raw.flatMap { try? LanguageRouter.validatedText($0) }
            let canCapture = raw == nil && fallback == nil &&
                SelectionAccessPolicy.offersSelectionGestureFallback(bundleIdentifier: fallbackBundleID, selectedRange: range)
            guard text != nil || canCapture else { continue }
            var bounds: [CGRect] = []
            if let nativeRef, range != nil,
               let box = rangeBounds(element, kAXBoundsForRangeParameterizedAttribute, nativeRef, primaryHeight) {
                bounds.append(box)
            }
            if let marker, let box = rangeBounds(element, "AXBoundsForTextMarkerRange", marker, primaryHeight) {
                bounds.append(box)
            }
            if let gesture {
                let owned = textOwners.contains { CFEqual($0, element) } ||
                    (bounds.isEmpty && markerBelongsToHit(marker, element: element, textOwners: textOwners))
                guard gesture.matches(bounds: bounds, sameTextOwner: owned) else { continue }
            }
            var access = editabilityInContext(element)
            if access == .unknown, fallbackBundleID == SelectionAccessPolicy.weChat,
               let fromMenu = weChatCommands(app)?.selection {
                access = fromMenu
            }
            if let text, let raw {
                let anchor = gesture?.end ?? bounds.first?.origin ?? mouse
                let handle = SelectionHandle(element: element, rawText: raw, range: range,
                                             interactionGeneration: interaction, editability: access)
                return .selected(CapturedSelection(text: text, anchor: anchor, processID: processID,
                                                   rangeLocation: range?.location ?? 0, replacement: handle))
            }
            fallback = access
        }
        // WeChat's Qt window can have no AX text children at all. A selection
        // gesture inside the source app plus an enabled Copy command lets us
        // offer the button without issuing a background Copy.
        if fallback == nil, fallbackBundleID == SelectionAccessPolicy.weChat, !Task.isCancelled {
            return .weChatWindow(weChatCommands(app)?.selection)
        }
        return fallback.map(Probe.candidate) ?? .none
    }

    private static func selectionAncestors(_ leaf: AXUIElement) -> [AXUIElement] {
        var chain: [AXUIElement] = []
        var item = leaf
        for _ in 0..<32 {
            if isSecure(item) { return [] }
            chain.append(item)
            let role = attribute(item, kAXRoleAttribute) as? String
            if ["AXWebArea", kAXWindowRole, kAXApplicationRole].contains(role ?? "") { break }
            guard let parent = elementAttribute(item, kAXParentAttribute), !CFEqual(item, parent) else { break }
            item = parent
        }
        return chain
    }

    private static func rangeBounds(_ element: AXUIElement, _ name: String, _ range: CFTypeRef,
                                    _ primaryHeight: CGFloat) -> CGRect? {
        var result: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element, name as CFString, range, &result) == .success,
              let result, CFGetTypeID(result) == AXValueGetTypeID() else { return nil }
        var rect = CGRect.zero
        guard AXValueGetValue(result as! AXValue, .cgRect, &rect), !rect.isEmpty,
              rect.minX.isFinite, rect.maxY.isFinite else { return nil }
        return CGRect(x: rect.minX, y: primaryHeight - rect.maxY, width: rect.width, height: rect.height)
    }

    private static func markerBelongsToHit(_ marker: CFTypeRef?, element: AXUIElement, textOwners: [AXUIElement]) -> Bool {
        guard !textOwners.isEmpty, let marker, CFGetTypeID(marker) == AXTextMarkerRangeGetTypeID() else { return false }
        let range = marker as! AXTextMarkerRange
        for endpoint in [AXTextMarkerRangeCopyStartMarker(range), AXTextMarkerRangeCopyEndMarker(range)] {
            var value: CFTypeRef?
            if AXUIElementCopyParameterizedAttributeValue(element, "AXUIElementForTextMarker" as CFString, endpoint, &value) == .success,
               let value, CFGetTypeID(value) == AXUIElementGetTypeID(), textOwners.contains(where: { CFEqual($0, value) }) {
                return true
            }
        }
        return false
    }

    private static func hitElement(processID: pid_t, point: CGPoint, primaryHeight: CGFloat) -> AXUIElement? {
        let system = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(system, 0.2)
        var hit: AXUIElement?
        let x = Float(point.x), y = Float(primaryHeight - point.y)
        if AXUIElementCopyElementAtPosition(system, x, y, &hit) != .success || hit == nil {
            let app = AXUIElementCreateApplication(processID)
            AXUIElementSetMessagingTimeout(app, 0.2)
            guard AXUIElementCopyElementAtPosition(app, x, y, &hit) == .success else { return nil }
        }
        guard let hit else { return nil }
        var owner: pid_t = 0
        guard AXUIElementGetPid(hit, &owner) == .success, owner == processID else { return nil }
        return hit
    }

    /// An opaque Qt window can fail AX hit-testing. Check the top visible
    /// window at the gesture without reading screen pixels or window titles.
    private static func inFrontWindow(processID: pid_t, point: CGPoint, primaryHeight: CGFloat) -> Bool {
        let location = CGPoint(x: point.x, y: primaryHeight - point.y)
        guard let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return false }
        for window in windows {
            guard let bounds = window[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: bounds), rect.contains(location),
                  (window[kCGWindowAlpha as String] as? Double ?? 1) > 0 else { continue }
            return window[kCGWindowOwnerPID as String] as? Int32 == processID &&
                window[kCGWindowLayer as String] as? Int == 0 && location.y >= rect.minY + 28
        }
        return false
    }

    static func prepareAccess(processID: pid_t, bundleIdentifier: String?) {
        guard AXIsProcessTrusted() else { return }
        let attributeName: String
        switch bundleIdentifier {
        case "com.openai.codex": attributeName = "AXManualAccessibility"
        case "com.google.Chrome", "com.bytedance.macos.feishu": attributeName = "AXEnhancedUserInterface"
        default: return
        }
        let app = AXUIElementCreateApplication(processID)
        AXUIElementSetMessagingTimeout(app, 0.2)
        _ = attribute(app, kAXRoleAttribute)
        if attribute(app, attributeName) as? Bool != true {
            AXUIElementSetAttributeValue(app, attributeName as CFString, kCFBooleanTrue)
        }
    }

    static func read(processID: pid_t, mouse: CGPoint, primaryHeight: CGFloat,
                     interactionGeneration: UInt64 = 0) -> CapturedSelection? {
        let result = probe(processID: processID, pointerStart: nil, mouse: mouse, primaryHeight: primaryHeight,
                           interaction: interactionGeneration, fallbackBundleID: nil)
        if case .selected(let selection) = result { return selection }
        return nil
    }

    static func replace(_ selection: CapturedSelection, with translation: String, permit: ReplacementPermit) -> ReplacementResult {
        guard AXIsProcessTrusted() else { return .failed }
        let handle = selection.replacement
        guard handle.editability == .editable, let element = handle.element, !isSecure(element),
              editabilityInContext(element) == .editable else { return .readOnly }
        var settable = DarwinBoolean(false)
        guard AXUIElementIsAttributeSettable(element, kAXSelectedTextAttribute as CFString, &settable) == .success,
              settable.boolValue else { return .failed }

        let system = AXUIElementCreateSystemWide()
        guard let focusedApp = elementAttribute(system, kAXFocusedApplicationAttribute) else { return .selectionChanged }
        var activePID: pid_t = 0
        AXUIElementGetPid(focusedApp, &activePID)
        guard activePID == selection.processID,
              let focused = elementAttribute(focusedApp, kAXFocusedUIElementAttribute), !isSecure(focused) else {
            return .selectionChanged
        }
        var sameElement = CFEqual(focused, element)
        var parent = focused
        for _ in 0..<4 where !sameElement {
            guard let next = elementAttribute(parent, kAXParentAttribute) else { break }
            sameElement = CFEqual(next, element)
            parent = next
        }
        guard let currentText = attribute(element, kAXSelectedTextAttribute) as? String else { return .selectionChanged }
        var currentRange: NSRange?
        if let value = attribute(element, kAXSelectedTextRangeAttribute), CFGetTypeID(value) == AXValueGetTypeID() {
            var range = CFRange()
            if AXValueGetValue(value as! AXValue, .cfRange, &range) {
                currentRange = NSRange(location: range.location, length: range.length)
            }
        }
        // Missing ranges are an unsupported AX representation, not proof that the
        // user changed selection. Let the guarded clipboard path verify it instead.
        guard handle.range != nil, currentRange != nil else { return .failed }
        guard ReplacementPolicy.mayReplace(originalText: handle.rawText, currentText: currentText,
                                           originalRange: handle.range, currentRange: currentRange,
                                           sameApplication: activePID == selection.processID, sameElement: sameElement) else {
            return .selectionChanged
        }
        let replacement = ReplacementPolicy.preservingWhitespace(original: handle.rawText, translation: translation)
        // Set only the original selection; never rewrite a document or synthesize paste events.
        return permit.performIfActive {
            guard AXUIElementSetAttributeValue(element, kAXSelectedTextAttribute as CFString, replacement as CFString) == .success else { return .failed }
            // Some web editors acknowledge the AX setter without changing text.
            // A still-identical selection is a definite no-op, so allow native paste.
            if replacement != handle.rawText,
               let after = attribute(element, kAXSelectedTextAttribute) as? String, after == handle.rawText {
                return .failed
            }
            return ReplacementResult.replaced
        } ?? .selectionChanged
    }

    static func permitsCopy(processID: pid_t) -> Bool {
        let app = AXUIElementCreateApplication(processID)
        AXUIElementSetMessagingTimeout(app, 0.2)
        guard let focused = elementAttribute(app, kAXFocusedUIElementAttribute) else { return true }
        return !isSecure(focused)
    }

    static func focusedEditability(processID: pid_t) -> SelectionEditability {
        let app = AXUIElementCreateApplication(processID)
        AXUIElementSetMessagingTimeout(app, 0.2)
        guard let focused = elementAttribute(app, kAXFocusedUIElementAttribute) else { return .unknown }
        return editabilityInContext(focused)
    }

    /// Resolve the selection verified by Command-C. In an opaque WeChat
    /// window the focused element can be unrelated to the selected message.
    static func copiedEditability(processID: pid_t, bundleID: String?, text: String, anchor: CGPoint,
                                  primaryHeight: CGFloat, generation: UInt64) -> SelectionEditability {
        let captured = read(processID: processID, mouse: anchor, primaryHeight: primaryHeight,
                            interactionGeneration: generation)
        if let captured, captured.text == text,
           bundleID != SelectionAccessPolicy.weChat || captured.replacement.editability != .unknown {
            return captured.replacement.editability
        }
        if bundleID == SelectionAccessPolicy.weChat {
            let app = AXUIElementCreateApplication(processID)
            AXUIElementSetMessagingTimeout(app, 0.2)
            return weChatCommands(app, refresh: true)?.selection ?? .unknown
        }
        return focusedEditability(processID: processID)
    }

    private static func weChatCommands(_ app: AXUIElement, refresh: Bool = false) -> WeChatEditCommands? {
        guard let bar = elementAttribute(app, kAXMenuBarAttribute),
              let headings = attribute(bar, kAXChildrenAttribute) as? [AXUIElement] else { return nil }
        for heading in headings {
            if Task.isCancelled { return nil }
            let title = attribute(heading, kAXTitleAttribute) as? String ?? ""
            let knownEditMenu = ["Edit", "编辑", "編輯"].contains(title)
            func submenu() -> AXUIElement? {
                (attribute(heading, kAXChildrenAttribute) as? [AXUIElement])?.first
            }
            let initial = submenu().flatMap(editMenuState)
            guard knownEditMenu || initial != nil else { continue }
            if !refresh { return initial }
            // WeChat validates these commands when Edit opens. Refresh only
            // for an invoked translation. AXCancel dismisses our menu without
            // invoking any item (in particular, Cut is never executed).
            if attribute(heading, kAXSelectedAttribute) as? Bool == true {
                return initial
            }
            var pid: pid_t = 0
            guard AXUIElementGetPid(app, &pid) == .success,
                  NSWorkspace.shared.frontmostApplication?.processIdentifier == pid,
                  !Task.isCancelled else { return nil }
            let result = AXUIElementPerformAction(heading, kAXPressAction as CFString)
            weChatSelectionLog.debug("Refresh Edit menu: AX result \(result.rawValue)")
            guard result == .success || result == .cannotComplete else { return nil }
            defer {
                if let menu = submenu() {
                    if AXUIElementPerformAction(menu, kAXCancelAction as CFString) != .success {
                        AXUIElementPerformAction(heading, kAXCancelAction as CFString)
                    }
                } else {
                    AXUIElementPerformAction(heading, kAXCancelAction as CFString)
                }
            }
            return submenu().flatMap(editMenuState)
        }
        return nil
    }

    private static func editMenuState(_ menu: AXUIElement) -> WeChatEditCommands? {
        guard let items = attribute(menu, kAXChildrenAttribute) as? [AXUIElement] else { return nil }
        var copy: AXUIElement?
        var cut: AXUIElement?
        for item in items {
            switch attribute(item, kAXIdentifierAttribute) as? String {
            case "copy:": copy = item
            case "cut:": cut = item
            default:
                let title = attribute(item, kAXTitleAttribute) as? String ?? ""
                if ["Copy", "拷贝", "拷貝", "复制", "複製"].contains(title) { copy = item }
                if ["Cut", "剪切", "剪下"].contains(title) { cut = item }
                if attribute(item, kAXMenuItemCmdModifiersAttribute) as? Int == 0 {
                    switch (attribute(item, kAXMenuItemCmdCharAttribute) as? String)?.lowercased() {
                    case "c": copy = item
                    case "x": cut = item
                    default: break
                    }
                }
            }
            if let copy, let cut {
                let copyEnabled = attribute(copy, kAXEnabledAttribute) as? Bool
                let cutEnabled = attribute(cut, kAXEnabledAttribute) as? Bool
                weChatSelectionLog.debug("Edit commands: Copy \(copyEnabled == true), Cut \(cutEnabled == true)")
                return WeChatEditCommands(copyEnabled: copyEnabled, cutEnabled: cutEnabled)
            }
        }
        return nil
    }

    private static func editabilityInContext(_ element: AXUIElement) -> SelectionEditability {
        var current = element
        var foundReadOnlyText = false
        for _ in 0..<9 {
            if isSecure(current) { return .readOnly }
            let explicitlyEditable = attribute(current, kAXIsEditableAttribute) as? Bool
            let enabled = attribute(current, kAXEnabledAttribute) as? Bool
            if explicitlyEditable == false || enabled == false { return .readOnly }
            let role = attribute(current, kAXRoleAttribute) as? String
            let result = SelectionEditabilityPolicy.classify(
                role: role, explicitlyEditable: explicitlyEditable, enabled: enabled,
                selectedTextSettable: isSettable(current, kAXSelectedTextAttribute),
                valueSettable: isSettable(current, kAXValueAttribute))
            if result == .editable { return .editable }
            if result == .readOnly { foundReadOnlyText = true }
            // A static span may be inside a contenteditable text area. Walk to its
            // editor, but never cross the surrounding page into an unrelated input.
            if [kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole, "AXWebArea"].contains(role ?? "") {
                return result
            }
            if [kAXWindowRole, kAXApplicationRole].contains(role ?? "") { break }
            guard let parent = elementAttribute(current, kAXParentAttribute) else { break }
            current = parent
        }
        return foundReadOnlyText ? .readOnly : .unknown
    }

    private static func isSettable(_ element: AXUIElement, _ name: String) -> Bool? {
        var settable = DarwinBoolean(false)
        guard AXUIElementIsAttributeSettable(element, name as CFString, &settable) == .success else { return nil }
        return settable.boolValue
    }

    private static func selectedText(of element: AXUIElement, nativeRange: CFTypeRef?, markerRange: CFTypeRef?) -> String? {
        if let text = attribute(element, kAXSelectedTextAttribute) as? String, !text.isEmpty { return text }
        // Web content may expose a text-marker selection instead of a native range.
        if let range = markerRange {
            var value: CFTypeRef?
            if AXUIElementCopyParameterizedAttributeValue(element, "AXStringForTextMarkerRange" as CFString,
                                                         range, &value) == .success {
                if let text = value as? String, !text.isEmpty { return text }
            }
        }
        // Some controls expose the selected range and its substring separately.
        if let range = nativeRange, let selection = validRange(range) {
            var value: CFTypeRef?
            if AXUIElementCopyParameterizedAttributeValue(element, kAXStringForRangeParameterizedAttribute as CFString,
                                                         range, &value) == .success, let string = value as? String, !string.isEmpty {
                return string
            }
            // Some native controls publish their value and a UTF-16 range, but
            // do not implement AXStringForRange. Read only known text controls.
            if let role = attribute(element, kAXRoleAttribute) as? String,
               [kAXTextFieldRole, kAXTextAreaRole, kAXStaticTextRole].contains(role),
               !isSecure(element), let string = attribute(element, kAXValueAttribute) as? String,
               let exact = Range(selection, in: string) {
                return String(string[exact])
            }
        }
        return nil
    }

    private static func validRange(_ value: CFTypeRef) -> NSRange? {
        guard CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var range = CFRange()
        guard AXValueGetValue(value as! AXValue, .cfRange, &range),
              range.location >= 0, range.location != NSNotFound, range.length > 0 else { return nil }
        return NSRange(location: range.location, length: range.length)
    }

    /// A known disabled Paste menu item identifies read-only selections. Some custom
    /// editors expose neither text controls nor menu commands, so absence is unknown.
    static func pasteAvailable(processID: pid_t) -> Bool? {
        let app = AXUIElementCreateApplication(processID)
        AXUIElementSetMessagingTimeout(app, 0.2)
        guard let menu = elementAttribute(app, kAXMenuBarAttribute) else { return nil }
        var pending = [(menu, 0)]
        var visited = 0
        while let (item, depth) = pending.popLast(), visited < 250 {
            visited += 1
            if let key = attribute(item, kAXMenuItemCmdCharAttribute) as? String, key.lowercased() == "v",
               let modifiers = attribute(item, kAXMenuItemCmdModifiersAttribute) as? Int, modifiers == 0 {
                return attribute(item, kAXEnabledAttribute) as? Bool
            }
            if depth < 4, let children = attribute(item, kAXChildrenAttribute) as? [AXUIElement] {
                pending.append(contentsOf: children.map { ($0, depth + 1) })
            }
        }
        return nil
    }

    private static func isSecure(_ element: AXUIElement) -> Bool {
        let subrole = attribute(element, kAXSubroleAttribute) as? String
        return subrole == kAXSecureTextFieldSubrole
    }

    private static func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
        var value: CFTypeRef?
        return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
    }

    private static func elementAttribute(_ element: AXUIElement, _ name: String) -> AXUIElement? {
        guard let value = attribute(element, name), CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }
}
