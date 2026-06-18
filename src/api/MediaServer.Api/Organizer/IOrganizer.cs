using MediaServer.Api.Data;

namespace MediaServer.Api.Organizer;

/// <summary>The result of hardlinking one source file into the clean library layout.</summary>
public sealed record OrganizedFile(Guid SourceFileId, Guid MediaItemId, string LibraryRelativePath, string AbsolutePath);

/// <summary>
/// Hardlinks confirmed playable source files from <c>files/</c> into the catalog's clean
/// <c>library/</c> layout (zero copy, same filesystem), and unlinks the <c>files/</c> seed copy when
/// seeding stops. See <c>docs/planning/torrents-and-organizer.md</c>.
/// </summary>
public interface IOrganizer
{
    Task<IReadOnlyList<OrganizedFile>> OrganizeAsync(
        Download download, IReadOnlyList<SourceFile> sourceFiles, Catalog catalog, CancellationToken cancellationToken);

    /// <summary>Unlinks the <c>files/</c> seed copies; any <c>library/</c> hardlink keeps the data alive.</summary>
    Task UnlinkSeedCopyAsync(Download download, CancellationToken cancellationToken);
}
