using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Raven.Hosting.Components;

namespace Raven.Hosting;

/// <summary>
/// Who is signed in to the upgrade page: an identity whose owner proved it with YouAuth, the host it was last seen
/// on, and the name on its public profile card when it signed in. In a data-protected, time-limited cookie for the whole site, so the landing page knows them too.
/// </summary>
public class UpgradeSession(IDataProtectionProvider dataProtection)
{
    public const string CookieName = "raven_upgrade";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("upgrade-session").ToTimeLimitedDataProtector();

    public record Data(string Identity, string? Host, string? Name = null);

    /// <summary>The signed-in identity; null when there is no cookie, or it expired or was tampered with</summary>
    public Data? Read(HttpContext context)
    {
        var cookie = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Data>(_protector.Unprotect(cookie));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    public void Write(HttpContext context, Data data) =>
        context.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(data), Lifetime), Options());

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName, Options());

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = Lifetime,
    };
}

/// <summary>
/// Where the upgrade page's stylesheet is, with a version from its content: its URL changes when it does, so browsers
/// can keep it for a year
/// </summary>
public static class SiteAssets
{
    private const string StylesheetPath = "/assets/site.css";
    private const string AdminStylesheetPath = "/assets/admin.css";

    public static string Stylesheet { get; private set; } = StylesheetPath;

    /// <summary>The admin pages' stylesheet (Tailwind, built by build-admin-css.sh)</summary>
    public static string AdminStylesheet { get; private set; } = AdminStylesheetPath;

    public static void Load(IWebHostEnvironment environment)
    {
        Stylesheet = Versioned(environment, StylesheetPath);
        AdminStylesheet = Versioned(environment, AdminStylesheetPath);
    }

    private static string Versioned(IWebHostEnvironment environment, string path)
    {
        using var css = environment.WebRootFileProvider.GetFileInfo(path.TrimStart('/')).CreateReadStream();
        return $"{path}?v={Convert.ToHexStringLower(SHA256.HashData(css))[..12]}";
    }
}

/// <summary>
/// The page where an owner gets a bigger plan (README, "Upgrade page"). The owner console sends them to
/// <c>/upgrade?id=&lt;identity&gt;&amp;host=&lt;provisioning domain&gt;</c>; they sign in with YouAuth, then go
/// through four steps: choose a plan (from the plans marked for sale), confirm it, a test checkout, and done. The
/// checkout is a stand-in for payment: completing it makes a purchase grant, which sets the plan, and what it gives,
/// on the identity's host. The pages are Razor components (Components/), rendered on the server.
/// </summary>
public static class UpgradePage
{
    public const string Root = "/upgrade";

    // The plan chosen before signing in, kept across the trip to the identity and back
    private const string NextPlanCookie = "raven_next_plan";

    /// <summary>
    /// The value of next (on /sign-in and here) that goes on to the admin pages after signing in, kept across the
    /// trip like a plan, in place of one
    /// </summary>
    public const string NextAdmin = "admin";

    /// <summary>The value of next that goes on to the owner's own page, /account</summary>
    public const string NextAccount = "account";

    /// <summary>The value of next that goes back to a join link: join/&lt;token&gt;</summary>
    public static string NextJoin(string token) => $"join/{token}";

    /// <summary>
    /// Where a value of next goes after signing in: the admin pages, /account, or a join link; null for anything else
    /// (a plan Id, which goes on to confirming it, or nothing)
    /// </summary>
    public static string? NextPath(string? next) => next switch
    {
        NextAdmin => AdminPages.Root,
        NextAccount => AccountPages.Root,
        _ when next != null && next.StartsWith("join/", StringComparison.Ordinal) &&
               System.Text.RegularExpressions.Regex.IsMatch(next[5..], "^[A-Za-z0-9_-]{20,64}$") => "/" + next,
        _ => null,
    };

    /// <summary>
    /// Where "Back to your Homebase" goes once the plan is changed. The one place to change it.
    /// </summary>
    public static string ReturnUrl(string identity) => $"https://{identity}/owner/settings/plan";

