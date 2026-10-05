using Microsoft.EntityFrameworkCore;

namespace LocalGhost.Dashboard.Data;

public sealed class ProjectDbContext(DbContextOptions<ProjectDbContext> options) : DbContext(options)
{
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<DeploymentRunEntity> DeploymentRuns => Set<DeploymentRunEntity>();
    public DbSet<ProjectBranchBaselineEntity> ProjectBranchBaselines => Set<ProjectBranchBaselineEntity>();
    public DbSet<DeploymentLogEntity> DeploymentLogs => Set<DeploymentLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectEntity>().HasIndex(x => new { x.OwnerUserId, x.Name });
        modelBuilder.Entity<DeploymentRunEntity>().HasIndex(x => new { x.ProjectId, x.StartedAt });
        modelBuilder.Entity<ProjectBranchBaselineEntity>().HasKey(x => new { x.ProjectId, x.Branch });
        modelBuilder.Entity<DeploymentLogEntity>().HasIndex(x => new { x.ProjectId, x.DeployId, x.Timestamp });
    }
}
