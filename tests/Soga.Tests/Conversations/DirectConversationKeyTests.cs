using Soga.Conversations;

namespace Soga.Tests.Conversations;

public sealed class DirectConversationKeyTests
{
    [Fact]
    public void Create_WhenUserOrderIsSwapped_ReturnsSameKey()
    {
        Assert.Equal(
            DirectConversationKey.Create("alice", "bob", null),
            DirectConversationKey.Create("bob", "alice", null));
    }

    [Fact]
    public void Create_WhenExternalReferenceDiffers_ReturnsDifferentKeys()
    {
        var general = DirectConversationKey.Create("alice", "bob", null);
        var order1 = DirectConversationKey.Create("alice", "bob", "order:1");
        var order2 = DirectConversationKey.Create("alice", "bob", "order:2");

        Assert.Equal(3, new[] { general, order1, order2 }.Distinct().Count());
    }

    [Fact]
    public void Create_WhenExternalReferenceIsEmptyVersusNull_ReturnsDifferentKeys()
    {
        Assert.NotEqual(
            DirectConversationKey.Create("alice", "bob", null),
            DirectConversationKey.Create("alice", "bob", string.Empty));
    }

    [Fact]
    public void Create_WhenUserIdsAreCaseDifferent_TreatsThemAsDifferentUsers()
    {
        Assert.NotEqual(
            DirectConversationKey.Create("Alice", "bob", null),
            DirectConversationKey.Create("alice", "bob", null));
    }

    [Fact]
    public void Create_WhenBoundariesShift_DoesNotCollide()
    {
        Assert.NotEqual(
            DirectConversationKey.Create("ab", "c", null),
            DirectConversationKey.Create("a", "bc", null));
    }

    [Fact]
    public void Create_ReturnsFixedLengthHex()
    {
        var key = DirectConversationKey.Create("alice", "bob", "order:1");

        Assert.Equal(64, key.Length);
    }
}
