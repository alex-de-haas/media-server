namespace MediaServer.Api.Library;

/// <summary>Separate version deletion must leave at least one source attached to its item.</summary>
public sealed class LastMediaSourceException() : Exception(
    "The last version cannot be removed separately. Delete the movie or episode instead.")
{
    /// <summary>The stable API error that directs callers to whole-item deletion.</summary>
    public const string ErrorCode = "last_media_source";
}
