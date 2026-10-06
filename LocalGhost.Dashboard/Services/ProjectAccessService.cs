using LocalGhost.Dashboard.Data;
using Microsoft.EntityFrameworkCore;

namespace LocalGhost.Dashboard.Services;

public sealed class ProjectAccessService(IDbContextFactory<ProjectDbContext> dbFactory)
{
    public async Task<ProjectMemberRole?> GetRoleAsync(string userId, Guid projectId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking()
            .Where(x => x.Id == projectId && !x.IsArchived)
            .Select(x => new { x.OwnerUserId, x.ProjectGroupId }).FirstOrDefaultAsync();
        if (project is null) return null;
        if (project.OwnerUserId == userId) return ProjectMemberRole.Manager;
        var member = await db.ProjectMembers.AsNoTracking()
            .Where(x => x.ProjectGroupId == project.ProjectGroupId && x.UserId == userId)
            .Select(x => (ProjectMemberRole?)x.Role).FirstOrDefaultAsync();
        if (member is null) return null;
        var environment = await db.ProjectEnvironmentGrants.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.UserId == userId)
            .Select(x => (ProjectMemberRole?)x.Role).FirstOrDefaultAsync();
        return environment ?? member;
    }

    public async Task<bool> CanAsync(string userId, Guid projectId, ProjectMemberRole minimum) =>
        await GetRoleAsync(userId, projectId) is { } role && role >= minimum;
}
