import Foundation

public enum ReplacementPolicy {
    /// Selection and focus must still match the user's original action.
    public static func mayReplace(originalText: String, currentText: String,
                                  originalRange: NSRange?, currentRange: NSRange?,
                                  sameApplication: Bool, sameElement: Bool) -> Bool {
        guard let originalRange, let currentRange else { return false }
        return sameApplication && sameElement && originalText == currentText &&
            originalRange == currentRange && !originalText.isEmpty
    }

    public static func preservingWhitespace(original: String, translation: String) -> String {
        let prefix = original.prefix(while: { $0.isWhitespace })
        let suffix = original.reversed().prefix(while: { $0.isWhitespace }).reversed()
        return String(prefix) + translation.trimmingCharacters(in: .whitespacesAndNewlines) + String(suffix)
    }
}

/// Cancellation and the final AX write share a lock, so a dismissed request cannot
/// begin a replacement later even if an accessibility read is still in flight.
public final class ReplacementPermit: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false

    public init() {}

    public var isActive: Bool {
        lock.lock()
        defer { lock.unlock() }
        return !cancelled
    }

    public func cancel() {
        lock.lock()
        defer { lock.unlock() }
        cancelled = true
    }

    public func performIfActive<T>(_ action: () -> T) -> T? {
        lock.lock()
        defer { lock.unlock() }
        guard !cancelled else { return nil }
        return action()
    }
}
