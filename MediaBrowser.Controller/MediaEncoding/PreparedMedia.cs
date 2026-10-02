using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// The pure rules behind binge-ahead: which transcodes a prepared file can replace, how it is encoded,
/// and when it is safe to serve in place of the original.
/// </summary>
public static class PreparedMedia
{
    /// <summary>
    /// The transcode reasons a prepared file fixes. It re-encodes only the first video stream to 8-bit H.264
    /// and copies everything else, so audio, subtitle and container reasons are not in this set.
    /// </summary>
    public const TranscodeReason VideoReasons =
        TranscodeReason.VideoCodecNotSupported
        | TranscodeReason.VideoProfileNotSupported
        | TranscodeReason.VideoRangeTypeNotSupported
        | TranscodeReason.VideoCodecTagNotSupported
        | TranscodeReason.VideoLevelNotSupported
        | TranscodeReason.VideoBitDepthNotSupported;

    /// <summary>
    /// The highest video bitrate a prepared file is encoded at.
    /// </summary>
    public const int MaxVideoBitrate = 8_000_000;

    /// <summary>
    /// The lowest video bitrate a prepared file is encoded at.
    /// </summary>
    public const int MinVideoBitrate = 2_000_000;

    /// <summary>
    /// Gets a value indicating whether every reason in <paramref name="reasons"/> is one a prepared file fixes.
    /// </summary>
    /// <param name="reasons">The transcode reasons of a playback decision.</param>
    /// <returns><c>true</c> when the decision is a transcode caused by the video stream alone.</returns>
    public static bool IsVideoOnly(TranscodeReason reasons)
        => reasons != 0 && (reasons & ~VideoReasons) == 0;

    /// <summary>
    /// Gets a value indicating whether a playback decision for an original source is one a prepared file can replace:
    /// not direct play, a transcode was planned, and only the video stream caused it.
    /// </summary>
    /// <param name="decided">The original source after the device-specific decision.</param>
    /// <returns><c>true</c> when a prepared file should be offered instead.</returns>
    public static bool IsReplaceable(MediaSourceInfo decided)
        => !decided.SupportsDirectPlay
            && decided.TranscodingUrl is not null
            && IsVideoOnly(decided.TranscodeReasons);

    /// <summary>
    /// Gets the signature of a video stream: the properties that make a client unable to play it.
    /// </summary>
    /// <param name="video">The video stream.</param>
    /// <returns>For example <c>hevc|10|SDR</c>.</returns>
    public static string Signature(MediaStream video)
        => string.Join(
            '|',
            (video.Codec ?? string.Empty).ToLowerInvariant(),
            (video.BitDepth ?? 8).ToString(CultureInfo.InvariantCulture),
            video.VideoRangeType.ToString());

    /// <summary>
    /// Gets a value indicating whether a prepared file can be made for this video: SDR only, since the
    /// prepared encode does no tone mapping.
    /// </summary>
    /// <param name="video">The video stream.</param>
    /// <returns><c>true</c> when it can be prepared.</returns>
    public static bool CanPrepare(MediaStream video)
        => video.VideoRange is VideoRange.SDR or VideoRange.Unknown
            && video.VideoRangeType is VideoRangeType.SDR or VideoRangeType.Unknown;

    /// <summary>
    /// Gets the video bitrate to encode at: H.264 needs about 1.5x the bits of HEVC for the same picture.
    /// </summary>
    /// <param name="sourceVideoBitrate">The source video bitrate, if known.</param>
    /// <returns>The bitrate in bits per second.</returns>
    public static int TargetBitrate(int? sourceVideoBitrate)
        => sourceVideoBitrate is > 0
            ? Math.Clamp((int)Math.Min(int.MaxValue, sourceVideoBitrate.Value * 1.5), MinVideoBitrate, MaxVideoBitrate)
            : 6_000_000;

