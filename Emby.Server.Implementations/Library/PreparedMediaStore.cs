using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Prepared files live in <c>cache/prepared/{itemId}.mkv</c> with a <c>{itemId}.json</c> probe result next to them.
/// Device records are kept in <c>data/binge-ahead.json</c>. Which client is being served a prepared file is
/// in memory only, keyed by client address because the clients that need this compose their own stream URL
/// and send no token or session id with it.
/// </summary>
public sealed class PreparedMediaStore : IPreparedMediaStore
{
    /// <summary>
    /// How long a client keeps being served the prepared file after its PlaybackInfo chose it.
    /// </summary>
    public static readonly TimeSpan ServingLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<(string Ip, Guid ItemId), DateTime> _serving = new();
    private readonly string _devicesPath;
    private readonly ILogger<PreparedMediaStore> _logger;
    private readonly object _devicesLock = new();
    private readonly Func<DateTime> _now;
    private Dictionary<string, PreparedDeviceRecord> _devices;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreparedMediaStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public PreparedMediaStore(IApplicationPaths appPaths, ILogger<PreparedMediaStore> logger)
        : this(Path.Combine(appPaths.CachePath, "prepared"), Path.Combine(appPaths.DataPath, "binge-ahead.json"), logger, () => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PreparedMediaStore"/> class.
    /// </summary>
    /// <param name="directory">The folder prepared files live in.</param>
    /// <param name="devicesPath">The device records file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">The clock.</param>
    public PreparedMediaStore(string directory, string devicesPath, ILogger<PreparedMediaStore> logger, Func<DateTime> now)
    {
        Directory = directory;
        _devicesPath = devicesPath;
        _logger = logger;
        _now = now;
        _devices = LoadDevices();
    }

    /// <inheritdoc />
    public string Directory { get; }

    /// <inheritdoc />
    public string GetPreparedPath(Guid itemId)
        => Path.Combine(Directory, itemId.ToString("N", CultureInfo.InvariantCulture) + ".mkv");

    /// <inheritdoc />
    public MediaSourceInfo? GetPreparedSource(MediaSourceInfo original)
    {
        if (!Guid.TryParse(original.Id, out var itemId))
        {
            return null;
        }

        var info = ReadInfo(itemId);
        var path = GetPreparedPath(itemId);
        if (info is null || !File.Exists(path))
        {
            return null;
        }

        return PreparedMedia.CreateSource(original, path, info);
    }

    /// <inheritdoc />
    public void SetServing(string clientIp, Guid itemId, bool active)
    {
        if (active)
        {
            _serving[(clientIp, itemId)] = _now() + ServingLifetime;
        }
        else
        {
            _serving.TryRemove((clientIp, itemId), out _);
        }
    }

    /// <inheritdoc />
    public MediaSourceInfo? GetServing(string clientIp, MediaSourceInfo original)
    {
        if (!Guid.TryParse(original.Id, out var itemId)
            || !_serving.TryGetValue((clientIp, itemId), out var expires))
        {
            return null;
        }

        if (expires < _now())
        {
            _serving.TryRemove((clientIp, itemId), out _);
            return null;
        }

        // A file evicted since PlaybackInfo falls back to the original.
        return GetPreparedSource(original);
    }

    /// <inheritdoc />
    public void RecordTranscode(string deviceId, Guid userId, Guid seriesId, string signature)
    {
        lock (_devicesLock)
        {
            if (!_devices.TryGetValue(deviceId, out var record))
            {
                record = new PreparedDeviceRecord { DeviceId = deviceId };
                _devices[deviceId] = record;
            }

            record.UserId = userId;
            record.LastSeen = _now();
            record.Series[seriesId] = signature;
            SaveDevices();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PreparedDeviceRecord> GetDevices(TimeSpan within)
    {
        lock (_devicesLock)
        {
            var since = _now() - within;
            return _devices.Values.Where(d => d.LastSeen >= since).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PreparedMediaInfo> ListPrepared()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out var id) ? ReadInfo(id) : null)
            .OfType<PreparedMediaInfo>()
            .ToList();
    }

    /// <inheritdoc />
    public void SavePrepared(PreparedMediaInfo info)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(GetInfoPath(info.ItemId), JsonSerializer.Serialize(info, JsonDefaults.Options));
    }

    /// <inheritdoc />
    public void DeletePrepared(Guid itemId)
    {
        foreach (var path in new[] { GetPreparedPath(itemId), GetInfoPath(itemId) })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete prepared file {Path}", path);
            }
        }
    }

    private string GetInfoPath(Guid itemId)
        => Path.Combine(Directory, itemId.ToString("N", CultureInfo.InvariantCulture) + ".json");

    private PreparedMediaInfo? ReadInfo(Guid itemId)
    {
        var path = GetInfoPath(itemId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PreparedMediaInfo>(File.ReadAllText(path), JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable prepared info {Path}", path);
            return null;
        }
    }

    private Dictionary<string, PreparedDeviceRecord> LoadDevices()
    {
        try
        {
            if (File.Exists(_devicesPath))
            {
                var records = JsonSerializer.Deserialize<List<PreparedDeviceRecord>>(File.ReadAllText(_devicesPath), JsonDefaults.Options) ?? [];
                return records.ToDictionary(r => r.DeviceId, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable binge-ahead records {Path}", _devicesPath);
        }

        return new Dictionary<string, PreparedDeviceRecord>(StringComparer.Ordinal);
    }

    private void SaveDevices()
    {
        try
        {
            var tmp = _devicesPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_devices.Values.ToList(), JsonDefaults.Options));
            File.Move(tmp, _devicesPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save binge-ahead records {Path}", _devicesPath);
        }
    }
}
