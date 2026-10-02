using System;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Drawing;

/// <summary>
/// The parameters of one resized image request, as the image cache keys it.
/// Two requests with the same shape for the same image hit the same cache file.
/// </summary>
/// <param name="ItemKind">The kind of item the image belongs to.</param>
/// <param name="ImageType">The image type.</param>
/// <param name="MaxWidth">The requested max width.</param>
/// <param name="MaxHeight">The requested max height.</param>
/// <param name="Width">The requested fixed width.</param>
/// <param name="Height">The requested fixed height.</param>
/// <param name="FillWidth">The requested fill width.</param>
/// <param name="FillHeight">The requested fill height.</param>
/// <param name="Quality">The effective quality.</param>
/// <param name="Formats">The output formats the client accepts, comma separated, in preference order.</param>
public sealed record ImageShape(
    BaseItemKind ItemKind,
    ImageType ImageType,
    int? MaxWidth,
    int? MaxHeight,
    int? Width,
    int? Height,
    int? FillWidth,
    int? FillHeight,
    int Quality,
    string Formats)
{
    /// <summary>
    /// Gets the accepted output formats.
    /// </summary>
    /// <returns>The formats, in preference order.</returns>
    public ImageFormat[] GetFormats()
        => Formats.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => Enum.TryParse<ImageFormat>(f, out var format) ? (ImageFormat?)format : null)
            .Where(f => f.HasValue)
            .Select(f => f!.Value)
            .ToArray();

    /// <summary>
    /// Builds the processing options a live request of this shape would use for the given image.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="image">The item's image of <see cref="ImageType"/>.</param>
    /// <returns>The options.</returns>
    public ImageProcessingOptions ToOptions(BaseItem item, ItemImageInfo image)
        => new ImageProcessingOptions
        {
            Height = Height,
            ImageIndex = 0,
            Image = image,
            Item = item,
            ItemId = item.Id,
            MaxHeight = MaxHeight,
            MaxWidth = MaxWidth,
            FillHeight = FillHeight,
            FillWidth = FillWidth,
            Quality = Quality,
            Width = Width,
            PercentPlayed = 0,
            SupportedOutputFormats = GetFormats()
        };
}
