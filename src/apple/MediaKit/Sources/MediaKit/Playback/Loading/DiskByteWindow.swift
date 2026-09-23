import Foundation

/// Queue-confined storage. Disk and memory share range semantics, not allocation behavior.
protocol LoaderByteWindow {
    var start: Int64 { get }
    var count: Int { get }
    var budget: Int { get }
    func read(from offset: Int64, upTo limit: Int) throws -> Data?
    mutating func append(_ chunk: Data) throws
    mutating func trim(keepingFrom offset: Int64) throws
    mutating func restart(at offset: Int64) throws
}

extension ByteWindow: LoaderByteWindow {}

extension LoaderByteWindow {
    var end: Int64 { start + Int64(count) }
    var room: Int { max(0, budget - count) }
    func holds(_ offset: Int64) -> Bool { offset >= start && offset < end }

    func place(_ offset: Int64, lag: Int64) -> ByteWindow.Placement {
        if holds(offset) { return .held }
        // A larger cache must not turn a distant seek into waiting for gigabytes of sequential I/O.
        if offset >= end && offset < end + Int64(min(budget, 128 << 20)) { return .ahead }
        if offset < start && offset >= start - lag { return .behind }
        return .away
    }
}

/// A contiguous byte interval backed by removable blocks. Handles and metadata stay small;
/// the movie is never mapped or read into one large Data. Owned by the loader's serial queue.
final class DiskByteWindow: LoaderByteWindow {
    static let maximumBudget = 6 << 30
    static let spaceReserve: Int64 = 2 << 30
    private static let productionRoot: URL = {
        let fm = FileManager.default
        let root = fm.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MediaServerPlayback", isDirectory: true)
        // Runs once per process, before any session in this process is created.
        if let children = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) {
            for child in children where child.lastPathComponent.hasPrefix("session-") {
                try? fm.removeItem(at: child)
            }
        }
        return root
    }()

    enum Failure: Error { case insufficientSpace, missingBytes, capacityExceeded }

    private(set) var start: Int64
    private(set) var count = 0
    let budget: Int
    let directory: URL
    private let blockSize: Int
    private let reserve: Int64
    private let available: (URL) throws -> Int64
    private var blocks: Set<Int64> = []
    private var writer: (index: Int64, handle: FileHandle)?
    private var reader: (index: Int64, handle: FileHandle)?
    private var bytesSinceSpaceCheck = 32 << 20

    init(start: Int64 = 0, budget: Int = maximumBudget, root: URL? = nil,
         blockSize: Int = 8 << 20, reserve: Int64 = spaceReserve,
         available: @escaping (URL) throws -> Int64 = DiskByteWindow.freeSpace) throws {
        precondition(budget > 0 && blockSize > 0 && start >= 0)
        let root = root ?? Self.productionRoot
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let free = try available(root)
        // Allow for partially occupied first/last blocks as well as the free-space reserve.
        let usable = max(0, free - reserve - Int64(blockSize) * 2)
        guard usable >= Int64(min(budget, 128 << 20)) else { throw Failure.insufficientSpace }
        self.budget = Int(min(Int64(budget), usable))
        self.start = start
        self.blockSize = blockSize
        self.reserve = reserve
        self.available = available
        self.directory = root.appendingPathComponent("session-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
    }

    deinit {
        try? writer?.handle.close()
        try? reader?.handle.close()
        try? FileManager.default.removeItem(at: directory)
    }

    static func freeSpace(at url: URL) throws -> Int64 {
        // Available now, without assuming the OS can purge other apps' data for us.
        let values = try url.resourceValues(forKeys: [.volumeAvailableCapacityKey])
        guard let capacity = values.volumeAvailableCapacity else { throw Failure.insufficientSpace }
        return Int64(capacity)
    }

    private func file(_ index: Int64) -> URL {
        directory.appendingPathComponent("\(index).bytes")
    }

    func append(_ chunk: Data) throws {
        guard chunk.count <= room else { throw Failure.capacityExceeded }
        if bytesSinceSpaceCheck >= 32 << 20 {
            guard try available(directory) - Int64(chunk.count) >= reserve else {
                throw Failure.insufficientSpace
            }
            bytesSinceSpaceCheck = 0
        }
        var consumed = 0
        while consumed < chunk.count {
            let offset = end
            let index = offset / Int64(blockSize)
            let within = Int(offset % Int64(blockSize))
            if writer?.index != index {
                try writer?.handle.close()
                writer = nil
                if !blocks.contains(index) {
                    guard FileManager.default.createFile(atPath: file(index).path, contents: nil) else {
                        throw CocoaError(.fileWriteUnknown)
                    }
                    blocks.insert(index)
                }
                writer = (index, try FileHandle(forWritingTo: file(index)))
            }
            let amount = min(chunk.count - consumed, blockSize - within)
            try writer!.handle.seek(toOffset: UInt64(within))
            let from = chunk.startIndex + consumed
            try writer!.handle.write(contentsOf: chunk[from ..< from + amount])
            // Publish only successfully written bytes.
            count += amount
            consumed += amount
            bytesSinceSpaceCheck += amount
        }
    }

    func read(from offset: Int64, upTo limit: Int) throws -> Data? {
        guard holds(offset), limit > 0 else { return nil }
        let amount = min(limit, Int(end - offset))
        var result = Data(capacity: amount)
        while result.count < amount {
            let at = offset + Int64(result.count)
            let index = at / Int64(blockSize)
            let within = Int(at % Int64(blockSize))
            if reader?.index != index {
                try reader?.handle.close()
                reader = nil
                reader = (index, try FileHandle(forReadingFrom: file(index)))
            }
            try reader!.handle.seek(toOffset: UInt64(within))
            let take = min(amount - result.count, blockSize - within)
            guard let bytes = try reader!.handle.read(upToCount: take), bytes.count == take else {
                throw Failure.missingBytes
            }
            result.append(bytes)
        }
        return result
    }

    func trim(keepingFrom offset: Int64) throws {
        let next = min(end, max(start, offset))
        for index in blocks where (index + 1) * Int64(blockSize) <= next {
            if writer?.index == index { try writer?.handle.close(); writer = nil }
            if reader?.index == index { try reader?.handle.close(); reader = nil }
            try FileManager.default.removeItem(at: file(index))
            blocks.remove(index)
        }
        count -= Int(next - start)
        start = next
    }

    func restart(at offset: Int64) throws {
        try writer?.handle.close()
        writer = nil
        try reader?.handle.close()
        reader = nil
        for index in blocks { try FileManager.default.removeItem(at: file(index)) }
        blocks.removeAll()
        start = offset
        count = 0
    }
}
