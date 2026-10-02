using System;
using System.IO;
using System.Linq;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class PreparedMediaStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "prepared-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _itemId = Guid.NewGuid();
    private DateTime _now = new(2026, 10, 3, 22, 0, 0, DateTimeKind.Utc);

    public PreparedMediaStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private PreparedMediaStore NewStore()
        => new(Path.Combine(_dir, "prepared"), Path.Combine(_dir, "binge-ahead.json"), NullLogger<PreparedMediaStore>.Instance, () => _now);

    private MediaSourceInfo Original()
        => new()
        {
            Id = _itemId.ToString("N"),
            Path = "/data/tv/orig.mkv",
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc" }]
        };

    private void Prepare(PreparedMediaStore store)
    {
        store.SavePrepared(new PreparedMediaInfo
        {
            ItemId = _itemId,
            Created = _now,
            Size = 10,
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }]
        });
        File.WriteAllText(store.GetPreparedPath(_itemId), "x");
    }

    [Fact]
    public void GetPreparedSource_NeedsBothFileAndInfo()
    {
        var store = NewStore();
        Assert.Null(store.GetPreparedSource(Original()));

        Prepare(store);
        var source = store.GetPreparedSource(Original());
        Assert.NotNull(source);
        Assert.Equal(store.GetPreparedPath(_itemId), source!.Path);
        Assert.Equal("h264", source.VideoStream.Codec);

        File.Delete(store.GetPreparedPath(_itemId));
        Assert.Null(store.GetPreparedSource(Original()));
    }

    [Fact]
    public void GetServing_OnlyForTheClientThatWasToldAndOnlyWhileValid()
    {
        var store = NewStore();
        Prepare(store);

        Assert.Null(store.GetServing("192.168.1.50", Original()));

        store.SetServing("192.168.1.50", _itemId, true);
        Assert.NotNull(store.GetServing("192.168.1.50", Original()));
        Assert.Null(store.GetServing("192.168.1.51", Original()));

        // Another PlaybackInfo from the same client that did not choose the prepared file clears it.
        store.SetServing("192.168.1.50", _itemId, false);
        Assert.Null(store.GetServing("192.168.1.50", Original()));
    }

    [Fact]
    public void GetServing_Expires()
    {
        var store = NewStore();
        Prepare(store);
        store.SetServing("192.168.1.50", _itemId, true);

        _now += PreparedMediaStore.ServingLifetime + TimeSpan.FromMinutes(1);

        Assert.Null(store.GetServing("192.168.1.50", Original()));
    }

    [Fact]
    public void GetServing_FileEvictedFallsBackToOriginal()
    {
        var store = NewStore();
        Prepare(store);
        store.SetServing("192.168.1.50", _itemId, true);

        store.DeletePrepared(_itemId);

        Assert.Null(store.GetServing("192.168.1.50", Original()));
        Assert.Empty(store.ListPrepared());
    }

    [Fact]
    public void RecordTranscode_PersistsAndFiltersByAge()
    {
        var series = Guid.NewGuid();
        var user = Guid.NewGuid();
        NewStore().RecordTranscode("firetv", user, series, "hevc|10|SDR");

        var reloaded = NewStore();
        var device = Assert.Single(reloaded.GetDevices(TimeSpan.FromDays(14)));
        Assert.Equal("firetv", device.DeviceId);
        Assert.Equal(user, device.UserId);
        Assert.Equal("hevc|10|SDR", device.Series[series]);

        _now += TimeSpan.FromDays(15);
        Assert.Empty(reloaded.GetDevices(TimeSpan.FromDays(14)));
    }

    [Fact]
    public void ListPrepared_ReadsStoredInfo()
    {
        var store = NewStore();
        Prepare(store);

        var info = Assert.Single(store.ListPrepared());
        Assert.Equal(_itemId, info.ItemId);
        Assert.Equal(_now, info.Created);
    }
}
