using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Raven.Hosting;

// Raven Hosting's service. It holds the invitation codes (#1908) and counts
// their uses: the identity hosts call it with the signup key to check and use a code at signup; admins call it with
// the create key to create and list.
// It holds the hosting plans, which admins manage with the create key (README, "Plans"), and grants them: it sets
// the plan an identity is owed, and what the plan gives, on the identity host that serves it, with the plan key
// (README, "Plan grants"). Owners buy bigger plans on the upgrade page, after signing in with YouAuth (README,
// "Upgrade page"), and anyone sees the plans on the site around it, at ravenhosting.cloud (SitePages), which also
// takes reports of content that breaks the terms.

var builder = WebApplication.CreateBuilder(args);

var databasePath = Required("RavenHosting:DatabasePath");
var signupApiKey = Required("RavenHosting:SignupApiKey");
var createApiKey = Required("RavenHosting:CreateApiKey");

var requestsPerMinute = builder.Configuration.GetValue("RavenHosting:RequestsPerMinutePerIp", 300);

var store = new InvitationCodeStore(databasePath);
var planStore = new PlanStore(databasePath);
var grantStore = new PlanGrantStore(databasePath);
builder.Services.AddSingleton(planStore);
var reportStore = new ReportStore(databasePath);
builder.Services.AddSingleton(reportStore);
var purchaseAccess = new PurchaseAccessStore(databasePath);
builder.Services.AddSingleton(purchaseAccess);
var adminStore = new AdminStore(databasePath);
builder.Services.AddSingleton(adminStore);
builder.Services.AddSingleton(new AddOnStore(databasePath));
builder.Services.AddSingleton(new SubscriptionStore(databasePath));
builder.Services.AddSingleton(store);

var planHosts = PlanHosts.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(planHosts);
builder.Services.AddSingleton(grantStore);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PlanService>();
builder.Services.AddSingleton<SubscriptionService>();
builder.Services.AddHostedService<PlanGrantWorker>();
builder.Services.AddHttpClient(PlanService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));

// The upgrade page: YouAuth logins in progress and the keys sealing them and its session cookie, in the database
var loginStore = new YouAuthLoginStore(databasePath);
builder.Services.AddSingleton(loginStore);
builder.Services.AddSingleton(UpgradeOptions.FromConfiguration(builder.Configuration));
builder.Services.AddDataProtection().SetApplicationName("raven-hosting");
builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = loginStore);
builder.Services.AddSingleton<YouAuthLogin>();
builder.Services.AddSingleton<UpgradeSession>();
builder.Services.AddSingleton<PublicProfiles>();
builder.Services.AddHttpClient(YouAuthLogin.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
// The upgrade page is Razor components (Components/), rendered on the server from its endpoints
builder.Services.AddRazorComponents();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "raven_upgrade_antiforgery";
    options.Cookie.Path = "/";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Every real request is well under 1 KB
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = InvitationCodeApi.MaxRequestBodySize);

// Caddy, on a private address, says who the client is and that it came over https (it ends TLS and
// proxies plain http); nobody else is believed. The upgrade page's secure cookies need the https.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var network in new[] { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7" })
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, IPAddress>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress ?? IPAddress.None,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseForwardedHeaders();
// The upgrade page's stylesheet and fonts (wwwroot/assets). The stylesheet's link carries a version from its content,
// so it is kept a year; the fonts never change under the same name
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = file =>
        file.Context.Response.Headers.CacheControl =
            file.Context.Request.Query.ContainsKey("v") || file.File.Name.EndsWith(".ttf")
                ? "public, max-age=31536000, immutable"
                : "no-cache",
});
app.UseRateLimiter();
app.Use(InvitationCodeApi.RequireApiKey);
app.UseAntiforgery();

app.MapGet("/ping", () => "pong");

UpgradePage.Map(app);
SitePages.Map(app);
AccountPages.Map(app);
AdminPages.Map(app);

// The reports filed on the site's report page, newest first
app.MapGet(ReportApi.Root, async (int? sinceDays) =>
        await reportStore.ListAsync(sinceDays is > 0
            ? DateTimeOffset.UtcNow.AddDays(-sinceDays.Value).ToUnixTimeMilliseconds()
            : null))
    .WithMetadata(new ApiKey(createApiKey));

