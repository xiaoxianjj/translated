import Foundation

public struct SelectionGesture: Sendable {
    public let start: CGPoint
    public let end: CGPoint

    public init(start: CGPoint, end: CGPoint) {
        self.start = start
        self.end = end
    }

    /// If an app supplies geometry, compare it with the entire drag. Some web
    /// views supply bounds only for their text-marker range.
    public func matches(bounds: [CGRect], sameTextOwner: Bool) -> Bool {
        if bounds.isEmpty { return sameTextOwner }
        return bounds.contains { box in
            let rect = box.insetBy(dx: -3, dy: -3)
            var entry: CGFloat = 0
            var exit: CGFloat = 1
            for (origin, delta, lower, upper) in [
                (start.x, end.x - start.x, rect.minX, rect.maxX),
                (start.y, end.y - start.y, rect.minY, rect.maxY)
            ] {
                if delta == 0 {
                    if origin < lower || origin > upper { return false }
                } else {
                    let a = (lower - origin) / delta
                    let b = (upper - origin) / delta
                    entry = max(entry, min(a, b))
                    exit = min(exit, max(a, b))
                    if entry > exit { return false }
                }
            }
            return true
        }
    }
}
