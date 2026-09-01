using System.Text.Json;
using System.Text.RegularExpressions;
using MediaServer.Api.Mcp;

namespace MediaServer.Api.Tests.Mcp;

/// <summary>
/// The skill this app hands an agent, checked against the tools it describes.
/// </summary>
/// <remarks>
/// The skill is text the model reads before it decides anything, so a name in it that no longer
/// exists does not fail — it sends the model to a dead end and the operator gets "that tool is not
/// available" for a question this server can answer. Nothing else notices, because the skill is prose.
/// </remarks>
public sealed class AgentSkillTests
{
    [Fact]
    public void The_manifest_points_at_a_skill_that_is_there()
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "manifest.json")));
        var skillFile = manifest.RootElement.GetProperty("agent").GetProperty("skillFile").GetString();

        Assert.False(string.IsNullOrWhiteSpace(skillFile));
        Assert.True(
            File.Exists(Path.Combine(RepositoryRoot(), skillFile!)),
            $"manifest declares {skillFile}, which is what Hosty vendors at install time");
    }

    [Fact]
    public void Every_tool_the_skill_names_exists()
    {
        // Drift in the direction that matters. The other direction — a tool the skill does not mention —
        // is a judgement call about what is worth explaining, so it is deliberately not asserted.
        var declared = McpToolInvoker.Tools()
            .Select(tool => tool!["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        var mentioned = Regex.Matches(SkillText(), @"`([a-z][a-z_]{3,})`")
            .Select(match => match.Groups[1].Value)
            .Where(name => name.Contains('_', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            // Argument names are written the same way; only the ones that look like tools are checked.
            .Where(name => !ArgumentNames.Contains(name))
            .ToArray();

        Assert.NotEmpty(mentioned);
        foreach (var name in mentioned)
        {
            Assert.Contains(name, declared);
        }
    }

    [Fact]
    public void The_skill_teaches_the_word_nothing_else_surfaces()
    {
        // An operator finds out about a NeedsReview item when a film they downloaded never appears.
        // A model that does not know the term will answer "it is not in your library" and stop.
        Assert.Contains("NeedsReview", SkillText(), StringComparison.Ordinal);
    }

    /// <summary>Argument names that share the tool-name shape and are not tools.</summary>
    private static readonly HashSet<string> ArgumentNames = new(StringComparer.Ordinal)
    {
        "downloadId", "sourceFileId", "catalogId", "providerId", "entryId", "userRating",
        "keepSeeding", "file_not_found",
    };

    private static string SkillText() => File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "agent.md"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "manifest.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
