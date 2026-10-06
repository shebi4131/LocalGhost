using LocalGhost.Dashboard.Data;
using Microsoft.EntityFrameworkCore;

namespace LocalGhost.Dashboard.Services;

public sealed class UserNotificationService(
    IDbContextFactory<ProjectDbContext> dbFactory,
    ILogger<UserNotificationService> logger)
{
    public event Action<string, UserNotificationEntity>? Created;
    public event Action<string>? Changed;

    public async Task PublishProjectAsync(Guid projectId, string eventKey, string kind, string title,
        string message, string url, ProjectMemberRole minimumRole = ProjectMemberRole.Viewer)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId && !x.IsArchived);
            if (project is null) return;
            var members = await db.ProjectMembers.AsNoTracking()
                .Where(x => x.ProjectGroupId == project.ProjectGroupId).ToListAsync();
            var grants = await db.ProjectEnvironmentGrants.AsNoTracking()
                .Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.UserId, x => x.Role);
            var recipients = members.Where(x => (grants.GetValueOrDefault(x.UserId, x.Role)) >= minimumRole)
                .Select(x => x.UserId).Append(project.OwnerUserId).Distinct(StringComparer.Ordinal).ToList();
            var existing = await db.UserNotifications.AsNoTracking()
                .Where(x => x.EventKey == eventKey && recipients.Contains(x.UserId))
                .Select(x => x.UserId).ToListAsync();
            var now = DateTime.UtcNow;
            var added = recipients.Except(existing, StringComparer.Ordinal).Select(userId => new UserNotificationEntity
            {
                UserId = userId, ProjectId = projectId, EventKey = eventKey, Kind = kind,
                Title = title, Message = message, Url = url, CreatedAt = now
            }).ToList();
            if (added.Count == 0) return;
            db.UserNotifications.AddRange(added);
            await db.SaveChangesAsync();
            foreach (var item in added) Created?.Invoke(item.UserId, item);
        }
        catch (Exception ex)
        {
            // Alerts must never prevent a deployment or approval decision.
            logger.LogError(ex, "Could not publish notification {EventKey} for project {ProjectId}", eventKey, projectId);
        }
    }

    public async Task<NotificationPage> GetPageAsync(string userId, int page, int pageSize = 10, bool unreadOnly = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.UserNotifications.AsNoTracking().Where(x => x.UserId == userId &&
            db.Projects.Any(project => project.Id == x.ProjectId && !project.IsArchived &&
                (project.OwnerUserId == userId || db.ProjectMembers.Any(member =>
                    member.ProjectGroupId == project.ProjectGroupId && member.UserId == userId))));
        var unread = await query.CountAsync(x => x.ReadAt == null);
        if (unreadOnly) query = query.Where(x => x.ReadAt == null);
        var total = await query.CountAsync();
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new NotificationPage(items, total, unread, page);
    }

    public async Task<bool> MarkReadAsync(string userId, Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.UserNotifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (item is null) return false;
        if (item.ReadAt is null)
        {
            item.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Changed?.Invoke(userId);
        }
        return true;
    }

    public async Task MarkAllReadAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var items = await db.UserNotifications.Where(x => x.UserId == userId && x.ReadAt == null).ToListAsync();
        if (items.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var item in items) item.ReadAt = now;
        await db.SaveChangesAsync();
        Changed?.Invoke(userId);
    }
}

public sealed record NotificationPage(IReadOnlyList<UserNotificationEntity> Items, int Total, int Unread, int Page);
