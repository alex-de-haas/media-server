namespace MediaServer.Api.Torrents;

/// <summary>Lifecycle refusals are actionable conflicts, not unhandled server errors.</summary>
public sealed class TorrentConflictFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (TorrentRequestException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
