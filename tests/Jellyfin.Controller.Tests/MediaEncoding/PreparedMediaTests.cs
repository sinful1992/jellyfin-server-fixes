using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Controller.Tests.MediaEncoding;

public class PreparedMediaTests
{
    private static MediaStream Video(string codec = "hevc", int bitDepth = 10, string? transfer = null, int? dvProfile = null)
        => new() { Type = MediaStreamType.Video, Index = 0, Codec = codec, BitDepth = bitDepth, ColorTransfer = transfer, ColorPrimaries = transfer is null ? null : "bt2020", CodecTag = dvProfile is null ? null : "dvhe", DvProfile = dvProfile };

    private static List<MediaStream> Layout(params MediaStreamType[] types)
        => types.Select((t, i) => new MediaStream { Type = t, Index = i }).ToList();

    [Theory]
    [InlineData(TranscodeReason.VideoProfileNotSupported, true)]
    [InlineData(TranscodeReason.VideoCodecNotSupported | TranscodeReason.VideoBitDepthNotSupported, true)]
    [InlineData(TranscodeReason.VideoProfileNotSupported | TranscodeReason.AudioCodecNotSupported, false)]
    [InlineData(TranscodeReason.AudioCodecNotSupported, false)]
    [InlineData(TranscodeReason.ContainerBitrateExceedsLimit, false)]
    [InlineData((TranscodeReason)0, false)]
    public void IsVideoOnly_OnlyVideoReasons(TranscodeReason reasons, bool expected)
        => Assert.Equal(expected, PreparedMedia.IsVideoOnly(reasons));

    [Fact]
    public void IsReplaceable_NeedsAPlannedVideoOnlyTranscode()
    {
        var transcode = new MediaSourceInfo { SupportsDirectPlay = false, TranscodingUrl = "/videos/x/master.m3u8", TranscodeReasons = TranscodeReason.VideoProfileNotSupported };
        Assert.True(PreparedMedia.IsReplaceable(transcode));

        Assert.False(PreparedMedia.IsReplaceable(new MediaSourceInfo { SupportsDirectPlay = true, TranscodeReasons = TranscodeReason.VideoProfileNotSupported }));
        Assert.False(PreparedMedia.IsReplaceable(new MediaSourceInfo { SupportsDirectPlay = false, TranscodingUrl = null, TranscodeReasons = TranscodeReason.VideoProfileNotSupported }));
        Assert.False(PreparedMedia.IsReplaceable(new MediaSourceInfo { SupportsDirectPlay = false, TranscodingUrl = "/t", TranscodeReasons = TranscodeReason.AudioCodecNotSupported }));
    }

    [Fact]
    public void Signature_IsCodecBitDepthAndRange()
    {
        Assert.Equal("hevc|10|SDR", PreparedMedia.Signature(Video()));
        Assert.Equal("h264|8|SDR", PreparedMedia.Signature(new MediaStream { Type = MediaStreamType.Video, Codec = "H264" }));
    }

    [Fact]
    public void CanPrepare_SdrOnly()
    {
        Assert.True(PreparedMedia.CanPrepare(Video()));
        Assert.False(PreparedMedia.CanPrepare(Video(transfer: "smpte2084")));
        Assert.False(PreparedMedia.CanPrepare(Video(dvProfile: 5)));
    }

    [Theory]
    [InlineData(null, 6_000_000)]
    [InlineData(1_000_000, PreparedMedia.MinVideoBitrate)]
    [InlineData(3_000_000, 4_500_000)]
    [InlineData(20_000_000, PreparedMedia.MaxVideoBitrate)]
    public void TargetBitrate_IsOneAndAHalfTimesClamped(int? source, int expected)
        => Assert.Equal(expected, PreparedMedia.TargetBitrate(source));

