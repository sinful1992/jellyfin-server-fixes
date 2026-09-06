using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Api.Auth.StreamAccessPolicy;
using Jellyfin.Api.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Auth.StreamAccessPolicy
{
    /// <summary>
    /// Covers the branch the LAN-scoped predecessor of this policy could not exercise on a
    /// NATed Docker host: an anonymous caller that never negotiated playback must be refused,
    /// whatever subnet it is on.
    /// </summary>
    public class StreamAccessHandlerTests
    {
        private static readonly IPAddress _client = IPAddress.Parse("192.168.1.42");
        private static readonly IPAddress _otherClient = IPAddress.Parse("192.168.1.99");

        [Fact]
        public async Task Anonymous_without_a_ticket_is_refused()
        {
            var itemId = Guid.NewGuid();
            var store = new StreamTicketStore();

            Assert.False(await Authorize(store, Anonymous(), _client, itemId));
        }

        [Fact]
        public async Task Anonymous_with_a_ticket_for_the_item_is_allowed()
        {
            var itemId = Guid.NewGuid();
            var store = new StreamTicketStore();
            store.Issue(_client, Guid.NewGuid(), new[] { itemId });

            Assert.True(await Authorize(store, Anonymous(), _client, itemId));
        }

        [Fact]
        public async Task A_ticket_does_not_travel_to_another_address()
        {
            var itemId = Guid.NewGuid();
            var store = new StreamTicketStore();
            store.Issue(_client, Guid.NewGuid(), new[] { itemId });

            Assert.False(await Authorize(store, Anonymous(), _otherClient, itemId));
        }

        [Fact]
        public async Task A_ticket_does_not_cover_another_item()
        {
            var store = new StreamTicketStore();
            store.Issue(_client, Guid.NewGuid(), new[] { Guid.NewGuid() });

            Assert.False(await Authorize(store, Anonymous(), _client, Guid.NewGuid()));
        }

        [Fact]
        public async Task A_ticket_covers_a_media_source_id_sent_in_the_query()
        {
            // Direct play addresses the route by item id but names the media source in the
            // query string; for a multi-version item the two differ.
            var itemId = Guid.NewGuid();
            var mediaSourceId = Guid.NewGuid();
            var store = new StreamTicketStore();
            store.Issue(_client, Guid.NewGuid(), new[] { itemId, mediaSourceId });

            Assert.True(await Authorize(store, Anonymous(), _client, Guid.NewGuid(), mediaSourceId));
        }

        [Fact]
        public async Task An_authenticated_caller_needs_no_ticket()
        {
            var store = new StreamTicketStore();

            Assert.True(await Authorize(store, Authenticated(), _client, Guid.NewGuid()));
        }

        private static ClaimsPrincipal Anonymous()
            => new ClaimsPrincipal(new ClaimsIdentity());

        private static ClaimsPrincipal Authenticated()
        {
            var claims = new[]
            {
                new Claim(InternalClaimTypes.UserId, Guid.NewGuid().ToString("N")),
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        private static async Task<bool> Authorize(
            IStreamTicketStore store,
            ClaimsPrincipal principal,
            IPAddress remoteAddress,
            Guid routeItemId,
            Guid? queryMediaSourceId = null)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = remoteAddress;
            httpContext.Request.RouteValues = new RouteValueDictionary
            {
                ["itemId"] = routeItemId.ToString("N"),
            };

            if (queryMediaSourceId.HasValue)
            {
                httpContext.Request.QueryString =
                    new QueryString("?mediaSourceId=" + queryMediaSourceId.Value.ToString("N"));
            }

            var accessor = new Mock<IHttpContextAccessor>();
            accessor.Setup(a => a.HttpContext).Returns(httpContext);

            var handler = new StreamAccessHandler(store, accessor.Object);
            var requirement = new StreamAccessRequirement();
            var context = new AuthorizationHandlerContext(
                new[] { requirement },
                principal,
                null);

            await handler.HandleAsync(context);

            return context.HasSucceeded;
        }
    }
}
