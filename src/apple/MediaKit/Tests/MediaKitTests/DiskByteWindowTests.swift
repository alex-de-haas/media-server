import Foundation
import Testing

@testable import MediaKit

@Suite("Disk playback window")
struct DiskByteWindowTests {
    private func root() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    }
    private func payload(_ start: Int, _ count: Int) -> Data {
        Data((start ..< start + count).map { UInt8($0 % 251) })
    }

    @Test("Unaligned writes, cross-block reads and eviction preserve exact virtual offsets")
    func blockBoundaries() throws {
        let root = root()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = try DiskByteWindow(start: 11, budget: 48, root: root, blockSize: 16, reserve: 0)
        try cache.append(payload(11, 40))
        #expect(try cache.read(from: 13, upTo: 36) == payload(13, 36))
        try cache.trim(keepingFrom: 30)
        #expect(cache.start == 30)
        #expect(cache.count == 21)
        #expect(!FileManager.default.fileExists(atPath: cache.directory.appendingPathComponent("0.bytes").path))
        #expect(try cache.read(from: 29, upTo: 1) == nil)
        try cache.append(payload(51, 20))
        #expect(try cache.read(from: 30, upTo: 100) == payload(30, 41))
    }

    @Test("Restart and teardown remove old blocks without reusing their bytes")
    func cleanup() throws {
        let root = root()
        defer { try? FileManager.default.removeItem(at: root) }
        var cache: DiskByteWindow? = try DiskByteWindow(budget: 64, root: root, blockSize: 16, reserve: 0)
        let directory = cache!.directory
        try cache!.append(payload(0, 48))
        try cache!.restart(at: 103)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).isEmpty)
        try cache!.append(payload(103, 17))
        #expect(try cache!.read(from: 103, upTo: 100) == payload(103, 17))
        cache = nil
        #expect(!FileManager.default.fileExists(atPath: directory.path))
    }

    @Test("Truncated or missing blocks fail rather than returning plausible but incorrect bytes")
    func damagedFiles() throws {
        let root = root()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = try DiskByteWindow(budget: 64, root: root, blockSize: 16, reserve: 0)
        try cache.append(payload(0, 48))
        let handle = try FileHandle(forWritingTo: cache.directory.appendingPathComponent("0.bytes"))
        try handle.truncate(atOffset: 4)
        try handle.close()
        #expect(throws: (any Error).self) { try cache.read(from: 0, upTo: 8) }
        try FileManager.default.removeItem(at: cache.directory.appendingPathComponent("1.bytes"))
        #expect(throws: (any Error).self) { try cache.read(from: 16, upTo: 8) }
    }

    @Test("Space reserve lowers capacity and rejects insufficient storage")
    func capacity() throws {
        let root = root()
        defer { try? FileManager.default.removeItem(at: root) }
        #expect(throws: (any Error).self) {
            try DiskByteWindow(budget: 64, root: root, blockSize: 16, reserve: 100, available: { _ in 120 })
        }
        let cache = try DiskByteWindow(budget: 512 << 20, root: root, blockSize: 16,
                                      reserve: 100, available: { _ in (256 << 20) + 132 })
        #expect(cache.budget == 256 << 20)
    }

    @Test("A session that loses free space fails its next space check")
    func spaceDisappears() throws {
        let root = root()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = try DiskByteWindow(budget: 64, root: root, blockSize: 16, reserve: 100,
                                      available: { url in url == root ? 1_000 : 100 })
        #expect(throws: (any Error).self) { try cache.append(Data(repeating: 1, count: 16)) }
        #expect(cache.count == 0)
    }

    @Test("Capacity is enforced and a large disk budget does not disguise distant seeks")
    func bounds() throws {
        let root = root()
        defer { try? FileManager.default.removeItem(at: root) }
        let cache = try DiskByteWindow(budget: 64, root: root, blockSize: 16, reserve: 0)
        #expect(throws: (any Error).self) { try cache.append(Data(count: 65)) }
        #expect(cache.count == 0)
        try cache.append(Data(count: 64))
        try cache.trim(keepingFrom: 1_000)
        #expect(cache.start == 64)
        #expect(cache.count == 0)
        let large: any LoaderByteWindow = try DiskByteWindow(budget: 1 << 30, root: root, reserve: 0)
        #expect(large.place(512 << 20, lag: 0) == .away)
    }
}
