using Microsoft.AspNetCore.SignalR;

namespace LocalGhost.Dashboard.Hubs;

public class DeployHub : Hub
{
    public const string Url = "/hubs/deploy";

    // All browser clients join this group on connect
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
        await base.OnConnectedAsync();
    }
}