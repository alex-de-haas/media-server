namespace MediaServer.Api.Mux;

/// <summary>Output tags for one appended audio stream. Null keeps whatever tag the source stream carries.</summary>
public sealed record AudioMuxStreamTag(string? Language, string? Title);

/// <summary>One external audio file to append: its absolute path and the output tags for each of its
/// audio streams, in stream order (an <c>.mka</c> may carry several tracks).</summary>
public sealed record AudioMuxInput(string AbsolutePath, IReadOnlyList<AudioMuxStreamTag> Streams);

/// <summary>
/// A single mux: every stream of the video plus the inputs' audio streams, stream-copied into a Matroska
/// output at <see cref="OutputAbsolutePath"/>. <see cref="VideoAudioStreamCount"/> is how many audio
/// streams the video already has — the appended streams' metadata positions start after them.
/// </summary>
public sealed record AudioMuxPlan(
    string VideoAbsolutePath,
    int VideoAudioStreamCount,
    IReadOnlyList<AudioMuxInput> AudioInputs,
    string OutputAbsolutePath);

/// <summary>Merges external audio tracks into a video container without re-encoding.</summary>
public interface IAudioMuxer
{
    Task MuxAsync(AudioMuxPlan plan, CancellationToken cancellationToken);
}
