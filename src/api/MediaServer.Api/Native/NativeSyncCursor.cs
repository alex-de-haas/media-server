using System.Buffers.Text;
using System.Text;

namespace MediaServer.Api.Native;

/// <summary>
/// Where a client is in the sync stream. Opaque to callers — it is a position we own, and clients that
/// parse it would pin the encoding — but deliberately legible to us when debugging.
///
/// A sync starts by paging a bounded snapshot of the library, then switches to the change log at the
/// sequence captured when that snapshot began. Capturing the watermark *first* is what makes the
/// hand-off lossless: anything that changes while the snapshot is still paging is behind the
/// watermark and gets replayed from the log.
/// </summary>
internal readonly record struct NativeSyncCursor(NativeSyncMode Mode, long Watermark, string Position)
{
    private const string Version = "1";

    public static NativeSyncCursor StartSnapshot(long watermark) =>
        new(NativeSyncMode.Snapshot, watermark, string.Empty);

    public static NativeSyncCursor Delta(long sequence) =>
        new(NativeSyncMode.Delta, sequence, string.Empty);

    public string Encode()
    {
        var mode = Mode == NativeSyncMode.Snapshot ? "s" : "d";
        var raw = $"{Version}|{mode}|{Watermark}|{Position}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string? cursor, out NativeSyncCursor result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = raw.Split('|');
        if (parts.Length != 4 || parts[0] != Version || !long.TryParse(parts[2], out var watermark))
        {
            return false;
        }

        var mode = parts[1] switch
        {
            "s" => NativeSyncMode.Snapshot,
            "d" => NativeSyncMode.Delta,
            _ => (NativeSyncMode?)null,
        };

        if (mode is null)
        {
            return false;
        }

        result = new NativeSyncCursor(mode.Value, watermark, parts[3]);
        return true;
    }
}

internal enum NativeSyncMode
{
    Snapshot,
    Delta,
}
