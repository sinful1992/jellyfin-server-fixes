using System;
using System.IO;
using System.Linq;
using Emby.Server.Implementations.Images;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Images;

public sealed class ImageShapeTrackerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shape-tests-" + Guid.NewGuid().ToString("N"));

    public ImageShapeTrackerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static ImageShape Poster(int maxHeight, int quality = 60)
        => new(BaseItemKind.Movie, ImageType.Primary, (int)Math.Round(maxHeight * 2 / 3.0), maxHeight, null, null, null, null, quality, "Webp,Jpg");

    private ImageShapeTracker NewTracker()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_dir);
        return new ImageShapeTracker(paths.Object, NullLogger<ImageShapeTracker>.Instance);
    }

    [Fact]
    public void GetTopShapes_RanksByCountAndAppliesMinCount()
    {
        var tracker = NewTracker();
        for (var i = 0; i < 5; i++)
        {
            tracker.Record(Poster(300));
        }

        for (var i = 0; i < 3; i++)
        {
            tracker.Record(Poster(450, 85));
        }

        tracker.Record(Poster(1080));

        var top = tracker.GetTopShapes(3, 10);

        Assert.Equal(new[] { Poster(300), Poster(450, 85) }, top);
        Assert.Single(tracker.GetTopShapes(1, 1));
    }

    [Fact]
    public void Record_IgnoresNewShapesBeyondCap()
    {
        var tracker = NewTracker();
        for (var h = 1; h <= ImageShapeTracker.MaxShapes + 10; h++)
        {
            tracker.Record(Poster(h));
        }

        Assert.Equal(ImageShapeTracker.MaxShapes, tracker.GetTopShapes(1, int.MaxValue).Count);

        // A shape already known still counts once the cap is reached.
        tracker.Record(Poster(1));
        Assert.Equal(Poster(1), tracker.GetTopShapes(2, 1).Single());
    }

    [Fact]
    public void Decay_HalvesCountsAndDropsZeros()
    {
        var tracker = NewTracker();
        for (var i = 0; i < 6; i++)
        {
            tracker.Record(Poster(300));
        }

        tracker.Record(Poster(450));

        tracker.Decay();

        Assert.Equal(new[] { Poster(300) }, tracker.GetTopShapes(1, 10));
        Assert.Single(tracker.GetTopShapes(3, 10));
        Assert.Empty(tracker.GetTopShapes(4, 10));
    }

    [Fact]
    public void Save_RoundTripsThroughANewInstance()
    {
        var tracker = NewTracker();
        for (var i = 0; i < 4; i++)
        {
            tracker.Record(Poster(300));
        }

        tracker.Save();

        var reloaded = NewTracker();
        Assert.Equal(new[] { Poster(300) }, reloaded.GetTopShapes(4, 10));
    }

    [Fact]
    public void Load_IgnoresACorruptFile()
    {
        File.WriteAllText(Path.Combine(_dir, "image-shapes.json"), "{not json");
        Assert.Empty(NewTracker().GetTopShapes(1, 10));
    }

    [Fact]
    public void ToOptions_MatchesTheLiveRequestCacheInputs()
    {
        var shape = new ImageShape(BaseItemKind.Movie, ImageType.Primary, 133, 200, null, null, null, null, 60, "Webp,Jpg");
        var item = new Movie { Id = Guid.NewGuid() };
        var image = new ItemImageInfo { Path = "/config/metadata/poster.jpg", Type = ImageType.Primary, DateModified = DateTime.UtcNow };

        var options = shape.ToOptions(item, image);

        // Every input of ImageProcessor's cache file name, as ImageController sets it for the same request.
        Assert.Same(image, options.Image);
        Assert.Equal(133, options.MaxWidth);
        Assert.Equal(200, options.MaxHeight);
        Assert.Null(options.Width);
        Assert.Null(options.Height);
        Assert.Null(options.FillWidth);
        Assert.Null(options.FillHeight);
        Assert.Equal(60, options.Quality);
        Assert.Equal(new[] { ImageFormat.Webp, ImageFormat.Jpg }, options.SupportedOutputFormats);
        Assert.Equal(0, options.PercentPlayed);
        Assert.Null(options.UnplayedCount);
        Assert.Null(options.Blur);
        Assert.Null(options.BackgroundColor);
        Assert.Null(options.ForegroundLayer);
        Assert.Equal(0, options.ImageIndex);
        Assert.Equal(item.Id, options.ItemId);
    }

    [Fact]
    public void PlanByKind_GroupsShapesAndKeepsRankOrder()
    {
        var episode = new ImageShape(BaseItemKind.Episode, ImageType.Primary, 356, 200, null, null, null, null, 60, "Jpg");
        var plan = ImagePrerenderTask.PlanByKind(new[] { Poster(300), episode, Poster(450, 85) });

        Assert.Equal(new[] { Poster(300), Poster(450, 85) }, plan[BaseItemKind.Movie]);
        Assert.Equal(new[] { episode }, plan[BaseItemKind.Episode]);
    }
}
