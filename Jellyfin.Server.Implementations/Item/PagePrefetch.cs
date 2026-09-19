using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// The per-item reads a page of DTOs would otherwise repeat once per item, batch-read for the
/// items of the current request.
/// </summary>
/// <remarks>
/// <para>
/// Building a page of DTOs calls <c>item.GetMediaSources()</c> per item, and that reads media
/// streams, attachments, alternate-version links and the media-segment flag from the database --
/// one round-trip each, per item, buried in <c>BaseItem.GetVersionInfo</c> where a batch cannot
/// be threaded through as a parameter. Measured on a 15k-item library: ~1,400 commands and
/// ~1 s for a 134-episode series list, of which the database itself accounted for under 100 ms.
/// The cost is round-trips, not data.
/// </para>
/// <para>
/// The scope is an <see cref="AsyncLocal{T}"/>: it belongs to the request that opened it, flows
/// into anything that request awaits, and is gone when disposed. Nothing is cached across
/// requests, so there is no staleness window. A miss for an item the scope covers means "none":
/// the readers return empty instead of falling through to the per-item query for exactly the
/// empty case. An item outside the scope falls through as before.
/// </para>
/// <para>
/// This lives here, not in MediaBrowser.Controller, because that assembly is what plugins bind
/// against and this fork ships it untouched. The readers that consult the scope are in
/// Emby.Server.Implementations and this assembly, both of which the fork already replaces.
/// </para>
/// </remarks>
public sealed class PagePrefetch : IDisposable
{
    private static readonly AsyncLocal<PagePrefetch?> _current = new();

    private readonly PagePrefetch? _previous;
    private readonly IReadOnlySet<Guid> _itemIds;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<MediaStream>>? _mediaStreams;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<MediaAttachment>>? _mediaAttachments;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? _localAlternateVersionIds;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? _linkedAlternateVersionIds;
    private readonly IReadOnlySet<Guid>? _itemsWithSegments;

    private PagePrefetch(
        IReadOnlySet<Guid> itemIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<MediaStream>>? mediaStreams,
        IReadOnlyDictionary<Guid, IReadOnlyList<MediaAttachment>>? mediaAttachments,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? localAlternateVersionIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? linkedAlternateVersionIds,
        IReadOnlySet<Guid>? itemsWithSegments)
    {
        _itemIds = itemIds;
        _mediaStreams = mediaStreams;
        _mediaAttachments = mediaAttachments;
        _localAlternateVersionIds = localAlternateVersionIds;
        _linkedAlternateVersionIds = linkedAlternateVersionIds;
        _itemsWithSegments = itemsWithSegments;
        _previous = _current.Value;
        _current.Value = this;
    }

    /// <summary>
    /// Gets the scope of the current request, or null when none is open.
    /// </summary>
    public static PagePrefetch? Current => _current.Value;

    /// <summary>
    /// Opens a scope for <paramref name="itemIds"/>. Each batch is optional: a reader whose batch
    /// is null falls through to its per-item query as if no scope were open.
    /// </summary>
    /// <param name="itemIds">The items the page is about to render.</param>
    /// <param name="mediaStreams">Media streams by item, or null.</param>
    /// <param name="mediaAttachments">Media attachments by item, or null.</param>
    /// <param name="localAlternateVersionIds">Local alternate version ids by item, or null.</param>
    /// <param name="linkedAlternateVersionIds">Linked alternate version ids by item, or null.</param>
    /// <param name="itemsWithSegments">The items that have media segments, or null.</param>
    /// <returns>The scope; dispose it when the page is built.</returns>
    public static PagePrefetch Begin(
        IReadOnlySet<Guid> itemIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<MediaStream>>? mediaStreams = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<MediaAttachment>>? mediaAttachments = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? localAlternateVersionIds = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? linkedAlternateVersionIds = null,
        IReadOnlySet<Guid>? itemsWithSegments = null)
    {
        return new PagePrefetch(itemIds, mediaStreams, mediaAttachments, localAlternateVersionIds, linkedAlternateVersionIds, itemsWithSegments);
    }

    /// <summary>
    /// Answers a whole-item media stream read from the batch.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="streams">The item's streams, empty when the item has none.</param>
    /// <returns>True when the scope covers the item and streams were batched.</returns>
    public bool TryGetMediaStreams(Guid itemId, out IReadOnlyList<MediaStream> streams)
        => TryGetList(_mediaStreams, itemId, out streams);

    /// <summary>
    /// Answers a whole-item media attachment read from the batch.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="attachments">The item's attachments, empty when the item has none.</param>
    /// <returns>True when the scope covers the item and attachments were batched.</returns>
    public bool TryGetMediaAttachments(Guid itemId, out IReadOnlyList<MediaAttachment> attachments)
        => TryGetList(_mediaAttachments, itemId, out attachments);

    /// <summary>
    /// Answers a local alternate version id read from the batch.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="ids">The ids, empty when the item has none.</param>
    /// <returns>True when the scope covers the item and links were batched.</returns>
    public bool TryGetLocalAlternateVersionIds(Guid itemId, out IReadOnlyList<Guid> ids)
        => TryGetList(_localAlternateVersionIds, itemId, out ids);

    /// <summary>
    /// Answers a linked alternate version id read from the batch.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="ids">The ids, empty when the item has none.</param>
    /// <returns>True when the scope covers the item and links were batched.</returns>
    public bool TryGetLinkedAlternateVersionIds(Guid itemId, out IReadOnlyList<Guid> ids)
        => TryGetList(_linkedAlternateVersionIds, itemId, out ids);

    /// <summary>
    /// Answers a media-segment existence check from the batch.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="hasSegments">Whether the item has segments.</param>
    /// <returns>True when the scope covers the item and segment flags were batched.</returns>
    public bool TryGetHasSegments(Guid itemId, out bool hasSegments)
    {
        if (_itemsWithSegments is not null && _itemIds.Contains(itemId))
        {
            hasSegments = _itemsWithSegments.Contains(itemId);
            return true;
        }

        hasSegments = false;
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _current.Value = _previous;
    }

    private bool TryGetList<T>(IReadOnlyDictionary<Guid, IReadOnlyList<T>>? batch, Guid itemId, out IReadOnlyList<T> list)
    {
        if (batch is not null && _itemIds.Contains(itemId))
        {
            list = batch.GetValueOrDefault(itemId) ?? Array.Empty<T>();
            return true;
        }

        list = Array.Empty<T>();
        return false;
    }
}
