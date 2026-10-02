using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Binge-ahead: overnight, prepares an H.264 copy of the next episodes a device is likely to watch but can't
/// play, so they direct play instead of being transcoded live. Only series a device actually transcoded are
/// considered: Next Up is per user and is shared with TVs that play the originals fine.
/// </summary>
public class BingeAheadTask : IScheduledTask
{
    /// <summary>
    /// Episodes prepared per series.
    /// </summary>
    public const int EpisodesPerSeries = 2;

    /// <summary>
    /// The most prepared files kept at once.
    /// </summary>
    public const int MaxPrepared = 4;

    /// <summary>
    /// Prepared files older than this are deleted.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly IPreparedMediaStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ISessionManager _sessionManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<BingeAheadTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BingeAheadTask"/> class.
    /// </summary>
    /// <param name="store">The prepared media store.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public BingeAheadTask(
        IPreparedMediaStore store,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        IMediaEncoder mediaEncoder,
        IConfigurationManager configurationManager,
        ILogger<BingeAheadTask> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
        _mediaEncoder = mediaEncoder;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Binge-ahead";

    /// <inheritdoc />
    public string Description => "Prepares H.264 copies of the next episodes for devices that had to transcode them.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "BingeAhead";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var devices = _store.GetDevices(MaxAge);
        Evict(devices);

        if (IsAnythingPlaying())
        {
            _logger.LogInformation("Binge-ahead skipped: something is playing");
            return;
        }

        var prepared = _store.ListPrepared().Select(p => p.ItemId).ToHashSet();
        var todo = FindEpisodes(devices).Where(e => !prepared.Contains(e.Id)).ToList();
        var room = Math.Max(0, MaxPrepared - prepared.Count);
        todo = todo.Take(room).ToList();

        for (var i = 0; i < todo.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsAnythingPlaying())
            {
                _logger.LogInformation("Binge-ahead stopped: playback started");
                break;
            }

            await PrepareAsync(todo[i], cancellationToken).ConfigureAwait(false);
            progress.Report(100d * (i + 1) / todo.Count);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // After the evening's viewing, which runs past 01:30 here, and before the 03:30 image pre-render ends.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    private bool IsAnythingPlaying()
        => _sessionManager.Sessions.Any(s => s.NowPlayingItem is not null);

    private void Evict(IReadOnlyList<PreparedDeviceRecord> devices)
    {
        var users = devices.Select(d => _userManager.GetUserById(d.UserId)).OfType<Jellyfin.Database.Implementations.Entities.User>().ToList();
        foreach (var info in _store.ListPrepared())
        {
            var item = _libraryManager.GetItemById(info.ItemId);
            var played = item is not null && users.Any(u => _userDataManager.GetUserData(u, item)?.Played == true);
            if (item is null || played || info.Created < DateTime.UtcNow - MaxAge)
            {
                _logger.LogInformation("Binge-ahead: removing prepared copy of {Item}", item?.Name ?? info.ItemId.ToString());
                _store.DeletePrepared(info.ItemId);
            }
        }
    }

    private IEnumerable<Episode> FindEpisodes(IReadOnlyList<PreparedDeviceRecord> devices)
    {
        foreach (var device in devices.OrderByDescending(d => d.LastSeen))
        {
            var user = _userManager.GetUserById(device.UserId);
            if (user is null)
            {
                continue;
            }

            foreach (var (seriesId, signature) in device.Series)
            {
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
                    {
                        AncestorIds = [seriesId],
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                        OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
                        DtoOptions = new DtoOptions(false)
                    })
                    .OfType<Episode>()
                    .Where(e => e.ParentIndexNumber is not 0)
                    .ToList();

                var next = PreparedMedia.SelectNext(
                    episodes,
                    e => _userDataManager.GetUserData(user, e)?.Played == true,
                    e => e.GetDefaultVideoStream(),
                    signature,
                    EpisodesPerSeries);

                foreach (var episode in next)
                {
                    yield return episode;
                }
            }
        }
    }

    private async Task PrepareAsync(Episode episode, CancellationToken cancellationToken)
    {
        var finalPath = _store.GetPreparedPath(episode.Id);
        var partPath = finalPath + ".part";
        Directory.CreateDirectory(_store.Directory);

        var encoding = _configurationManager.GetEncodingOptions();
        var vaapi = encoding.HardwareAccelerationType == HardwareAccelerationType.vaapi ? encoding.VaapiDevice : null;
        var video = episode.GetDefaultVideoStream();
        var args = PreparedMedia.BuildArguments(episode.Path, partPath, vaapi, PreparedMedia.TargetBitrate(video?.BitRate));

        _logger.LogInformation("Binge-ahead: preparing {Series} {Episode}", episode.SeriesName, episode.Name);
        var started = Stopwatch.StartNew();
        try
        {
            if (!await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var probe = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo { Path = partPath, Protocol = MediaProtocol.File },
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false
                },
                cancellationToken).ConfigureAwait(false);

            if (!PreparedMedia.StreamLayoutMatches(episode.GetMediaStreams(), probe.MediaStreams))
            {
                _logger.LogWarning("Binge-ahead: streams of the prepared {Episode} don't line up with the original; discarded", episode.Name);
                return;
            }

            File.Move(partPath, finalPath, true);
            _store.SavePrepared(new PreparedMediaInfo
            {
                ItemId = episode.Id,
                Created = DateTime.UtcNow,
                Size = new FileInfo(finalPath).Length,
                Bitrate = probe.Bitrate,
                MediaStreams = probe.MediaStreams
            });
            _logger.LogInformation("Binge-ahead: prepared {Episode} in {Elapsed}", episode.Name, started.Elapsed);
        }
        finally
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }
        }
    }

    private async Task<bool> RunFfmpegAsync(string args, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not lower the ffmpeg priority");
        }

        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Binge-ahead: ffmpeg exited with {Code}: {Error}", process.ExitCode, await stderr.ConfigureAwait(false));
            return false;
        }

        return true;
    }
}
