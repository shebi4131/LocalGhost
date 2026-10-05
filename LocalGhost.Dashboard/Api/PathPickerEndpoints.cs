using Microsoft.AspNetCore.Antiforgery;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LocalGhost.Dashboard.Api;

public static class PathPickerEndpoints
{
    public static void MapPathPickerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/path-picker", PickAsync).RequireAuthorization();
    }

    private static async Task<IResult> PickAsync(
        HttpContext context,
        PathPickerRequest request,
        IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context);

        if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!Environment.UserInteractive)
            return Results.Conflict(new { message = "The native path picker is available when the Dashboard runs interactively. Enter the path manually while it runs as a Windows service." });
        if (request.Mode is not ("folder" or "csproj"))
            return Results.BadRequest(new { message = "Unsupported path selection mode." });

        try
        {
            var selectedPath = await ShowPickerAsync(request);
            return Results.Ok(new { path = selectedPath });
        }
        catch (Exception)
        {
            return Results.Problem("Windows could not open the path picker. Enter the path manually.");
        }
    }

    private static Task<string?> ShowPickerAsync(PathPickerRequest request)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                string? selected;
                if (request.Mode == "csproj")
                {
                    using var dialog = new OpenFileDialog
                    {
                        Title = request.Title ?? "Select .NET project",
                        Filter = ".NET project (*.csproj)|*.csproj",
                        CheckFileExists = true,
                        Multiselect = false,
                        InitialDirectory = ExistingDirectory(request.CurrentPath)
                    };
                    selected = ShowDialog(dialog) == DialogResult.OK ? dialog.FileName : null;
                }
                else
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = request.Title ?? "Select folder",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = true,
                        SelectedPath = ExistingDirectory(request.CurrentPath)
                    };
                    selected = ShowDialog(dialog) == DialogResult.OK ? dialog.SelectedPath : null;
                }

                completion.TrySetResult(selected);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "LocalGhost path picker"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (Directory.Exists(path)) return Path.GetFullPath(path);
        if (File.Exists(path)) return Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

        var candidate = path;
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (candidate is not null && Directory.Exists(candidate)) return candidate;
        }
        return string.Empty;
    }

    private static DialogResult ShowDialog(CommonDialog dialog)
    {
        var foreground = GetForegroundWindow();
        return foreground == IntPtr.Zero
            ? dialog.ShowDialog()
            : dialog.ShowDialog(new WindowHandle(foreground));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private sealed class WindowHandle(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    public sealed record PathPickerRequest(string Mode, string? CurrentPath, string? Title);
}
