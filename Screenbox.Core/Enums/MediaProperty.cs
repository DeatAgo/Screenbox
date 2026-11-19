namespace Screenbox.Core.Enums;

public enum MediaProperty
{
    // General
    CompleteName,
    Size,
    Length,
    OverallBitRate,
    FrameRate,
    Title,
    Performer,
    Genre,
    Description,
    RecordedDate,
    Comment,

    // Video
    Format,
    Width,
    Height,
    DisplayAspectRatio,

    // Audio
    AudioFormat,
    AudioBitRate,
    AudioSamplingRate,
    AudioBitDepth,

    // Existing / Legacy properties preserved
    Subtitle,
    Year,
    Producers,
    Writers,
    Resolution,
    BitRate,
    ContributingArtists,
    Album,
    AlbumArtist,
    Composers,
    Track,
    FileType,
    ContentType,
    LastModified,
}
