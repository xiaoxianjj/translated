import Foundation

public enum ClipboardReplacementPolicy {
    public static func mayReplace(originalText: String, currentText: String,
                                  originalProcess: Int32, currentProcess: Int32,
                                  originalGeneration: UInt64, currentGeneration: UInt64) -> Bool {
        !originalText.isEmpty && originalText == currentText && originalProcess == currentProcess &&
            originalGeneration == currentGeneration
    }

    public static func mayRestore(ownedChangeCount: Int?, currentChangeCount: Int) -> Bool {
        guard let ownedChangeCount else { return false }
        return ownedChangeCount == currentChangeCount
    }
}
