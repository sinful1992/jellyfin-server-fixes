using System;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

/// <summary>
/// Endpoints that serve media bytes must not be reachable anonymously.
/// Only DefaultPolicy is configured, so an action without [Authorize] carries no
/// authorization metadata and is served to unauthenticated callers.
/// </summary>
public sealed class MediaStreamingAuthTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private static string? _accessToken;

    public MediaStreamingAuthTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string> MediaEndpoints()
    {
        var itemId = Guid.NewGuid();
        var mediaSourceId = Guid.NewGuid();

        return new TheoryData<string>
        {
            $"Audio/{itemId}/stream",
            $"Audio/{itemId}/stream.mp3",
            $"Videos/{itemId}/stream",
            $"Videos/{itemId}/stream.mp4",
            $"Videos/{itemId}/{mediaSourceId}/Attachments/0",
            $"Videos/{itemId}/{mediaSourceId}/Subtitles/0/Stream.vtt",
            $"Videos/{itemId}/{mediaSourceId}/Subtitles/0/0/Stream.vtt",
            $"Audio/{itemId}/hls/{mediaSourceId}/stream.mp3",
            $"Audio/{itemId}/hls/{mediaSourceId}/stream.aac",
            $"Videos/{itemId}/hls/{mediaSourceId}/segment.ts",
            $"LiveTv/LiveRecordings/{itemId}/stream",
            $"LiveTv/LiveStreamFiles/{itemId}/stream.ts",
        };
    }

    [Theory]
    [MemberData(nameof(MediaEndpoints))]
    public async Task MediaEndpoint_NoToken_ReturnsUnauthorized(string url)
    {
        var client = _factory.CreateClient();

        // Complete startup first so the first-time-setup policies are not what rejects the request.
        _accessToken ??= await AuthHelper.CompleteStartupAsync(client);

        var anonymousClient = _factory.CreateClient();
        var response = await anonymousClient.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
