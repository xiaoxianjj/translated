import SwiftUI
import TranslationCore

struct WorkspaceView: View {
    @ObservedObject var preferences: AppPreferences
    let translate: (String) -> Void
    @State private var tab = 0
    @State private var input = ""
    @State private var validationMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack {
                Wordmark()
                Spacer()
                Text(preferences.progress ?? (preferences.accessibilityGranted ? "Ready" : "Let’s set up"))
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .padding(.horizontal, 10).padding(.vertical, 6)
                    .background(AppStyle.ink.opacity(0.07), in: Capsule())
            }
            Picker("Workspace", selection: $tab) {
                Text("Translate").tag(0)
                Text("Settings").tag(1)
            }.pickerStyle(.segmented).labelsHidden()

            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    if !preferences.accessibilityGranted {
                        HStack(spacing: 12) {
                            Image(systemName: "cursorarrow.click.2").font(.system(size: 22)).accessibilityHidden(true)
                            VStack(alignment: .leading, spacing: 3) {
                                Text("Enable text selection").font(.system(size: 13, weight: .semibold))
                                Text("Allow Accessibility to replace selected words.").font(.system(size: 11)).foregroundStyle(.secondary)
                            }
                            Spacer(minLength: 0)
                            Button("Open Settings") { preferences.requestAccessibility() }.buttonStyle(PeachButtonStyle())
                        }.padding(14).background(AppStyle.ink.opacity(0.045), in: RoundedRectangle(cornerRadius: 14))
                    }
                    if tab == 0 { translationEditor } else { settings }
                }.padding(.vertical, 4)
            }
            HStack {
                Label(preferences.provider == .apple ? "On-device translation" : preferences.provider.title,
                      systemImage: preferences.provider == .apple ? "lock.shield" : "sparkles")
                Spacer()
                Text(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.1.1")
            }.font(.system(size: 11)).foregroundStyle(.secondary)
        }
        .padding(28).foregroundStyle(AppStyle.ink).background(AppStyle.paper).tint(AppStyle.forest)
        .frame(minWidth: 580, minHeight: 680)
    }

    private var translationEditor: some View {
        VStack(alignment: .leading, spacing: 22) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 14) {
                    Text("English").font(.system(size: 32, weight: .bold, design: .rounded))
                    Image(systemName: "arrow.left.arrow.right").font(.system(size: 20, weight: .medium)).foregroundStyle(.secondary)
                    Text("中文").font(.system(size: 30, weight: .semibold, design: .rounded))
                }
                HStack(spacing: 6) {
                    Text("Select text, then press")
                    Text(preferences.shortcut.display).fontWeight(.semibold)
                }.font(.system(size: 14)).foregroundStyle(.secondary)
            }
            HStack {
                Text("Words or whole passages").font(.system(size: 15, weight: .semibold, design: .rounded))
                Spacer()
                Button {
                    if let text = NSPasteboard.general.string(forType: .string) { input = text; validationMessage = nil }
                    else { validationMessage = "There’s no text on your clipboard." }
                } label: { Label("Paste", systemImage: "doc.on.clipboard") }.buttonStyle(.plain).font(.system(size: 12))
            }
            ZStack(alignment: .topLeading) {
                if input.isEmpty {
                    Text("Paste or type something…").foregroundStyle(.tertiary).padding(10).allowsHitTesting(false)
                }
                TextEditor(text: $input).font(.system(size: 15)).scrollContentBackground(.hidden)
                    .padding(4).accessibilityLabel("Text to translate")
            }
            .frame(height: 230)
            .background(AppStyle.ink.opacity(0.035), in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(AppStyle.ink.opacity(0.12)))
            HStack {
                Text("\(input.count.formatted()) / 50,000").font(.system(size: 11)).foregroundStyle(input.count > 50_000 ? .red : .secondary)
                Spacer()
                Button { submit() } label: { Label("Translate", systemImage: "arrow.right") }
                    .buttonStyle(PeachButtonStyle()).keyboardShortcut(.return, modifiers: .command)
            }
            if let validationMessage { Text(validationMessage).font(.system(size: 12)).foregroundStyle(.red) }
        }
    }

    private var settings: some View {
        VStack(alignment: .leading, spacing: 24) {
            ShortcutSettingsView(preferences: preferences)
            Divider()
            ProviderSettingsView(preferences: preferences)
            Divider()
            VStack(spacing: 17) {
                settingToggle("Replace selected text while typing", value: $preferences.replaceSelection)
                settingToggle("Show button after selection", value: $preferences.automaticButton)
                if preferences.automaticButton {
                    settingToggle("Check WeChat selections with Copy", value: $preferences.verifyWeChatSelection)
                        .help("Temporarily copies highlighted words, then restores your clipboard. Clipboard managers may see the copy.")
                }
                HStack {
                    Text("Translate English to").font(.system(size: 13, weight: .medium))
                    Spacer()
                    Picker("Chinese variant", selection: $preferences.chinese) {
                        ForEach(ChineseVariant.allCases, id: \.self) { Text($0.title).tag($0) }
                    }.pickerStyle(.segmented).labelsHidden().frame(width: 180)
                }
            }
        }
    }

    private func submit() {
        do { let text = try LanguageRouter.validatedText(input); validationMessage = nil; translate(text) }
        catch { validationMessage = error.localizedDescription }
    }
}

