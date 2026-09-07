using System.Diagnostics;

namespace MediaServer.Api.Library;

// Keep attributes bounded and free of library content or user identity.
internal static class LibraryDiagnostics
{
    internal const string SourceName = "MediaServer.Library";
    internal static readonly ActivitySource Source = new(SourceName);

    internal static async Task<T> MeasureAsync<T>(string name, Func<Task<T>> action, Func<T, int>? count = null)
    {
        using var activity = Source.StartActivity(name);
        try
        {
            var result = await action();
            if (activity is not null && count is not null)
            {
                activity.SetTag("library.result.count", count(result));
            }
            return result;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", exception.GetType().FullName);
            throw;
        }
    }
}
