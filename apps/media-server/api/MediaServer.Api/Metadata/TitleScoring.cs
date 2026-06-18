using System.Text;

namespace MediaServer.Api.Metadata;

/// <summary>Scores a provider candidate against the parsed query (0..1) for auto-match vs review.</summary>
public static class TitleScoring
{
    /// <summary>Candidates at or above this score auto-match; below it the item routes to review.</summary>
    public const double AutoMatchThreshold = 0.80;

    public static double Score(string queryTitle, int? queryYear, string candidateTitle, int? candidateYear)
    {
        var titleScore = TitleSimilarity(queryTitle, candidateTitle);

        double yearScore = (queryYear, candidateYear) switch
        {
            (null, _) or (_, null) => 0.0,
            var (q, c) when q == c => 0.15,
            var (q, c) when Math.Abs(q!.Value - c!.Value) == 1 => 0.05,
            _ => -0.15,
        };

        return Math.Clamp(titleScore * 0.85 + yearScore + (titleScore >= 0.999 ? 0.15 : 0), 0, 1);
    }

    private static double TitleSimilarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1.0;
        }

        // Token overlap (Jaccard) handles word reordering and extra release noise.
        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var intersection = tokensA.Intersect(tokensB).Count();
        var union = tokensA.Union(tokensB).Count();
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (char.IsWhiteSpace(character) || character is '-' or '.' or ':')
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
