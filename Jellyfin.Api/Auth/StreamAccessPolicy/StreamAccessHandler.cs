using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Api.Auth.StreamAccessPolicy
{
    /// <summary>
    /// Authorizes the media file endpoints.
    /// </summary>
    /// <remarks>
    /// These routes cannot simply require credentials: several official clients compose the
    /// media URL themselves from the playback negotiation result and attach no token to it
    /// (measured on Jellyfin for Android TV 0.19.10), so a plain [Authorize] 401s them off
    /// direct play and onto a server-side remux. Instead an authenticated PlaybackInfo call
    /// issues a short-lived ticket for the item and its media sources, bound to the caller's
    /// address, and this handler redeems it. Anonymous callers who never negotiated playback
    /// get nothing, which is the case the endpoints were open to before.
    /// </remarks>
    public class StreamAccessHandler : AuthorizationHandler<StreamAccessRequirement>
    {
        private static readonly string[] _idRouteKeys =
        {
            "itemId",
            "routeItemId",
            "videoId",
            "mediaSourceId",
            "routeMediaSourceId"
        };

        private readonly IStreamTicketStore _ticketStore;
        private readonly IHttpContextAccessor _httpContextAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamAccessHandler"/> class.
        /// </summary>
        /// <param name="ticketStore">Instance of the <see cref="IStreamTicketStore"/> interface.</param>
        /// <param name="httpContextAccessor">Instance of the <see cref="IHttpContextAccessor"/> interface.</param>
        public StreamAccessHandler(
            IStreamTicketStore ticketStore,
            IHttpContextAccessor httpContextAccessor)
        {
            _ticketStore = ticketStore;
            _httpContextAccessor = httpContextAccessor;
        }

        /// <inheritdoc />
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StreamAccessRequirement requirement)
        {
            ArgumentNullException.ThrowIfNull(context);

            // A caller that did present credentials is treated like any other endpoint.
            // DefaultAuthorizationHandler has already had its chance to Fail() this context
            // on remote access or parental schedule, and that failure outranks this success.
            if (context.User.GetIsApiKey() || !context.User.GetUserId().IsEmpty())
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is not null
                && _ticketStore.TryRedeem(httpContext.GetNormalizedRemoteIP(), GetCandidateIds(httpContext)))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            context.Fail();
            return Task.CompletedTask;
        }

        private static IReadOnlyList<Guid> GetCandidateIds(HttpContext httpContext)
        {
            var ids = new List<Guid>(_idRouteKeys.Length + 1);

            foreach (var key in _idRouteKeys)
            {
                if (httpContext.Request.RouteValues.TryGetValue(key, out var routeValue)
                    && Guid.TryParse(routeValue?.ToString(), out var routeId))
                {
                    ids.Add(routeId);
                }
            }

            // Direct play sends the media source id in the query string, not the route.
            if (httpContext.Request.Query.TryGetValue("mediaSourceId", out var queryValue)
                && Guid.TryParse(queryValue.ToString(), out var queryId))
            {
                ids.Add(queryId);
            }

            return ids;
        }
    }
}
