using System.Text.Json;
using MediaServer.Api.Data;
using CreditKey = (string ProviderId, MediaServer.Api.Data.PersonRole Role, string? Character, string? Job, string? Department);

namespace MediaServer.Api.People;

/// <summary>
/// A single parsed credit from a provider payload, provider-agnostic. Carries both the person attributes
/// (<see cref="ProviderId"/>, <see cref="Name"/>, …) and the per-item credit attributes (<see cref="Role"/>,
/// <see cref="Character"/>, …). The owning provider key is supplied by the caller.
/// </summary>
public sealed record PersonCredit(
    string ProviderId,
    string Name,
    string? ProfilePath,
    string? KnownForDepartment,
    PersonRole Role,
    string? Character,
    string? Job,
    string? Department,
    int Order);

/// <summary>
/// Pure parser for the <c>credits.cast</c>/<c>credits.crew</c> arrays inside a cached TMDb detail payload
/// (<see cref="MetadataRecord.Raw"/>). No database access, so it is straightforward to unit test.
/// </summary>
public static class PersonCredits
{
    private const string ProfileImageBase = "https://image.tmdb.org/t/p/original";

    /// <summary>
    /// Parses every cast then crew credit out of a raw TMDb detail payload. Entries without a usable id and
    /// name are skipped; exact-duplicate credits are collapsed. Returns empty for null/blank/invalid JSON.
    /// </summary>
    public static IReadOnlyList<PersonCredit> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var result = new List<PersonCredit>();
            // Dedupe on credit identity (ignoring the array-derived Order) so a person listed twice with the
            // same role/job — TMDb does this — collapses to a single credit keeping its first position.
            var seen = new HashSet<CreditKey>();

            AppendCast(credits, result, seen);
            AppendCrew(credits, result, seen);

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AppendCast(JsonElement credits, List<PersonCredit> result, HashSet<CreditKey> seen)
    {
        if (!credits.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var member in cast.EnumerateArray())
        {
            if (TryReadPerson(member, out var providerId, out var name))
            {
                var credit = new PersonCredit(
                    providerId,
                    name,
                    ProfilePath(member),
                    EmptyToNull(JsonString(member, "known_for_department")),
                    PersonRole.Cast,
                    Character: EmptyToNull(JsonString(member, "character")),
                    Job: null,
                    Department: null,
                    Order: JsonInt(member, "order") ?? index);
                Add(result, seen, credit);
            }

            index++;
        }
    }

    private static void AppendCrew(JsonElement credits, List<PersonCredit> result, HashSet<CreditKey> seen)
    {
        if (!credits.TryGetProperty("crew", out var crew) || crew.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var member in crew.EnumerateArray())
        {
            if (TryReadPerson(member, out var providerId, out var name))
            {
                var credit = new PersonCredit(
                    providerId,
                    name,
                    ProfilePath(member),
                    EmptyToNull(JsonString(member, "known_for_department")),
                    PersonRole.Crew,
                    Character: null,
                    Job: EmptyToNull(JsonString(member, "job")),
                    Department: EmptyToNull(JsonString(member, "department")),
                    Order: index);
                Add(result, seen, credit);
            }

            index++;
        }
    }

    private static void Add(List<PersonCredit> result, HashSet<CreditKey> seen, PersonCredit credit)
    {
        if (seen.Add((credit.ProviderId, credit.Role, credit.Character, credit.Job, credit.Department)))
        {
            result.Add(credit);
        }
    }

    private static bool TryReadPerson(JsonElement member, out string providerId, out string name)
    {
        providerId = string.Empty;
        name = string.Empty;
        if (member.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (JsonInt(member, "id") is not { } id)
        {
            return false;
        }

        if (EmptyToNull(JsonString(member, "name")) is not { } parsedName)
        {
            return false;
        }

        providerId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        name = parsedName;
        return true;
    }

    private static string? ProfilePath(JsonElement member) => EmptyToNull(JsonString(member, "profile_path"));

    /// <summary>Builds the absolute profile image URL from a raw provider path; null when there is none.</summary>
    public static string? ProfileUrl(string? profilePath) =>
        string.IsNullOrWhiteSpace(profilePath) ? null : ProfileImageBase + profilePath;

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? JsonInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
