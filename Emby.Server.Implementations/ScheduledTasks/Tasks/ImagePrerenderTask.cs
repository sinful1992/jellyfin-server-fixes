using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Renders the image sizes clients actually ask for, for recently added items, so the first
/// look at a new poster is a cache hit instead of a resize on the request path.
/// </summary>
public class ImagePrerenderTask : IScheduledTask
{
    /// <summary>
    /// A shape must have been requested at least this often to be pre-rendered.
    /// </summary>
    public const int MinShapeCount = 3;

    /// <summary>
    /// The most shapes pre-rendered per run.
    /// </summary>
    public const int MaxShapes = 32;

    /// <summary>
    /// Items added within this window are pre-rendered.
    /// </summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    private readonly ILibraryManager _libraryManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly IImageShapeTracker _shapeTracker;
    private readonly ILogger<ImagePrerenderTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImagePrerenderTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="shapeTracker">The image shape tracker.</param>
    /// <param name="logger">The logger.</param>
    public ImagePrerenderTask(
        ILibraryManager libraryManager,
        IImageProcessor imageProcessor,
        IImageShapeTracker shapeTracker,
        ILogger<ImagePrerenderTask> logger)
    {
        _libraryManager = libraryManager;
        _imageProcessor = imageProcessor;
        _shapeTracker = shapeTracker;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Pre-render images";

    /// <inheritdoc />
    public string Description => "Renders the image sizes clients request most, for items added in the last 7 days.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "PrerenderImages";

    /// <summary>
    /// Groups the shapes by the item kind they apply to.
    /// </summary>
    /// <param name="shapes">The shapes, most requested first.</param>
    /// <returns>The shapes per item kind, in the same order.</returns>
    public static IReadOnlyDictionary<BaseItemKind, ImageShape[]> PlanByKind(IEnumerable<ImageShape> shapes)
        => shapes
            .GroupBy(s => s.ItemKind)
            .ToDictionary(g => g.Key, g => g.ToArray());

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plan = PlanByKind(_shapeTracker.GetTopShapes(MinShapeCount, MaxShapes));
        if (plan.Count == 0)
        {
            _logger.LogInformation("No image shapes requested often enough yet; nothing to pre-render");
            progress.Report(100);
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = plan.Keys.ToArray(),
            MinDateCreated = DateTime.UtcNow - RecentWindow,
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false) { EnableImages = true }
        });

        int rendered = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            foreach (var shape in plan[item.GetBaseItemKind()])
            {
                var image = item.GetImageInfo(shape.ImageType, 0);
                if (image is null || !image.IsLocalFile)
                {
                    continue;
                }

                try
                {
                    // ProcessImage returns the cached file when it already exists, so re-runs are cheap.
                    await _imageProcessor.ProcessImage(shape.ToOptions(item, image)).ConfigureAwait(false);
                    rendered++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Pre-render of {ImageType} for {Item} failed", shape.ImageType, item.Name);
                }
            }

            progress.Report(100d * (i + 1) / items.Count);
        }

        _logger.LogInformation("Pre-rendered {Count} images for {Items} recent items", rendered, items.Count);
        _shapeTracker.Decay();
        _shapeTracker.Save();
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = new TimeSpan(3, 30, 0).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(2).Ticks
        };
    }
}