    public static bool SamePlan(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether owners can buy it: marked for sale, with a price</summary>
    public static bool IsForSale(Plan plan) => plan.ForSale && plan.Prices.Count > 0;

    public static string ConfirmUrl(string planId) => $"{Root}/confirm?plan={Uri.EscapeDataString(planId)}";

    public static void Map(WebApplication app)
    {
        SiteAssets.Load(app.Environment);

        app.MapGet("/.well-known/youauth-client.json", (UpgradeOptions options) =>
            options.Enabled
                ? Results.Json(new { name = options.DisplayName, redirect_uris = new[] { options.CallbackUrl } })
                : Results.NotFound());

        var page = app.MapGroup(Root);

        // Step 1, choose. id and host are hints anyone can write into a link: id only says whom to sign in, host is
        // used only if it is a host this service knows. plan, from the landing page, goes straight to confirming it
        page.MapGet("", async (HttpContext context, string? id, string? host, string? plan, string? next, UpgradeOptions options,
            UpgradeSession session, YouAuthLogin login, PlanService plans, PlanStore planStore, PlanHosts hosts) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var chosen = await planStore.GetAsync(plan) is { } p && IsForSale(p) ? p : null;
            var requested = string.IsNullOrWhiteSpace(id) ? null : DomainNames.Normalize(id);
            if (!string.IsNullOrWhiteSpace(id) && requested == null)
            {
                return Show<SignInPage>(new
                {
                    Plan = chosen,
                    Identity = id,
                    Problem = $"'{id}' is not a Homebase identity. Type its domain, like frodo.homebase.id.",
                }, StatusCodes.Status400BadRequest);
            }

            var hint = DomainNames.Normalize(host);
            if (!hosts.IsKnown(hint))
            {
                hint = null;
            }

            var nextPath = NextPath(next);
            var signedIn = session.Read(context);
            if (signedIn != null && (requested == null || requested == signedIn.Identity))
            {
                if (nextPath != null)
                {
                    return Results.Redirect(nextPath);
                }
                if (chosen != null)
                {
                    return Results.Redirect(ConfirmUrl(chosen.Id));
                }

                var located = await LocateAsync(context, signedIn with { Host = signedIn.Host ?? hint }, session, plans);
                if (located.Error != null)
                {
                    return located.Error;
                }
                return Show<ChoosePage>(new
                {
                    Now = located.Now,
                    CurrentPlanName = await PlanNameAsync(planStore, located.PlanId),
                    Plans = (await planStore.ListAsync()).Where(IsForSale).ToList(),
                });
            }

            if (requested == null)
            {
                return Results.Redirect(chosen == null ? SitePages.SignIn : SitePages.SignInFor(chosen.Id));
            }

            if (nextPath != null)
            {
                context.Response.Cookies.Append(NextPlanCookie, next!, NextPlanCookieOptions());
            }
            else if (chosen != null)
            {
                context.Response.Cookies.Append(NextPlanCookie, chosen.Id, NextPlanCookieOptions());
            }
            else
            {
                context.Response.Cookies.Delete(NextPlanCookie, NextPlanCookieOptions());
            }
            return Results.Redirect(await login.BeginAsync(requested, hint));
        });

        page.MapGet("/youauth-callback", async (HttpContext context, [FromQuery] string? state,
            [FromQuery(Name = "public_key")] string? publicKey, [FromQuery] string? salt, [FromQuery] string? error,
            [FromQuery(Name = "error_description")] string? errorDescription, UpgradeOptions options,
            UpgradeSession session, YouAuthLogin login, PublicProfiles profiles) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var result = await login.CompleteAsync(state, publicKey, salt, error, errorDescription);
            if (result.Identity == null)
            {
                return Message(StatusCodes.Status400BadRequest, "Not signed in", result.Error!);
            }

            session.Write(context, new UpgradeSession.Data(result.Identity, result.HostHint,
                await profiles.NameAsync(result.Identity)));
            var next = context.Request.Cookies[NextPlanCookie];
            context.Response.Cookies.Delete(NextPlanCookie, NextPlanCookieOptions());
            return Results.Redirect(NextPath(next) ?? (PlanIds.IsValid(next) ? ConfirmUrl(next!) : Root));
        });

