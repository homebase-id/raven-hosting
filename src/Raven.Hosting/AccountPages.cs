using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Raven.Hosting.Components;

namespace Raven.Hosting;

/// <summary>
/// The owner's own page (/account), signed in with YouAuth like the upgrade page: their plan now, the subscription they
/// pay for (its term, auto-renew, invoices, and on a group plan its members and their shares), the plan they are on
/// that someone else pays for, and invitations waiting for them. And the join links (/join/&lt;token&gt;) the owner of a
/// group plan sends the people they name: opened signed in as the identity named, they join it. Every change is an
/// antiforgery-checked form post, answered with a redirect back saying what it did or why not. The rules are
/// <see cref="SubscriptionService"/>'s.
/// </summary>
public static class AccountPages
{
    public const string Root = "/account";

    /// <summary>The join link for an invitation, as the owner sends it</summary>
    public static string JoinUrl(UpgradeOptions options, string token) => $"https://{options.PublicDomain}/join/{token}";

    public static void Map(WebApplication app)
    {
        app.MapGet(Root, async (HttpContext context, string? done, string? error, UpgradeOptions options,
            UpgradeSession session, PlanService plans, PlanStore planStore, SubscriptionService subscriptions,
            PurchaseAccessStore purchaseAccess, IAntiforgery antiforgery) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }
            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return Results.Redirect(SitePages.SignInThen(UpgradePage.NextAccount));
            }

