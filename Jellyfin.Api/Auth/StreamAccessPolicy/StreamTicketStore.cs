using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

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

        /// <summary>
        /// How often a ticket slid by an in-flight stream is written back to disk. Issue always
        /// writes; a slide only refreshes a ticket that was already on disk, so it can wait.
        /// </summary>
        private static readonly TimeSpan _slideSaveInterval = TimeSpan.FromMinutes(1);

        private readonly ConcurrentDictionary<TicketKey, DateTimeOffset> _tickets = new();

        private readonly string? _path;

        private readonly ILogger<StreamTicketStore>? _logger;

        private readonly object _saveLock = new();

        private long _lastPruneTicks = DateTimeOffset.UtcNow.UtcTicks;

        private long _lastSaveTicks;

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamTicketStore"/> class that keeps
        /// tickets in memory only.
        /// </summary>
        public StreamTicketStore()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamTicketStore"/> class that persists
        /// tickets under the data path, so a server restart does not revoke them.
        /// </summary>
        /// <remarks>
        /// Clients such as the Android TV app fetch a direct-play stream with no credentials at all,
        /// relying on the ticket issued at PlaybackInfo. With the table in memory only, every restart
        /// turned an in-progress playback into a loop of 401s until the user pressed play again --
        /// seen live on 2026-09-20. Stock Jellyfin has no such failure because it has no such check;
        /// the fork must not introduce one.
        /// </remarks>
        /// <param name="applicationPaths">The application paths.</param>
        /// <param name="logger">The logger.</param>
        public StreamTicketStore(IApplicationPaths applicationPaths, ILogger<StreamTicketStore> logger)
        {
            ArgumentNullException.ThrowIfNull(applicationPaths);

            _path = Path.Combine(applicationPaths.DataPath, "stream-tickets.json");
            _logger = logger;
            Load();
        }

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
            Save();
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

            if (redeemed && now - new DateTimeOffset(Interlocked.Read(ref _lastSaveTicks), TimeSpan.Zero) >= _slideSaveInterval)
            {
                Save();
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

        private void Load()
        {
            if (_path is null || !File.Exists(_path))
            {
                return;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var loaded = 0;
                using var stream = File.OpenRead(_path);
                foreach (var ticket in JsonSerializer.Deserialize<List<PersistedTicket>>(stream) ?? [])
                {
                    if (ticket.Expiry > now && ticket.Address is not null)
                    {
                        _tickets[new TicketKey(ticket.Address, ticket.Id)] = ticket.Expiry;
                        loaded++;
                    }
                }

                _logger?.LogInformation("Restored {Count} stream ticket(s) from {Path}", loaded, _path);
            }
            catch (Exception ex)
            {
                // A corrupt file costs the tickets it held, nothing more: clients renegotiate.
                _logger?.LogWarning(ex, "Could not read stream tickets from {Path}; starting empty", _path);
            }
        }

        private void Save()
        {
            if (_path is null)
            {
                return;
            }

            lock (_saveLock)
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var tickets = new List<PersistedTicket>(_tickets.Count);
                    foreach (var (key, expiry) in _tickets)
                    {
                        if (expiry > now)
                        {
                            tickets.Add(new PersistedTicket(key.Address, key.Id, expiry));
                        }
                    }

                    // Write beside, then move over: a crash mid-write must not leave a torn file.
                    var temp = _path + ".tmp";
                    using (var stream = File.Create(temp))
                    {
                        JsonSerializer.Serialize(stream, tickets);
                    }

                    File.Move(temp, _path, overwrite: true);
                    Interlocked.Exchange(ref _lastSaveTicks, now.UtcTicks);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Could not write stream tickets to {Path}", _path);
                }
            }
        }

        private readonly record struct TicketKey(string Address, Guid Id);

        private sealed record PersistedTicket(string Address, Guid Id, DateTimeOffset Expiry);
    }
}
