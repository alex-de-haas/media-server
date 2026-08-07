using MediaServer.Api.Data;

namespace MediaServer.Api.Sidecars;

/// <summary>
/// Where an external <see cref="MediaStream"/>'s index comes from.
/// <para>
/// External streams are not part of the container's own numbering, so they start past it rather than
/// colliding with an embedded track — and past whatever the source already carries, so a re-drive, a later
/// release adding more tracks, or a track extracted out of the container never reuses an index a client is
/// already selecting on.
/// </para>
/// <para>
/// Shared rather than inlined because two places now create external rows — ingest placing a release's
/// companion files, and extraction writing a container's own tracks out beside it. Two copies of a rule about
/// index collisions would eventually disagree, and the failure would be a client silently playing the wrong
/// track.
/// </para>
/// </summary>
public static class ExternalStreamIndex
{
    /// <summary>The first index an external stream may take. Well past any container's own numbering.</summary>
    public const int First = 1000;

    /// <summary>The next free index for a source, given the external streams it already has.</summary>
    public static int NextFor(IReadOnlyCollection<MediaStream> existingExternal) =>
        existingExternal.Count == 0
            ? First
            : Math.Max(First, existingExternal.Max(stream => stream.Index) + 1);
}
