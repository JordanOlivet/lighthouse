using FluentAssertions;
using Lighthouse.Services.Oidc;
using Microsoft.Extensions.Caching.Memory;

namespace Lighthouse.Tests.Services;

public class OidcLinkTicketStoreTests
{
    private static OidcLinkTicketStore NewStore() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void Consume_IssuedTicket_ReturnsUserIdOnce()
    {
        var store = NewStore();
        var ticket = store.Issue(7);

        ticket.Should().HaveLength(43); // 32 random bytes, base64url without padding
        store.Consume(ticket).Should().Be(7);
        store.Consume(ticket).Should().BeNull();
    }

    [Fact]
    public void Consume_UnknownOrEmptyTicket_ReturnsNull()
    {
        var store = NewStore();
        store.Consume("nope").Should().BeNull();
        store.Consume(null).Should().BeNull();
        store.Consume("").Should().BeNull();
    }
}