        // Step 2, confirm
        page.MapGet("/confirm", async (HttpContext context, string? plan, UpgradeOptions options,
            UpgradeSession session, PlanService plans, PlanStore planStore, PurchaseAccessStore purchaseAccess,
            SubscriptionService subscriptions) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return SessionEnded();
            }

            var chosen = await ForSaleAsync(planStore, plan);
            if (chosen == null)
            {
                return NotForSale();
            }
            if (chosen.Prices.Count == 0)
            {
                return NoPriceYet(chosen);
            }

            var located = await LocateAsync(context, signedIn, session, plans);
            if (located.Error != null)
            {
                return located.Error;
            }
            if (PlanFit.Of(chosen, located.Now) is { CanChoose: false } fit)
            {
                return TooSmall(chosen, fit);
            }

            return Show<ConfirmPage>(new
            {
                Plan = chosen,
                Now = located.Now,
                CurrentPlanName = await PlanNameAsync(planStore, located.PlanId),
                CanBuy = await purchaseAccess.CanBuyAsync(signedIn.Identity),
                Owned = await subscriptions.OwnedByAsync(signedIn.Identity),
            });
        });

        // Step 3, a test checkout of the term chosen
        page.MapGet("/checkout", async (HttpContext context, string? plan, int? months,
            UpgradeOptions options, UpgradeSession session, PlanStore planStore, IAntiforgery antiforgery,
            PurchaseAccessStore purchaseAccess) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return SessionEnded();
            }
            if (!await purchaseAccess.CanBuyAsync(signedIn.Identity))
            {
                return NotOpenYet();
            }

            var chosen = await ForSaleAsync(planStore, plan);
            if (chosen == null)
            {
                return NotForSale();
            }
            var price = TermOf(chosen, months);
            if (price == null)
            {
                return ChooseATerm(chosen);
            }

            return Show<CheckoutPage>(new
            {
                Plan = chosen,
                Price = price,
                Identity = signedIn.Identity,
                Antiforgery = antiforgery.GetAndStoreTokens(context),
            });
        });

        // Completing the test checkout buys the plan for the term: a subscription the owner pays for and is on, its first
        // invoice paid, which sets the plan on their host. Only planId and months are read; anything else in the form is
        // ignored
        page.MapPost("/checkout", async (HttpContext context, [FromForm] string? planId, [FromForm] int? months,
            UpgradeOptions options, UpgradeSession session, PlanService plans,
            PlanStore planStore, PurchaseAccessStore purchaseAccess, SubscriptionService subscriptions) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return SessionEnded();
            }

            // The lock: until purchases open, only testers can change their plan here
            if (!await purchaseAccess.CanBuyAsync(signedIn.Identity))
            {
                return NotOpenYet();
            }

            var plan = await ForSaleAsync(planStore, planId);
            if (plan == null)
            {
                return NotForSale();
            }
            var price = TermOf(plan, months);
            if (price == null)
            {
                return ChooseATerm(plan);
            }

            // Not onto a plan for one smaller than what they store (on a group plan they set their own share). A host that
            // cannot say just now does not stop a purchase: the grant waits for it
            var located = await LocateAsync(context, signedIn, session, plans);
            if (!plan.IsGroup && located.Error == null && PlanFit.Of(plan, located.Now) is { CanChoose: false } fit)
            {
                return TooSmall(plan, fit);
            }
            signedIn = located.SignedIn ?? signedIn;

            try
            {
                var bought = await subscriptions.BuyAsync(signedIn.Identity, plan, price, signedIn.Host,
                    "test-checkout", $"test-{Guid.NewGuid():N}");
                return Results.Redirect($"{Root}/done?subscription={bought.Id}");
            }
            catch (SubscriptionRefusedException e)
            {
                return Message(StatusCodes.Status400BadRequest, "Not changed", e.Message, "Manage your plan",
                    AccountPages.Root);
            }
        });

        // Step 4, done: only the signed-in identity's own subscription. The plan is named even if it is no longer for sale
        page.MapGet("/done", async (HttpContext context, long? subscription, UpgradeOptions options,
            UpgradeSession session, PlanGrantStore grants, PlanStore planStore, SubscriptionService subscriptions) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return SessionEnded();
            }

            var view = subscription == null ? null : await subscriptions.ViewAsync(subscription.Value);
            if (view == null || view.Subscription.Owner != signedIn.Identity)
            {
                return Message(StatusCodes.Status404NotFound, "Not found", "We could not find that purchase.");
            }

            // Their own plan as set on their host, if they took a seat
            var current = await grants.CurrentForAsync(signedIn.Identity);
            return Show<DonePage>(new
            {
                View = view,
                Plan = await planStore.GetAsync(view.Subscription.PlanId),
                Grant = current?.SubscriptionId == view.Subscription.Id ? current : null,
                Identity = signedIn.Identity,
            });
        });

        // The form is read only so the antiforgery token is checked
        page.MapPost("/sign-out", (HttpContext context, IFormCollection form, UpgradeOptions options,
            UpgradeSession session) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            session.Clear(context);
            return Results.Redirect("/");
        });
    }

    //

    /// <summary>The signed-in owner, found on their host, and what the host says they have now</summary>
    internal record Located(UpgradeSession.Data? SignedIn, IdentityOnHost? Now, IResult? Error)
    {
        public string? PlanId => Now?.PlanId;
    }

    // Where the identity is and its plan, asked of its host; remembers the host in the session when it moved
    internal static async Task<Located> LocateAsync(HttpContext context, UpgradeSession.Data signedIn,
        UpgradeSession session, PlanService plans)
    {
        var located = await plans.LookUpAsync(signedIn.Identity, signedIn.Host);
        if (located.Host == null)
        {
            return new Located(null, null, Message(StatusCodes.Status404NotFound, "Not found",
                $"We could not find {signedIn.Identity} on our hosts just now. Please try again in a few minutes."));
        }

        if (located.Host != signedIn.Host)
        {
            signedIn = signedIn with { Host = located.Host };
            session.Write(context, signedIn);
        }

        // Storage add-ons stay whatever plan they choose: plans are compared with the rest of their quota
        var extra = (await context.RequestServices.GetRequiredService<AddOnStore>().ListAsync(signedIn.Identity))
            .Where(a => a.IsActive && a.Kind == AddOnKind.Storage).Sum(a => a.Amount);
        return new Located(signedIn, located.Identity is { } now ? now with { ExtraBytes = extra } : null, null);
    }

    // The plan, if it is one for sale; null otherwise
    private static async Task<Plan?> ForSaleAsync(PlanStore planStore, string? planId) =>
        await planStore.GetAsync(planId) is { ForSale: true } plan ? plan : null;

    // The plan's name; its Id when it is not a plan here
    private static async Task<string> PlanNameAsync(PlanStore planStore, string? planId) =>
        (await planStore.GetAsync(planId))?.Name ?? planId ?? "none";

    // The term chosen; the plan's only one when none was and it has one
    private static PlanPrice? TermOf(Plan plan, int? months) =>
        months == null ? (plan.Prices.Count == 1 ? plan.Prices[0] : null) : plan.PriceFor(months);

    internal static RazorComponentResult Show<TPage>(object parameters, int status = StatusCodes.Status200OK)
        where TPage : IComponent => new RazorComponentResult<TPage>(parameters) { StatusCode = status };

    internal static RazorComponentResult Show<TPage>(IReadOnlyDictionary<string, object?> parameters,
        int status = StatusCodes.Status200OK)
        where TPage : IComponent => new RazorComponentResult<TPage>(parameters) { StatusCode = status };

    private static CookieOptions NextPlanCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = Root,
        MaxAge = YouAuthLogin.StateLifetime,
    };

    internal static RazorComponentResult Message(int status, string title, string message, string? linkText = null,
        string? linkHref = null) =>
        Show<MessagePage>(new { Title = title, Message = message, LinkText = linkText, LinkHref = linkHref }, status);

    private static IResult ChooseATerm(Plan plan) =>
        plan.Prices.Count == 0
            ? NoPriceYet(plan)
            : Message(StatusCodes.Status400BadRequest, "Choose how to pay", $"{plan.Name} is not sold for that term.",
                "Choose how to pay", ConfirmUrl(plan.Id));

    private static IResult NoPriceYet(Plan plan) =>
        Message(StatusCodes.Status400BadRequest, "Not for sale yet", $"{plan.Name} has no price yet.",
            "Choose another plan", Root);

    private static IResult SessionEnded() => Message(StatusCodes.Status401Unauthorized, "Not signed in",
        "Your session has ended. Sign in again to choose a plan.", "Sign in", SitePages.SignIn);

    private static IResult TooSmall(Plan plan, PlanFit fit) => Message(StatusCodes.Status400BadRequest,
        "Too small", $"{plan.Name} has {plan.Storage}. {fit.Note}: choose a plan with more room.", "Choose another plan",
        Root);

    private static IResult NotOpenYet() => Message(StatusCodes.Status403Forbidden, "Coming soon",
        "Buying a plan here opens soon. Until then your plan stays as it is.", "See the plans", Root);

    private static IResult NotForSale() => Message(StatusCodes.Status400BadRequest, "Not for sale",
        "That plan is not for sale.", "Choose another plan", Root);
}

