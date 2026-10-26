using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ERPSystem.Web.Setup;

/// <summary>
/// تهيئة أول مسؤول — تعمل مرة واحدة فقط على تثبيت جديد:
///  - لا تُنشئ أي مستخدم إن وُجد أي مستخدم في النظام (النظام مهيأ بالفعل).
///  - تتطلب رمز تهيئة يُقدَّم عبر متغير البيئة Setup__Token (أو Setup:Token)، ولا يوجد رمز افتراضي في الكود.
///  - كلمة المرور يدخلها المسؤول أثناء التهيئة ولا تُخزَّن إلا كتجزئة Identity داخل قاعدة العميل.
///  - بعد النجاح يصبح شرط (لا مستخدمين) غير متحقق، فتُغلق الآلية نهائيًا.
/// </summary>
public static class FirstRunSetupEndpoints
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> Attempts = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly string[] ModuleRoles =
    { "Sales", "Purchases", "Inventory", "Accounting", "Expenses", "Crm", "Hr", "Reports", "Pos", "Permissions" };

    public static void Map(WebApplication app)
    {
        app.MapGet("/setup", async (HttpContext ctx, UserManager<IdentityUser> users, IConfiguration cfg, IAntiforgery antiforgery) =>
        {
            if (!await IsAvailableAsync(users, cfg))
                return NotFound(ctx);

            var tokens = antiforgery.GetAndStoreTokens(ctx);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(Page(null, tokens.RequestToken!, locked: false));
            return Results.Empty;
        }).AllowAnonymous();

        app.MapPost("/setup", async (HttpContext ctx, UserManager<IdentityUser> users, IConfiguration cfg) =>
        {
            if (!await IsAvailableAsync(users, cfg))
                return NotFound(ctx);

            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (IsLockedOut(ip))
                return await PageWithStatus(ctx, 429, "تم قفل المحاولات مؤقتًا. حاول لاحقًا.");

            // .NET 10 marks the antiforgery result on the request; reading the form before checking it
            // throws. Return a clear 400 instead of a 500 when the token is missing/invalid.
            var af = ctx.Features.Get<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>();
            if (af is null || !af.IsValid)
                return Results.BadRequest("Invalid anti-forgery token.");

            var form = await ctx.Request.ReadFormAsync();
            var token = form["Token"].ToString();
            var email = form["Email"].ToString().Trim();
            var password = form["Password"].ToString();
            var confirm = form["ConfirmPassword"].ToString();

            if (!TokenMatches(cfg["Setup:Token"], token))
            {
                RegisterFailure(ip);
                return await PageWithStatus(ctx, 403, "رمز التهيئة غير صحيح.");
            }

            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return await PageWithStatus(ctx, 400, "أدخل بريدًا إلكترونيًا صحيحًا.");
            if (password.Length < 8)
                return await PageWithStatus(ctx, 400, "كلمة المرور قصيرة جدًا (8 أحرف على الأقل).");
            if (!string.Equals(password, confirm, StringComparison.Ordinal))
                return await PageWithStatus(ctx, 400, "كلمتا المرور غير متطابقتين.");

            await Gate.WaitAsync();
            try
            {
                // إعادة التحقق داخل القسم الحرج: يمنع سباق طلبين متزامنين من إنشاء مسؤولين.
                if (await users.Users.AnyAsync())
                    return NotFound(ctx);

                var admin = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
                var created = await users.CreateAsync(admin, password);
                if (!created.Succeeded)
                {
                    var msg = string.Join(" | ", created.Errors.Select(e => e.Description));
                    return await PageWithStatus(ctx, 400, "تعذّر إنشاء الحساب: " + msg);
                }

                await users.AddToRoleAsync(admin, "Admin");
                foreach (var role in ModuleRoles)
                    await users.AddToRoleAsync(admin, role);

                app.Logger.LogInformation("First-run setup completed: initial administrator provisioned.");
                return Results.LocalRedirect("/login?setup=done");
            }
            finally
            {
                Gate.Release();
            }
        }).WithMetadata(new RequireAntiforgeryTokenAttribute()).AllowAnonymous();
    }

    private static async Task<bool> IsAvailableAsync(UserManager<IdentityUser> users, IConfiguration cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg["Setup:Token"]))
            return false; // بلا رمز تهيئة = الآلية معطلة بالكامل
        return !await users.Users.AnyAsync();
    }

    private static bool TokenMatches(string? expected, string provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool IsLockedOut(string key)
    {
        if (!Attempts.TryGetValue(key, out var entry)) return false;
        if (DateTime.UtcNow - entry.WindowStart > LockoutWindow) { Attempts.TryRemove(key, out _); return false; }
        return entry.Count >= MaxFailedAttempts;
    }

    private static void RegisterFailure(string key)
    {
        Attempts.AddOrUpdate(key,
            _ => (1, DateTime.UtcNow),
            (_, old) => DateTime.UtcNow - old.WindowStart > LockoutWindow ? (1, DateTime.UtcNow) : (old.Count + 1, old.WindowStart));
    }

    private static IResult NotFound(HttpContext ctx)
    {
        ctx.Response.StatusCode = 404;
        return Results.Text("Not Found");
    }

    private static async Task<IResult> PageWithStatus(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var tokens = ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx);
        await ctx.Response.WriteAsync(Page(message, tokens.RequestToken!, locked: status == 429));
        return Results.Empty;
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string Page(string? error, string requestToken, bool locked) => $@"<!doctype html>
<html lang='ar' dir='rtl'><head><meta charset='utf-8'><title>Setup</title>
<style>
body{{font-family:Cairo,sans-serif;background:#F4F5F9;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}}
.card{{background:#fff;padding:28px;border-radius:12px;box-shadow:0 4px 24px rgba(0,0,0,.08);width:420px}}
input{{width:100%;padding:10px;margin:6px 0 12px;border:1px solid #E5E7EB;border-radius:8px;box-sizing:border-box}}
button{{width:100%;padding:10px;background:#6D5BD0;color:#fff;border:0;border-radius:8px;cursor:pointer;font-weight:700}}
button[disabled]{{background:#9CA3AF;cursor:not-allowed}}
label{{font-size:.9rem;color:#444}} .err{{color:#EF4444;text-align:center}}
</style></head><body><div class='card'>
<h3 style='text-align:center;color:#6D5BD0'>First-run setup</h3>
<p style='font-size:.85rem;color:#666;text-align:center'>Available once on a fresh installation only.</p>
{(string.IsNullOrEmpty(error) ? "" : $"<p class='err'>{Escape(error)}</p>")}
<form method='post' action='/setup'>
<input type='hidden' name='__RequestVerificationToken' value='{Escape(requestToken)}' />
<label>Setup token</label><input name='Token' type='password' autocomplete='off' required />
<label>Administrator e-mail</label><input name='Email' type='email' autocomplete='username' required />
<label>Password</label><input name='Password' type='password' autocomplete='new-password' minlength='8' required />
<label>Confirm password</label><input name='ConfirmPassword' type='password' autocomplete='new-password' minlength='8' required />
<button type='submit' {(locked ? "disabled" : "")}>Create administrator</button>
</form></div></body></html>";
}

