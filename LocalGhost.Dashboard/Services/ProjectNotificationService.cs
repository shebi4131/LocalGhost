using LocalGhost.Dashboard.Data;
using LocalGhost.Shared.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using System.Net;

namespace LocalGhost.Dashboard.Services;

public sealed class ProjectNotificationService(
    IDbContextFactory<ProjectDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    ILogger<ProjectNotificationService> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("LocalGhost.ProjectSecrets.v1");

    public async Task NotifyAsync(DeployRecord record)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == record.ProjectId);
        if (project is null || !project.EmailEnabled) return;
        if (record.Status == DeployStatus.Success && !project.NotifyOnSuccess) return;
        if (record.Status == DeployStatus.Failed && !project.NotifyOnFailure) return;

        try
        {
            var success = record.Status == DeployStatus.Success;
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(project.SmtpFromAddress));
            message.To.Add(MailboxAddress.Parse(project.SmtpToAddress));
            message.Subject = $"{(success ? "✅" : "💥")} LocalGhost — {project.Name} deployment {record.Status.ToString().ToUpperInvariant()} [{record.ShortSha}]";
            message.Body = new BodyBuilder
            {
                HtmlBody = $"""
                <div style="font-family:Segoe UI,Arial;background:#0f1117;color:#e2e8f0;padding:28px">
                  <div style="max-width:620px;margin:auto;background:#1a1d2e;border-radius:14px;overflow:hidden">
                    <div style="padding:22px 28px;background:{(success ? "#059669" : "#dc2626")};color:white"><h2 style="margin:0">{WebUtility.HtmlEncode(project.Name)} deployment {record.Status}</h2></div>
                    <div style="padding:26px 28px"><p><b>Commit:</b> {record.ShortSha}</p><p><b>Author:</b> {WebUtility.HtmlEncode(record.CommitAuthor)}</p><p><b>Message:</b> {WebUtility.HtmlEncode(record.CommitMessage)}</p><p><b>Duration:</b> {record.DurationDisplay}</p>{(record.ErrorMessage is null ? "" : $"<p style='color:#fca5a5'><b>Error:</b> {WebUtility.HtmlEncode(record.ErrorMessage)}</p>")}</div>
                  </div>
                </div>
                """
            }.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(project.SmtpHost, project.SmtpPort,
                project.SmtpEnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);
            await client.AuthenticateAsync(project.SmtpUsername, _protector.Unprotect(project.SmtpPasswordProtected));
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send deployment notification for {Project}", project.Name);
        }
    }
}
