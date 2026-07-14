using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MediaServer.Api.Data;

/// <summary>
/// Helpers that map flexible CLR shapes (string lists, provider dictionaries) to SQLite JSON1 TEXT
/// columns via System.Text.Json, with the matching <see cref="ValueComparer{T}"/> so EF change
/// tracking detects in-place mutations.
/// </summary>
public static class JsonColumnExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static PropertyBuilder<List<string>> HasJsonListConversion(this PropertyBuilder<List<string>> builder)
    {
        var converter = new ValueConverter<List<string>, string>(
            value => JsonSerializer.Serialize(value, SerializerOptions),
            json => Deserialize<List<string>>(json) ?? new List<string>());

        var comparer = new ValueComparer<List<string>>(
            (left, right) => left == null ? right == null : right != null && left.SequenceEqual(right),
            value => value == null ? 0 : value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
            value => value == null ? null! : value.ToList());

        builder.HasConversion(converter, comparer).HasColumnType("TEXT");
        return builder;
    }

    public static PropertyBuilder<Dictionary<string, string>> HasJsonDictionaryConversion(
        this PropertyBuilder<Dictionary<string, string>> builder)
    {
        var converter = new ValueConverter<Dictionary<string, string>, string>(
            value => JsonSerializer.Serialize(value, SerializerOptions),
            json => Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>());

        var comparer = new ValueComparer<Dictionary<string, string>>(
            (left, right) => left == null ? right == null : right != null && left.Count == right.Count && !left.Except(right).Any(),
            value => value == null ? 0 : value.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key.GetHashCode(), pair.Value.GetHashCode())),
            value => value == null ? null! : new Dictionary<string, string>(value));

        builder.HasConversion(converter, comparer).HasColumnType("TEXT");
        return builder;
    }

    /// <summary>Nullable int-list JSON column (e.g. monitored season numbers); null stays SQL NULL.</summary>
    public static PropertyBuilder<List<int>?> HasJsonIntListConversion(this PropertyBuilder<List<int>?> builder)
    {
        var converter = new ValueConverter<List<int>?, string?>(
            value => value == null ? null : JsonSerializer.Serialize(value, SerializerOptions),
            json => json == null ? null : Deserialize<List<int>>(json));

        var comparer = new ValueComparer<List<int>?>(
            (left, right) => left == null ? right == null : right != null && left.SequenceEqual(right),
            value => value == null ? 0 : value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
            value => value == null ? null : value.ToList());

        builder.HasConversion(converter, comparer).HasColumnType("TEXT");
        return builder;
    }

    private static T? Deserialize<T>(string json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, SerializerOptions);
}
