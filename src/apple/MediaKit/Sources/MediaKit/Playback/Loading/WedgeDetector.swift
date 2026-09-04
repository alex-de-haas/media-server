import Foundation

/// Tells a player that has stopped from one that is merely waiting.
///
/// At minute 22 of a 70 GB film the picture froze and never came back. The server saw eight seconds
/// without a request, the overlay saw an empty buffer and nothing arriving, and nothing recovered until
/// the viewer pressed pause and play. That is not starvation — starvation ends when bytes arrive — and
/// there is no setting for it, so the remedy is the viewer's, done for them.
///
/// The one thing that must not happen is doing it to a film that was fine. Three cases look alike from
/// a distance and are told apart here:
///
/// - **Starving**: the play head is still and nothing is being delivered, but we hold nothing ahead
///   either — the server is slow, and re-seating the player would change nothing.
/// - **Waiting on a slow feed**: the play head is still but bytes are still being handed over. It is
///   being fed; leave it.
/// - **Wedged**: the play head is still, nothing has been handed over, and we are *holding bytes the
///   player has not asked for*. It has stopped asking with the answer in hand.
///
/// Pure, so those three cases are tests rather than assertions.
public struct WedgeDetector: Sendable {
    public struct Reading: Sendable {
        public let position: Double
        public let delivered: Int64
        public let heldAhead: Int64
        public let paused: Bool

        public init(position: Double, delivered: Int64, heldAhead: Int64, paused: Bool) {
            self.position = position
            self.delivered = delivered
            self.heldAhead = heldAhead
            self.paused = paused
        }
    }

    /// How many consecutive still readings count as stuck. Generous on purpose: a false alarm
    /// interrupts a film that was fine, and the failure this is for lasts until somebody intervenes.
    public let patience: Int

    private var last: Reading?
    private var still = 0

    public init(patience: Int = 6) {
        self.patience = patience
    }

    /// Feed one reading a second. True on the reading that makes it stuck, after which the count
    /// begins again — so a remedy that does not work fires again `patience` seconds later.
    public mutating func observe(_ now: Reading) -> Bool {
        // A paused film is not a stuck one. Nothing is compared across a pause either: the first
        // reading after it is a fresh baseline, not a second of stillness — which is why this is
        // not a `defer`, since one would put the paused reading back as the baseline on the way out.
        guard !now.paused else {
            last = nil
            still = 0
            return false
        }

        guard let previous = last else {
            last = now
            return false
        }

        last = now

        let moved = now.position > previous.position + 0.02 || now.delivered > previous.delivered
        guard !moved, now.heldAhead > 0 else {
            still = 0
            return false
        }

        still += 1
        guard still >= patience else { return false }

        still = 0
        return true
    }

    /// The player was re-seated. Its next reading is a baseline, not a comparison against the item
    /// that was replaced.
    public mutating func restarted() {
        last = nil
        still = 0
    }
}