var root = app.MapGroup(InvitationCodeApi.Root).AddEndpointFilter(InvitationCodeApi.BadRequestOnInvalid);

//
// Signup key: the identity hosts
//

var signup = root.MapGroup("").WithMetadata(new ApiKey(signupApiKey));

signup.MapPost("/validate", async (CodeRequest request) =>
{
    var code = await store.GetAsync(request.Code);
    return new ValidateResponse(code is { UsesLeft: > 0, Disabled: false });
});

// The code comes back with what its plan gives now, which the identity host stores on the new identity. A code whose
// plan is not here (made before plans were) carries its plan Id alone, and the host uses its defaults. A host that
// says who is signing up (identity) has them kept on the use, for the channel pages, and starts their subscription
// here: a free one of the code's plan, with no end, set on the host by the grant worker once the identity is there.
// Hosts that do not say leave it to the host alone.
signup.MapPost("/use", async (CodeRequest request, ILogger<Program> logger, TimeProvider time) =>
{
    var identity = DomainNames.Normalize(request.Identity);
    var code = await store.TryUseAsync(request.Code, identity);
    if (code == null)
    {
        return new UseResponse(false, null);
    }

    var plan = await planStore.GetAsync(code.PlanId);
    if (plan == null && code.PlanId != null)
    {
        logger.LogWarning("Code {code} was used with plan {planId}, which is not a plan here: the identity host " +
                          "gives its default quota (raven.sh plan-create)", code.Code, code.PlanId);
    }

    if (plan != null && identity != null)
    {
        // Never in the way of the signup: the use is taken whatever happens here
        try
        {
            await grantStore.CreateAsync(identity, plan, PlanGrantSource.Code, $"code:{code.Code}:{Guid.NewGuid():N}",
                null, time.GetUtcNow().ToUnixTimeMilliseconds(), note: $"Signed up with code {code.Code}");
        }
        catch (Exception e) when (e is InvalidInvitationCodeRequestException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(e, "Could not start {identity}'s subscription from code {code}", identity, code.Code);
        }
    }
    return new UseResponse(true, UsedInvitationCode.From(code, plan));
});

signup.MapPost("/undo-use", async (CodeRequest request) =>
{
    await store.UndoUseAsync(request.Code);
    return Results.NoContent();
});

//
// Create key: admins
//

var admin = root.MapGroup("").WithMetadata(new ApiKey(createApiKey));

admin.MapPost("/", async (CreateRequest request) =>
    await store.CreateAsync(request.Owner, request.MaxUses, request.Source, request.EnablePublicWebPresence ?? true,
        await ExistingPlanIdAsync(request.PlanId)));

admin.MapGet("/", async (string? owner) => await store.ListAsync(owner));

// Counts codes and signups; sinceDays limits the signups counted to the last that many days
admin.MapGet("/stats", async (int? sinceDays) =>
{
    if (sinceDays is < 1)
    {
        throw new InvalidInvitationCodeRequestException("sinceDays must be 1 or more");
    }

    var since = sinceDays == null ? (long?)null : DateTimeOffset.UtcNow.AddDays(-sinceDays.Value).ToUnixTimeMilliseconds();
    return await store.StatsAsync(since);
});

// Change a code, or disable or enable it. Fields left out stay as they are; "owner": "" removes the owner.
admin.MapPatch("/{code}", async (string code, InvitationCodeChange change) =>
    await store.UpdateAsync(code, change with { PlanId = await ExistingPlanIdAsync(change.PlanId) }) is { } updated
        ? Results.Ok(updated)
        : Results.NotFound());

//
// Plans, create key: admins
//

var plansApi = app.MapGroup(PlanApi.Root)
    .AddEndpointFilter(InvitationCodeApi.BadRequestOnInvalid)
    .WithMetadata(new ApiKey(createApiKey));

plansApi.MapPost("/", async (PlanRequest request) =>
{
    try
    {
        return Results.Ok(await planStore.CreateAsync(request));
    }
    catch (PlanConflictException e)
    {
        return Results.Conflict(e.Message);
    }
});

plansApi.MapGet("/", async () => await planStore.ListAsync());

plansApi.MapGet("/{id}", async (string id) =>
    await planStore.GetAsync(id) is { } plan ? Results.Ok(plan) : Results.NotFound());

