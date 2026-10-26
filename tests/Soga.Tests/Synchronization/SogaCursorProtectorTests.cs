using Microsoft.AspNetCore.DataProtection;
using Soga.Synchronization;

namespace Soga.Tests.Synchronization;

public sealed class SogaCursorProtectorTests
{
    private const int HistoryKind = (int)SogaCursorKind.History;
    private const int SyncKind = (int)SogaCursorKind.Sync;

    private readonly SogaCursorProtector _protector = new(DataProtectionProvider.Create("soga-tests"));

    [Fact]
    public void Decode_WhenCursorIsRoundTripped_ReturnsPosition()
    {
        var cursor = _protector.Encode(SogaCursorKind.History, "t1", "conversation-a", 42);

        Assert.True(_protector.TryDecode(cursor, SogaCursorKind.History, "t1", "conversation-a", out var position));
        Assert.Equal(42, position);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    public void Decode_WhenCursorIsMalformed_ReturnsFalse(string? cursor)
    {
        Assert.False(_protector.TryDecode(cursor, SogaCursorKind.Sync, "t1", "alice", out _));
    }

    [Fact]
    public void Decode_WhenCursorIsTampered_ReturnsFalse()
    {
        var cursor = _protector.Encode(SogaCursorKind.Sync, "t1", "alice", 7);
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "B" : "A") + cursor[^1];

        Assert.False(_protector.TryDecode(tampered, SogaCursorKind.Sync, "t1", "alice", out _));
    }

    [Fact]
    public void Decode_WhenCursorIsOversized_ReturnsFalse()
    {
        Assert.False(_protector.TryDecode(new string('A', 5_000), SogaCursorKind.Sync, "t1", "alice", out _));
    }

    [Theory]
    [InlineData(SyncKind, "t1", "conversation-a")]
    [InlineData(HistoryKind, "t2", "conversation-a")]
    [InlineData(HistoryKind, "t1", "conversation-b")]
    public void Decode_WhenKindTenantOrScopeDiffers_ReturnsFalse(int kind, string tenant, string scope)
    {
        var cursor = _protector.Encode(SogaCursorKind.History, "t1", "conversation-a", 5);

        Assert.False(_protector.TryDecode(cursor, (SogaCursorKind)kind, tenant, scope, out _));
    }

    [Fact]
    public void Decode_WhenCursorWasIssuedUnderAnotherKey_ReturnsFalse()
    {
        var other = new SogaCursorProtector(DataProtectionProvider.Create("another-application"));
        var cursor = other.Encode(SogaCursorKind.Sync, "t1", "alice", 1);

        Assert.False(_protector.TryDecode(cursor, SogaCursorKind.Sync, "t1", "alice", out _));
    }
}
