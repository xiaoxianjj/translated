import Foundation
import Testing
@testable import TranslationCore

private let selectedLine = CGRect(x: 20, y: 100, width: 80, height: 12)

@Test("Drags beginning or ending in a margin can still select words in either direction")
func dragsAcrossSelection() {
    let from = CGPoint(x: 10, y: 105), into = CGPoint(x: 75, y: 105), beyond = CGPoint(x: 120, y: 105)
    for (start, end) in [(from, into), (into, from), (from, beyond), (beyond, from)] {
        #expect(SelectionGesture(start: start, end: end).matches(bounds: [selectedLine], sameTextOwner: false))
    }
}

@Test("A word at the pointer matches when its web marker is the only matching rectangle")
func markerOnlyWordBounds() {
    let point = CGPoint(x: 50, y: 103)
    let click = SelectionGesture(start: point, end: point)
    let stale = CGRect(x: 400, y: 300, width: 60, height: 12)
    #expect(click.matches(bounds: [stale, selectedLine], sameTextOwner: false))
    #expect(!click.matches(bounds: [stale], sameTextOwner: true))
}

@Test("A stale selection inside the drag's outer box but off its actual path is excluded")
func unrelatedSelectionBounds() {
    let drag = SelectionGesture(start: .zero, end: CGPoint(x: 120, y: 120))
    #expect(!drag.matches(bounds: [CGRect(x: 10, y: 90, width: 15, height: 15)], sameTextOwner: true))
}

@Test("A selection without geometry must belong to the control under the pointer")
func requiresLocalSelectionOwner() {
    let click = SelectionGesture(start: .zero, end: .zero)
    #expect(!click.matches(bounds: [], sameTextOwner: false))
    #expect(click.matches(bounds: [], sameTextOwner: true))
}
