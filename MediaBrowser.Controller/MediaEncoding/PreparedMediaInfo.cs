using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// What is stored next to a prepared file: its probe result, so serving it needs no probe.
/// </summary>
public sealed class PreparedMediaInfo
{
    /// <summary>
    /// Gets or sets the item the file was prepared for.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets when the file was prepared (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Gets or sets the overall bitrate.
    /// </summary>
    public int? Bitrate { get; set; }

    /// <summary>
    /// Gets or sets the embedded streams, as probed.
    /// </summary>
    public IReadOnlyList<MediaStream> MediaStreams { get; set; } = [];
}
