import Carbon
import TranslationCore

@MainActor
final class GlobalShortcut {
    private var hotKey: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private var active: ShortcutConfiguration?
    private let identifier: UInt32
    var action: (() -> Void)?

    init(identifier: UInt32 = 1) { self.identifier = identifier }

    func register(_ configuration: ShortcutConfiguration) -> Bool {
        guard configuration.validationMessage == nil else { return false }
        if hotKey != nil && active == configuration { return true }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let context = Unmanaged.passUnretained(self).toOpaque()
        if handler == nil {
            let handlerStatus = InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
                guard let context, let event else { return OSStatus(eventNotHandledErr) }
                var eventID = EventHotKeyID()
                guard GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                        nil, MemoryLayout<EventHotKeyID>.size, nil, &eventID) == noErr else {
                    return OSStatus(eventNotHandledErr)
                }
                return MainActor.assumeIsolated {
                    let shortcut = Unmanaged<GlobalShortcut>.fromOpaque(context).takeUnretainedValue()
                    guard eventID.signature == 0x54726E73, eventID.id == shortcut.identifier else { return OSStatus(eventNotHandledErr) }
                    shortcut.action?()
                    return noErr
                }
            }, 1, &spec, context, &handler)
            guard handlerStatus == noErr else { return false }
        }
        let identifier = EventHotKeyID(signature: 0x54726E73, id: identifier)
        var modifiers: UInt32 = 0
        if configuration.command { modifiers |= UInt32(cmdKey) }
        if configuration.option { modifiers |= UInt32(optionKey) }
        if configuration.control { modifiers |= UInt32(controlKey) }
        if configuration.shift { modifiers |= UInt32(shiftKey) }
        var candidate: EventHotKeyRef?
        guard RegisterEventHotKey(configuration.keyCode, modifiers, identifier,
                                  GetApplicationEventTarget(), OptionBits(kEventHotKeyExclusive), &candidate) == noErr else { return false }
        // Acquire the new combination before releasing the working one.
        if let hotKey { UnregisterEventHotKey(hotKey) }
        hotKey = candidate
        active = configuration
        return true
    }

    func unregister() {
        if let hotKey { UnregisterEventHotKey(hotKey) }
        if let handler { RemoveEventHandler(handler) }
        hotKey = nil
        handler = nil
        active = nil
    }
}
