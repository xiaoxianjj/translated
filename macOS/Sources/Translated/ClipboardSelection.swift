import AppKit
import Carbon
import TranslationCore

/// Keeps the original clipboard in memory for each short transaction.
@MainActor
enum ClipboardSelection {
    static let eventTag: Int64 = 0x5472616E736C6174
    private(set) static var interactionGeneration: UInt64 = 0
    private static var busy = false

    static func userInteracted() { interactionGeneration &+= 1 }

    /// Opt-in WeChat gesture check. Discard the text immediately after validation.
    static func verifyWeChatText(processID: pid_t, generation: UInt64) async -> Bool {
        guard NSRunningApplication(processIdentifier: processID)?.bundleIdentifier == SelectionAccessPolicy.weChat,
              !busy, generation == interactionGeneration, canUse(processID) else { return false }
        busy = true
        defer { busy = false }
        guard let snapshot = PasteboardSnapshot(),
              let copied = await copy(processID, generation: generation, expectedClipboard: snapshot.changeCount) else { return false }
        defer { snapshot.restore(ifOwned: copied.changeCount) }
        guard !Task.isCancelled, generation == interactionGeneration, canUse(processID),
              NSPasteboard.general.changeCount == copied.changeCount,
              NSPasteboard.general.types?.contains(.fileURL) != true else { return false }
        return (try? LanguageRouter.validatedText(copied.text)) != nil
    }

    static func capture(processID: pid_t, anchor: CGPoint) async -> CapturedSelection? {
        do {
            let bundleID = NSRunningApplication(processIdentifier: processID)?.bundleIdentifier
            await Task.detached(priority: .userInitiated) {
                SelectionReader.prepareAccess(processID: processID, bundleIdentifier: bundleID)
            }.value
            try await acquire()
            defer { busy = false }
            let generation = interactionGeneration
            guard canUse(processID), let snapshot = PasteboardSnapshot() else { return nil }
            var ownedCount: Int?
            defer { snapshot.restore(ifOwned: ownedCount) }
            guard let copied = await copy(processID, generation: generation, expectedClipboard: snapshot.changeCount) else { return nil }
            ownedCount = copied.changeCount
            try Task.checkCancellation()
            guard canUse(processID), generation == interactionGeneration,
                  let text = try? LanguageRouter.validatedText(copied.text) else { return nil }
            let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
            let editability = await Task.detached(priority: .userInitiated) {
                SelectionReader.copiedEditability(processID: processID, bundleID: bundleID, text: text,
                                                   anchor: anchor, primaryHeight: primaryHeight, generation: generation)
            }.value
            try Task.checkCancellation()
            guard canUse(processID), generation == interactionGeneration else { return nil }
            return CapturedSelection(text: text, anchor: anchor, processID: processID, rangeLocation: 0,
                                     replacement: SelectionHandle(element: nil, rawText: copied.text, range: nil,
                                                                  interactionGeneration: generation, editability: editability))
        } catch { return nil }
    }

    static func replace(_ selection: CapturedSelection, with translation: String,
                        permit: ReplacementPermit) async -> ReplacementResult {
        guard selection.replacement.editability == .editable else { return .readOnly }
        do {
            try await acquire()
            defer { busy = false }
            let generation = selection.replacement.interactionGeneration
            guard canUse(selection.processID), generation == interactionGeneration,
                  permit.isActive else { return .selectionChanged }
            guard let snapshot = PasteboardSnapshot() else { return .failed }
            var ownedCount: Int?
            defer { snapshot.restore(ifOwned: ownedCount) }
            guard let current = await copy(selection.processID, generation: generation, expectedClipboard: snapshot.changeCount) else { return .selectionChanged }
            ownedCount = current.changeCount
            try Task.checkCancellation()
            let currentPID = NSWorkspace.shared.frontmostApplication?.processIdentifier ?? -1
            guard ClipboardReplacementPolicy.mayReplace(originalText: selection.replacement.rawText,
                                                         currentText: current.text,
                                                         originalProcess: selection.processID, currentProcess: currentPID,
                                                         originalGeneration: generation, currentGeneration: interactionGeneration),
                  canUse(selection.processID) else { return .selectionChanged }
            let bundleID = NSRunningApplication(processIdentifier: selection.processID)?.bundleIdentifier
            let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
            let currentEditability = await Task.detached(priority: .userInitiated) {
                if bundleID == SelectionAccessPolicy.weChat {
                    return SelectionReader.copiedEditability(processID: selection.processID, bundleID: bundleID,
                        text: selection.text, anchor: selection.anchor, primaryHeight: primaryHeight, generation: generation)
                }
                return SelectionReader.focusedEditability(processID: selection.processID)
            }.value
            guard currentEditability == .editable else { return .readOnly }
            // Custom web composers can report a disabled native Paste menu even
            // while their focused input handles Command-V correctly.
            if !SelectionAccessPolicy.prefersClipboard(bundleIdentifier: bundleID),
               SelectionReader.pasteAvailable(processID: selection.processID) == false { return .readOnly }
            let replacement = ReplacementPolicy.preservingWhitespace(original: current.text, translation: translation)
            let posted = permit.performIfActive {
                guard generation == interactionGeneration, canUse(selection.processID),
                      NSPasteboard.general.changeCount == current.changeCount else { return false }
                ownedCount = NSPasteboard.general.clearContents()
                guard NSPasteboard.general.setString(replacement, forType: .string) else { return false }
                ownedCount = NSPasteboard.general.changeCount
                return postCommand(key: 9, to: selection.processID) // Command-V
            } ?? false
            guard posted else { return .selectionChanged }
            // A posted paste is asynchronous. Even cancellation must leave enough time
            // for the destination to consume the clipboard before we restore it.
            await pauseUncancelled(milliseconds: 400)
            return .replaced
        } catch { return .selectionChanged }
    }

