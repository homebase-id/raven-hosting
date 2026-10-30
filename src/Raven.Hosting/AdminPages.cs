using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Raven.Hosting.Components;
using Raven.Hosting.Components.Admin;

namespace Raven.Hosting;

/// <summary>
/// What every admin page gets: who is signed in, the token its forms carry, and what the last change did
/// </summary>
/// <param name="Identity">The signed-in admin</param>
/// <param name="Name">The name on their public profile card; null when it has none</param>
/// <param name="Done">What the last change did, to say so at the top; null when nothing was just done</param>
/// <param name="Error">Why the last change was refused; null when it was not</param>
/// <param name="Checkout">Whether anyone may buy, and how many testers may while not: the beta badge says so</param>
public record AdminContext(string Identity, string? Name, AntiforgeryTokenSet Antiforgery, string? Done, string? Error,
    PurchaseAccess Checkout);

/// <summary>
/// One identity's subscription as the admin pages show it: its latest grant, what it gives with its add-ons, the paid
/// subscription that covers it if any, and whether it is running out
/// </summary>
/// <param name="PlanName">The plan's name; its Id when it is no longer a plan</param>
/// <param name="AddOns">Its active add-ons</param>
/// <param name="Now">When it was looked at (unix ms)</param>
/// <param name="Paid">The paid subscription its plan is from; null for a plan with no end</param>
/// <param name="Member">Its place on that subscription</param>
public record SubscriptionRow(PlanGrant Grant, string PlanName, Entitlement Entitlement, List<AddOn> AddOns, long Now,
    Subscription? Paid = null, SubscriptionMember? Member = null)
{
    /// <summary>What the list filters by</summary>
    public static readonly string[] States = ["active", "expiring", "grace", "paid", "free", "out-of-sync", "add-ons"];

    /// <summary>A paid subscription runs out this many days before it shows as expiring</summary>
    public const int ExpiringDays = 30;

    public bool IsPaid => Paid != null;

    /// <summary>When it runs out: when the member's place on a plan it left does, or else the paid term; null when it
    /// does not</summary>
    public long? EndsAt => Member?.IsLeaving == true ? Member.EndedAt : Paid?.PeriodEnd;

    /// <summary>Whole days until it runs out (0 on the day, less once it has); null when it does not</summary>
    public int? DaysLeft => EndsAt is { } ends ? (int)Math.Ceiling((ends - Now) / 86_400_000d) : null;

    /// <summary>active, expiring (within <see cref="ExpiringDays"/>) or grace (ran out, not yet moved to free)</summary>
    public string State => EndsAt is { } ends && ends < Now ? "grace" : DaysLeft <= ExpiringDays ? "expiring" : "active";

    /// <summary>When the grace ends and it goes back to the free plan; null when it is not in its grace</summary>
    public long? GraceEndsAt => State == "grace" ? EndsAt + (long)PlanService.Grace.TotalMilliseconds : null;

    /// <summary>How it came about, as people read it</summary>
    public string How => Paid != null && Member?.IsLeaving == true
        ? $"Leaving {(Paid.Owner == Grant.Identity ? "its own" : $"{Paid.Owner}'s")} plan"
        : Grant.Source switch
    {
        PlanGrantSource.Purchase => Paid != null ? $"Paid {Paid.Price.Text}" : "Paid",
        PlanGrantSource.Group => Paid != null ? $"On {Paid.Owner}'s plan" : "On a group plan",
        PlanGrantSource.Code => "Free, from a code",
        PlanGrantSource.Expiry => "Free, after a paid plan ran out",
        _ => "Free, from an admin",
    };

    public bool Is(string state) => state switch
    {
        "active" => State == "active",
        "expiring" => State == "expiring",
        "grace" => State == "grace",
        "paid" => IsPaid,
        "free" => !IsPaid,
        "out-of-sync" => Grant.Status != PlanGrantStatus.Applied,
        "add-ons" => AddOns.Count > 0,
        _ => true,
    };
}

/// <summary>
/// One signup on a channel's page, with the subscription the identity has now; null when the signup did not say who
/// it was, or the identity has no subscription here
/// </summary>
public record ChannelSignup(CodeSignup Signup, SubscriptionRow? Row);

/// <summary>
/// One paid subscription on the renewals list: when it runs out, and whether it is in its grace
/// </summary>
public record RenewalRow(SubscriptionView View, long Now)
{
    /// <summary>Whole days until it runs out (0 on the day, less once it has)</summary>
    public int DaysLeft => (int)Math.Ceiling((View.Subscription.PeriodEnd - Now) / 86_400_000d);

    /// <summary>active, expiring (within <see cref="SubscriptionRow.ExpiringDays"/>) or grace</summary>
    public string State => View.Subscription.State(Now) == "grace" ? "grace"
        : DaysLeft <= SubscriptionRow.ExpiringDays ? "expiring" : "active";
}

/// <summary>
/// The admin pages (/admin): everything raven.sh does with the create key, for admins signed in with their Homebase
/// identity (README, "Admin pages"). Who is an admin is in <see cref="AdminStore"/>, managed with raven.sh admins.
/// Someone not signed in is sent to sign in and back; someone signed in who is not an admin is told there is nothing
/// here. Every change is an antiforgery-checked form post, answered with a redirect back to the page, saying what it
/// did or why it was refused, and logged with who made it.
/// </summary>
public static class AdminPages
{
    public const string Root = "/admin";

    private const string AdminItem = "raven-admin";

    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup(Root).AddEndpointFilter(GuardAsync);

        //
        // Overview
        //

