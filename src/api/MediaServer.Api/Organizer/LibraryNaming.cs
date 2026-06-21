using System.Text;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;

namespace MediaServer.Api.Organizer;

/// <summary>
/// Builds clean catalog-root-relative paths from confirmed identity, preserving the original extension
/// (the container is never changed — playback is Direct Play/Stream only). Movies use the catalog naming
/// template; series/anime use the Jellyfin <c>Show/Season NN/Show SxxEyy</c> layout. Published media
/// lives directly at the catalog root (no <c>library/</c> subtree). See <c>docs/features/catalogs.md</c>.
/// </summary>
public static class LibraryNaming
{
    /// <summary>
    /// Builds the canonical path (relative to the catalog root) for a movie file. <paramref name="edition"/>
    /// disambiguates alternate versions of one movie (e.g. "Black &amp; White") by appending a
    /// <c> - {edition}</c> suffix to the filename; the versions share one movie folder.
    /// </summary>
    public static string ForMovie(Catalog catalog, MediaItem movie, string extension, string? edition = null)
    {
        var baseName = RenderTemplate(catalog.NamingTemplate, movie.Title, movie.Year);
        var folder = Sanitize(baseName);
        return Combine(folder, Sanitize(baseName + EditionSuffix(edition)) + NormalizeExtension(extension));
    }

    /// <summary>
    /// Builds the canonical path for an episode file, given the owning series. <paramref name="edition"/>
    /// disambiguates alternate versions of one episode by appending a <c> - {edition}</c> suffix.
    /// </summary>
    public static string ForEpisode(MediaItem series, MediaItem episode, string extension, string? edition = null)
    {
        var showFolder = Sanitize(series.Year is { } year ? $"{series.Title} ({year})" : series.Title);
        var season = episode.ParentIndexNumber ?? 1;
        var episodeNumber = episode.IndexNumber ?? 0;

        var episodeToken = episode.IndexNumberEnd is { } end && end > episodeNumber
            ? $"E{episodeNumber:D2}-E{end:D2}"
            : $"E{episodeNumber:D2}";

        var fileName = Sanitize($"{series.Title} S{season:D2}{episodeToken}{EditionSuffix(edition)}") + NormalizeExtension(extension);
        return Combine(showFolder, $"Season {season:D2}", fileName);
    }

    private static string EditionSuffix(string? edition) =>
        string.IsNullOrWhiteSpace(edition) ? string.Empty : $" - {edition}";

    /// <summary>Renders the movie naming template; tokens: <c>{Title}</c>, <c>{Year}</c>.</summary>
    internal static string RenderTemplate(string template, string title, int? year)
    {
        var rendered = template
            .Replace("{Title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", year?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // Drop an empty "( )" left when there is no year, and collapse whitespace.
        rendered = rendered.Replace("()", string.Empty).Trim();
        return string.Join(' ', rendered.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Canonical media lives directly at the catalog root; segments join posix-style (forward slashes).
    private static string Combine(params string[] segments) => string.Join('/', segments);

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => ' ',
                _ when char.IsControl(character) => ' ',
                _ => character,
            });
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.');
        return string.Join(' ', sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
