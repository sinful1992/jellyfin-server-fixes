using System;
using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Binge-ahead: prepared H.264 copies of the next episodes for a device that could not play the originals,
/// the devices that needed them, and which client is currently being served a prepared file.
/// </summary>
public interface IPreparedMediaStore
{
    /// <summary>
    /// Gets the folder prepared files live in.
    /// </summary>
    string Directory { get; }

    /// <summary>
    /// Gets the source to serve in place of <paramref name="original"/>, if a prepared file exists for it.
    /// </summary>
    /// <param name="original">The original media source (its id is the item id).</param>
    /// <returns>The prepared source under the original's id, or <c>null</c>.</returns>
    MediaSourceInfo? GetPreparedSource(MediaSourceInfo original);

    /// <summary>
    /// Remembers that <paramref name="clientIp"/> was told to direct play the prepared file of an item, or forgets it.
    /// </summary>
    /// <param name="clientIp">The client's normalized address.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="active">Whether the client is being served the prepared file.</param>
    void SetServing(string clientIp, Guid itemId, bool active);

    /// <summary>
    /// Gets the prepared source for a stream request, if that client was told to play it and the file still exists.
    /// </summary>
    /// <param name="clientIp">The client's normalized address.</param>
    /// <param name="original">The original media source the request resolved to.</param>
    /// <returns>The prepared source, or <c>null</c> to serve the original.</returns>
    MediaSourceInfo? GetServing(string clientIp, MediaSourceInfo original);

    /// <summary>
    /// Records that a device had to transcode an episode for video reasons only.
    /// </summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="userId">The user.</param>
    /// <param name="seriesId">The episode's series.</param>
    /// <param name="signature">The video signature it could not play (<see cref="PreparedMedia.Signature"/>).</param>
    void RecordTranscode(string deviceId, Guid userId, Guid seriesId, string signature);

    /// <summary>
    /// Gets the devices that transcoded within the given time.
    /// </summary>
    /// <param name="within">How far back to look.</param>
    /// <returns>The device records.</returns>
    IReadOnlyList<PreparedDeviceRecord> GetDevices(TimeSpan within);

    /// <summary>
    /// Gets the prepared files.
    /// </summary>
    /// <returns>Their stored info.</returns>
    IReadOnlyList<PreparedMediaInfo> ListPrepared();

    /// <summary>
    /// Gets the path a prepared file for an item is stored at.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The path.</returns>
    string GetPreparedPath(Guid itemId);

    /// <summary>
    /// Stores the info of a finished prepared file.
    /// </summary>
    /// <param name="info">The info.</param>
    void SavePrepared(PreparedMediaInfo info);

    /// <summary>
    /// Deletes a prepared file and its info.
    /// </summary>
    /// <param name="itemId">The item.</param>
    void DeletePrepared(Guid itemId);
}