        admin.MapGet("", async (HttpContext context, PlanStore plans, PlanGrantStore grants, AddOnStore addOns,
            SubscriptionService subscriptions, InvitationCodeStore codes, ReportStore reports, AdminStore admins,
            TimeProvider time) =>
        {
            var rows = await SubscriptionRowsAsync(grants, addOns, plans, subscriptions, time);
            return await Show<AdminHome>(context, new
            {
                Plans = await plans.ListAsync(),
                Subscriptions = rows,
                Counts = SubscriptionRow.States.ToDictionary(s => s, s => rows.Count(r => r.Is(s))),
                Stats = await codes.StatsAsync(null),
                WeekStats = await codes.StatsAsync(DaysAgo(time, 7)),
                RecentReports = (await reports.ListAsync(DaysAgo(time, 7))).Count,
                Admins = await admins.ListAsync(),
            });
        });

        //
        // Plans
        //

        admin.MapGet("/plans", async (HttpContext context, PlanStore plans) =>
            await Show<AdminPlans>(context, new { Plans = await plans.ListAsync() }));

        // Typed Task<IResult>: a lambda taking only HttpContext would otherwise bind as a RequestDelegate, whose result
        // is thrown away, and answer an empty 200
        admin.MapGet("/plans/new", Task<IResult> (HttpContext context) => Show<AdminPlanForm>(context, new { }));

