using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace LocalGhost.Dashboard.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/register", RegisterAsync).AllowAnonymous();
        app.MapPost("/auth/login", LoginAsync).AllowAnonymous();
        app.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();
        app.MapPost("/auth/profile", UpdateProfileAsync).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<IdentityUser> users)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync();
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Error("/register", "Email and password are required.");
        if (password != confirmPassword)
            return Error("/register", "Passwords do not match.");

        var result = await users.CreateAsync(new IdentityUser
        {
            UserName = email,
            Email = email
        }, password);

        if (!result.Succeeded)
            return Error("/register", string.Join(" ", result.Errors.Select(e => e.Description)));

        return Results.Redirect("/login?registered=true");
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        SignInManager<IdentityUser> signInManager)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync();
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var rememberMe = form["rememberMe"] == "on";
        var returnUrl = SafeReturnUrl(form["returnUrl"]);

        var result = await signInManager.PasswordSignInAsync(
            email, password, rememberMe, lockoutOnFailure: true);

        return result.Succeeded
            ? Results.LocalRedirect(returnUrl)
            : Error("/login", "Invalid email or password.", returnUrl);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        SignInManager<IdentityUser> signInManager)
    {
        await antiforgery.ValidateRequestAsync(context);
        await signInManager.SignOutAsync();
        return Results.Redirect("/login");
    }

    private static async Task<IResult> UpdateProfileAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        UserManager<IdentityUser> users,
        SignInManager<IdentityUser> signInManager)
    {
        await antiforgery.ValidateRequestAsync(context);
        var user = await users.GetUserAsync(context.User);
        if (user is null) return Results.Redirect("/login");

        var form = await context.Request.ReadFormAsync();
        var displayName = form["displayName"].ToString().Trim();
        var email = form["email"].ToString().Trim();
        var phone = form["phone"].ToString().Trim();
        var currentPassword = form["currentPassword"].ToString();
        var newPassword = form["newPassword"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();

        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(email))
            return Error("/profile", "Display name and email are required.");

        var owner = await users.FindByEmailAsync(email);
        if (owner is not null && owner.Id != user.Id)
            return Error("/profile", "That email address is already in use.");

        if (!string.IsNullOrEmpty(currentPassword) || !string.IsNullOrEmpty(newPassword))
        {
            if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword))
                return Error("/profile", "Enter both your current and new password.");
            if (newPassword != confirmPassword)
                return Error("/profile", "New passwords do not match.");

            var passwordResult = await users.ChangePasswordAsync(user, currentPassword, newPassword);
            if (!passwordResult.Succeeded)
                return Error("/profile", string.Join(" ", passwordResult.Errors.Select(e => e.Description)));
        }

        user.Email = email;
        user.UserName = email;
        user.PhoneNumber = string.IsNullOrWhiteSpace(phone) ? null : phone;
        var updateResult = await users.UpdateAsync(user);
        if (!updateResult.Succeeded)
            return Error("/profile", string.Join(" ", updateResult.Errors.Select(e => e.Description)));

        const string displayNameClaim = "display_name";
        var oldClaim = (await users.GetClaimsAsync(user)).FirstOrDefault(c => c.Type == displayNameClaim);
        var claimResult = oldClaim is null
            ? await users.AddClaimAsync(user, new Claim(displayNameClaim, displayName))
            : await users.ReplaceClaimAsync(user, oldClaim, new Claim(displayNameClaim, displayName));

        if (!claimResult.Succeeded)
            return Error("/profile", "Your account was updated, but the display name could not be saved.");

        await signInManager.RefreshSignInAsync(user);
        return Results.Redirect("/profile?saved=true");
    }

    private static string SafeReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') && !value.StartsWith("//")
            ? value
            : "/dashboard";

    private static IResult Error(string path, string message, string? returnUrl = null)
    {
        var query = $"error={Uri.EscapeDataString(message)}";
        if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl != "/")
            query += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return Results.Redirect($"{path}?{query}");
    }
}