// Fields left out stay as they are. Identities already on the plan keep what they were given.
plansApi.MapPatch("/{id}", async (string id, PlanChange change) =>
    await planStore.UpdateAsync(id, change) is { } plan ? Results.Ok(plan) : Results.NotFound());

// What the plan costs for this many months: added, or changed
plansApi.MapPut("/{id}/prices/{months:int}", async (string id, int months, PriceRequest request) =>
    await planStore.SetPriceAsync(id, months,
        request.AmountCents ?? throw new InvalidPlanRequestException("amountCents is required")) is { } plan
        ? Results.Ok(plan)
        : Results.NotFound());

// A term the plan is no longer sold for; not the last price of a plan for sale
plansApi.MapDelete("/{id}/prices/{months:int}", async (string id, int months) =>
{
    try
    {
        var (plan, found) = await planStore.RemovePriceAsync(id, months);
        return found ? Results.Ok(plan) : Results.NotFound();
    }
    catch (PlanConflictException e)
    {
        return Results.Conflict(e.Message);
    }
});

// Only a plan no code or grant uses; to retire one in use, mark it not for sale
plansApi.MapDelete("/{id}", async (string id) =>
    await AdminPages.DeletePlanAsync(planStore, store, grantStore, id) switch
    {
        (false, _) => Results.NotFound(),
        (true, { } conflict) => Results.Conflict(conflict),
        _ => Results.NoContent(),
    });

//
// Plan grants, create key: admins. Paid plans are subscriptions (SubscriptionService), bought on the upgrade page
//

//
// Who may buy on the upgrade page, create key: admins. Closed, with no testers, until an admin opens it
//

var purchasesApi = app.MapGroup(PurchaseApi.Root)
    .AddEndpointFilter(InvitationCodeApi.BadRequestOnInvalid)
    .WithMetadata(new ApiKey(createApiKey));

purchasesApi.MapGet("/", async () => await purchaseAccess.GetAsync());

purchasesApi.MapPut("/open", async (PurchasesOpenRequest request) =>
    await purchaseAccess.SetOpenAsync(
        request.Open ?? throw new InvalidPurchaseAccessRequestException("open is required: true or false")));

purchasesApi.MapPut("/testers/{identity}", async (string identity, TimeProvider time) =>
    await purchaseAccess.AddTesterAsync(identity, time.GetUtcNow().ToUnixTimeMilliseconds()));

purchasesApi.MapDelete("/testers/{identity}", async (string identity) =>
    await purchaseAccess.RemoveTesterAsync(identity) is { } access ? Results.Ok(access) : Results.NotFound());

//
// Who may use the admin pages, create key: the first admins are added here (raven.sh admins add)
//

var adminsApi = app.MapGroup(AdminApi.Root)
    .AddEndpointFilter(InvitationCodeApi.BadRequestOnInvalid)
    .WithMetadata(new ApiKey(createApiKey));

adminsApi.MapGet("/", async () => await adminStore.ListAsync());

adminsApi.MapPut("/{identity}", async (string identity, TimeProvider time) =>
    await adminStore.AddAsync(identity, time.GetUtcNow().ToUnixTimeMilliseconds()));

adminsApi.MapDelete("/{identity}", async (string identity) =>
    await adminStore.RemoveAsync(identity) is { } admins ? Results.Ok(admins) : Results.NotFound());

var grants = app.MapGroup(PlanGrantApi.Root)
    .AddEndpointFilter(InvitationCodeApi.BadRequestOnInvalid)
    .WithMetadata(new ApiKey(createApiKey));

// Owe an identity a plan and set it on its host now; the same reference again is the same grant
grants.MapPost("/", async (GrantRequest request, PlanService plans) =>
{
    try
    {
        return Results.Ok(await plans.GrantAsync(request.Identity, request.PlanId, request.Source, request.Reference,
            request.Host));
    }
    catch (PlanGrantConflictException e)
    {
        return Results.Conflict(e.Message);
    }
});

grants.MapGet("/", async (string? identity, string? status, PlanGrantStore grantStore) =>
    await grantStore.ListAsync(identity, status));

// Try a pending or failed grant again now; 404 when there is none, 409 when it is already applied
grants.MapPost("/{id:long}/retry", async (long id, PlanService plans, PlanGrantStore grantStore) =>
    await plans.RetryAsync(id) is { } grant ? Results.Ok(grant)
    : await grantStore.GetAsync(id) is null ? Results.NotFound()
    : Results.Conflict("The grant is already applied"));

