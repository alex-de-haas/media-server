using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MediaServer.Api.Data;

/// <summary>
/// Stores every <see cref="DateTimeOffset"/> as a normalized UTC ISO-8601 string
/// (<c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>). SQLite has no native date type and the EF Core SQLite
/// provider refuses to translate <see cref="DateTimeOffset"/> ordering/comparison, which previously
/// forced every call site to materialize and sort client-side. Because this representation is
/// fixed-width, UTC-normalized, and zero-padded, lexical TEXT ordering equals chronological ordering,
/// so EF can emit <c>ORDER BY</c> / <c>WHERE</c> against the column directly.
///
/// All timestamps in this app are written as <see cref="DateTimeOffset.UtcNow"/>, so normalizing the
/// stored value to UTC is lossless. The converter is registered globally for both
/// <see cref="DateTimeOffset"/> and <see cref="Nullable{DateTimeOffset}"/> in
/// <c>MediaServerDbContext.ConfigureConventions</c>.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, string>
{
    // Fixed-width on purpose: a constant 7-digit fraction and a literal 'Z' keep TEXT sort order
    // aligned with chronological order. Changing the width would silently break that invariant.
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";

    public UtcDateTimeOffsetConverter()
        : base(
            value => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
            value => DateTimeOffset.ParseExact(
                value,
                Format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))
    {
    }
}