            // Their plan as their host says, after the page (plan-now.js); the page shows without it if the host
            // cannot say just now
            var pending = !UpgradePage.WantsPlanNow(context);
            var located = pending ? null : await UpgradePage.LocateAsync(context, signedIn, session, plans);
            var covering = await subscriptions.CoveringAsync(signedIn.Identity);
            return UpgradePage.Show<AccountPage>(new Dictionary<string, object?>
            {
                ["Identity"] = signedIn.Identity,
                ["PlanNowPending"] = pending,
                ["Now"] = located?.Now,
                ["CurrentPlanName"] = (await planStore.GetAsync(located?.PlanId))?.Name ?? located?.PlanId,
                ["Owned"] = await subscriptions.OwnedByAsync(signedIn.Identity),
                ["Covering"] = covering is { } c && c.View.Subscription.Owner != signedIn.Identity ? c.View : null,
                ["Membership"] = covering is { } m && m.View.Subscription.Owner != signedIn.Identity ? m.Member : null,
                ["Invitations"] = await subscriptions.InvitationsForAsync(signedIn.Identity),
                ["CanPay"] = await purchaseAccess.CanBuyAsync(signedIn.Identity),
                ["Options"] = options,
                ["Antiforgery"] = antiforgery.GetAndStoreTokens(context),
                ["Done"] = Text(done),
                ["Error"] = Text(error),
            });
        });

        // The owner names someone on their plan, or takes a seat on it themselves
        app.MapPost($"{Root}/members", (HttpContext context, IFormCollection form) =>
            ActAsync(context, async (identity, subscriptions) =>
            {
                var owned = await OwnedAsync(subscriptions, identity);
                var member = await subscriptions.AddMemberAsync(owned.Subscription.Id, identity, Text(form["identity"]),
                    Gigabytes(form["share"]), Session(context)?.Host);
                return member.Status == MemberStatus.Active
                    ? $"You have a seat on your plan, with {StorageSize.Format(member.QuotaBytes)}."
                    : $"{member.Identity} is invited, with {StorageSize.Format(member.QuotaBytes)}. Send them their link, below.";
            }));

        app.MapPost($"{Root}/members/{{id:long}}/remove", (HttpContext context, long id, IFormCollection form) =>
            ActAsync(context, async (identity, subscriptions) =>
            {
                var owned = await OwnedAsync(subscriptions, identity);
                var removed = await subscriptions.RemoveAsync(owned.Subscription.Id, identity, id);
                return removed.IsLeaving
                    ? $"{removed.Identity} is off your plan. They keep it until {Day(removed.KeepsUntil!.Value)}, then go to the free plan."
                    : $"{removed.Identity}'s invitation is cancelled.";
            }));

        app.MapPost($"{Root}/members/{{id:long}}/link", (HttpContext context, long id, IFormCollection form) =>
            ActAsync(context, async (identity, subscriptions) =>
            {
                var owned = await OwnedAsync(subscriptions, identity);
                var member = await subscriptions.NewLinkAsync(owned.Subscription.Id, identity, id);
                return $"A new link for {member.Identity}; the old one no longer works.";
            }));

        // Shares as typed, in GB, one field per member: share-<member id>
        app.MapPost($"{Root}/shares", (HttpContext context, IFormCollection form) =>
            ActAsync(context, async (identity, subscriptions) =>
            {
                var owned = await OwnedAsync(subscriptions, identity);
                var shares = new Dictionary<long, long>();
                foreach (var key in form.Keys.Where(k => k.StartsWith("share-", StringComparison.Ordinal)))
                {
                    if (long.TryParse(key[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                        Gigabytes(form[key]) is { } bytes)
                    {
                        shares[id] = bytes;
                    }
                }
                await subscriptions.SetSharesAsync(owned.Subscription.Id, identity, shares);
                return "Shares saved.";
            }));

        app.MapPost($"{Root}/split", (HttpContext context, IFormCollection form) =>
            ActAsync(context, async (identity, subscriptions) =>
            {
                var owned = await OwnedAsync(subscriptions, identity);
                await subscriptions.SplitEvenlyAsync(owned.Subscription.Id, identity);
                return "Split evenly.";
            }));

        app.MapPost($"{Root}/auto-renew", (HttpContext context, IFormCollection form) =>
            ActAsync(context, async (identity, subscriptions) =>
            {
                var owned = await OwnedAsync(subscriptions, identity);
                var on = form["on"] == "true";
                var changed = await subscriptions.SetAutoRenewAsync(owned.Subscription.Id, identity, on);
                return on
                    ? "Auto-renew is on."
                    : $"Auto-renew is off: your plan runs until {Day(changed.PeriodEnd)}, then ends.";
            }));

        // Paying an invoice is the test checkout too, so only for those who may buy
        app.MapPost($"{Root}/invoices/{{id:long}}/pay", (HttpContext context, long id, IFormCollection form) =>
            ActAsync(context, async (identity, subscriptions) =>
            {
                if (!await context.RequestServices.GetRequiredService<PurchaseAccessStore>().CanBuyAsync(identity))
                {
                    throw new SubscriptionRefusedException("Paying here opens soon. Until then, contact us to pay.");
                }
                var (_, renewed) = await subscriptions.PayAsync(id, "test-checkout", $"test-{Guid.NewGuid():N}", identity);
                return $"Paid: your plan runs until {Day(renewed.PeriodEnd)}.";
            }));

        app.MapPost($"{Root}/leave", (HttpContext context, IFormCollection form) =>
            ActAsync(context, async (identity, subscriptions) =>
            {
                var left = await subscriptions.LeaveAsync(identity);
                return $"You left the plan. You keep it until {Day(left.KeepsUntil!.Value)}, then go to the free plan.";
            }));

        //
        // Join links
        //

        app.MapGet("/join/{token}", async (HttpContext context, string token, UpgradeOptions options,
            UpgradeSession session, SubscriptionService subscriptions, IAntiforgery antiforgery) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }
            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return UpgradePage.NextPath(UpgradePage.NextJoin(token)) == null
                    ? NoLongerWorks()
                    : Results.Redirect(SitePages.SignInThen(UpgradePage.NextJoin(token)));
            }

            try
            {
                var (view, member) = await subscriptions.InvitationAsync(token, signedIn.Identity);
                var covering = await subscriptions.CoveringAsync(signedIn.Identity);
                return UpgradePage.Show<JoinPage>(new Dictionary<string, object?>
                {
                    ["View"] = view,
                    ["Member"] = member,
                    ["Plan"] = await context.RequestServices.GetRequiredService<PlanStore>().GetAsync(view.Subscription.PlanId),
                    ["Covering"] = covering?.View,
                    ["Token"] = token,
                    ["Antiforgery"] = antiforgery.GetAndStoreTokens(context),
                });
            }
            catch (SubscriptionRefusedException e)
            {
                return UpgradePage.Message(StatusCodes.Status400BadRequest, "Cannot join", e.Message, "Your plan", Root);
            }
        });

        app.MapPost("/join/{token}", async (HttpContext context, string token, IFormCollection form,
            UpgradeOptions options, UpgradeSession session, SubscriptionService subscriptions) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }
            var signedIn = session.Read(context);
            if (signedIn == null)
            {
                return Results.Redirect(SitePages.SignInThen(UpgradePage.NextJoin(token)));
            }

            try
            {
                if (form["action"] == "decline")
                {
                    await subscriptions.DeclineAsync(token, signedIn.Identity);
                    return Back(done: "Invitation declined.");
                }
                var (view, member) = await subscriptions.JoinAsync(token, signedIn.Identity, signedIn.Host);
                return Back(done: $"You joined {view.Subscription.Owner}'s {view.PlanName}, with {StorageSize.Format(member.QuotaBytes)}.");
            }
            catch (SubscriptionRefusedException e)
            {
                return UpgradePage.Message(StatusCodes.Status400BadRequest, "Cannot join", e.Message, "Your plan", Root);
            }
        });
    }

    //

    // Signed in, does the change, and goes back to /account saying what it did or why not
    private static async Task<IResult> ActAsync(HttpContext context,
        Func<string, SubscriptionService, Task<string>> change)
    {
        if (!context.RequestServices.GetRequiredService<UpgradeOptions>().Enabled)
        {
            return Results.NotFound();
        }
        var signedIn = Session(context);
        if (signedIn == null)
        {
            return Results.Redirect(SitePages.SignInThen(UpgradePage.NextAccount));
        }
        try
        {
            return Back(done: await change(signedIn.Identity, context.RequestServices.GetRequiredService<SubscriptionService>()));
        }
        catch (InvalidInvitationCodeRequestException e)
        {
            return Back(error: e.Message);
        }
    }

    private static UpgradeSession.Data? Session(HttpContext context) =>
        context.RequestServices.GetRequiredService<UpgradeSession>().Read(context);

    private static async Task<SubscriptionView> OwnedAsync(SubscriptionService subscriptions, string identity) =>
        await subscriptions.OwnedByAsync(identity) ??
        throw new SubscriptionRefusedException("You do not pay for a plan.");

    private static IResult Back(string? done = null, string? error = null) =>
        Results.Redirect(done != null ? $"{Root}?done={Uri.EscapeDataString(done)}"
            : error != null ? $"{Root}?error={Uri.EscapeDataString(error)}"
            : Root);

    private static IResult NoLongerWorks() => UpgradePage.Message(StatusCodes.Status404NotFound, "Cannot join",
        "This link does not work. Ask whoever sent it for a new one.");

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // GB as typed (50, 12.5) in bytes, 1024-based like the plans; null when nothing was typed
    private static long? Gigabytes(string? value)
    {
        var text = Text(value);
        if (text == null)
        {
            return null;
        }
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var gb) || gb <= 0)
        {
            throw new SubscriptionRefusedException($"'{text}' is not a number of GB, like 50 or 12.5");
        }
        return (long)decimal.Round(gb * (1L << 30));
    }

    private static string Day(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("d MMMM yyyy");
}
