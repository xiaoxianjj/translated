import AppKit
import TranslationCore

@MainActor
final class SelectionMonitor {
    var onSelection: ((CapturedSelection) -> Void)?
    var onCopyCandidate: ((pid_t, CGPoint, UInt64, SelectionEditability) -> Void)?
    var onDismiss: ((_ escape: Bool) -> Void)?
    var shouldRead: (() -> Bool)?
    var verifyWeChatSelection: (() -> Bool)?
    var shortcuts: (() -> [ShortcutConfiguration])?
    private var monitor: Any?
    private var workspaceObserver: NSObjectProtocol?
    private var pending: Task<Void, Never>?
    private var generation = 0
    private var lastSignature: String?
    private var mouseDownPoint: CGPoint?
    private var didDrag = false

    func start() {
        guard monitor == nil else { return }
        prepareForegroundAccess()
        monitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .leftMouseDragged, .leftMouseUp, .keyDown, .keyUp, .scrollWheel]) { [weak self] event in
            MainActor.assumeIsolated { self?.handle(event) }
        }
        workspaceObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard NSWorkspace.shared.frontmostApplication?.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
                ClipboardSelection.userInteracted()
                self?.cancelSelectionRead()
                self?.lastSignature = nil
                // Clicking into a window can activate its app halfway through a
                // drag. The AX hit test on release verifies the receiving app.
                self?.onDismiss?(false)
                self?.prepareForegroundAccess()
            }
        }
    }

    private func prepareForegroundAccess() {
        guard let app = NSWorkspace.shared.frontmostApplication else { return }
        let pid = app.processIdentifier
        let bundleID = app.bundleIdentifier
        Task.detached(priority: .userInitiated) {
            SelectionReader.prepareAccess(processID: pid, bundleIdentifier: bundleID)
        }
    }

    func stop() {
        cancelSelectionRead()
        mouseDownPoint = nil
        didDrag = false
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        if let workspaceObserver { NSWorkspace.shared.notificationCenter.removeObserver(workspaceObserver) }
        workspaceObserver = nil
    }

    func cancelSelectionRead() {
        generation += 1
        pending?.cancel()
        pending = nil
    }


    private func handle(_ event: NSEvent) {
        if event.cgEvent?.getIntegerValueField(.eventSourceUserData) == ClipboardSelection.eventTag { return }
        if event.type == .keyDown || event.type == .keyUp, isTranslationShortcut(event) { return }
        if event.type == .leftMouseDragged {
            didDrag = true
            return
        }
        if event.type == .keyDown {
            ClipboardSelection.userInteracted()
            cancelSelectionRead()
            lastSignature = nil
            onDismiss?(event.keyCode == 53)
            return
        }
        if event.type == .leftMouseDown || event.type == .scrollWheel {
            if event.type == .leftMouseDown {
                let point = screenPoint(for: event)
                mouseDownPoint = insideWorkArea(point) ? point : nil
                didDrag = false
            }
            ClipboardSelection.userInteracted()
            cancelSelectionRead()
            lastSignature = nil
            onDismiss?(false)
            return
        }
        var pointerStart: CGPoint?
        var anchor = NSEvent.mouseLocation
        var offerCopyFallback = false
        if event.type == .leftMouseUp {
            defer { mouseDownPoint = nil; didDrag = false }
            anchor = screenPoint(for: event)
            guard let start = mouseDownPoint, insideWorkArea(anchor) else { return }
            let dragged = didDrag || hypot(anchor.x - start.x, anchor.y - start.y) >= 6
            offerCopyFallback = dragged || event.clickCount >= 2
            guard offerCopyFallback || event.modifierFlags.contains(.shift) else { return }
            pointerStart = start
        }
        if event.type == .keyUp {
            if event.keyCode == 53 { return }
            let changesSelection = event.modifierFlags.contains(.shift) ||
                (event.modifierFlags.contains(.command) && event.keyCode == 0)
            guard changesSelection else { return }
            offerCopyFallback = true
        }
        guard shouldRead?() == true else { return }
        captureAfterDelay(pointerStart: pointerStart, mouse: anchor, offerCopyFallback: offerCopyFallback)
    }

    private func screenPoint(for event: NSEvent) -> CGPoint {
        guard let location = event.cgEvent?.location, let primary = NSScreen.screens.first else { return NSEvent.mouseLocation }
        return CGPoint(x: location.x, y: primary.frame.maxY - location.y)
    }

    private func insideWorkArea(_ point: CGPoint) -> Bool {
        SelectionAccessPolicy.insideWorkArea(point, workAreas: NSScreen.screens.map(\.visibleFrame))
    }

    private func isTranslationShortcut(_ event: NSEvent) -> Bool {
        let flags = event.modifierFlags.intersection([.control, .option, .command, .shift])
        return (shortcuts?() ?? []).contains { shortcut in
            guard event.keyCode == shortcut.keyCode else { return false }
            var expected: NSEvent.ModifierFlags = []
            if shortcut.control { expected.insert(.control) }
            if shortcut.option { expected.insert(.option) }
            if shortcut.command { expected.insert(.command) }
            if shortcut.shift { expected.insert(.shift) }
            return flags == expected
        }
    }

    private func captureAfterDelay(pointerStart: CGPoint?, mouse: CGPoint, offerCopyFallback: Bool) {
        cancelSelectionRead()
        let token = generation
        guard let front = NSWorkspace.shared.frontmostApplication,
              front.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
        let processID = front.processIdentifier
        let fallbackBundleID = offerCopyFallback ? front.bundleIdentifier : nil
        if fallbackBundleID == SelectionAccessPolicy.weChat {
            weChatSelectionLog.debug("Detected WeChat selection gesture")
        }
        let height = NSScreen.screens.first?.frame.height ?? 0
        let interaction = ClipboardSelection.interactionGeneration
        pending = Task { [weak self] in
            // Web views can publish their selection after mouse-up. Retry a
            // miss once; new input or switching apps invalidates either read.
            for delay in [160, 240] {
                do { try await Task.sleep(for: .milliseconds(delay)) } catch { return }
                guard let self, self.generation == token, self.shouldRead?() == true,
                      NSWorkspace.shared.frontmostApplication?.processIdentifier == processID else { return }
                let read = Task.detached(priority: .userInitiated) {
                    SelectionReader.probe(processID: processID, pointerStart: pointerStart, mouse: mouse,
                                          primaryHeight: height, interaction: interaction, fallbackBundleID: fallbackBundleID)
                }
                let result = await withTaskCancellationHandler { await read.value } onCancel: { read.cancel() }
                guard !Task.isCancelled, self.generation == token, self.shouldRead?() == true,
                      NSWorkspace.shared.frontmostApplication?.processIdentifier == processID else { return }
                switch result {
                case .selected(let selection):
                    guard self.lastSignature != selection.signature else { return }
                    self.lastSignature = selection.signature
                    self.onSelection?(selection)
                    return
                case .candidate(let editability):
                    self.onCopyCandidate?(processID, mouse, interaction, editability)
                    return
                case .weChatWindow(let menuAccess):
                    if pointerStart != nil, self.verifyWeChatSelection?() == true {
                        let verified = await ClipboardSelection.verifyWeChatText(processID: processID, generation: interaction)
                        weChatSelectionLog.debug("Temporary Copy verified text: \(verified)")
                        guard verified, !Task.isCancelled, self.generation == token, self.shouldRead?() == true,
                              self.verifyWeChatSelection?() == true,
                              NSWorkspace.shared.frontmostApplication?.processIdentifier == processID else { return }
                        self.onCopyCandidate?(processID, mouse, interaction, .unknown)
                        return
                    }
                    if let menuAccess {
                        self.onCopyCandidate?(processID, mouse, interaction, menuAccess)
                        return
                    }
                case .none:
                    self.lastSignature = nil
                }
            }
        }
    }
}
