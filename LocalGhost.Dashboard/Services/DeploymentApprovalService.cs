using LocalGhost.Dashboard.Data;
using Microsoft.EntityFrameworkCore;

namespace LocalGhost.Dashboard.Services;

public sealed class DeploymentApprovalService(
    IDbContextFactory<ProjectDbContext> dbFactory,
    ProjectAccessService access)
{
    public async Task<List<ApprovalView>> GetPendingAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await (from request in db.DeploymentApprovals.AsNoTracking()
            join project in db.Projects.AsNoTracking() on request.ProjectId equals project.Id
            where request.Approved == null && !project.IsArchived &&
                (project.OwnerUserId == userId || db.ProjectMembers.Any(m =>
                    m.ProjectGroupId == project.ProjectGroupId && m.UserId == userId))
            orderby request.RequestedAt descending
            select new ApprovalView(request.Id, project.Id, project.Name, project.Environment,
                request.Branch, request.CommitSha, request.RequestedAt)).ToListAsync();
    }

    public async Task<bool> DecideAsync(string userId, Guid requestId, bool approve)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var request = await db.DeploymentApprovals.FirstOrDefaultAsync(x => x.Id == requestId && x.Approved == null);
        if (request is null || !await access.CanAsync(userId, request.ProjectId, ProjectMemberRole.Manager)) return false;
        var project = await db.Projects.SingleAsync(x => x.Id == request.ProjectId);
        request.Approved = approve;
        request.DecidedByUserId = userId;
        request.DecidedAt = DateTime.UtcNow;
        if (approve) project.LastPolledAt = null;
        await db.SaveChangesAsync();
        return true;
    }
}

public sealed record ApprovalView(Guid Id, Guid ProjectId, string ProjectName,
    string Environment, string Branch, string CommitSha, DateTime RequestedAt);