@MainActor
func settingToggle(_ title: String, value: Binding<Bool>) -> some View {
    HStack {
        Text(title).font(.system(size: 13, weight: .medium))
        Spacer()
        Toggle(title, isOn: value).labelsHidden().toggleStyle(.switch).controlSize(.small)
    }
}

struct ShortcutSettingsView: View {
    @ObservedObject var preferences: AppPreferences
    @State private var draft = ShortcutConfiguration.standard
    @State private var message: String?
    @State private var isError = false
    private var current: ShortcutConfiguration { preferences.shortcut }
    private var available: Bool { preferences.shortcutAvailable }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Translation shortcut").font(AppStyle.settingsHeading)
                Spacer()
                Text("Active · \(current.display)").font(.system(size: 13, weight: .semibold, design: .monospaced))
                    .padding(.horizontal, 10).padding(.vertical, 6).background(AppStyle.softGreen, in: Capsule()).foregroundStyle(AppStyle.forest)
            }
            HStack(spacing: 8) {
                Toggle("⌃ Control", isOn: $draft.control)
                Toggle("⌥ Option", isOn: $draft.option)
                Toggle("⇧ Shift", isOn: $draft.shift)
                Toggle("⌘ Command", isOn: $draft.command)
            }.toggleStyle(.button).font(.system(size: 12)).controlSize(.small)
            HStack {
                Picker("Key", selection: $draft.keyCode) {
                    ForEach(ShortcutConfiguration.keys, id: \.code) { Text($0.label).tag($0.code) }
                }.frame(width: 125)
                Spacer()
                Button("Reset") { draft = .standard; save() }.buttonStyle(.plain)
                Button("Apply \(draft.display)") { save() }.buttonStyle(PeachButtonStyle())
                    .disabled(draft == current && available)
            }
            if draft != current {
                Text("Not applied").font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
            }
            if let message { Text(message).font(.system(size: 12)).foregroundStyle(isError ? .red : .secondary) }
            if !available {
                Text("The saved shortcut is in use. Choose another combination.").font(.system(size: 12)).foregroundStyle(.red)
            }
        }
        .onAppear { draft = current }
        .onChange(of: draft) { _, _ in message = nil }
    }

    private func save() {
        do { try preferences.changeShortcut(draft); isError = false; message = "Saved · \(draft.display)" }
        catch { isError = true; message = error.localizedDescription }
    }
}

