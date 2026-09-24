using MediaServer.Api.Data;

namespace MediaServer.Api.Organizer;

/// <summary>The result of moving one source file into the canonical catalog layout.</summary>
public sealed record OrganizedFile(Guid SourceFileId, Guid MediaItemId, string LibraryRelativePath, string AbsolutePath);

/// <summary>
/// Places confirmed playable files in the canonical layout. Retained torrents use independent copies;
/// other imports move their files. Download retention owns cleanup after all required processing.
/// </summary>
public interface IOrganizer
{
    Task<IReadOnlyList<OrganizedFile>> OrganizeAsync(
        IReadOnlyList<SourceFile> sourceFiles, Catalog catalog, CancellationToken cancellationToken);
}