        admin.MapPost("/plans/new", async (HttpContext context, IFormCollection form, PlanStore plans) =>
        {
            try
            {
                var prices = new List<PlanPriceRequest>();
                foreach (var months in NewPlanTerms)
                {
                    if (!string.IsNullOrWhiteSpace(form[$"price{months}"]))
                    {
                        prices.Add(new PlanPriceRequest(months, Cents(form[$"price{months}"])));
                    }
                }
                var plan = await plans.CreateAsync(new PlanRequest(
                    Text(form["id"]), Text(form["name"]), Bytes(form["quota"], form["unit"]), Checked(form, "publicWebPresence"),
                    Checked(form, "forSale"), Text(form["description"]) ?? "", prices, Number(form["sortOrder"], "Sort order") ?? 0,
                    Checked(form, "publicEmail"), Number(form["emailAliases"], "Email aliases") ?? 0,
                    Checked(form, "recommended"), Number(form["seats"], "Seats") ?? 1));
                Log(context, "created plan {PlanId}", plan.Id);
                return Back(PlanUrl(plan.Id), done: $"Plan {plan.Name} created.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return await Show<AdminPlanForm>(context, new { Form = form }, e.Message, StatusCodes.Status400BadRequest);
            }
        });

        admin.MapGet("/plans/{id}", async (HttpContext context, string id, PlanStore plans, InvitationCodeStore codes,
            PlanGrantStore grants) =>
            await plans.GetAsync(id) is { } plan
                ? await Show<AdminPlanForm>(context, new
                {
                    Plan = plan,
                    CodesUsing = await codes.CountWithPlanAsync(plan.Id),
                    GrantsUsing = await grants.CountWithPlanAsync(plan.Id),
                })
                : await NotFound(context, "There is no such plan."));

        admin.MapPost("/plans/{id}", async (HttpContext context, string id, IFormCollection form, PlanStore plans,
            InvitationCodeStore codes, PlanGrantStore grants) =>
        {
            try
            {
                var plan = await plans.UpdateAsync(id, new PlanChange(
                    Text(form["name"]) ?? "", Text(form["description"]) ?? "", Bytes(form["quota"], form["unit"]),
                    Checked(form, "publicWebPresence"), Checked(form, "forSale"),
                    Number(form["sortOrder"], "Sort order") ?? 0, Checked(form, "publicEmail"),
                    Number(form["emailAliases"], "Email aliases") ?? 0, Checked(form, "recommended"),
                    Number(form["seats"], "Seats") ?? 1));
                if (plan == null)
                {
                    return await NotFound(context, "There is no such plan.");
                }
                Log(context, "changed plan {PlanId}", plan.Id);
                return Back(PlanUrl(plan.Id), done: "Saved.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return await plans.GetAsync(id) is { } plan
                    ? await Show<AdminPlanForm>(context, new
                    {
                        Plan = plan,
                        Form = form,
                        CodesUsing = await codes.CountWithPlanAsync(plan.Id),
                        GrantsUsing = await grants.CountWithPlanAsync(plan.Id),
                    }, e.Message, StatusCodes.Status400BadRequest)
                    : await NotFound(context, "There is no such plan.");
            }
        });

        admin.MapPost("/plans/{id}/prices", async (HttpContext context, string id, IFormCollection form, PlanStore plans) =>
        {
            try
            {
                var months = Number(form["months"], "Months") ?? throw new InvalidPlanRequestException("Give the months");
                var plan = await plans.SetPriceAsync(id, months, Cents(form["dollars"]));
                if (plan == null)
                {
                    return await NotFound(context, "There is no such plan.");
                }
                Log(context, "set plan {PlanId}'s price for {Months} months", plan.Id, months);
                return Back(PlanUrl(plan.Id), done: $"{plan.PriceFor(months)!.Text} set.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return Back(PlanUrl(id), error: e.Message);
            }
        });

        admin.MapPost("/plans/{id}/prices/{months:int}/remove", async (HttpContext context, string id, int months,
            IFormCollection form, PlanStore plans) =>
        {
            try
            {
                var (plan, found) = await plans.RemovePriceAsync(id, months);
                if (plan == null)
                {
                    return await NotFound(context, "There is no such plan.");
                }
                if (!found)
                {
                    return Back(PlanUrl(plan.Id), error: $"{plan.Name} has no price for {BillingTerm.Name(months)}.");
                }
                Log(context, "removed plan {PlanId}'s price for {Months} months", plan.Id, months);
                return Back(PlanUrl(plan.Id), done: $"The price per {BillingTerm.Name(months)} is removed.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return Back(PlanUrl(id), error: e.Message);
            }
        });

        admin.MapPost("/plans/{id}/delete", async (HttpContext context, string id, IFormCollection form, PlanStore plans,
            InvitationCodeStore codes, PlanGrantStore grants) =>
        {
            var (found, conflict) = await DeletePlanAsync(plans, codes, grants, id);
            if (!found)
            {
                return await NotFound(context, "There is no such plan.");
            }
            if (conflict != null)
            {
                return Back(PlanUrl(id), error: conflict);
            }
            Log(context, "deleted plan {PlanId}", id);
            return Back($"{Root}/plans", done: $"Plan {id} deleted.");
        });

        //
        // Subscriptions: each identity's latest grant, its add-ons, and the grants before it
        //

        admin.MapGet("/grants", (string? identity) =>
            Results.Redirect(DomainNames.Normalize(identity) is { } found ? SubscriptionUrl(found) : $"{Root}/subscriptions"));

        admin.MapGet("/subscriptions", async (HttpContext context, string? q, string? state, PlanGrantStore grants,
            AddOnStore addOns, PlanStore plans, SubscriptionService subscriptions, PlanHosts hosts, TimeProvider time) =>
        {
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim().ToLowerInvariant();
            state = SubscriptionRow.States.Contains(state) ? state : null;
            var rows = await SubscriptionRowsAsync(grants, addOns, plans, subscriptions, time);
            return await Show<AdminSubscriptions>(context, new
            {
                Rows = rows.Where(r => (q == null || r.Grant.Identity.Contains(q)) && (state == null || r.Is(state))).ToList(),
                Counts = SubscriptionRow.States.ToDictionary(s => s, s => rows.Count(r => r.Is(s))),
                Plans = await plans.ListAsync(),
                Hosts = hosts.Hosts,
                Query = q,
                State = state,
            });
        });

        // Change an identity's plan: a new subscription, with the one it had going to its history
        admin.MapPost("/subscriptions", async (HttpContext context, IFormCollection form, PlanService service,
            PlanGrantStore grants, AddOnStore addOns, PlanStore plans, SubscriptionService subscriptions, PlanHosts hosts,
            TimeProvider time) =>
        {
            var admin = Who(context);
            try
            {
                var grant = await service.GrantAsync(Text(form["identity"]), Text(form["planId"]), PlanGrantSource.Admin,
                    Text(form["reference"]), Text(form["host"]), note: Text(form["note"]), madeBy: admin);
                await subscriptions.DetachAsync(grant.Identity, $"admin:{admin}");
                Log(context, "put {Identity} on plan {PlanId} (grant {GrantId})", grant.Identity, grant.PlanId, grant.Id);
                return Back(SubscriptionUrl(grant.Identity),
                    done: grant.Status == PlanGrantStatus.Applied
                        ? $"{grant.Identity} is on {grant.PlanId}, set on {grant.Host}."
                        : $"{grant.Identity} is on {grant.PlanId}; the host does not have it yet ({grant.Status}).");
            }
            catch (Exception e) when (IsRefusal(e) || e is PlanGrantConflictException)
            {
                var rows = await SubscriptionRowsAsync(grants, addOns, plans, subscriptions, time);
                return await Show<AdminSubscriptions>(context, new
                {
                    Rows = rows,
                    Counts = SubscriptionRow.States.ToDictionary(s => s, s => rows.Count(r => r.Is(s))),
                    Plans = await plans.ListAsync(),
                    Hosts = hosts.Hosts,
                    Form = form,
                }, e.Message, StatusCodes.Status400BadRequest);
            }
        });

        admin.MapGet("/subscriptions/{identity}", async (HttpContext context, string identity, PlanGrantStore grants,
            AddOnStore addOns, PlanStore plans, SubscriptionService subscriptions, PlanHosts hosts, TimeProvider time) =>
        {
            if (DomainNames.Normalize(identity) is not { } found)
            {
                return await NotFound(context, "That is not an identity.");
            }
            return await Show<AdminSubscription>(context,
                await SubscriptionPageAsync(found, grants, addOns, plans, subscriptions, hosts, time));
        });

        // Add months to the paid subscription the identity owns, free of charge
        admin.MapPost("/subscriptions/{identity}/extend", async (HttpContext context, string identity,
            IFormCollection form, SubscriptionService subscriptions) =>
        {
            var found = DomainNames.Normalize(identity) ?? "";
            try
            {
                var owned = await subscriptions.OwnedByAsync(found) ??
                            throw new SubscriptionRefusedException($"{found} pays for no subscription.");
                var months = Number(form["months"], "Months") ?? throw new SubscriptionRefusedException("Give the months");
                var extended = await subscriptions.ExtendAsync(owned.Subscription.Id, months);
                Log(context, "extended {Identity}'s subscription {SubscriptionId} by {Months} months", found, extended.Id, months);
                return Back(SubscriptionUrl(found),
                    done: $"Extended by {BillingTerm.Name(months)}: now until {AdminForm.Day(extended.PeriodEnd)}.");
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                return Back(SubscriptionUrl(found), error: e.Message);
            }
        });

        // An invoice paid outside the site: a bank transfer, cash, a payment taken by hand
        admin.MapPost("/subscriptions/{identity}/invoices/{id:long}/paid", async (HttpContext context, string identity,
            long id, IFormCollection form, SubscriptionService subscriptions) =>
        {
            var found = DomainNames.Normalize(identity) ?? "";
            try
            {
                var (invoice, renewed) = await subscriptions.PayAsync(id, $"admin:{Who(context)}", Text(form["reference"]));
                Log(context, "marked invoice {InvoiceId} ({Amount}) paid for subscription {SubscriptionId}", invoice.Id,
                    invoice.Price.Text, renewed.Id);
                return Back(SubscriptionUrl(found), done: $"Paid: now until {AdminForm.Day(renewed.PeriodEnd)}.");
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                return Back(SubscriptionUrl(found), error: e.Message);
            }
        });

        admin.MapPost("/subscriptions/{identity}/auto-renew", async (HttpContext context, string identity,
            IFormCollection form, SubscriptionService subscriptions) =>
        {
            var found = DomainNames.Normalize(identity) ?? "";
            try
            {
                var owned = await subscriptions.OwnedByAsync(found) ??
                            throw new SubscriptionRefusedException($"{found} pays for no subscription.");
                var on = form["on"] == "true";
                await subscriptions.SetAutoRenewAsync(owned.Subscription.Id, found, on);
                Log(context, "turned auto-renew {OnOff} for {Identity}'s subscription", on ? "on" : "off", found);
                return Back(SubscriptionUrl(found), done: on ? "Auto-renew is on." : "Auto-renew is off: it runs out at the end of its term.");
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                return Back(SubscriptionUrl(found), error: e.Message);
            }
        });

        admin.MapPost("/subscriptions/{identity}/add-ons", async (HttpContext context, string identity,
            IFormCollection form, AddOnStore addOns, PlanService service, TimeProvider time) =>
        {
            var found = DomainNames.Normalize(identity) ?? "";
            try
            {
                var kind = Text(form["kind"]) ?? "";
                long amount = kind switch
                {
                    AddOnKind.Storage => Bytes(form["amount"], form["unit"]),
                    AddOnKind.EmailAliases => Number(form["amount"], "Aliases") ?? 0,
                    AddOnKind.PublicWebPresence => form["on"] == "true" ? 1 : 0,
                    _ => 1,
                };
                var addOn = await addOns.AddAsync(found, kind, amount, Text(form["note"]), Day(form["endsOn"]), Who(context),
                    time.GetUtcNow().ToUnixTimeMilliseconds());
                Log(context, "gave {Identity} add-on {AddOnId}: {AddOn}", found, addOn.Id, addOn.Description);
                if (AddOnKind.IsSentToHosts(kind))
                {
                    var current = await service.ReapplyAsync(found);
                    return current is { Status: PlanGrantStatus.Applied }
                        ? Back(SubscriptionUrl(found), done: $"{addOn.Description}, set on {current.Host}.")
                        : Back(SubscriptionUrl(found), done: $"{addOn.Description} added; its host gets it with the plan.");
                }
                return Back(SubscriptionUrl(found), done: $"{addOn.Description} recorded; email is not offered yet.");
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                return Back(SubscriptionUrl(found), error: e.Message);
            }
        });

        admin.MapPost("/subscriptions/{identity}/add-ons/{id:long}/remove", async (HttpContext context, string identity,
            long id, IFormCollection form, AddOnStore addOns, PlanService service, TimeProvider time) =>
        {
            var found = DomainNames.Normalize(identity) ?? "";
            var removed = await addOns.RemoveAsync(found, id, Who(context), time.GetUtcNow().ToUnixTimeMilliseconds());
            if (removed == null)
            {
                return Back(SubscriptionUrl(found), error: "That add-on is not active.");
            }
            Log(context, "took away {Identity}'s add-on {AddOnId}: {AddOn}", found, id, removed.Description);
            if (AddOnKind.IsSentToHosts(removed.Kind))
            {
                await service.ReapplyAsync(found);
            }
            return Back(SubscriptionUrl(found), done: $"{removed.Description} taken away.");
        });

        // Try the subscription on its host again now: a pending or failed one, or an applied one set again
        admin.MapPost("/subscriptions/{identity}/apply", async (HttpContext context, string identity,
            IFormCollection form, PlanService service) =>
        {
            var found = DomainNames.Normalize(identity) ?? "";
            var current = await service.ReapplyAsync(found);
            if (current == null)
            {
                return Back(SubscriptionUrl(found), error: $"{found} has no subscription.");
            }
            Log(context, "set {Identity}'s subscription on its host again", found);
            return current.Status == PlanGrantStatus.Applied
                ? Back(SubscriptionUrl(found), done: $"Set on {current.Host}.")
                : Back(SubscriptionUrl(found), error: $"Still {current.Status}: {current.LastError}");
        });

        //
        // Renewals: paid subscriptions running out, in their grace, or moved back to free
        //

        admin.MapGet("/renewals", async (HttpContext context, string? within, PlanGrantStore grants,
            SubscriptionService subscriptions, SubscriptionStore subscriptionStore, TimeProvider time) =>
        {
            within = within is { } chosen && RenewalsWithin.Contains(chosen) ? chosen : "30";
            return await Show<AdminRenewals>(context, new
            {
                Rows = await RenewalsAsync(within, subscriptions, subscriptionStore, time),
                Dropped = await DroppedAsync(grants, time),
                Within = within,
            });
        });

        admin.MapGet("/renewals.csv", async (HttpContext context, string? within, SubscriptionService subscriptions,
            SubscriptionStore subscriptionStore, TimeProvider time) =>
        {
            within = within is { } chosen && RenewalsWithin.Contains(chosen) ? chosen : "30";
            var csv = new System.Text.StringBuilder(
                "owner,plan,members,price,term_months,auto_renew,since,until,days_left,state,open_invoice\n");
            foreach (var row in await RenewalsAsync(within, subscriptions, subscriptionStore, time))
            {
                var s = row.View.Subscription;
                csv.AppendLine(string.Join(",", Csv(s.Owner), Csv(s.PlanId),
                    row.View.Holding.Count.ToString(CultureInfo.InvariantCulture), AdminForm.Dollars(s.PriceCents),
                    s.TermMonths.ToString(CultureInfo.InvariantCulture), s.AutoRenew ? "yes" : "no",
                    AdminForm.Day(s.CreatedAt), AdminForm.Day(s.PeriodEnd),
                    row.DaysLeft.ToString(CultureInfo.InvariantCulture), row.State,
                    row.View.OpenInvoice is { } open ? AdminForm.Dollars(open.AmountCents) : ""));
            }
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
                $"renewals-{within}-{time.GetUtcNow():yyyy-MM-dd}.csv");
        });

        //
        // Codes
        //

        admin.MapGet("/codes", async (HttpContext context, string? owner, InvitationCodeStore codes, PlanStore plans,
            PlanHosts hosts) =>
        {
            owner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();
            string? problem = null;
            List<InvitationCode> list;
            try
            {
                list = await codes.ListAsync(owner);
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                list = [];
                problem = e.Message;
            }
            list.Reverse();
            return await Show<AdminCodes>(context, new
            {
                Codes = list,
                Plans = await plans.ListAsync(),
                Owner = owner,
                SignupHost = SignupHosts(hosts)[0],
            }, problem);
        });

        admin.MapPost("/codes", async (HttpContext context, IFormCollection form, InvitationCodeStore codes, PlanStore plans,
            PlanHosts hosts) =>
        {
            try
            {
                var plan = await ExistingPlanAsync(plans, form["planId"]);
                var code = await codes.CreateAsync(Text(form["owner"]),
                    Number(form["uses"], "Uses") ?? throw new InvalidInvitationCodeRequestException("Give the number of uses"),
                    Text(form["source"]), true, plan.Id);
                Log(context, "created code {Code} for plan {PlanId}", code.Code, plan.Id);
                return Back(CodeUrl(code.Code), done: $"Code {code.Code} created.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                var list = await codes.ListAsync(null);
                list.Reverse();
                return await Show<AdminCodes>(context, new
                {
                    Codes = list,
                    Plans = await plans.ListAsync(),
                    Form = form,
                    SignupHost = SignupHosts(hosts)[0],
                }, e.Message, StatusCodes.Status400BadRequest);
            }
        });

        admin.MapGet("/codes/stats", async (HttpContext context, int? days, InvitationCodeStore codes, TimeProvider time) =>
        {
            days = days is > 0 ? days : null;
            return await Show<AdminStats>(context, new
            {
                Stats = await codes.StatsAsync(days == null ? null : DaysAgo(time, days.Value)),
                Days = days,
            });
        });

        // One channel's signups: who, with which code and when, and the subscription each has now
        admin.MapGet("/codes/stats/channel", async (HttpContext context, string? name, int? days, InvitationCodeStore codes,
            PlanGrantStore grants, AddOnStore addOns, PlanStore plans, SubscriptionService subscriptions, TimeProvider time) =>
        {
            days = days is > 0 ? days : null;
            var channel = name?.Trim() ?? "";
            var rows = (await SubscriptionRowsAsync(grants, addOns, plans, subscriptions, time))
                .ToDictionary(r => r.Grant.Identity, StringComparer.OrdinalIgnoreCase);
            var signups = await codes.SignupsAsync(channel, days == null ? null : DaysAgo(time, days.Value));
            return await Show<AdminChannel>(context, new
            {
                Channel = channel,
                Days = days,
                Signups = signups.Select(s => new ChannelSignup(s,
                    s.Identity == null ? null : rows.GetValueOrDefault(s.Identity))).ToList(),
            });
        });

        // The site's traffic over the last day, 7, 30 or 90
        admin.MapGet("/traffic", async (HttpContext context, int? days, AnalyticsStore analytics, TimeProvider time) =>
        {
            days = days is 1 or 7 or 30 or AnalyticsStore.KeepDays ? days : 7;
            return await Show<AdminTraffic>(context, new { Stats = await analytics.StatsAsync(days.Value, time.GetUtcNow()) });
        });

        admin.MapGet("/codes/{code}", async (HttpContext context, string code, InvitationCodeStore codes, PlanStore plans,
            PlanHosts hosts) =>
            await codes.GetAsync(code) is { } found
                ? await Show<AdminCode>(context, new { Code = found, Plans = await plans.ListAsync(), SignupHosts = SignupHosts(hosts) })
                : await NotFound(context, "There is no such code."));

        admin.MapPost("/codes/{code}", async (HttpContext context, string code, IFormCollection form,
            InvitationCodeStore codes, PlanStore plans, PlanHosts hosts) =>
        {
            try
            {
                var plan = await ExistingPlanAsync(plans, form["planId"]);
                var updated = await codes.UpdateAsync(code, new InvitationCodeChange(
                    MaxUses: Number(form["uses"], "Uses") ?? throw new InvalidInvitationCodeRequestException("Give the number of uses"),
                    Source: Text(form["source"]) ?? "",
                    Owner: Text(form["owner"]) ?? "",
                    PlanId: plan.Id));
                if (updated == null)
                {
                    return await NotFound(context, "There is no such code.");
                }
                Log(context, "changed code {Code}", updated.Code);
                return Back(CodeUrl(updated.Code), done: "Saved.");
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return await codes.GetAsync(code) is { } found
                    ? await Show<AdminCode>(context, new
                    {
                        Code = found,
                        Plans = await plans.ListAsync(),
                        Form = form,
                        SignupHosts = SignupHosts(hosts),
                    }, e.Message, StatusCodes.Status400BadRequest)
                    : await NotFound(context, "There is no such code.");
            }
        });

        admin.MapPost("/codes/{code}/{action:regex(^(disable|enable)$)}", async (HttpContext context, string code,
            string action, IFormCollection form, InvitationCodeStore codes) =>
        {
            var updated = await codes.UpdateAsync(code, new InvitationCodeChange(Disabled: action == "disable"));
            if (updated == null)
            {
                return await NotFound(context, "There is no such code.");
            }
            Log(context, action + "d code {Code}", updated.Code);
            return Back(CodeUrl(updated.Code), done: updated.Disabled ? "Disabled." : "Enabled.");
        });

        // As a signup would, with the signup key: take a use, or give the latest back
        admin.MapPost("/codes/{code}/use", async (HttpContext context, string code, IFormCollection form,
            InvitationCodeStore codes) =>
        {
            if (await codes.GetAsync(code) is not { } found)
            {
                return await NotFound(context, "There is no such code.");
            }
            var used = await codes.TryUseAsync(found.Code);
            if (used == null)
            {
                return Back(CodeUrl(found.Code), error: "No use taken: the code is disabled or used up.");
            }
            Log(context, "took a use of code {Code}", found.Code);
            return Back(CodeUrl(found.Code), done: $"A use taken; {used.UsesLeft} left.");
        });

        admin.MapPost("/codes/{code}/undo-use", async (HttpContext context, string code, IFormCollection form,
            InvitationCodeStore codes) =>
        {
            if (await codes.GetAsync(code) is not { } found)
            {
                return await NotFound(context, "There is no such code.");
            }
            await codes.UndoUseAsync(found.Code);
            var after = await codes.GetAsync(found.Code);
            Log(context, "gave back a use of code {Code}", found.Code);
            return after!.UsesLeft > found.UsesLeft
                ? Back(CodeUrl(found.Code), done: $"A use given back; {after.UsesLeft} left.")
                : Back(CodeUrl(found.Code), error: "Nothing given back: the code has no uses taken.");
        });

        //
        // Checkout: who may buy, while there is no real payment (a beta switch)
        //

        admin.MapGet("/purchases", () => Results.Redirect($"{Root}/checkout"));

        admin.MapGet("/checkout", async (HttpContext context, PurchaseAccessStore purchases) =>
            await Show<AdminCheckout>(context, new { Access = await purchases.GetAsync() }));

        admin.MapPost("/checkout/open", async (HttpContext context, IFormCollection form, PurchaseAccessStore purchases) =>
        {
            var open = form["open"] == "true";
            await purchases.SetOpenAsync(open);
            Log(context, open ? "opened checkout" : "closed checkout");
            return Back($"{Root}/checkout",
                done: open ? "Checkout is open: anyone signed in can buy." : "Checkout is closed: only testers can buy.");
        });

        admin.MapPost("/checkout/testers", async (HttpContext context, IFormCollection form,
            PurchaseAccessStore purchases, TimeProvider time) =>
        {
            try
            {
                var identity = DomainNames.Normalize(form["identity"]);
                await purchases.AddTesterAsync(form["identity"], time.GetUtcNow().ToUnixTimeMilliseconds());
                Log(context, "added tester {Identity}", identity);
                return Back($"{Root}/checkout", done: $"{identity} can buy.");
            }
            catch (InvalidInvitationCodeRequestException e)
            {
                return Back($"{Root}/checkout", error: e.Message);
            }
        });

        admin.MapPost("/checkout/testers/{identity}/remove", async (HttpContext context, string identity,
            IFormCollection form, PurchaseAccessStore purchases) =>
        {
            if (await purchases.RemoveTesterAsync(identity) == null)
            {
                return Back($"{Root}/checkout", error: $"{identity} is not a tester.");
            }
            Log(context, "removed tester {Identity}", identity);
            return Back($"{Root}/checkout", done: $"{identity} is no longer a tester.");
        });

        //
        // Abuse reports
        //

        admin.MapGet("/reports", async (HttpContext context, int? days, ReportStore reports, TimeProvider time) =>
        {
            days = days is > 0 ? days : null;
            return await Show<AdminReports>(context, new
            {
                Reports = await reports.ListAsync(days == null ? null : DaysAgo(time, days.Value)),
                Days = days,
            });
        });
    }

    //

    /// <summary>
    /// Deletes a plan no code or grant uses. Found is false when there is no such plan; Conflict says why one in use
    /// was not deleted.
    /// </summary>
    public static async Task<(bool Found, string? Conflict)> DeletePlanAsync(PlanStore plans, InvitationCodeStore codes,
        PlanGrantStore grants, string id)
    {
        if (await plans.GetAsync(id) is not { } plan)
        {
            return (false, null);
        }

        var codesUsing = await codes.CountWithPlanAsync(plan.Id);
        var grantsUsing = await grants.CountWithPlanAsync(plan.Id);
        if (codesUsing > 0 || grantsUsing > 0)
        {
            return (true, $"Plan {plan.Id} is used by {codesUsing} code(s) and {grantsUsing} grant(s); " +
                          "mark it not for sale instead");
        }

        await plans.DeleteAsync(plan.Id);
        return (true, null);
    }

    /// <summary>
    /// The provisioning servers a code can be signed up with, as the sign-up links offer them: the site's own sign-up
    /// server first (SitePages.DefaultSignupUrl), then every identity host the service knows. A code works on any of
    /// them: it is not tied to one.
    /// </summary>
    public static List<string> SignupHosts(PlanHosts hosts) =>
        new[] { new Uri(SitePages.DefaultSignupUrl).Host }.Concat(hosts.Hosts).Distinct().ToList();

    /// <summary>Where someone signs up with the code: the sign-up page, with the code filled in</summary>
    public static string SignupUrl(string host, string code) =>
        $"https://{host}/?invitation-code={Uri.EscapeDataString(code)}";

    public static string SubscriptionUrl(string identity) => $"{Root}/subscriptions/{Uri.EscapeDataString(identity)}";

    /// <summary>The renewals list's windows: running out within this many days, or in their grace</summary>
    public static readonly string[] RenewalsWithin = ["7", "30", "90", "grace"];

    private static async Task<List<SubscriptionRow>> SubscriptionRowsAsync(PlanGrantStore grants, AddOnStore addOns,
        PlanStore plans, SubscriptionService subscriptions, TimeProvider time)
    {
        var names = (await plans.ListAsync()).ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);
        var active = await addOns.ActiveByIdentityAsync();
        var coverage = await subscriptions.CoverageAsync();
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return (await grants.CurrentAsync()).Select(g =>
        {
            var own = active.GetValueOrDefault(g.Identity) ?? [];
            // Only while its plan is still the subscription's: an admin may have changed it since
            var paid = g.SubscriptionId != null && coverage.TryGetValue(g.Identity, out var c) &&
                       c.Subscription.Id == g.SubscriptionId ? c : default;
            return new SubscriptionRow(g, names.GetValueOrDefault(g.PlanId) ?? g.PlanId, Entitlement.Of(g, own), own, now,
                paid.Subscription, paid.Member);
        }).ToList();
    }

    private static async Task<object> SubscriptionPageAsync(string identity, PlanGrantStore grants, AddOnStore addOns,
        PlanStore plans, SubscriptionService subscriptions, PlanHosts hosts, TimeProvider time)
    {
        var history = await grants.ListAsync(identity, null);
        var all = await addOns.ListAsync(identity);
        var active = all.Where(a => a.IsActive).ToList();
        var planList = await plans.ListAsync();
        var current = history.FirstOrDefault();
        var covering = await subscriptions.CoveringAsync(identity);
        var paid = current?.SubscriptionId != null && covering?.View.Subscription.Id == current.SubscriptionId ? covering : null;
        return new
        {
            Identity = identity,
            Row = current == null
                ? null
                : new SubscriptionRow(current, planList.FirstOrDefault(p => UpgradePage.SamePlan(p.Id, current.PlanId))?.Name
                                               ?? current.PlanId, Entitlement.Of(current, active), active,
                    time.GetUtcNow().ToUnixTimeMilliseconds(), paid?.View.Subscription, paid?.Member),
            Owned = await subscriptions.OwnedByAsync(identity),
            History = history,
            AddOns = all,
            Plans = planList,
            Hosts = hosts.Hosts,
        };
    }

    // Running subscriptions whose term runs out within the window, or in their grace, soonest first
    private static async Task<List<RenewalRow>> RenewalsAsync(string within, SubscriptionService subscriptions,
        SubscriptionStore store, TimeProvider time)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var rows = new List<RenewalRow>();
        foreach (var subscription in await store.ListAsync(activeOnly: true))
        {
            var row = new RenewalRow(await subscriptions.ViewOfAsync(subscription), now);
            if (within == "grace" ? row.State == "grace" : row.State != "grace" && row.DaysLeft <= int.Parse(within))
            {
                rows.Add(row);
            }
        }
        return rows.OrderBy(r => r.View.Subscription.PeriodEnd).ToList();
    }

    // Identities moved back to the free plan in the last 90 days, newest first
    private static async Task<List<PlanGrant>> DroppedAsync(PlanGrantStore grants, TimeProvider time)
    {
        var since = DaysAgo(time, 90);
        return (await grants.ListAsync(null, null))
            .Where(g => g.Source == PlanGrantSource.Expiry && g.CreatedAt >= since)
            .ToList();
    }

    public static string PlanUrl(string id) => $"{Root}/plans/{Uri.EscapeDataString(id)}";

    public static string CodeUrl(string code) => $"{Root}/codes/{Uri.EscapeDataString(code)}";

    /// <summary>One channel's signups, over the last so many days (null for all)</summary>
    public static string ChannelUrl(string channel, int? days) =>
        $"{Root}/codes/stats/channel?name={Uri.EscapeDataString(channel)}" + (days == null ? "" : $"&days={days}");

    /// <summary>The terms the new-plan form asks a price for: a month, a year, two years</summary>
    public static readonly int[] NewPlanTerms = [1, 12, 24];

    // Not signed in: to sign in, and back. Signed in, not an admin: nothing here. Otherwise the admin, for the page
    private static async ValueTask<object?> GuardAsync(EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var services = context.RequestServices;
        if (!services.GetRequiredService<UpgradeOptions>().Enabled)
        {
            return Results.NotFound();
        }

        var signedIn = services.GetRequiredService<UpgradeSession>().Read(context);
        if (signedIn == null)
        {
            return Results.Redirect(SitePages.SignInForAdmin);
        }
        if (!await services.GetRequiredService<AdminStore>().IsAdminAsync(signedIn.Identity))
        {
            return UpgradePage.Show<MessagePage>(new
            {
                Title = "Not found",
                Message = "There is nothing here.",
                LinkText = "Back to the plans",
                LinkHref = "/",
            }, StatusCodes.Status404NotFound);
        }

        context.Items[AdminItem] = signedIn;
        return await next(invocation);
    }

    private static async Task<IResult> Show<TPage>(HttpContext context, object parameters, string? error = null,
        int status = StatusCodes.Status200OK) where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        var signedIn = (UpgradeSession.Data)context.Items[AdminItem]!;
        var query = context.Request.Query;
        var admin = new AdminContext(signedIn.Identity, signedIn.Name,
            context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context),
            context.Request.Method == HttpMethods.Get ? Text(query["done"]) : null,
            error ?? (context.Request.Method == HttpMethods.Get ? Text(query["error"]) : null),
            await context.RequestServices.GetRequiredService<PurchaseAccessStore>().GetAsync());