struct ProviderSettingsView: View {
    @ObservedObject var preferences: AppPreferences
    @State private var chosen = TranslationProvider.apple
    @State private var configuration = LLMConfiguration(provider: .apple)
    @State private var newKey = ""
    @State private var hasSavedKey = false
    @State private var message: String?
    @State private var isError = false
    @State private var testing = false
    @State private var testTask: Task<Void, Never>?
    @State private var removingKey = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Translation provider").font(AppStyle.settingsHeading)
                Spacer()
                Picker("Translation provider", selection: $chosen) {
                    ForEach(TranslationProvider.allCases, id: \.self) { Text($0.title).tag($0) }
                }.labelsHidden().frame(width: 210)
            }
            if chosen == .apple {
                settingToggle("Prefer Apple Intelligence", value: $preferences.highQuality)
                Text("Uses Apple Intelligence when available, with Apple’s standard translation as fallback.")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            } else {
                Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 11) {
                    GridRow {
                        Text("Model")
                        TextField("Model name", text: $configuration.model).textFieldStyle(.roundedBorder)
                    }
                    GridRow {
                        Text("API URL")
                        if chosen == .custom {
                            TextField("https://…/v1", text: $configuration.baseURL).textFieldStyle(.roundedBorder)
                        } else {
                            Text(configuration.baseURL).foregroundStyle(.secondary).textSelection(.enabled)
                        }
                    }
                    GridRow {
                        Text("API key")
                        SecureField(hasSavedKey ? "Saved in Keychain · leave blank to keep" : "Paste your API key", text: $newKey)
                            .textFieldStyle(.roundedBorder).accessibilityLabel("API key")
                    }
                }.font(.system(size: 12))
                if chosen == .deepSeek {
                    HStack {
                        Text("Reasoning").font(.system(size: 13, weight: .medium))
                        Spacer()
                        Picker("Reasoning", selection: $configuration.reasoning) {
                            Text("Off · Faster").tag(false)
                            Text("Low").tag(true)
                        }.pickerStyle(.segmented).labelsHidden().frame(width: 210)
                    }
                }
                if configuration.isLocal {
                    Text("Sends selected text to your local server. A key is optional.").font(.system(size: 11)).foregroundStyle(.secondary)
                } else {
                    Text("Sends selected text to \((try? configuration.endpoint().host) ?? "your provider"). API usage may be billed.")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                }
            }
            HStack(spacing: 12) {
                if chosen != .apple {
                    Button(testing ? "Testing…" : "Test translation") { test() }
                        .disabled(testing || chosen != preferences.provider || configuration != preferences.configuration() || !newKey.isEmpty)
                    if hasSavedKey {
                        Button("Remove key", role: .destructive) { removingKey = true }.buttonStyle(.plain)
                    }
                }
                Spacer(minLength: 0)
                Button(chosen == preferences.provider ? "Save settings" : "Save & use") { save() }
                    .buttonStyle(PeachButtonStyle()).disabled(testing)
            }
            if let message { Text(message).font(.system(size: 12)).foregroundStyle(isError ? .red : .secondary) }
        }
        .onAppear { chosen = preferences.provider; reload() }
        .onChange(of: chosen) { _, _ in reload() }
        .onChange(of: configuration.baseURL) { _, _ in refreshKeyPresence() }
        .onDisappear { testTask?.cancel(); newKey = "" }
        .confirmationDialog("Remove this provider’s saved API key?", isPresented: $removingKey) {
            Button("Remove key", role: .destructive) {
                do {
                    if let account = configuration.credentialAccount { try APIKeyStore.remove(account: account) }
                    refreshKeyPresence(); message = "Key removed."; isError = false
                } catch { message = error.localizedDescription; isError = true }
            }
        }
    }

    private func reload() {
        testTask?.cancel(); testing = false
        configuration = preferences.configuration(for: chosen)
        newKey = ""; message = nil
        refreshKeyPresence()
    }
    private func refreshKeyPresence() {
        hasSavedKey = configuration.credentialAccount.map { APIKeyStore.contains(account: $0) } ?? false
    }
    private func save() {
        do {
            try preferences.saveProvider(configuration, newKey: newKey)
            newKey = ""; refreshKeyPresence(); message = "Saved · \(chosen.title)"; isError = false
        } catch { message = error.localizedDescription; isError = true }
    }
    private func test() {
        testing = true; message = nil
        let snapshot = configuration
        testTask = Task {
            defer { testing = false }
            do {
                let start = ContinuousClock.now
                _ = try await LLMTranslationEngine.translate(text: "Hello, world!", route: .init(sourceCode: "en", targetCode: "zh-Hans"), configuration: snapshot)
                try Task.checkCancellation()
                let duration = start.duration(to: .now).components
                let seconds = Double(duration.seconds) + Double(duration.attoseconds) / 1e18
                message = "Connected · \(String(format: "%.1f", seconds)) s"; isError = false
            } catch {
                if !Task.isCancelled { message = error.localizedDescription; isError = true }
            }
        }
    }
}