    [Fact]
    public void BuildArguments_Vaapi_CopiesEverythingButVideoZero()
    {
        var args = PreparedMedia.BuildArguments("/data/tv/a b.mkv", "/cache/prepared/x.mkv.part", "/dev/dri/renderD128", 4_500_000);

        Assert.Contains("-init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va", args, System.StringComparison.Ordinal);
        Assert.Contains("-i \"/data/tv/a b.mkv\" -map 0 -c copy -c:v:0 h264_vaapi", args, System.StringComparison.Ordinal);
        Assert.Contains("-b:v:0 4500000 -maxrate:v:0 4500000 -bufsize:v:0 9000000", args, System.StringComparison.Ordinal);
        Assert.EndsWith("-f matroska -y \"/cache/prepared/x.mkv.part\"", args, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArguments_NoVaapi_UsesLibx264()
    {
        var args = PreparedMedia.BuildArguments("/in.mkv", "/out.mkv", null, 6_000_000);

        Assert.DoesNotContain("vaapi", args, System.StringComparison.Ordinal);
        Assert.Contains("-map 0 -c copy -c:v:0 libx264", args, System.StringComparison.Ordinal);
    }

    [Fact]
    public void StreamLayoutMatches_SameTypesAtSameIndexes()
    {
        var original = Layout(MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Subtitle, MediaStreamType.Subtitle);
        original.Add(new MediaStream { Type = MediaStreamType.Subtitle, Index = 4, IsExternal = true });

        Assert.True(PreparedMedia.StreamLayoutMatches(original, Layout(MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Subtitle, MediaStreamType.Subtitle)));
        Assert.False(PreparedMedia.StreamLayoutMatches(original, Layout(MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Subtitle)));
        Assert.False(PreparedMedia.StreamLayoutMatches(original, Layout(MediaStreamType.Video, MediaStreamType.Subtitle, MediaStreamType.Audio, MediaStreamType.Subtitle)));
    }

    [Fact]
    public void CreateSource_KeepsTheOriginalIdentityAndDescribesThePreparedFile()
    {
        var external = new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, IsExternal = true, Codec = "srt" };
        var original = new MediaSourceInfo
        {
            Id = "6cb99322bd28e078fbd8491edd440259",
            Name = "Original",
            Path = "/data/tv/orig.mkv",
            Container = "mkv",
            Size = 900_000_000,
            Bitrate = 2_500_000,
            RunTimeTicks = 13_000_000_000,
            DefaultAudioStreamIndex = 1,
            MediaStreams = [Video(), new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "eac3" }, external]
        };
        var prepared = new PreparedMediaInfo
        {
            Size = 1_000_000_000,
            Bitrate = 4_700_000,
            MediaStreams = [Video("h264", 8), new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "eac3" }]
        };

        var source = PreparedMedia.CreateSource(original, "/cache/prepared/x.mkv", prepared);

        Assert.Equal(original.Id, source.Id);
        Assert.Equal("/cache/prepared/x.mkv", source.Path);
        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.Equal("mkv", source.Container);
        Assert.Equal(1_000_000_000, source.Size);
        Assert.Equal(4_700_000, source.Bitrate);
        Assert.Equal(original.RunTimeTicks, source.RunTimeTicks);
        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.Equal("h264", source.VideoStream.Codec);
        Assert.Equal(3, source.MediaStreams.Count);
        Assert.Same(external, source.MediaStreams[2]);
        Assert.NotEqual(original.ETag, source.ETag);
    }

    [Fact]
    public void SelectNext_TakesUnplayedAfterLastPlayedWithTheSignature()
    {
        // E09..E15 played (except a skipped E10), E16 HDR, E17..E19 SDR hevc10, E20 h264.
        var episodes = new List<(int Number, bool Played, MediaStream Video)>
        {
            (9, true, Video()),
            (10, false, Video()),
            (15, true, Video()),
            (16, false, Video(transfer: "smpte2084")),
            (17, false, Video()),
            (18, false, Video()),
            (19, false, Video()),
            (20, false, Video("h264", 8)),
        };

        var next = PreparedMedia.SelectNext(episodes, e => e.Played, e => e.Video, "hevc|10|SDR", 2);

        Assert.Equal(new[] { 17, 18 }, next.Select(e => e.Number));
    }

    [Fact]
    public void SelectNext_NothingPlayed_StartsAtTheBeginning()
    {
        var episodes = new List<(int Number, bool Played, MediaStream Video)> { (1, false, Video()), (2, false, Video()) };

        Assert.Equal(new[] { 1 }, PreparedMedia.SelectNext(episodes, e => e.Played, e => e.Video, "hevc|10|SDR", 1).Select(e => e.Number));
    }
}
