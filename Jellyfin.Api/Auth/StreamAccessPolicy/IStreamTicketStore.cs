using System;
using System.Collections.Generic;
using System.Net;

namespace Jellyfin.Api.Auth.StreamAccessPolicy
{
    /// <summary>
    /// Short-lived grants that let an already-authenticated playback negotiation authorize
    /// the credential-less media requests the client makes immediately afterwards.
    /// </summary>
    public interface IStreamTicketStore
    {
        /// <summary>
        /// Records that <paramref name="remoteAddress"/> authenticated and negotiated playback
        /// of the given item and media sources.
        /// </summary>
        /// <param name="remoteAddress">Normalized remote address of the negotiating client.</param>
        /// <param name="userId">The authenticated user, recorded for diagnostics.</param>
        /// <param name="ids">Item id and media source ids the negotiation covered.</param>
        void Issue(IPAddress? remoteAddress, Guid userId, IReadOnlyList<Guid> ids);

        /// <summary>
        /// Looks for a live ticket covering any of <paramref name="ids"/> for
        /// <paramref name="remoteAddress"/>, extending it when one is found.
        /// </summary>
        /// <param name="remoteAddress">Normalized remote address of the requesting client.</param>
        /// <param name="ids">Candidate ids taken from the request route and query.</param>
        /// <returns><c>true</c> if a live ticket covered the request.</returns>
        bool TryRedeem(IPAddress? remoteAddress, IReadOnlyList<Guid> ids);
    }
}
