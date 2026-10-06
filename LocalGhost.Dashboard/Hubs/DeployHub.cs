using Microsoft.AspNetCore.SignalR;

namespace LocalGhost.Dashboard.Hubs;

[Microsoft.AspNetCore.Authorization.Authorize]
public class DeployHub(LocalGhost.Dashboard.Services.ProjectAccessService access) : Hub
{
    public const string Url = "/hubs/deploy";

    public async Task JoinProject(Guid projectId)
    {
        var userId = Context.UserIdentifier;
        if (userId is null || !await access.CanAsync(userId, projectId, LocalGhost.Dashboard.Data.ProjectMemberRole.Viewer))
            throw new HubException("Project access denied.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project:{projectId}");
    }
}