/// <summary>
/// How a plan compares with what the owner has now, as their host says: more room, the same, less, or too little for
/// what they store, which they cannot choose
/// </summary>
/// <param name="Note">"440 GB more than now", "Less than you have now (1 TB)"; null when there is nothing to compare</param>
/// <param name="IsMore">It has more room than they have now</param>
public record PlanFit(string? Note, bool CanChoose, bool IsMore = false)
{
    public static PlanFit Of(Plan plan, IdentityOnHost? now)
    {
        if (now == null || UpgradePage.SamePlan(plan.Id, now.PlanId))
        {
            return new PlanFit(null, true);
        }
        if (now.UsedBytes is { } used && used >= plan.QuotaBytes + now.ExtraBytes)
        {
            return new PlanFit($"You store {StorageSize.Format(used)}", false);
        }
        return now.PlanQuotaBytes switch
        {
            null => new PlanFit(null, true),
            var quota when plan.QuotaBytes > quota =>
                new PlanFit($"{StorageSize.Format(plan.QuotaBytes - quota.Value)} more than now", true, IsMore: true),
            var quota when plan.QuotaBytes == quota => new PlanFit("The same space as now", true),
            var quota => new PlanFit($"Less than you have now ({StorageSize.Format(quota.Value)})", true),
        };
    }
}

/// <summary>
/// The plan whose card is marked recommended. Someone signed in whose host says their quota is recommended the next
/// size up: the smallest plan with more room than they have that they can choose, the first listed when two are the
/// same size; none when no plan has more. Anyone else is recommended the first plan marked so, unless it is theirs.
/// </summary>
public static class PlanRecommendation
{
    public static Plan? For(IEnumerable<Plan> plans, IdentityOnHost? now)
    {
        if (now?.PlanQuotaBytes is { } quota)
        {
            return plans
                .Where(p => p.QuotaBytes > quota && !UpgradePage.SamePlan(p.Id, now.PlanId) && PlanFit.Of(p, now).CanChoose)
                .OrderBy(p => p.QuotaBytes)
                .FirstOrDefault();
        }
        return plans.FirstOrDefault(p => p.Recommended && !UpgradePage.SamePlan(p.Id, now?.PlanId));
    }
}
