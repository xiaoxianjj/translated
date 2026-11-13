import AppKit

let destination = CommandLine.arguments[1]
try FileManager.default.createDirectory(atPath: destination, withIntermediateDirectories: true)
for size in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = size * scale
        let image = NSImage(size: NSSize(width: pixels, height: pixels))
        image.lockFocus()
        let context = NSGraphicsContext.current!.cgContext
        context.scaleBy(x: CGFloat(pixels) / 1024, y: CGFloat(pixels) / 1024)
        NSColor(red: 0.98, green: 0.95, blue: 0.88, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: 60, y: 60, width: 904, height: 904), xRadius: 205, yRadius: 205).fill()
        NSColor(red: 0.79, green: 0.86, blue: 0.70, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: 154, y: 334, width: 418, height: 450), xRadius: 112, yRadius: 112).fill()
        NSColor(red: 1, green: 0.69, blue: 0.49, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: 452, y: 230, width: 418, height: 450), xRadius: 112, yRadius: 112).fill()
        let ink = NSColor(red: 0.16, green: 0.30, blue: 0.25, alpha: 1)
        ("A" as NSString).draw(at: NSPoint(x: 245, y: 402), withAttributes: [.font: NSFont.systemFont(ofSize: 270, weight: .bold), .foregroundColor: ink])
        ("文" as NSString).draw(at: NSPoint(x: 522, y: 320), withAttributes: [.font: NSFont.systemFont(ofSize: 240, weight: .semibold), .foregroundColor: ink])
        image.unlockFocus()
        let representation = NSBitmapImageRep(data: image.tiffRepresentation!)!
        let data = representation.representation(using: .png, properties: [:])!
        let suffix = scale == 2 ? "@2x" : ""
        try data.write(to: URL(fileURLWithPath: "\(destination)/icon_\(size)x\(size)\(suffix).png"))
    }
}
