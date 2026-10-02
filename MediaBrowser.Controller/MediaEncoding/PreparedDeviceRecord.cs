using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// A device that had to transcode episodes for video reasons, and the series it did that for.
/// </summary>
public sealed class PreparedDeviceRecord
{
    /// <summary>
    /// Gets or sets the device id.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user who was watching.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets when the device last transcoded (UTC).
    /// </summary>
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Gets or sets the video signature the device couldn't play, per series.
    /// </summary>
    public Dictionary<Guid, string> Series { get; set; } = [];
}