        var values = new Dictionary<string, object?> { ["Admin"] = admin };
        foreach (var property in parameters.GetType().GetProperties())
        {
            values[property.Name] = property.GetValue(parameters);
        }
        return UpgradePage.Show<TPage>(values, status);
    }

    private static Task<IResult> NotFound(HttpContext context, string message) =>
        Show<AdminMessage>(context, new { Message = message }, status: StatusCodes.Status404NotFound);

    // Back to a page, saying what was done or why not; the page shows it once
    private static IResult Back(string path, string? done = null, string? error = null)
    {
        var separator = path.Contains('?') ? '&' : '?';
        return done != null ? Results.Redirect($"{path}{separator}done={Uri.EscapeDataString(done)}")
            : error != null ? Results.Redirect($"{path}{separator}error={Uri.EscapeDataString(error)}")
            : Results.Redirect(path);
    }

    // The signed-in admin
    private static string Who(HttpContext context) => ((UpgradeSession.Data)context.Items[AdminItem]!).Identity;

    // A day as the date field sends it (2027-01-31), as unix ms at its start, UTC; null when none was given
    private static long? Day(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateTimeOffset.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var day) ? day.ToUnixTimeMilliseconds()
        : throw new InvalidAddOnRequestException($"'{value}' is not a date");

    // A CSV field, quoted when it has to be
    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static void Log(HttpContext context, string action, params object?[] args)
    {
        var signedIn = (UpgradeSession.Data)context.Items[AdminItem]!;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Raven.Hosting.Admin");
        logger.LogInformation("Admin {Admin} " + action, [signedIn.Identity, .. args]);
    }

    // What the stores refuse with a message for the person: bad input, or a change that conflicts
    private static bool IsRefusal(Exception e) =>
        e is InvalidInvitationCodeRequestException or PlanConflictException;

    private static async Task<Plan> ExistingPlanAsync(PlanStore plans, string? planId) =>
        await plans.GetAsync(planId) ??
        throw new InvalidInvitationCodeRequestException(string.IsNullOrWhiteSpace(planId)
            ? "Choose a plan"
            : $"There is no plan '{planId.Trim()}'");

    private static long DaysAgo(TimeProvider time, int days) =>
        time.GetUtcNow().AddDays(-days).ToUnixTimeMilliseconds();

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Checked(IFormCollection form, string name) => form[name] == "true";

    private static int? Number(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? null
        : int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n
        : throw new InvalidInvitationCodeRequestException($"{what} must be a whole number");

    // Dollars as typed (5, 4.99, $1,200) in cents
    private static long Cents(string? dollars)
    {
        var text = dollars?.Trim().TrimStart('$').Replace(",", "");
        if (string.IsNullOrEmpty(text) ||
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            decimal.Round(amount, 2) != amount)
        {
            throw new InvalidPlanRequestException($"'{dollars}' is not an amount in dollars, like 5 or 4.99");
        }
        return (long)(amount * 100);
    }

    // A storage size as typed, in the unit chosen, in bytes (1024-based, like the plans show it)
    private static long Bytes(string? amount, string? unit)
    {
        var multiplier = unit switch
        {
            "TB" => 1L << 40,
            "GB" => 1L << 30,
            "MB" => 1L << 20,
            "bytes" => 1L,
            _ => throw new InvalidPlanRequestException("Choose a storage unit"),
        };
        if (!decimal.TryParse(amount?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out var value) || value <= 0)
        {
            throw new InvalidPlanRequestException("Storage must be a number more than 0, like 100 or 1.5");
        }
        return (long)decimal.Round(value * multiplier);
    }
}

