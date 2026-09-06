using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Jellyfin.Extensions;

namespace Jellyfin.Api.Auth.StreamAccessPolicy
{
    /// <summary>
    /// In-memory <see cref="IStreamTicketStore"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not persisted. A ticket is only useful for the playback it was issued
    /// for, and a server restart tears down the streams anyway; on the next play the client
    /// re-negotiates and a fresh ticket is issued.
    /// </remarks>
    public class StreamTicketStore : IStreamTicketStore
    {
        /// <summary>
        /// How long a ticket stays valid without being used. Every successful media request
        /// slides it, so this only has to outlast the gap between negotiating playback and
        /// the next byte range the client asks for -- which for a paused client can be long.
        /// </summary>
        public static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(4);

        private const int PruneCountThreshold = 256;

        private static readonly TimeSpan _pruneInterval = TimeSpan.FromMinutes(5);

        private readonly ConcurrentDictionary<TicketKey, DateTimeOffset> _tickets = new();

        private long _lastPruneTicks = DateTimeOffset.UtcNow.UtcTicks;

        /// <inheritdoc />
        public void Issue(IPAddress? remoteAddress, Guid userId, IReadOnlyList<Guid> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);

            var address = Normalize(remoteAddress);
            var expiry = DateTimeOffset.UtcNow + TicketLifetime;

            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i].IsEmpty())
                {
                    continue;
                }

                _tickets[new TicketKey(address, ids[i])] = expiry;
            }

            Prune();
        }

        /// <inheritdoc />
        public bool TryRedeem(IPAddress? remoteAddress, IReadOnlyList<Guid> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);

            var address = Normalize(remoteAddress);
            var now = DateTimeOffset.UtcNow;
            var redeemed = false;

            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i].IsEmpty())
                {
                    continue;
                }

                var key = new TicketKey(address, ids[i]);
                if (!_tickets.TryGetValue(key, out var expiry))
                {
                    continue;
                }

                if (expiry <= now)
                {
                    _tickets.TryRemove(new KeyValuePair<TicketKey, DateTimeOffset>(key, expiry));
                    continue;
                }

                // Slide the ticket so a long playback does not expire mid-stream.
                _tickets[key] = now + TicketLifetime;
                redeemed = true;
            }

            return redeemed;
        }

        private static string Normalize(IPAddress? remoteAddress)
            // A null address means loopback, which AnonymousLanAccessHandler treats the same way.
            => remoteAddress?.ToString() ?? string.Empty;

        private void Prune()
        {
            var now = DateTimeOffset.UtcNow;
            var last = new DateTimeOffset(Interlocked.Read(ref _lastPruneTicks), TimeSpan.Zero);
            if (_tickets.Count < PruneCountThreshold && now - last < _pruneInterval)
            {
                return;
            }

            Interlocked.Exchange(ref _lastPruneTicks, now.UtcTicks);

            foreach (var ticket in _tickets)
            {
                if (ticket.Value <= now)
                {
                    _tickets.TryRemove(ticket);
                }
            }
        }

        private readonly record struct TicketKey(string Address, Guid Id);
    }
}
