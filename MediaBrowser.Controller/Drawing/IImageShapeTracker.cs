using System.Collections.Generic;

namespace MediaBrowser.Controller.Drawing;

/// <summary>
/// Counts the resize shapes clients actually request, so the shapes worth pre-rendering
/// are learned from traffic instead of guessed.
/// </summary>
public interface IImageShapeTracker
{
    /// <summary>
    /// Records one served request.
    /// </summary>
    /// <param name="shape">The request's shape.</param>
    void Record(ImageShape shape);

    /// <summary>
    /// Gets the most requested shapes.
    /// </summary>
    /// <param name="minCount">The minimum number of requests a shape needs.</param>
    /// <param name="limit">The maximum number of shapes to return.</param>
    /// <returns>The shapes, most requested first.</returns>
    IReadOnlyList<ImageShape> GetTopShapes(int minCount, int limit);

    /// <summary>
    /// Halves every count and forgets shapes that reach zero, so old client layouts fade out.
    /// </summary>
    void Decay();

    /// <summary>
    /// Writes the counts to disk.
    /// </summary>
    void Save();
}
