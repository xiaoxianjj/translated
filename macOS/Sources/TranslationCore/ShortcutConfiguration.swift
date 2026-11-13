import Foundation

public struct ShortcutConfiguration: Codable, Equatable, Sendable {
    public var keyCode: UInt32
    public var command: Bool
    public var option: Bool
    public var control: Bool
    public var shift: Bool

    public init(keyCode: UInt32, command: Bool = false, option: Bool = false, control: Bool = false, shift: Bool = false) {
        self.keyCode = keyCode
        self.command = command
        self.option = option
        self.control = control
        self.shift = shift
    }

    public static let standard = Self(keyCode: 17, option: true, control: true)
    public static let keys: [(code: UInt32, label: String)] = [
        (0,"A"),(11,"B"),(8,"C"),(2,"D"),(14,"E"),(3,"F"),(5,"G"),(4,"H"),(34,"I"),
        (38,"J"),(40,"K"),(37,"L"),(46,"M"),(45,"N"),(31,"O"),(35,"P"),(12,"Q"),
        (15,"R"),(1,"S"),(17,"T"),(32,"U"),(9,"V"),(13,"W"),(7,"X"),(16,"Y"),(6,"Z"),
        (18,"1"),(19,"2"),(20,"3"),(21,"4"),(23,"5"),(22,"6"),(26,"7"),(28,"8"),(25,"9"),(29,"0"),
        (49,"Space")
    ]
    public var keyLabel: String { Self.keys.first { $0.code == keyCode }?.label ?? "?" }
    public var display: String {
        (control ? "⌃" : "") + (option ? "⌥" : "") + (shift ? "⇧" : "") + (command ? "⌘" : "") + keyLabel
    }
    public var validationMessage: String? {
        guard Self.keys.contains(where: { $0.code == keyCode }) else { return "Choose a letter, number, or Space." }
        guard command || option || control else { return "Include Command, Option, or Control." }
        if command && !control && !option && !shift && [0,6,7,8,9,12,13,46,4].contains(keyCode) {
            return "Choose a combination that keeps common editing shortcuts available."
        }
        if keyCode == 49 && ((command && !control && !option) || (control && !command && !option)) {
            return "This combination is commonly used by macOS. Add another modifier."
        }
        return nil
    }
}
