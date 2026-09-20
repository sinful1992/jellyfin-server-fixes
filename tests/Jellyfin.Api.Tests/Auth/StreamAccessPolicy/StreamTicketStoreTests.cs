using System;
using System.IO;
using System.Net;
using Jellyfin.Api.Auth.StreamAccessPolicy;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Auth.StreamAccessPolicy
{
    public sealed class StreamTicketStoreTests : IDisposable
    {
        private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "jf-tickets-" + Guid.NewGuid().ToString("N"));
        private readonly IApplicationPaths _paths;

        public StreamTicketStoreTests()
        {
            Directory.CreateDirectory(_dataPath);
            var paths = new Mock<IApplicationPaths>();
            paths.Setup(p => p.DataPath).Returns(_dataPath);
            _paths = paths.Object;
        }

        [Fact]
        public void Ticket_SurvivesRestart()
        {
            var address = IPAddress.Parse("192.168.1.10");
            var itemId = Guid.NewGuid();

            var before = new StreamTicketStore(_paths, NullLogger<StreamTicketStore>.Instance);
            before.Issue(address, Guid.NewGuid(), [itemId]);

            // A new store from the same data path is what a restart produces.
            var after = new StreamTicketStore(_paths, NullLogger<StreamTicketStore>.Instance);

            Assert.True(after.TryRedeem(address, [itemId]));
            Assert.False(after.TryRedeem(IPAddress.Parse("192.168.1.11"), [itemId]));
            Assert.False(after.TryRedeem(address, [Guid.NewGuid()]));
        }

        [Fact]
        public void ExpiredTicket_IsNotRestored()
        {
            var address = IPAddress.Parse("192.168.1.10");
            var itemId = Guid.NewGuid();
            var file = Path.Combine(_dataPath, "stream-tickets.json");
            File.WriteAllText(file, $"[{{\"Address\":\"{address}\",\"Id\":\"{itemId}\",\"Expiry\":\"{DateTimeOffset.UtcNow.AddMinutes(-1):O}\"}}]");

            var store = new StreamTicketStore(_paths, NullLogger<StreamTicketStore>.Instance);

            Assert.False(store.TryRedeem(address, [itemId]));
        }

        [Fact]
        public void CorruptFile_StartsEmpty()
        {
            File.WriteAllText(Path.Combine(_dataPath, "stream-tickets.json"), "not json");

            var store = new StreamTicketStore(_paths, NullLogger<StreamTicketStore>.Instance);

            Assert.False(store.TryRedeem(IPAddress.Loopback, [Guid.NewGuid()]));
        }

        [Fact]
        public void MemoryOnlyStore_WritesNothing()
        {
            var store = new StreamTicketStore();
            store.Issue(IPAddress.Loopback, Guid.NewGuid(), [Guid.NewGuid()]);

            Assert.Empty(Directory.GetFiles(_dataPath));
        }

        public void Dispose()
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }
}