app.Run();

// A code's plan must be one of the plans here; returns its Id as the plan has it. Empty or malformed Ids are left
// for the store to refuse with its own message.
async Task<string?> ExistingPlanIdAsync(string? planId)
{
    if (string.IsNullOrWhiteSpace(planId) || !PlanIds.IsValid(planId.Trim()))
    {
        return planId;
    }
    return (await planStore.GetAsync(planId))?.Id ??
           throw new InvalidInvitationCodeRequestException(
               $"There is no plan '{planId.Trim()}'; create it first (raven.sh plan-create)");
}

string Required(string key)
{
    var value = builder.Configuration[key];
    return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{key} is not configured") : value;
}

/// <param name="Identity">On /use, who is signing up with it, if the host says: starts their subscription here</param>
public record CodeRequest(string? Code, string? Identity = null);
// EnablePublicWebPresence is optional and on by default: identity hosts from odin-core #1974 ignore it and take
// public web presence from the plan. It is still stored and sent for hosts from before that.
public record CreateRequest(string? Owner, int MaxUses, string? Source, bool? EnablePublicWebPresence,
    string? PlanId);
public record ValidateResponse(bool Valid);
/// <param name="Source">code, purchase or admin; admin when left out</param>
/// <param name="Reference">The caller's id for it, e.g. a payment id; made up when left out</param>
/// <param name="Host">The provisioning domain of the host the identity is on, if known; a hint, tried first</param>
public record GrantRequest(string? Identity, string? PlanId, string? Source, string? Reference, string? Host);
/// <param name="AmountCents">What the term costs, in US cents; 0 is free</param>
public record PriceRequest(long? AmountCents);
public record UseResponse(bool Used, UsedInvitationCode? Code);

/// <summary>
/// A code just used, with what its plan gives now, for the identity host to store on the new identity
/// (odin-core <c>InvitationCodeUse</c>)
/// </summary>
/// <param name="QuotaBytes">The plan's quota; null when the code's plan is not here, and the host uses its default</param>
/// <param name="PublicWebPresence">The plan's public web presence; null like QuotaBytes</param>
public record UsedInvitationCode(
    string Code,
    string? Owner,
    string Source,
    bool EnablePublicWebPresence,
    string? PlanId,
    int MaxUses,
    int UsesLeft,
    bool Disabled,
    long? QuotaBytes,
    bool? PublicWebPresence)
{
    public static UsedInvitationCode From(InvitationCode code, Plan? plan) => new(code.Code, code.Owner, code.Source,
        code.EnablePublicWebPresence, code.PlanId, code.MaxUses, code.UsesLeft, code.Disabled, plan?.QuotaBytes,
        plan?.PublicWebPresence);
}

public partial class Program;

/// <summary>
/// The key an endpoint needs in the <see cref="InvitationCodeApi.ApiKeyHeader"/> header
/// </summary>
internal sealed class ApiKey(string key)
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(key);

    public bool Matches(string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected);
}

internal static class PlanApi
{
    public const string Root = "/api/v1/plans";
}

internal static class ReportApi
{
    public const string Root = "/api/v1/reports";
}

internal static class AdminApi
{
    public const string Root = "/api/v1/admins";
}

internal static class PurchaseApi
{
    public const string Root = "/api/v1/purchases";
}

internal static class PlanGrantApi
{
    public const string Root = "/api/v1/plans/grants";
}

internal static class InvitationCodeApi
{
    public const string Root = "/api/v1/invitation-codes";
    public const string ApiKeyHeader = "Odin-Api-Key";
    public const long MaxRequestBodySize = 4 * 1024;

    /// <summary>
    /// Refuses a request without its endpoint's <see cref="ApiKey"/> before anything reads the body
    /// </summary>
    public static async Task RequireApiKey(HttpContext context, RequestDelegate next)
    {
        var apiKey = context.GetEndpoint()?.Metadata.GetMetadata<ApiKey>();
        if (apiKey != null && !apiKey.Matches(context.Request.Headers[ApiKeyHeader].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(context);
    }

    public static async ValueTask<object?> BadRequestOnInvalid(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidInvitationCodeRequestException e)
        {
            return Results.BadRequest(e.Message);
        }
    }
}
