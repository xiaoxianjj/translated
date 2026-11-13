import SwiftUI
@preconcurrency import Translation
import TranslationCore

enum AppStyle {
    static let settingsHeading = Font.system(size: 15, weight: .semibold, design: .rounded)
    static let ink = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            ? NSColor(red: 0.91, green: 0.94, blue: 0.88, alpha: 1)
            : NSColor(red: 0.16, green: 0.25, blue: 0.22, alpha: 1)
    })
    static let paper = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            ? NSColor(red: 0.13, green: 0.17, blue: 0.16, alpha: 1)
            : NSColor(red: 0.99, green: 0.97, blue: 0.93, alpha: 1)
    })
    static let peach = Color(red: 1, green: 0.70, blue: 0.51)
    static let forest = Color(red: 0.16, green: 0.30, blue: 0.25)
    static let softGreen = Color(red: 0.80, green: 0.87, blue: 0.72)
}

struct PeachButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold, design: .rounded))
            .padding(.horizontal, 16).padding(.vertical, 10)
            .foregroundStyle(AppStyle.forest)
            .background(AppStyle.peach.opacity(configuration.isPressed ? 0.72 : 1), in: RoundedRectangle(cornerRadius: 12))
            .contentShape(RoundedRectangle(cornerRadius: 12))
    }
}

struct Wordmark: View {
    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 13).fill(AppStyle.softGreen).frame(width: 36, height: 40)
                    .rotationEffect(.degrees(-12)).offset(x: -10, y: -2)
                RoundedRectangle(cornerRadius: 13).fill(AppStyle.peach).frame(width: 36, height: 40)
                    .rotationEffect(.degrees(9)).offset(x: 10, y: 3)
                HStack(spacing: 8) {
                    Text("A").font(.system(size: 20, weight: .bold, design: .rounded)).offset(y: -2)
                    Text("文").font(.system(size: 19, weight: .semibold)).offset(y: 3)
                }.foregroundStyle(AppStyle.forest)
            }.frame(width: 62, height: 52).accessibilityHidden(true)
            Text("Translated").font(.system(size: 27, weight: .bold, design: .rounded)).tracking(-0.8)
        }
    }
}

struct QuickButtonView: View {
    let translate: () -> Void

    var body: some View {
        Button(action: translate) {
            HStack(spacing: 8) {
                Image(systemName: "character.bubble.fill").font(.system(size: 15))
                Text("Translate").font(.system(size: 13, weight: .semibold, design: .rounded))
            }
            .padding(.horizontal, 13).frame(height: 38).contentShape(Capsule())
            .foregroundStyle(AppStyle.forest).background(AppStyle.peach, in: Capsule())
            .overlay(Capsule().strokeBorder(.white.opacity(0.45), lineWidth: 1))
        }
        .buttonStyle(.plain).accessibilityLabel("Translate")
        .help("Translate selected text")
        .padding(4)
    }
}

enum SelectionTranslationStatus {
    case translating, succeeded, copied, failed

    var label: String {
        switch self {
        case .translating: "Translating"
        case .succeeded, .copied: "Translated"
        case .failed: "Translation failed"
        }
    }
}

@MainActor
final class SelectionStatusPresentation: ObservableObject {
    @Published var status: SelectionTranslationStatus = .translating
}

struct SelectionStatusView: View {
    @ObservedObject var presentation: SelectionStatusPresentation
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private let workingColors = [Color(red: 0.75, green: 0.68, blue: 1), Color(red: 1, green: 0.75, blue: 0.85)]
    private let finishedColors = [Color(red: 0.48, green: 0.90, blue: 0.82), Color(red: 0.82, green: 0.96, blue: 0.66)]

