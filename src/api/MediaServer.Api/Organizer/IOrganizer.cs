using MediaServer.Api.Data;

namespace MediaServer.Api.Organizer;

/// <summary>The result of moving one source file into the canonical catalog layout.</summary>
public sealed record OrganizedFile(Guid SourceFileId, Guid MediaItemId, string LibraryRelativePath, string AbsolutePath);

/// <summary>
/// Moves confirmed playable source files from their current location — a torrent's <c>.incoming/</c>
/// staging area, or wherever a scanned file already sits — into the catalog's canonical layout at the
/// catalog root, renaming per the confirmed metadata. A move within one filesystem is atomic and
/// zero-copy; there are no hardlinks. Emptied <c>.incoming/</c> staging folders are removed.
/// See <c>docs/features/torrents-and-organizer.md</c>.
/// </summary>
public interface IOrganizer
{
    Task<IReadOnlyList<OrganizedFile>> OrganizeAsync(
        IReadOnlyList<SourceFile> sourceFiles, Catalog catalog, CancellationToken cancellationToken);
}