    /// <summary>
    /// Builds the ffmpeg arguments. <c>-map 0 -c copy</c> keeps every stream at its index, so the audio and
    /// subtitle indexes the client takes from the original item stay valid; only video stream 0 is re-encoded.
    /// </summary>
    /// <param name="inputPath">The original file.</param>
    /// <param name="outputPath">The prepared file to write (Matroska).</param>
    /// <param name="vaapiDevice">The VAAPI render node, or <c>null</c> to encode with libx264.</param>
    /// <param name="videoBitrate">The target video bitrate.</param>
    /// <returns>The arguments.</returns>
    public static string BuildArguments(string inputPath, string outputPath, string? vaapiDevice, int videoBitrate)
    {
        var rate = videoBitrate.ToString(CultureInfo.InvariantCulture);
        var buffer = (videoBitrate * 2L).ToString(CultureInfo.InvariantCulture);
        var encoder = string.IsNullOrEmpty(vaapiDevice)
            ? "-c:v:0 libx264 -preset veryfast -pix_fmt yuv420p -profile:v:0 high"
            : "-c:v:0 h264_vaapi -vf:v:0 \"format=nv12,hwupload\" -rc_mode VBR";
        var device = string.IsNullOrEmpty(vaapiDevice)
            ? string.Empty
            : $"-init_hw_device vaapi=va:{vaapiDevice} -filter_hw_device va ";

        return $"-hide_banner -nostdin -loglevel error {device}-i \"{inputPath}\" -map 0 -c copy {encoder} "
            + $"-b:v:0 {rate} -maxrate:v:0 {rate} -bufsize:v:0 {buffer} -max_muxing_queue_size 2048 -f matroska -y \"{outputPath}\"";
    }

    /// <summary>
    /// Checks that the prepared file has the same streams at the same indexes as the original.
    /// External streams (sidecar subtitle files) are not in either file and are ignored.
    /// </summary>
    /// <param name="original">The original's streams.</param>
    /// <param name="prepared">The prepared file's probed streams.</param>
    /// <returns><c>true</c> when every embedded stream lines up by index and type.</returns>
    public static bool StreamLayoutMatches(IReadOnlyList<MediaStream> original, IReadOnlyList<MediaStream> prepared)
    {
        var embedded = original.Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
        var made = prepared.OrderBy(s => s.Index).ToList();
        if (embedded.Count != made.Count)
        {
            return false;
        }

        for (var i = 0; i < embedded.Count; i++)
        {
            if (embedded[i].Index != made[i].Index || embedded[i].Type != made[i].Type)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the source served in place of the original: the original's identity and external streams,
    /// with the prepared file's path and embedded streams.
    /// </summary>
    /// <param name="original">The original media source.</param>
    /// <param name="preparedPath">The prepared file.</param>
    /// <param name="prepared">The prepared file's probe result.</param>
    /// <returns>The source.</returns>
    public static MediaSourceInfo CreateSource(MediaSourceInfo original, string preparedPath, PreparedMediaInfo prepared)
        => new MediaSourceInfo
        {
            Id = original.Id,
            Name = original.Name,
            ETag = original.ETag + "-prepared",
            Path = preparedPath,
            Protocol = MediaProtocol.File,
            Type = original.Type,
            Container = "mkv",
            Size = prepared.Size,
            Bitrate = prepared.Bitrate,
            RunTimeTicks = original.RunTimeTicks,
            VideoType = VideoType.VideoFile,
            IsRemote = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = original.SupportsDirectStream,
            SupportsTranscoding = original.SupportsTranscoding,
            SupportsProbing = false,
            RequiresOpening = false,
            DefaultAudioStreamIndex = original.DefaultAudioStreamIndex,
            DefaultSubtitleStreamIndex = original.DefaultSubtitleStreamIndex,
            MediaAttachments = original.MediaAttachments,
            HasSegments = original.HasSegments,
            MediaStreams = prepared.MediaStreams
                .Concat(original.MediaStreams.Where(s => s.IsExternal))
                .ToArray()
        };

    /// <summary>
    /// Picks the episodes to prepare for one series: the first <paramref name="count"/> unplayed episodes after
    /// the last played one whose video has <paramref name="signature"/> and can be prepared.
    /// </summary>
    /// <typeparam name="T">The episode type.</typeparam>
    /// <param name="episodesInOrder">The series' episodes, in viewing order.</param>
    /// <param name="isPlayed">Whether the user has played an episode.</param>
    /// <param name="videoOf">The episode's first video stream, if any.</param>
    /// <param name="signature">The signature the device could not play.</param>
    /// <param name="count">How many to pick.</param>
    /// <returns>The episodes, in viewing order.</returns>
    public static IReadOnlyList<T> SelectNext<T>(
        IReadOnlyList<T> episodesInOrder,
        Func<T, bool> isPlayed,
        Func<T, MediaStream?> videoOf,
        string signature,
        int count)
    {
        var lastPlayed = -1;
        for (var i = 0; i < episodesInOrder.Count; i++)
        {
            if (isPlayed(episodesInOrder[i]))
            {
                lastPlayed = i;
            }
        }

        return episodesInOrder
            .Skip(lastPlayed + 1)
            .Where(e => !isPlayed(e))
            .Where(e => videoOf(e) is { } video && CanPrepare(video) && Signature(video) == signature)
            .Take(count)
            .ToList();
    }
}
