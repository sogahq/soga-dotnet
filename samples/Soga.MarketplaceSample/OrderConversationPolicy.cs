using Microsoft.EntityFrameworkCore;
using Soga.Authorization;

/// <summary>
/// Only the buyer and the seller of an order may open its conversation, and only with each other. Everything else, such
/// as unsolicited direct messages or groups, is refused. Once a conversation exists, Soga's own membership checks apply.
/// </summary>
internal sealed class OrderConversationPolicy(MarketplaceDbContext db) : ISogaAuthorizationPolicy
{
    private const string Prefix = "order:";

    public async ValueTask<bool> IsAllowedAsync(SogaAuthorizationContext context, CancellationToken cancellationToken)
    {
        switch (context.Action)
        {
            case SogaAction.CreateGroup:
                return false;

            case SogaAction.CreateDirectConversation:
                if (context.ExternalReference is not { } reference ||
                    !reference.StartsWith(Prefix, StringComparison.Ordinal) ||
                    !int.TryParse(reference[Prefix.Length..], out var orderId))
                {
                    return false;
                }

                var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
                return order is not null &&
                    ((context.UserId == order.BuyerId && context.TargetUserId == order.SellerId) ||
                     (context.UserId == order.SellerId && context.TargetUserId == order.BuyerId));

            default:
                return true;
        }
    }
}
