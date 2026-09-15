using MediaServer.Api.Library;

namespace MediaServer.Api.Groups;

/// <summary>A validated condition; source conditions within an all-rule must match one video stream.</summary>
public sealed record GroupCondition(string Field, string Operator, string Value, string? EndValue = null);

/// <summary>Versioned flat conjunction or disjunction. Empty conditions are valid only for manual groups.</summary>
public sealed record GroupRules(int Version, string Match, IReadOnlyList<GroupCondition> Conditions);

/// <summary>Complete editable definition. Kind is fixed after creation.</summary>
public sealed record SaveGroupRequest(string Name, string Kind, string CatalogType, GroupRules Rules, IReadOnlyList<Guid> MemberIds);

/// <summary>Stored definition for the settings editor, including temporarily removed manual members.</summary>
public sealed record GroupDefinitionDto(Guid Id, string Name, string Kind, string CatalogType, GroupRules Rules, IReadOnlyList<Guid> MemberIds);

/// <summary>A group folder with its fixed catalog type and current title count.</summary>
public sealed record GroupSummaryDto(Guid Id, string Name, string Kind, string CatalogType, int ItemCount);

/// <summary>A deterministic page of distinct titles with their existing per-user state.</summary>
public sealed record GroupDetailDto(Guid Id, string Name, string Kind, string CatalogType, IReadOnlyList<LibraryItemDto> Items, int Total, int Limit, int Offset);

/// <summary>Supported format values and matching metadata values for the settings picker.</summary>
public sealed record GroupOptionsDto(IReadOnlyList<string> Resolutions, IReadOnlyList<string> HdrFormats, IReadOnlyList<string> Tags, IReadOnlyList<string> Genres);

/// <summary>Invalid group definitions are reported to the caller as HTTP 400.</summary>
public sealed class GroupValidationException(string message) : Exception(message);
