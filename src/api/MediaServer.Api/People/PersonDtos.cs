namespace MediaServer.Api.People;

// UI-facing DTOs for the internal `/api/persons` surface. Serialized camelCase by the global JSON options,
// mirroring the `/api/library` surface. A person is addressed by its stable provider identity
// (provider + providerId) — the same pair the library detail's CastMemberDto carries — so the UI links
// straight from a cast member to the person page.

/// <summary>
/// A person page: provider-identified details plus the person's filmography limited to titles in the
/// library, split into <see cref="Cast"/> and <see cref="Crew"/> (crew grouped by department).
/// </summary>
public sealed record PersonDetailDto(
    string Provider,
    string ProviderId,
    string Name,
    // Absolute, ready-to-render profile image URL, or null when the provider has no photo.
    string? ProfileUrl,
    // Long-form biography from the person-detail fetch; null when the provider has none.
    string? Biography,
    // The department the provider considers this person best known for, e.g. "Acting".
    string? KnownForDepartment,
    // Birth/death dates as the provider returns them (e.g. "1974-11-11"); null when unknown.
    string? Birthday,
    string? Deathday,
    string? PlaceOfBirth,
    // Acting credits within the library, newest first.
    IReadOnlyList<PersonFilmographyEntryDto> Cast,
    // Crew credits within the library, grouped by department (Directing, Writing, …).
    IReadOnlyList<PersonCrewGroupDto> Crew);

/// <summary>
/// One filmography entry: a movie or series in the library the person is credited on. <see cref="Id"/> is the
/// media item id and doubles as the navigation target (the UI routes to the library detail by it).
/// </summary>
public sealed record PersonFilmographyEntryDto(
    Guid Id,
    string Kind,
    string Title,
    int? Year,
    string? PosterUrl,
    // The portrayed character, for a cast credit; null for crew.
    string? Character,
    // The crew job (e.g. "Director"), for a crew credit; null for cast.
    string? Job);

/// <summary>A person's crew filmography for one department (e.g. "Directing"), entries newest first.</summary>
public sealed record PersonCrewGroupDto(
    string Department,
    IReadOnlyList<PersonFilmographyEntryDto> Credits);
