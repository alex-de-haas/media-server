using MediaServer.Api.Data;
using MediaServer.Api.Library;

namespace MediaServer.Api.Tests.Library;

/// <summary>The record is stored as four columns and read as one object: a client either gets it whole or
/// not at all.</summary>
public sealed class DolbyVisionProjectionTests
{
    [Fact]
    public void A_recorded_stream_projects_the_whole_record()
    {
        var stream = new MediaStream
        {
            StreamType = StreamType.Video, HdrFormat = "Dolby Vision",
            DvProfile = 7, DvLevel = 6, DvBlSignalCompatibilityId = 6, DvElPresent = true,
        };

        Assert.Equal(new DolbyVisionDto(7, 6, 6, EnhancementLayer: true), LibraryReadService.DolbyVision(stream));
    }

    [Fact]
    public void A_stream_without_a_profile_projects_nothing()
    {
        // Labelled Dolby Vision before the record was stored, or not Dolby Vision at all: the same null, and
        // the label beside it says which.
        Assert.Null(LibraryReadService.DolbyVision(new MediaStream { StreamType = StreamType.Video, HdrFormat = "Dolby Vision" }));
        Assert.Null(LibraryReadService.DolbyVision(new MediaStream { StreamType = StreamType.Video, HdrFormat = "HDR10" }));
    }
}