/// <summary>
/// Reading back a form that was refused, so the person does not type it all again
/// </summary>
public static class AdminForm
{
    public static string Value(IFormCollection? form, string name, string? fallback) =>
        form != null && form.ContainsKey(name) ? form[name].ToString() : fallback ?? "";

    public static bool Checked(IFormCollection? form, string name, bool fallback) =>
        form != null ? form[name] == "true" : fallback;

    /// <summary>A size as the form shows it: the largest unit it is a whole number of, and that number</summary>
    public static (string Amount, string Unit) Size(long bytes)
    {
        foreach (var (size, unit) in new[] { (1L << 40, "TB"), (1L << 30, "GB"), (1L << 20, "MB") })
        {
            if (bytes % size == 0)
            {
                return ((bytes / size).ToString(CultureInfo.InvariantCulture), unit);
            }
        }
        return (bytes.ToString(CultureInfo.InvariantCulture), "bytes");
    }

    /// <summary>Unix ms as people read it, in UTC</summary>
    public static string When(long? ms) => ms is { } value
        ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
        : "";

    /// <summary>
    /// Marketing channels offered for a code's source, so the same one is spelled the same way and counts together in
    /// the code stats; any other text is fine too
    /// </summary>
    public static readonly string[] SourceSuggestions =
        ["X", "Instagram", "Facebook", "LinkedIn", "TikTok", "YouTube", "Reddit", "Newsletter", "Event", "Referral", "Website"];

    /// <summary>Unix ms as a day, UTC: 2027-10-23</summary>
    public static string Day(long? ms) => ms is { } value
        ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : "";

    /// <summary>Cents as the price form shows them: 5, 4.99</summary>
    public static string Dollars(long cents) =>
        (cents / 100m).ToString(cents % 100 == 0 ? "0" : "0.00", CultureInfo.InvariantCulture);
}