    private static func acquire() async throws {
        while busy { try await Task.sleep(for: .milliseconds(20)) }
        try Task.checkCancellation()
        busy = true
    }

    private static func canUse(_ processID: pid_t) -> Bool {
        AXIsProcessTrusted() && !IsSecureEventInputEnabled() &&
            NSWorkspace.shared.frontmostApplication?.processIdentifier == processID &&
            SelectionReader.permitsCopy(processID: processID)
    }

    private static func copy(_ processID: pid_t, generation: UInt64, expectedClipboard: Int) async -> (text: String, changeCount: Int)? {
        // Wait until the physical shortcut modifiers are released, so Command-C is
        // not interpreted as Control-Option-Command-C by the destination.
        let modifiers: CGEventFlags = [.maskCommand, .maskControl, .maskAlternate, .maskShift]
        for _ in 0..<75 {
            guard !Task.isCancelled, canUse(processID), generation == interactionGeneration else { return nil }
            if CGEventSource.flagsState(.combinedSessionState).intersection(modifiers).isEmpty { break }
            await pauseUncancelled(milliseconds: 20)
        }
        guard CGEventSource.flagsState(.combinedSessionState).intersection(modifiers).isEmpty,
              !Task.isCancelled, canUse(processID), generation == interactionGeneration else { return nil }
        let before = NSPasteboard.general.changeCount
        guard before == expectedClipboard, postCommand(key: 8, to: processID) else { return nil } // Command-C
        for _ in 0..<50 {
            await pauseUncancelled(milliseconds: 20)
            let count = NSPasteboard.general.changeCount
            if count != before {
                // A manual copy or app switch belongs to the user, not this transaction.
                guard generation == interactionGeneration,
                      NSWorkspace.shared.frontmostApplication?.processIdentifier == processID else { return nil }
                return (NSPasteboard.general.string(forType: .string) ?? "", count)
            }
        }
        return nil // Never mistake the previous clipboard contents for selected text.
    }

    private static func postCommand(key: CGKeyCode, to processID: pid_t) -> Bool {
        guard NSWorkspace.shared.frontmostApplication?.processIdentifier == processID,
              let source = CGEventSource(stateID: .privateState),
              let down = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false) else { return false }
        for event in [down, up] {
            event.flags = .maskCommand
            event.setIntegerValueField(.eventSourceUserData, value: eventTag)
            event.postToPid(processID)
        }
        return true
    }

    private static func pauseUncancelled(milliseconds: Int) async {
        await Task.detached { try? await Task.sleep(for: .milliseconds(milliseconds)) }.value
    }
}

@MainActor
private struct PasteboardSnapshot {
    let changeCount: Int
    private let items: [[NSPasteboard.PasteboardType: Data]]

    init?() {
        let pasteboard = NSPasteboard.general
        let before = pasteboard.changeCount
        var captured: [[NSPasteboard.PasteboardType: Data]] = []
        for item in pasteboard.pasteboardItems ?? [] {
            var values: [NSPasteboard.PasteboardType: Data] = [:]
            for type in item.types {
                guard let data = item.data(forType: type) else { return nil }
                values[type] = data
            }
            captured.append(values)
        }
        guard pasteboard.changeCount == before else { return nil }
        changeCount = before
        items = captured
    }

    func restore(ifOwned changeCount: Int?) {
        let pasteboard = NSPasteboard.general
        guard ClipboardReplacementPolicy.mayRestore(ownedChangeCount: changeCount, currentChangeCount: pasteboard.changeCount) else { return }
        let restored = items.map { values in
            let item = NSPasteboardItem()
            for (type, data) in values { item.setData(data, forType: type) }
            return item
        }
        pasteboard.clearContents()
        if !restored.isEmpty { pasteboard.writeObjects(restored) }
    }
}

@MainActor
enum SelectionReplacement {
    static func replace(_ selection: CapturedSelection, with translation: String,
                        permit: ReplacementPermit) async -> ReplacementResult {
        guard permit.isActive else { return .selectionChanged }
        guard selection.replacement.editability == .editable else { return .readOnly }
        let bundleID = NSRunningApplication(processIdentifier: selection.processID)?.bundleIdentifier
        if selection.replacement.element != nil && !SelectionAccessPolicy.prefersClipboard(bundleIdentifier: bundleID) {
            let result = await Task.detached(priority: .userInitiated) {
                SelectionReader.replace(selection, with: translation, permit: permit)
            }.value
            if result != .failed { return result }
        }
        return await ClipboardSelection.replace(selection, with: translation, permit: permit)
    }
}

@MainActor
enum TranslationClipboard {
    static func copy(_ translation: String, expectedChangeCount: Int, permit: ReplacementPermit) -> Bool {
        permit.performIfActive {
            let pasteboard = NSPasteboard.general
            guard SelectionDeliveryPolicy.mayCopy(translation: translation,
                                                  expectedClipboardChangeCount: expectedChangeCount,
                                                  currentClipboardChangeCount: pasteboard.changeCount) else { return false }
            pasteboard.clearContents()
            return pasteboard.setString(translation, forType: .string)
        } ?? false
    }
}
