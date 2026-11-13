import AppKit
import SwiftUI
import TranslationCore
@preconcurrency import Translation

private final class TranslationPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class PanelController {
    private var quickPanel: NSPanel?
    private var statusPanel: NSPanel?
    private let selectionStatus = SelectionStatusPresentation()
    private var statusExpiry: Task<Void, Never>?
    private var resultPanel: TranslationPanel?
    private var keepsResultForManualCopy = false
    private var escapeMonitor: Any?
    private var expiryTask: Task<Void, Never>?
    private var directTask: Task<Void, Never>?
    private var directReplacementPermit: ReplacementPermit?
    private var activeJobID: UUID?
    private let presentation = TranslationPresentation()
    private let preferences: AppPreferences
    private var anchor = CGPoint.zero
    var openWorkspace: (() -> Void)?

    init(preferences: AppPreferences) {
        self.preferences = preferences
        presentation.close = { [weak self] in self?.dismiss() }
        presentation.openWorkspace = { [weak self] in self?.dismiss(); self?.openWorkspace?() }
        presentation.didFinish = { [weak self] id, outcome in
            guard let self, let request = self.presentation.request, request.id == id,
                  let selection = request.selection else { return }
            switch outcome {
            case .delivered(let delivery):
                self.keepsResultForManualCopy = delivery == .preview && request.delivery != .preview
                if delivery != .preview { self.dismissResultPanel() }
                self.showStatus(delivery == .copy ? .copied : .succeeded, anchor: selection.anchor)
            case .failed:
                self.showStatus(.failed, anchor: selection.anchor)
            }
        }
        presentation.retry = { [weak self] text, source in
            guard let self, let request = self.presentation.request else { return }
            let delivery = self.keepsResultForManualCopy || (request.delivery == .replace && !request.replacementPermit.isActive)
                ? SelectionDelivery.preview : request.delivery
            self.showTranslation(text: text, anchor: self.anchor, sourceOverride: source,
                                 selection: request.selection, delivery: delivery)
        }
        escapeMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == 53 {
                MainActor.assumeIsolated { self?.dismiss() }
                return nil
            }
            return event
        }
    }

    func showButton(for selection: CapturedSelection) {
        showActionButton(anchor: selection.anchor) { [weak self] in
            self?.translateSelection(selection)
        }
    }

    func showCopyButton(anchor: CGPoint, copy: @escaping () -> Void) {
        showActionButton(anchor: anchor, action: copy)
    }

    private func showActionButton(anchor: CGPoint, action: @escaping () -> Void) {
        // A new selection can offer its own button while an earlier request finishes.
        guard resultPanel?.isVisible != true || keepsResultForManualCopy else { return }
        quickPanel?.orderOut(nil)
        expiryTask?.cancel()
        let popupFrame = frame(size: CGSize(width: 142, height: 46), anchor: anchor)
        let panel = NSPanel(contentRect: popupFrame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        configure(panel)
        panel.contentView = NSHostingView(rootView: QuickButtonView(translate: action))
        panel.setFrame(popupFrame, display: true)
        quickPanel = panel
        panel.orderFrontRegardless()
        expiryTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(12))
            guard !Task.isCancelled else { return }
            self?.quickPanel?.orderOut(nil)
        }
    }

    func translateSelection(_ selection: CapturedSelection, intent: SelectionIntent = .automatic) {
        dismiss()
        let delivery = SelectionDeliveryPolicy.delivery(intent: intent, editability: selection.replacement.editability,
                                                        replacementEnabled: preferences.replaceSelection)
        guard delivery != .preview else {
            showTranslation(text: selection.text, anchor: selection.anchor, selection: selection)
            return
        }
        let route = LanguageRouter.route(for: selection.text, chinese: preferences.chinese)
        let provider = preferences.provider
        let configuration = preferences.configuration()
        let highQuality = preferences.highQuality
        let expectedClipboardChangeCount = NSPasteboard.general.changeCount
        if provider != .apple || canTranslateWithoutUI(route: route) {
            let permit = ReplacementPermit()
            let jobID = UUID()
            activeJobID = jobID
            directReplacementPermit = delivery == .replace ? permit : nil
            preferences.progress = "Preparing translation…"
            showStatus(.translating, anchor: selection.anchor)
            directTask = Task { [weak self] in
                guard let self else { return }
                defer {
                    if self.activeJobID == jobID {
                        self.preferences.progress = nil
                        self.activeJobID = nil
                        self.directReplacementPermit = nil
                    }
                }
                do {
                    let translated: String
                    if provider != .apple {
                        translated = try await LLMTranslationEngine.translate(text: selection.text, route: route, configuration: configuration) { [weak self] part, total in
                            await MainActor.run {
                                if self?.activeJobID == jobID { self?.preferences.progress = "Translating \(part)/\(total)…" }
                            }
                        }
                    } else if #available(macOS 26.0, *), let sourceCode = route.sourceCode {
                        let source = Locale.Language(identifier: sourceCode)
                        let target = Locale.Language(identifier: route.targetCode)
                        let availability = await AppleTranslationEngine.availability(highQuality: highQuality).status(from: source, to: target)
                        try Task.checkCancellation()
                        guard availability == .installed else {
                            self.showTranslation(text: selection.text, anchor: selection.anchor, selection: selection, delivery: delivery,
                                                 permit: permit, expectedClipboardChangeCount: expectedClipboardChangeCount)
                            return
                        }
                        let session = AppleTranslationEngine.installedSession(source: source, target: target, highQuality: highQuality)
                        translated = try await AppleTranslationEngine.translate(text: selection.text, route: route, session: session) { part, total in
                            if self.activeJobID == jobID { self.preferences.progress = "Translating \(part)/\(total)…" }
                        }
                    } else { return }
                    try Task.checkCancellation()
                    if delivery == .copy {
                        if TranslationClipboard.copy(translated, expectedChangeCount: expectedClipboardChangeCount, permit: permit) {
                            self.showStatus(.copied, anchor: selection.anchor)
                        } else {
                            self.showTranslation(text: selection.text, anchor: selection.anchor, selection: selection, translatedText: translated)
                        }
                        return
                    }
                    let result = await withTaskCancellationHandler {
                        await SelectionReplacement.replace(selection, with: translated, permit: permit)
                    } onCancel: {
                        permit.cancel()
                    }
                    guard !Task.isCancelled else { return }
                    switch result {
                    case .replaced:
                        self.showStatus(.succeeded, anchor: selection.anchor)
                    case .readOnly:
                        if TranslationClipboard.copy(translated, expectedChangeCount: NSPasteboard.general.changeCount, permit: permit) {
                            self.showStatus(.copied, anchor: selection.anchor)
                        } else {
                            self.showTranslation(text: selection.text, anchor: selection.anchor, selection: selection, translatedText: translated)
                        }
                    case .selectionChanged, .failed:
                        self.showTranslation(text: selection.text, anchor: selection.anchor, selection: selection, translatedText: translated)
                    }
                } catch {
                    guard !Task.isCancelled else { return }
                    self.showMessage("Couldn’t translate. \(error.localizedDescription)", anchor: selection.anchor)
                    self.showStatus(.failed, anchor: selection.anchor)
                }
            }
        } else {
            showTranslation(text: selection.text, anchor: selection.anchor, selection: selection, delivery: delivery)
        }
    }

    private func canTranslateWithoutUI(route: TranslationRoute) -> Bool {
        if #available(macOS 26.0, *) { return route.sourceCode != nil }
        return false
    }

    func showTranslation(text: String, anchor: CGPoint, sourceOverride: String? = nil,
                         selection: CapturedSelection? = nil, translatedText: String? = nil,
                         delivery: SelectionDelivery = .preview, permit: ReplacementPermit = ReplacementPermit(),
                         expectedClipboardChangeCount: Int? = nil) {
        guard let validated = try? LanguageRouter.validatedText(text) else { return }
        self.anchor = anchor
        if translatedText == nil {
            expiryTask?.cancel()
            quickPanel?.orderOut(nil)
        }
        let route: TranslationRoute
        if let sourceOverride {
            route = TranslationRoute(sourceCode: sourceOverride, targetCode: sourceOverride == "en" ? preferences.chinese.rawValue : "en")
        } else {
            route = LanguageRouter.route(for: validated, chinese: preferences.chinese)
        }
        presentation.message = nil
        keepsResultForManualCopy = translatedText != nil && selection != nil
        presentation.request?.replacementPermit.cancel()
        presentation.request = TranslationRequest(text: validated, route: route,
                                                 selection: selection, delivery: delivery,
                                                 expectedClipboardChangeCount: expectedClipboardChangeCount ?? NSPasteboard.general.changeCount,
                                                 translatedText: translatedText,
                                                 apiConfiguration: preferences.provider == .apple ? nil : preferences.configuration(),
                                                 highQuality: preferences.highQuality, replacementPermit: permit)
        showResultPanel(anchor: anchor, takeFocus: delivery == .preview && translatedText == nil)
        if selection != nil {
            showStatus(translatedText == nil ? .translating : .succeeded, anchor: anchor)
        } else {
            hideStatus()
        }
    }

    private func showStatus(_ status: SelectionTranslationStatus, anchor: CGPoint) {
        statusExpiry?.cancel()
        let popupFrame = PopupPlacement.statusFrame(size: CGSize(width: 184, height: 48),
                                                    visibleFrame: screen(for: anchor).visibleFrame)
        let panel: NSPanel
        if let statusPanel {
            panel = statusPanel
        } else {
            panel = NSPanel(contentRect: popupFrame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            configure(panel)
            panel.ignoresMouseEvents = true
            statusPanel = panel
        }
        selectionStatus.status = status
        if panel.frame != popupFrame { panel.setFrame(popupFrame, display: true) }
        if !panel.isVisible {
            panel.contentView = NSHostingView(rootView: SelectionStatusView(presentation: selectionStatus))
            panel.orderFrontRegardless()
        }
        guard status != .translating else { return }
        statusExpiry = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(2)) } catch { return }
            self?.hideStatus()
        }
    }

    private func hideStatus() {
        statusExpiry?.cancel()
        statusExpiry = nil
        statusPanel?.orderOut(nil)
        statusPanel?.contentView = nil
    }

    func showMessage(_ message: String, anchor: CGPoint) {
        keepsResultForManualCopy = false
        hideStatus()
        quickPanel?.orderOut(nil)
        presentation.request?.replacementPermit.cancel()
        presentation.request = nil
        presentation.message = message
        showResultPanel(anchor: anchor)
    }

    func dismiss() {
        hideStatus()
        activeJobID = nil
        preferences.progress = nil
        directReplacementPermit?.cancel()
        directTask?.cancel()
        directTask = nil
        directReplacementPermit = nil
        expiryTask?.cancel()
        quickPanel?.orderOut(nil)
        dismissResultPanel()
    }

    private func dismissResultPanel() {
        keepsResultForManualCopy = false
        presentation.request?.replacementPermit.cancel()
        resultPanel?.orderOut(nil)
        // Removing the view cancels its translationTask, including in-flight requests.
        resultPanel?.contentViewController = nil
        resultPanel?.contentView = nil
        presentation.request = nil
    }

    func dismissForExternalInteraction(escape: Bool = false) {
        // The system download sheet must remain alive while the user interacts with it.
        guard resultPanel?.attachedSheet == nil, NSApp.modalWindow == nil else { return }
        let translatingSelection = activeJobID != nil ||
            (presentation.request?.selection != nil && selectionStatus.status == .translating)
        guard !escape, translatingSelection || statusExpiry != nil || keepsResultForManualCopy else { dismiss(); return }
        // Invalidate a pending write into the old selection. Keep the translation
        // alive so the result is available even if the user has moved elsewhere.
        directReplacementPermit?.cancel()
        if let request = presentation.request, request.delivery == .replace {
            request.replacementPermit.cancel()
        }
        quickPanel?.orderOut(nil)
    }

    private func showResultPanel(anchor: CGPoint, takeFocus: Bool = true) {
        if resultPanel == nil {
            let panel = TranslationPanel(contentRect: frame(size: CGSize(width: 440, height: 360), anchor: anchor),
                                         styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            configure(panel)
            panel.isMovableByWindowBackground = false
            resultPanel = panel
        }
        guard let panel = resultPanel else { return }
        if panel.contentViewController == nil {
            let controller = NSHostingController(rootView: TranslationPanelView(presentation: presentation))
            controller.view.frame = CGRect(origin: .zero, size: CGSize(width: 440, height: 360))
            panel.contentViewController = controller
        }
        panel.setFrame(frame(size: CGSize(width: 440, height: 360), anchor: anchor), display: true)
        if takeFocus { panel.makeKeyAndOrderFront(nil) }
        panel.orderFrontRegardless()
        panel.contentView?.layoutSubtreeIfNeeded()
        panel.displayIfNeeded()
    }

    private func configure(_ panel: NSPanel) {
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        panel.animationBehavior = .none
    }

    private func frame(size: CGSize, anchor: CGPoint) -> CGRect {
        PopupPlacement.frame(size: size, anchor: anchor, screen: screen(for: anchor).visibleFrame)
    }

    private func screen(for anchor: CGPoint) -> NSScreen {
        NSScreen.screens.first(where: { $0.frame.contains(anchor) }) ?? NSScreen.main ?? NSScreen.screens[0]
    }
}