    var body: some View {
        let status = presentation.status
        let busy = status == .translating
        let ink = busy ? Color(red: 0.20, green: 0.10, blue: 0.34) : AppStyle.forest
        Group {
            if status == .failed {
                Label(status.label, systemImage: "exclamationmark.circle.fill")
                    .font(AppStyle.settingsHeading)
                    .foregroundStyle(Color(red: 0.40, green: 0.13, blue: 0.10))
            } else {
                translationLabel(busy: busy, ink: ink)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background {
            ZStack {
                Capsule().fill(LinearGradient(colors: workingColors, startPoint: .leading, endPoint: .trailing))
                Capsule().fill(LinearGradient(colors: finishedColors, startPoint: .leading, endPoint: .trailing))
                    .opacity(busy ? 0 : 1)
                if status == .failed { Capsule().fill(AppStyle.peach) }
            }
        }
        .overlay(alignment: .trailing) {
            Image(systemName: "doc.on.clipboard").font(.system(size: 11, weight: .medium))
                .foregroundStyle(AppStyle.forest).padding(.trailing, 13)
                .opacity(status == .copied ? 1 : 0)
        }
        .overlay(Capsule().strokeBorder(.white.opacity(0.55)))
        .padding(4)
        .animation(reduceMotion || busy ? nil : .timingCurve(0.22, 0.1, 0.28, 1, duration: 0.36), value: status)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(status == .copied ? "Translated and copied" : status.label)
    }

    private func translationLabel(busy: Bool, ink: Color) -> some View {
        HStack(spacing: 8) {
            ZStack {
                if busy {
                    TimelineView(.animation(minimumInterval: 1.0 / 60, paused: reduceMotion)) { tick in
                        let turn = tick.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 0.9) / 0.9
                        ZStack {
                            Circle().stroke(ink.opacity(0.16), lineWidth: 2)
                            Circle().trim(from: 0, to: 0.65)
                                .stroke(ink, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                                .rotationEffect(.degrees(reduceMotion ? -90 : turn * 360))
                        }
                    }
                    .frame(width: 14, height: 14)
                    .transition(.opacity)
                }
                Path { path in
                    path.move(to: CGPoint(x: 1, y: 7))
                    path.addLine(to: CGPoint(x: 5.5, y: 11.5))
                    path.addLine(to: CGPoint(x: 15, y: 2))
                }
                .trim(from: 0, to: busy ? 0 : 1)
                .stroke(AppStyle.forest, style: StrokeStyle(lineWidth: 2.4, lineCap: .round, lineJoin: .round))
                .opacity(busy ? 0 : 1)
                .frame(width: 16, height: 14)
            }
            .frame(width: 24, height: 24)
            .background(.white.opacity(0.48), in: Circle())
            HStack(spacing: 0) {
                Text(verbatim: "Translat")
                ZStack(alignment: .leading) {
                    Text(verbatim: "ing").offset(y: busy ? 0 : -20).opacity(busy ? 1 : 0)
                    Text(verbatim: "ed").offset(y: busy ? 20 : 0).opacity(busy ? 0 : 1)
                }
                .clipped()
            }
            .font(AppStyle.settingsHeading)
        }
        .foregroundStyle(ink)
    }
}

enum TranslationOutcome {
    case delivered(SelectionDelivery)
    case failed
}

struct TranslationRequest: Identifiable {
    let id = UUID()
    let text: String
    let route: TranslationRoute
    let selection: CapturedSelection?
    var delivery: SelectionDelivery = .preview
    var expectedClipboardChangeCount: Int = 0
    var translatedText: String? = nil
    var apiConfiguration: LLMConfiguration? = nil
    var highQuality = true
    let replacementPermit: ReplacementPermit
}

@MainActor
final class TranslationPresentation: ObservableObject {
    @Published var request: TranslationRequest?
    @Published var message: String?
    var close: (() -> Void)?
    var openWorkspace: (() -> Void)?
    var didFinish: ((UUID, TranslationOutcome) -> Void)?
    var retry: ((String, String?) -> Void)?
}

struct TranslationPanelView: View {
    @ObservedObject var presentation: TranslationPresentation

    var body: some View {
        Group {
            if let request = presentation.request {
                TranslationContentView(request: request, close: { presentation.close?() },
                                       didFinish: { presentation.didFinish?(request.id, $0) },
                                       retry: { presentation.retry?(request.text, $0) })
                    .id(request.id)
            } else {
                VStack(alignment: .leading, spacing: 18) {
                    HStack {
                        Label("Translated", systemImage: "character.bubble.fill").font(.headline)
                        Spacer()
                        closeButton { presentation.close?() }
                    }
                    Spacer()
                    Text(presentation.message ?? "Select some text to get started.")
                        .font(.system(size: 18, weight: .medium, design: .rounded))
                    Button("Open Translated") { presentation.openWorkspace?() }.buttonStyle(PeachButtonStyle())
                    Spacer()
                }.padding(22)
            }
        }
        .frame(width: 440, height: 360)
        .foregroundStyle(AppStyle.ink)
        .background(AppStyle.paper)
        .clipShape(RoundedRectangle(cornerRadius: 20))
        .overlay(RoundedRectangle(cornerRadius: 20).strokeBorder(AppStyle.ink.opacity(0.12)))
    }
}

private enum TranslationPhase {
    case preparing
    case translating
    case finished(String)
    case failed(String)
}

struct TranslationContentView: View {
    let request: TranslationRequest
    let close: () -> Void
    let didFinish: (TranslationOutcome) -> Void
    let retry: (String?) -> Void
    @State private var phase: TranslationPhase = .preparing
    @State private var copied = false
    @State private var replacementResult: ReplacementResult?
    @State private var progressText = "Translating…"

    init(request: TranslationRequest, close: @escaping () -> Void, didFinish: @escaping (TranslationOutcome) -> Void, retry: @escaping (String?) -> Void) {
        self.request = request
        self.close = close
        self.didFinish = didFinish
        self.retry = retry
        _phase = State(initialValue: request.translatedText.map { .finished($0) } ?? .preparing)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 9) {
                Menu {
                    Button("Auto-detect") { retry(nil) }
                    Divider()
                    Button("English") { retry("en") }
                    Button("Chinese (Simplified)") { retry("zh-Hans") }
                    Button("Chinese (Traditional)") { retry("zh-Hant") }
                    Button("Japanese") { retry("ja") }
                    Button("Korean") { retry("ko") }
                    Button("French") { retry("fr") }
                    Button("German") { retry("de") }
                    Button("Spanish") { retry("es") }
                } label: {
                    Text(request.route.sourceName).font(.system(size: 12, weight: .semibold))
                }
                .menuStyle(.borderlessButton).fixedSize().help("Change the detected language")
                Image(systemName: "arrow.right").font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
                Text(request.route.targetNativeName).font(.system(size: 12, weight: .semibold))
                    .padding(.horizontal, 9).padding(.vertical, 5)
                    .foregroundStyle(AppStyle.forest).background(AppStyle.softGreen, in: Capsule())
                Spacer(minLength: 4)
                closeButton(action: close)
            }
            .padding(.bottom, 18)

            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    switch phase {
                    case .preparing, .translating:
                        HStack(spacing: 10) {
                            ProgressView().controlSize(.small)
                            Text(loadingLabel).font(.system(size: 14, weight: .medium))
                        }.padding(.vertical, 22)
                    case .finished(let text):
                        Text(text)
                            .font(.system(size: 21, weight: .medium, design: .rounded))
                            .lineSpacing(6).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .accessibilityLabel("Translation: \(text)")
                    case .failed(let message):
                        VStack(alignment: .leading, spacing: 10) {
                            Label("Couldn’t translate", systemImage: "exclamationmark.bubble")
                                .font(.system(size: 17, weight: .semibold, design: .rounded))
                            Text(message).font(.system(size: 13)).foregroundStyle(.secondary)
                            Button("Try again") { retry(request.route.sourceCode) }.buttonStyle(PeachButtonStyle())
                        }.padding(.vertical, 10)
                    }
                }.padding(.bottom, 12)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            HStack {
                Label(replacementResult?.label ?? request.apiConfiguration?.provider.title ?? "On this Mac", systemImage: replacementIcon)
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                    .help(request.apiConfiguration == nil ? "Apple translates on your Mac." : "Translated using your configured API provider.")
                Spacer()
                if case .finished(let text) = phase {
                    Button {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(text, forType: .string)
                        copied = true
                    } label: {
                        Label(copied ? "Copied" : "Copy", systemImage: copied ? "checkmark" : "doc.on.doc")
                    }
                    .buttonStyle(PeachButtonStyle())
                    .accessibilityLabel(copied ? "Translation copied" : "Copy translation")
                }
            }.padding(.top, 14)
        }
        .padding(22)
        .onDisappear { request.replacementPermit.cancel() }
        .translationTask(request.apiConfiguration == nil && request.translatedText == nil
                         ? AppleTranslationEngine.configuration(route: request.route, highQuality: request.highQuality) : nil) { session in
            await translate(using: session)
        }
        .task {
            guard request.translatedText == nil, let configuration = request.apiConfiguration else { return }
            phase = .translating
            do {
                let translated = try await LLMTranslationEngine.translate(text: request.text, route: request.route, configuration: configuration) { part, total in
                    await MainActor.run { progressText = "Translating \(part)/\(total)…" }
                }
                try Task.checkCancellation()
                await finish(translated)
            } catch {
                if !Task.isCancelled { fail(error.localizedDescription) }
            }
        }
    }

    private var loadingLabel: String {
        if case .preparing = phase { return "Preparing languages…" }
        return progressText
    }

    private var replacementIcon: String {
        if case .replaced = replacementResult { return "checkmark.circle.fill" }
        return replacementResult == nil ? (request.apiConfiguration == nil ? "lock.shield" : "sparkles") : "text.cursor"
    }

    @MainActor
    private func translate(using session: TranslationSession) async {
        guard request.translatedText == nil else { return }
        do {
            if let source = request.route.sourceCode {
                let availability = await AppleTranslationEngine.availability(highQuality: request.highQuality).status(
                    from: Locale.Language(identifier: source), to: Locale.Language(identifier: request.route.targetCode))
                try Task.checkCancellation()
                guard availability != .unsupported else {
                    fail("This language isn’t supported by Apple Translation. You can change the detected language above.")
                    return
                }
            }
            try await session.prepareTranslation()
            try Task.checkCancellation()
            phase = .translating
            let translated = try await AppleTranslationEngine.translate(text: request.text, route: request.route, session: session) { part, total in
                progressText = "Translating \(part)/\(total)…"
            }
            try Task.checkCancellation()
            await finish(translated)
        } catch is CancellationError {
            // A system-language prompt can be cancelled while its result panel is still open.
            if !Task.isCancelled { fail("Translation cancelled.") }
        } catch {
            guard !Task.isCancelled else { return }
            fail(error.localizedDescription + " If a language download was cancelled, try again while connected to the internet.")
        }
    }

    private func fail(_ message: String) {
        phase = .failed(message)
        didFinish(.failed)
    }

    @MainActor
    private func finish(_ translated: String) async {
        guard !Task.isCancelled else { return }
        phase = .finished(translated)
        switch request.delivery {
        case .preview:
            didFinish(.delivered(.preview))
        case .copy:
            let copied = TranslationClipboard.copy(translated, expectedChangeCount: request.expectedClipboardChangeCount,
                                                   permit: request.replacementPermit)
            didFinish(.delivered(copied ? .copy : .preview))
        case .replace:
            guard let selection = request.selection else { didFinish(.delivered(.preview)); return }
            let permit = request.replacementPermit
            replacementResult = await withTaskCancellationHandler {
                await SelectionReplacement.replace(selection, with: translated, permit: permit)
            } onCancel: {
                permit.cancel()
            }
            guard !Task.isCancelled else { return }
            if case .replaced = replacementResult { didFinish(.delivered(.replace)) }
            else if case .readOnly = replacementResult {
                let copied = TranslationClipboard.copy(translated, expectedChangeCount: NSPasteboard.general.changeCount, permit: permit)
                didFinish(.delivered(copied ? .copy : .preview))
            } else {
                didFinish(.delivered(.preview))
            }
        }
    }
}

@MainActor @ViewBuilder
func closeButton(action: @escaping () -> Void) -> some View {
    Button(action: action) {
        Image(systemName: "xmark").font(.system(size: 11, weight: .bold))
            .frame(width: 25, height: 25)
            .background(AppStyle.ink.opacity(0.07), in: Circle())
    }.buttonStyle(.plain).accessibilityLabel("Close translation").help("Close · Esc")
}
