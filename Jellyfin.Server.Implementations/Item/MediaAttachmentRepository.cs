using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// Manager for handling Media Attachments.
/// </summary>
/// <param name="dbProvider">Efcore Factory.</param>
public class MediaAttachmentRepository(IDbContextFactory<JellyfinDbContext> dbProvider) : IMediaAttachmentRepository
{
    /// <inheritdoc />
    public void SaveMediaAttachments(
        Guid id,
        IReadOnlyList<MediaAttachment> attachments,
        CancellationToken cancellationToken)
    {
        using var context = dbProvider.CreateDbContext();
        using var transaction = context.Database.BeginTransaction();

        // Users may replace a media with a version that includes attachments to one without them.
        // So when saving attachments is triggered by a library scan, we always unconditionally
        // clear the old ones, and then add the new ones if given.
        context.AttachmentStreamInfos.Where(e => e.ItemId.Equals(id)).ExecuteDelete();
        if (attachments.Any())
        {
            context.AttachmentStreamInfos.AddRange(attachments.Select(e => Map(e, id)));
        }

        context.SaveChanges();
        transaction.Commit();
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery filter)
    {
        using var context = dbProvider.CreateDbContext();
        var query = context.AttachmentStreamInfos.AsNoTracking().Where(e => e.ItemId.Equals(filter.ItemId));
        if (filter.Index.HasValue)
        {
            query = query.Where(e => e.Index == filter.Index);
        }

        return query.AsEnumerable().Select(Map).ToArray();
    }

    /// <summary>
    /// Gets the media attachments of many items in one query.
    /// </summary>
    /// <remarks>
    /// A batch loader for <see cref="PagePrefetch"/>. Not on the repository interface, which lives in
    /// MediaBrowser.Controller -- the assembly plugins bind against, shipped untouched by this fork.
    /// Callers reach it by type-testing the instance they were given.
    /// </remarks>
    /// <param name="itemIds">The item ids.</param>
    /// <returns>The attachments of each item that has any.</returns>
    public IReadOnlyDictionary<Guid, IReadOnlyList<MediaAttachment>> GetMediaAttachmentsByItems(IReadOnlyList<Guid> itemIds)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<MediaAttachment>>();
        }

        using var context = dbProvider.CreateDbContext();
        var result = new Dictionary<Guid, IReadOnlyList<MediaAttachment>>();
        foreach (var group in context.AttachmentStreamInfos
                     .AsNoTracking()
                     .WhereOneOrMany(itemIds, e => e.ItemId)
                     .OrderBy(e => e.ItemId)
                     .ThenBy(e => e.Index)
                     .AsEnumerable()
                     .GroupBy(e => e.ItemId))
        {
            result[group.Key] = group.Select(Map).ToArray();
        }

        return result;
    }

    private MediaAttachment Map(AttachmentStreamInfo attachment)
    {
        return new MediaAttachment()
        {
            Codec = attachment.Codec,
            CodecTag = attachment.CodecTag,
            Comment = attachment.Comment,
            FileName = attachment.Filename,
            Index = attachment.Index,
            MimeType = attachment.MimeType,
        };
    }

    private AttachmentStreamInfo Map(MediaAttachment attachment, Guid id)
    {
        return new AttachmentStreamInfo()
        {
            Codec = attachment.Codec,
            CodecTag = attachment.CodecTag,
            Comment = attachment.Comment,
            Filename = attachment.FileName,
            Index = attachment.Index,
            MimeType = attachment.MimeType,
            ItemId = id,
            Item = null!
        };
    }
}
