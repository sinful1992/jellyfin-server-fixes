using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Images;

/// <summary>
/// In-memory counts of requested image shapes, persisted to <c>image-shapes.json</c> in the data folder.
/// </summary>
public sealed class ImageShapeTracker : IImageShapeTracker
{
    /// <summary>
    /// The most distinct shapes kept. A misbehaving client sending random sizes can't grow this without bound.
    /// </summary>
    public const int MaxShapes = 256;

    /// <summary>
    /// The counts are written to disk after this many recorded requests.
    /// </summary>
    public const int SaveEvery = 200;

    private readonly ConcurrentDictionary<ImageShape, int> _counts = new();
    private readonly string _path;
    private readonly ILogger<ImageShapeTracker> _logger;
    private readonly object _saveLock = new();
    private int _sinceSave;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageShapeTracker"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ImageShapeTracker(IApplicationPaths appPaths, ILogger<ImageShapeTracker> logger)
    {
        _path = Path.Combine(appPaths.DataPath, "image-shapes.json");
        _logger = logger;
        Load();
    }

    /// <inheritdoc />
    public void Record(ImageShape shape)
    {
        if (_counts.Count >= MaxShapes && !_counts.ContainsKey(shape))
        {
            return;
        }

        _counts.AddOrUpdate(shape, 1, (_, count) => count + 1);

        // Saved every SaveEvery records as well as after each pre-render run, so a restart loses little.
        if (Interlocked.Increment(ref _sinceSave) % SaveEvery == 0)
        {
            Save();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ImageShape> GetTopShapes(int minCount, int limit)
        => _counts
            .Where(kv => kv.Value >= minCount)
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => kv.Key)
            .ToList();

    /// <inheritdoc />
    public void Decay()
    {
        foreach (var (shape, count) in _counts)
        {
            var halved = count / 2;
            if (halved == 0)
            {
                _counts.TryRemove(shape, out _);
            }
            else
            {
                _counts[shape] = halved;
            }
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        var entries = _counts.Select(kv => new Entry(kv.Key, kv.Value)).ToList();
        lock (_saveLock)
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
                File.Move(tmp, _path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save image shapes to {Path}", _path);
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? [];
            foreach (var entry in entries.Take(MaxShapes))
            {
                _counts[entry.Shape] = entry.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable image shapes file {Path}", _path);
        }
    }

    private sealed record Entry(ImageShape Shape, int Count);
}
