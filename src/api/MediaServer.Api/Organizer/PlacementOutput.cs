namespace MediaServer.Api.Organizer;

/// <summary>Output boundary for testing write failures without filling the operator's disk.</summary>
public interface IPlacementOutput
{
    Stream Create(string path);
}

public sealed class PlacementOutput : IPlacementOutput
{
    public Stream Create(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
}
