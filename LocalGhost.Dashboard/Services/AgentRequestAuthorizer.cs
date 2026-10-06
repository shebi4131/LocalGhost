using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LocalGhost.Dashboard.Services;

public sealed class AgentRequestAuthorizer(IConfiguration configuration)
{
    public bool IsAllowed(HttpContext context)
    {
        if (!context.Request.IsHttps &&
            (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)))
            return false;
        var configured = configuration["AgentApiKey"];
        if (string.IsNullOrWhiteSpace(configured))
            return context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

        var supplied = context.Request.Headers["X-LocalGhost-Agent-Key"].ToString();
        if (string.IsNullOrEmpty(supplied)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configured), Encoding.UTF8.GetBytes(supplied));
    }
}
