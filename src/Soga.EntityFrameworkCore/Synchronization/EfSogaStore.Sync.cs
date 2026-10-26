using Microsoft.EntityFrameworkCore;
using Soga.EntityFrameworkCore.Entities;
using Soga.Persistence;

namespace Soga.EntityFrameworkCore.Repositories;

internal sealed partial class EfSogaStore<TContext>
{
    public async Task<long> GetChangeHeadAsync(string tenantId, CancellationToken cancellationToken)
    {
        var head = await _db.Set<TenantStateRow>()
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => (long?)t.LastChangeSequence)
            .FirstOrDefaultAsync(cancellationToken);
        return head ?? 0;
    }

    public async Task<ChangePage> GetChangesAsync(
        string tenantId,
        string userId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        // Access is evaluated now, not when the change was written: a user who lost a conversation stops receiving its
        // changes. Changes addressed to the user (their own removal) stay visible so they learn they lost access.
        var accessible = _db.Set<ParticipantRow>()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.UserId == userId && p.LeftAt == null)
            .Select(p => p.ConversationId);

        var rows = await _db.Set<ChangeRow>()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId &&
                c.Sequence > afterSequence &&
                (accessible.Contains(c.ConversationId) || c.TargetUserId == userId))
            .OrderBy(c => c.Sequence)
            .Take(limit + 1)
            .Select(c => new { c.Sequence, c.Payload })
            .ToListAsync(cancellationToken);

        var page = rows.Take(limit).Select(r => new ChangeRecord(r.Sequence, r.Payload)).ToList();
        return new ChangePage(page, rows.Count > limit);
    }
}
