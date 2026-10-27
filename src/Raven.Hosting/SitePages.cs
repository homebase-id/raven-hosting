using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Raven.Hosting.Components;

namespace Raven.Hosting;

/// <summary>
/// The site around the upgrade page, at ravenhosting.cloud: the landing page with the plans and their prices, sign-in,
/// the terms, the privacy policy and the report form. Anyone can see the plans; choosing one needs signing in
/// (<see cref="UpgradePage"/>). Off, like the upgrade page, while <c>RavenHosting:PublicDomain</c> is empty.
/// </summary>
public static class SitePages
{
    public const string SignIn = "/sign-in";

    /// <summary>The plan a new identity starts on, shown first on the landing page as the free one</summary>
    public const string FreePlanId = "free";

    /// <summary>
    /// Where "Create a free identity" goes before the browser picks the visitor's region (wwwroot/assets/signup.js):
    /// each region's cluster has its own sign-up host. Never the bare createme.ravenhosting.cloud, the old single one
    /// </summary>
    public const string DefaultSignupUrl = "https://createme.eu.ravenhosting.cloud/";

    // Reports hold a few kilobytes of text; everything else here stays under the service's 4 KB
    private const long ReportBodyLimit = 32 * 1024;

    public static string SignInFor(string planId) => $"{SignIn}?plan={Uri.EscapeDataString(planId)}";

    /// <summary>Signing in to the admin pages: back to them afterwards</summary>
    public const string SignInForAdmin = SignIn + "?next=admin";

    /// <summary>Signing in to go on to a page after: /account, or a join link (<see cref="UpgradePage.NextPath"/>)</summary>
    public static string SignInThen(string next) => $"{SignIn}?next={Uri.EscapeDataString(next)}";

    public static void Map(WebApplication app)
    {
        app.MapGet("/", async (HttpContext context, UpgradeOptions options, UpgradeSession session, PlanService plans,
            PlanStore planStore) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var all = await planStore.ListAsync();
            var parameters = new Dictionary<string, object?>
            {
                ["Free"] = all.FirstOrDefault(p => UpgradePage.SamePlan(p.Id, FreePlanId)),
                ["Plans"] = all.Where(UpgradePage.IsForSale).ToList(),
            };

            // Someone signed in sees their plan, after the page (plan-now.js); if their host cannot say just now, the
            // page shows without it
            var signedIn = session.Read(context);
            if (signedIn != null)
            {
                parameters["Identity"] = signedIn.Identity;
                parameters["PlanNowPending"] = !UpgradePage.WantsPlanNow(context);
            }
            if (signedIn != null && UpgradePage.WantsPlanNow(context))
            {
                var located = await UpgradePage.LocateAsync(context, signedIn, session, plans);
                if (located.Error == null)
                {
                    parameters["Now"] = located.Now;
                    parameters["CurrentPlanName"] = all.FirstOrDefault(p => UpgradePage.SamePlan(p.Id, located.PlanId))?.Name
                                                    ?? located.PlanId ?? "none";
                }
            }

            return UpgradePage.Show<LandingPage>(parameters);
        });

        // Whom to sign in; the form goes to the upgrade page, which starts YouAuth. Someone signed in goes on
        app.MapGet(SignIn, async (HttpContext context, string? plan, string? next, UpgradeOptions options,
            UpgradeSession session, PlanStore planStore) =>
        {
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var chosen = await planStore.GetAsync(plan) is { } p && UpgradePage.IsForSale(p) ? p : null;
            var nextPath = UpgradePage.NextPath(next);
            if (session.Read(context) != null)
            {
                return Results.Redirect(nextPath
                    ?? (chosen == null ? UpgradePage.Root : UpgradePage.ConfirmUrl(chosen.Id)));
            }

            return UpgradePage.Show<SignInPage>(new { Plan = nextPath != null ? null : chosen, Next = nextPath != null ? next : null });
        });

        app.MapGet("/terms-and-conditions", (UpgradeOptions options) =>
            options.Enabled ? UpgradePage.Show<TermsPage>(new { }) : Results.NotFound());

        app.MapGet("/privacy-policy", (UpgradeOptions options) =>
            options.Enabled ? UpgradePage.Show<PrivacyPage>(new { }) : Results.NotFound());

        app.MapGet("/report", (HttpContext context, bool? sent, UpgradeOptions options, IAntiforgery antiforgery) =>
            options.Enabled
                ? UpgradePage.Show<ReportPage>(new { Sent = sent == true, Antiforgery = antiforgery.GetAndStoreTokens(context) })
                : Results.NotFound());

        // Identity hosts link here to report content (Host:ReportContentUrl)
        app.MapGet("/report/content", (UpgradeOptions options) =>
            options.Enabled ? Results.Redirect("/report") : Results.NotFound());

        // A filed report is stored for admins (raven.sh reports); the form is the antiforgery-checked kind
        app.MapPost("/report", async (HttpContext context, IFormCollection form, UpgradeOptions options,
                ReportStore reports, TimeProvider time, IAntiforgery antiforgery, ILogger<ReportStore> logger) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                // Read by hand, not bound: a field left out is a problem to show, not a bad request
                var report = new ContentReportRequest(form["ContactInfo"], form["ContentType"], form["Violation"],
                    form["ContentAuthor"], form["Evidence"], form["Additional"]);
                var problem = report.Problem();
                if (problem != null)
                {
                    return UpgradePage.Show<ReportPage>(new
                    {
                        Problem = problem,
                        Values = report,
                        Antiforgery = antiforgery.GetAndStoreTokens(context),
                    }, StatusCodes.Status400BadRequest);
                }

                var id = await reports.AddAsync(report, time.GetUtcNow().ToUnixTimeMilliseconds());
                logger.LogWarning("Content report {id} filed; read it with raven.sh reports", id);
                return Results.Redirect("/report?sent=true");
            })
            .WithMetadata(new RequestSizeLimitAttribute(ReportBodyLimit));
    }
}
