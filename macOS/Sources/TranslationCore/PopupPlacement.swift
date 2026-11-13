import Foundation
import CoreGraphics

public enum PopupPlacement {
    /// The visible frame already excludes the menu bar and any reserved Dock area.
    public static func statusFrame(size: CGSize, visibleFrame: CGRect) -> CGRect {
        let width = min(size.width, visibleFrame.width)
        let height = min(size.height, visibleFrame.height)
        return CGRect(x: visibleFrame.midX - width / 2,
                      y: min(visibleFrame.minY + 12, visibleFrame.maxY - height),
                      width: width, height: height)
    }

    /// AppKit coordinates: the origin is at the bottom left of the primary screen.
    public static func frame(size: CGSize, anchor: CGPoint, screen: CGRect, gap: CGFloat = 10) -> CGRect {
        let margin: CGFloat = 10
        let width = min(size.width, screen.width - margin * 2)
        let height = min(size.height, screen.height - margin * 2)
        let x = min(max(anchor.x, screen.minX + margin), screen.maxX - width - margin)
        let below = anchor.y - gap - height
        let proposedY = below >= screen.minY + margin ? below : anchor.y + gap
        let y = min(max(proposedY, screen.minY + margin), screen.maxY - height - margin)
        return CGRect(x: x, y: y, width: width, height: height)
    }
}
