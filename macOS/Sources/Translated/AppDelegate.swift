import AppKit
import SwiftUI
import TranslationCore
import Combine

@main
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let preferences = AppPreferences()
    private let monitor = SelectionMonitor()
    private let shortcut = GlobalShortcut()
    private lazy var panels = PanelController(preferences: preferences)
    private var statusItem: NSStatusItem!
    private var workspaceWindow: NSWindow?
    private var permissionTimer: Timer?
    private var pauseItem: NSMenuItem?
    private var shortcutCapture: Task<Void, Never>?
    private var translationMenuItem: NSMenuItem?
    private var cancelMenuItem: NSMenuItem?
    private var observations = Set<AnyCancellable>()

    static func main() {
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        application.setActivationPolicy(.accessory)
        withExtendedLifetime(delegate) { application.run() }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        setupMenu()
        panels.openWorkspace = { [weak self] in self?.showWorkspace() }
        monitor.shouldRead = { [weak self] in
            guard let self else { return false }
            return self.preferences.automaticButton && self.preferences.accessibilityGranted
        }
        monitor.onSelection = { [weak self] selection in self?.panels.showButton(for: selection) }
        monitor.verifyWeChatSelection = { [weak self] in self?.preferences.verifyWeChatSelection == true }
        monitor.onCopyCandidate = { [weak self] processID, anchor, interaction, editability in
            self?.panels.showCopyButton(anchor: anchor) { [weak self] in
                guard ClipboardSelection.interactionGeneration == interaction,
                      let source = NSWorkspace.shared.frontmostApplication,
                      source.processIdentifier == processID else { return }
                let recheckWeChat = source.bundleIdentifier == SelectionAccessPolicy.weChat
                let intent: SelectionIntent = editability == .editable || recheckWeChat ? .automatic : .copy
                self?.captureSelection(intent: intent, buttonAnchor: anchor)
            }
        }
        monitor.onDismiss = { [weak self] escape in
            self?.shortcutCapture?.cancel()
            self?.panels.dismissForExternalInteraction(escape: escape)
        }
        monitor.shortcuts = { [weak self] in [self?.preferences.shortcut ?? .standard] }
        shortcut.action = { [weak self] in self?.translateSelection() }
        preferences.shortcutAvailable = shortcut.register(preferences.shortcut)
        preferences.registerShortcut = { [weak self] candidate in
            guard let self else { return false }
            let success = self.shortcut.register(candidate)
            if success { self.updateShortcutMenu(candidate) }
            return success
        }
        updateShortcutMenu(preferences.shortcut)
        preferences.$progress.sink { [weak self] progress in
            self?.statusItem.button?.toolTip = progress.map { "\($0) · Esc to cancel" } ?? "Translated · Select words, then translate"
            self?.statusItem.button?.image = NSImage(systemSymbolName: progress == nil ? "character.bubble" : "ellipsis.bubble", accessibilityDescription: progress ?? "Translated")
        }.store(in: &observations)
        updatePermission()
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.updatePermission() }
        }

        if !UserDefaults.standard.bool(forKey: "hasOpened") || !preferences.accessibilityGranted {
            showWorkspace()
            UserDefaults.standard.set(true, forKey: "hasOpened")
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { showWorkspace() }
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        permissionTimer?.invalidate()
        shortcutCapture?.cancel()
        shortcut.unregister()
        monitor.stop()
    }

    private func updatePermission() {
        let trusted = AXIsProcessTrusted()
        let wasTrusted = preferences.accessibilityGranted
        if preferences.accessibilityGranted != trusted { preferences.accessibilityGranted = trusted }
        if trusted { monitor.start() } else { monitor.stop() }
        if wasTrusted && !trusted { panels.dismiss() }
    }

    private func setupMenu() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            let image = NSImage(systemSymbolName: "character.bubble", accessibilityDescription: "Translated")
            image?.isTemplate = true
            button.image = image
            button.toolTip = "Translated · Select words, then translate"
        }
        let menu = NSMenu()
        menu.delegate = self
        let open = NSMenuItem(title: "Open Translated…", action: #selector(showWorkspace), keyEquivalent: "")
        open.target = self
        menu.addItem(open)
        let translate = NSMenuItem(title: "Translate Selection", action: #selector(translateSelection), keyEquivalent: "t")
        translate.keyEquivalentModifierMask = [.control, .option]
        translate.target = self
        translationMenuItem = translate
        menu.addItem(translate)
        let cancel = NSMenuItem(title: "Cancel Translation", action: #selector(cancelTranslation), keyEquivalent: "")
        cancel.target = self
        cancelMenuItem = cancel
        menu.addItem(cancel)
        menu.addItem(.separator())
        let pause = NSMenuItem(title: "Show Button After Selection", action: #selector(toggleAutomatic), keyEquivalent: "")
        pause.target = self
        pauseItem = pause
        menu.addItem(pause)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Quit Translated", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quit)
        statusItem.menu = menu

        // A normal Edit menu enables Command-C/V/A inside the paste editor and result.
        let mainMenu = NSMenu()
        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(NSMenuItem(title: "Quit Translated", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)
        let editItem = NSMenuItem(title: "Edit", action: nil, keyEquivalent: "")
        let editMenu = NSMenu(title: "Edit")
        for (title, action, key) in [("Undo", Selector(("undo:")), "z"), ("Cut", #selector(NSText.cut(_:)), "x"),
                                      ("Copy", #selector(NSText.copy(_:)), "c"), ("Paste", #selector(NSText.paste(_:)), "v"),
                                      ("Select All", #selector(NSText.selectAll(_:)), "a")] {
            editMenu.addItem(NSMenuItem(title: title, action: action, keyEquivalent: key))
        }
        editItem.submenu = editMenu
        mainMenu.addItem(editItem)
        NSApp.mainMenu = mainMenu
    }

    func menuWillOpen(_ menu: NSMenu) {
        pauseItem?.state = preferences.automaticButton ? .on : .off
        cancelMenuItem?.isHidden = preferences.progress == nil
    }

    @objc private func cancelTranslation() { panels.dismiss() }

    private func updateShortcutMenu(_ configuration: ShortcutConfiguration) {
        translationMenuItem?.keyEquivalent = configuration.keyCode == 49 ? " " : configuration.keyLabel.lowercased()
        var modifiers: NSEvent.ModifierFlags = []
        if configuration.command { modifiers.insert(.command) }
        if configuration.option { modifiers.insert(.option) }
        if configuration.control { modifiers.insert(.control) }
        if configuration.shift { modifiers.insert(.shift) }
        translationMenuItem?.keyEquivalentModifierMask = modifiers
    }

    @objc private func toggleAutomatic() {
        preferences.automaticButton.toggle()
        if !preferences.automaticButton { panels.dismiss() }
    }

    @objc func showWorkspace() {
        panels.dismiss()
        if workspaceWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 580, height: 700),
                                  styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
            window.title = "Translated"
            window.titlebarAppearsTransparent = true
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: WorkspaceView(preferences: preferences) { [weak self] text in
                guard let self else { return }
                let frame = self.workspaceWindow?.frame ?? .zero
                self.panels.dismiss()
                self.panels.showTranslation(text: text, anchor: CGPoint(x: frame.maxX + 14, y: frame.maxY - 70))
            })
            window.center()
            workspaceWindow = window
        }
        NSApp.activate(ignoringOtherApps: true)
        workspaceWindow?.makeKeyAndOrderFront(nil)
    }

    @objc private func translateSelection() {
        captureSelection(intent: .automatic)
    }

    private func captureSelection(intent: SelectionIntent, buttonAnchor: CGPoint? = nil) {
        guard preferences.accessibilityGranted else { showWorkspace(); return }
        guard let front = NSWorkspace.shared.frontmostApplication else { return }
        let processID = front.processIdentifier
        let mouse = buttonAnchor ?? NSEvent.mouseLocation
        let height = NSScreen.screens.first?.frame.height ?? 0
        let interaction = ClipboardSelection.interactionGeneration
        monitor.cancelSelectionRead()
        shortcutCapture?.cancel()
        panels.dismiss()
        shortcutCapture = Task { [weak self] in
            let clipboardFirst = buttonAnchor != nil || SelectionAccessPolicy.prefersClipboard(bundleIdentifier: front.bundleIdentifier)
            var selection: CapturedSelection?
            if clipboardFirst {
                selection = await ClipboardSelection.capture(processID: processID, anchor: mouse)
            }
            if selection == nil && buttonAnchor == nil && !Task.isCancelled {
                selection = await Task.detached(priority: .userInitiated) {
                    SelectionReader.read(processID: processID, mouse: mouse, primaryHeight: height, interactionGeneration: interaction)
                }.value
            }
            guard !Task.isCancelled, let self else { return }
            if selection == nil && !clipboardFirst {
                selection = await ClipboardSelection.capture(processID: processID, anchor: mouse)
            }
            guard !Task.isCancelled else { return }
            if let selection {
                self.panels.translateSelection(selection, intent: intent)
            } else if buttonAnchor == nil {
                self.panels.showMessage("This app didn’t provide selected text. Select words in its text field and try the shortcut again.", anchor: mouse)
            }
        }
    }
}
