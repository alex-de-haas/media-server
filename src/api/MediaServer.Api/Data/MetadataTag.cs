namespace MediaServer.Api.Data;

/// <summary>What a tag describes, which is also what a caller may filter on.</summary>
public enum MetadataTagKind
{
    /// <summary>A genre as the provider names it ("Comedy", "Action").</summary>
    Genre = 0,

    /// <summary>
    /// A provider keyword — the specific thing a title is *about* ("aircraft hijacking", "heist").
    /// </summary>
    /// <remarks>
    /// This is the difference between answering "something about a plane hijacking" with a tag match
    /// and guessing against the prose of a synopsis. TMDb caps what is stored per title, so absence of
    /// a keyword is weak evidence — it means "not among the ones kept", never "not about that".
    /// </remarks>
    Keyword = 1,
}

/// <summary>
/// One searchable tag belonging to a metadata record.
/// </summary>
/// <remarks>
/// Genres live on <see cref="MetadataRecord.Genres"/> as a converted JSON list and keywords live
/// inside its <see cref="MetadataRecord.Raw"/> payload; neither can be filtered on in SQL, so neither
/// could answer a search. This table is the queryable projection of both, rebuilt whenever the record
/// it belongs to is written.
///
/// Keyed to the metadata record rather than the item, so a title carries its tags per language and a
/// search matches any of them — someone who knows a film's English genre should find it in a
/// Russian-language library.
/// </remarks>
public sealed class MetadataTag
{
    public Guid Id { get; set; }

    public Guid MetadataRecordId { get; set; }

    public MetadataTagKind Kind { get; set; }

    public required string Value { get; set; }

    public MetadataRecord? MetadataRecord { get; set; }
}
