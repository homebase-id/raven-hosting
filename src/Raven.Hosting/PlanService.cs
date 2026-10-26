using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Raven.Hosting;

/// <summary>
/// The identity hosts the service may set plans on: their provisioning domains, and the one plan key they all take
/// (odin-core <c>Registry:AccountService:ApiKey</c>). A host not listed here is never called, whatever a request says.
/// </summary>
public class PlanHosts
{
    public PlanHosts(IEnumerable<string> hosts, string apiKey)
    {
        Hosts = hosts.Select(h => DomainNames.Normalize(h) ??
                                  throw new InvalidOperationException($"RavenHosting:IdentityHosts: '{h}' is not a domain name"))
            .Distinct()
            .ToList();
        ApiKey = apiKey.Trim();
        if (Hosts.Count > 0 && ApiKey == "")
        {
            throw new InvalidOperationException("RavenHosting:AccountServiceApiKey is not configured, but RavenHosting:IdentityHosts is");
        }
    }

    /// <summary>Provisioning domains, e.g. provisioning.demo.dominion.id; empty when no host is configured</summary>
    public IReadOnlyList<string> Hosts { get; }

    public string ApiKey { get; }

    public bool IsKnown(string? host) => host != null && Hosts.Contains(host);

    public static PlanHosts FromConfiguration(IConfiguration configuration) =>
        new(configuration.GetSection("RavenHosting:IdentityHosts").Get<string[]>() ?? [],
            configuration["RavenHosting:AccountServiceApiKey"] ?? "");
}

/// <summary>
/// Sets the plans grants owe on the identity hosts. Finds the host that serves an identity by asking the known
/// hosts (the hint and the last host seen first), sets the plan there with the grant's reference, and keeps a grant
/// pending, with backoff, until its host has it. The host API (odin-core #1994, on the provisioning domain):
/// <c>GET /api/plans/v1/identities/{identity}</c> answers what the identity has now (<see cref="IdentityOnHost"/>), and
/// <c>PUT .../{identity}/plan {planId, quotaBytes, publicWebPresence, reference}</c> sets them; values left out stay as
/// they are. Both are 404 for an identity the host does not serve.
/// </summary>
public class PlanService(
    PlanGrantStore store,
    PlanStore planStore,
    AddOnStore addOns,
    PlanHosts hosts,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<PlanService> logger)
{
    public const string HttpClientName = "plan-hosts";
    public const string ApiKeyHeader = "Odin-Api-Key";

    /// <summary>How long after a paid term runs out the identity keeps its plan, before going back to the free plan</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(14);

    // Pending grants wait 1, 2, 4... minutes between attempts, at most 6 hours: a paid plan is never given up on
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromHours(6);

    // One grant at a time, so the worker and an API call never set the same plan at once
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    //

    /// <summary>
    /// Makes the grant (or finds the one with this reference) and tries to set it on its host straight away. The
    /// grant records what the plan gives now; a plan that does not exist is refused.
    /// </summary>
    /// <param name="note">Why it was made, shown in the identity's history</param>
    /// <param name="madeBy">The admin who made it, on the admin pages</param>
    /// <param name="quotaBytes">The quota to give, when not the plan's: a member's share of a group plan</param>
    /// <param name="subscriptionId">The paid subscription it comes from (<see cref="SubscriptionService"/>)</param>
    public async Task<PlanGrant> GrantAsync(string? identity, string? planId, string? source, string? reference,
        string? host, string? note = null, string? madeBy = null, long? quotaBytes = null, long? subscriptionId = null)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            throw new InvalidPlanGrantRequestException("planId is required");
        }
        var plan = await planStore.GetAsync(planId) ??
                   throw new InvalidPlanGrantRequestException($"There is no plan '{planId.Trim()}'");

        var (grant, created) = await store.CreateAsync(identity, plan, source, reference, host, Now, note, madeBy,
            quotaBytes, subscriptionId);
        return created || grant.Status == PlanGrantStatus.Pending ? await ApplyAsync(grant) : grant;
    }

    /// <summary>
    /// Tries a pending or failed grant again now; null when there is no such grant or it is already applied
    /// </summary>
    public async Task<PlanGrant?> RetryAsync(long id)
    {
        var grant = await store.MakeDueAsync(id, Now);
        return grant == null ? null : await ApplyAsync(grant);
    }

    /// <summary>
    /// Sets the identity's subscription on its host again, with its add-ons as they are now: after an add-on was
    /// given or taken away. An applied one is set again straight away; a failed one is tried again; a pending one is
    /// left to its next attempt, which sends the add-ons as they are then. Null when it has no subscription.
    /// </summary>
    public async Task<PlanGrant?> ReapplyAsync(string identity)
    {
        var current = await store.CurrentForAsync(identity);
        switch (current?.Status)
        {
            case null:
                return null;
            case PlanGrantStatus.Failed:
                return await RetryAsync(current.Id);
            case PlanGrantStatus.Pending:
                return current;
        }

        await _applyLock.WaitAsync();
        try
        {
            var located = await LocateAsync(current.Identity, current.Host);
            if (located.Host == null)
            {
                logger.LogWarning("Could not set {identity}'s add-ons: {error}", identity, located.Error);
                return current;
            }
            var outcome = await SetPlanAsync(located.Host, current);
            if (outcome.Status != HttpStatusCode.OK)
            {
                logger.LogWarning("Could not set {identity}'s add-ons on {host}: {error}", identity, located.Host,
                    outcome.Error);
                return current;
            }
            await store.SetKnownHostAsync(identity, located.Host, Now);
            return await store.MarkReappliedAsync(current.Id, located.Host, Now);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>
    /// Takes away add-ons whose end date passed, and sets each identity's plan again without them
    /// </summary>
    public async Task ExpireAddOnsAsync()
    {
        foreach (var addOn in await addOns.EndedAsync(Now))
        {
            if (await addOns.RemoveAsync(addOn.Identity, addOn.Id, "expiry", Now) != null)
            {
                logger.LogInformation("Add-on {id} for {identity} ({addOn}) ran out", addOn.Id, addOn.Identity,
                    addOn.Description);
                await ReapplyAsync(addOn.Identity);
            }
        }
    }

    /// <summary>
    /// Tries every pending grant that is due; returns how many it tried
    /// </summary>
    public async Task<int> ApplyDueAsync(int limit = 100)
    {
        var due = await store.DueAsync(Now, limit);
        foreach (var grant in due)
        {
            await ApplyAsync(grant);
        }
        return due.Count;
    }

    //

    /// <summary>
    /// Sets the grant's plan on the host that serves its identity. Applied when the host says so; failed when the
    /// host refuses it (400: an empty plan Id or a quota that is not more than 0); otherwise pending, to try again
    /// later.
    /// </summary>
    public async Task<PlanGrant> ApplyAsync(PlanGrant grant)
    {
        await _applyLock.WaitAsync();
        try
        {
            var current = await store.GetAsync(grant.Id) ?? grant;
            if (current.Status != PlanGrantStatus.Pending)
            {
                return current;
            }

            var located = await LocateAsync(current.Identity, current.HostHint);
            if (located.Host == null)
            {
                return await PendingAsync(current, located.Error!);
            }

            var outcome = await SetPlanAsync(located.Host, current);
            if (outcome.Status == HttpStatusCode.NotFound)
            {
                // It moved since it was found: look again, without the cache, and try the host that has it now
                await store.ForgetKnownHostAsync(current.Identity);
                located = await LocateAsync(current.Identity, hint: null, skip: located.Host);
                if (located.Host == null)
                {
                    return await PendingAsync(current, located.Error!);
                }
                outcome = await SetPlanAsync(located.Host, current);
            }

            switch (outcome.Status)
            {
                case HttpStatusCode.OK:
                    await store.SetKnownHostAsync(current.Identity, located.Host, Now);
                    logger.LogInformation("Set {identity} to plan {planId} on {host} (grant {id}, reference {reference})",
                        current.Identity, current.PlanId, located.Host, current.Id, current.Reference);
                    return (await store.MarkAppliedAsync(current.Id, located.Host, Now))!;

                case HttpStatusCode.BadRequest:
                    logger.LogWarning("{host} refused plan {planId} for {identity} (grant {id}): {error}",
                        located.Host, current.PlanId, current.Identity, current.Id, outcome.Error);
                    return (await store.MarkFailedAsync(current.Id, located.Host,
                        $"{located.Host} refused the plan: {outcome.Error}", Now))!;

                default:
                    return await PendingAsync(current, $"{located.Host}: {outcome.Error}");
            }
        }
        finally
        {
            _applyLock.Release();
        }
    }

    //

    /// <summary>
    /// Re-sets the plan each identity's latest applied grant gave it, where its host says it has another plan Id,
    /// quota or public web presence: changed by hand, or lost. Only what both sides have is compared: a grant from
    /// before plans were kept here by its plan Id alone, and a host that does not say its quota or public web
    /// presence (from before odin-core #1994) not on those. Returns the identities it corrected.
    /// </summary>
    public async Task<List<string>> ReconcileAsync()
    {
        var corrected = new List<string>();
        foreach (var grant in await store.LatestAppliedAsync())
        {
            await _applyLock.WaitAsync();
            try
            {
                var located = await LocateIdentityAsync(grant.Identity, grant.Host, skip: null);
                var drift = located.Identity == null
                    ? null
                    : Drift(grant, Entitlement.Of(grant, await addOns.ListAsync(grant.Identity)), located.Identity);
                if (located.Host == null || drift == null)
                {
                    continue;
                }

                var outcome = await SetPlanAsync(located.Host, grant);
                if (outcome.Status == HttpStatusCode.OK)
                {
                    await store.SetKnownHostAsync(grant.Identity, located.Host, Now);
                    await store.MarkReappliedAsync(grant.Id, located.Host, Now);
                    corrected.Add(grant.Identity);
                    logger.LogWarning("Reconcile: {identity} had {drift} on {host}, set back to plan {planId} (grant {id})",
                        grant.Identity, drift, located.Host, grant.PlanId, grant.Id);
                }
                else
                {
                    logger.LogWarning("Reconcile: could not set {identity} back to {planId} on {host}: {error}",
                        grant.Identity, grant.PlanId, located.Host, outcome.Error);
                }
            }
            finally
            {
                _applyLock.Release();
            }
        }
        return corrected;
    }

    //

    /// <summary>
    /// Which host serves the identity, and the plan it has there. Asks the hint first if it is a known host, then the
    /// host last seen, then every other known host; the first to answer 200 has it. Host is null when none did, with
    /// the reason in Error.
    /// </summary>
    public async Task<(string? Host, string? PlanId, string? Error)> LocateAsync(string identity, string? hint,
        string? skip = null)
    {
        var located = await LocateIdentityAsync(identity, hint, skip);
        return (located.Host, located.Identity?.PlanId, located.Error);
    }

    /// <summary>
    /// As <see cref="LocateAsync"/>, with everything the host says the identity has now: its plan may have been
    /// changed there since this service last set it
    /// </summary>
    public Task<(string? Host, IdentityOnHost? Identity, string? Error)> LookUpAsync(string identity, string? hint) =>
        LocateIdentityAsync(identity, hint, skip: null);

    private async Task<(string? Host, IdentityOnHost? Identity, string? Error)> LocateIdentityAsync(string identity,
        string? hint, string? skip)
    {
        if (hosts.Hosts.Count == 0)
        {
            return (null, null, "no identity hosts are configured (RavenHosting:IdentityHosts)");
        }

        var order = new List<string>();
        foreach (var candidate in new[] { DomainNames.Normalize(hint), await store.GetKnownHostAsync(identity) }
                     .Concat(hosts.Hosts))
        {
            if (hosts.IsKnown(candidate) && candidate != skip && !order.Contains(candidate!))
            {
                order.Add(candidate!);
            }
        }

        var unreachable = new List<string>();
        foreach (var host in order)
        {
            var answer = await GetPlanAsync(host, identity);
            if (answer.Status == HttpStatusCode.OK)
            {
                return (host, answer.Identity, null);
            }
            if (answer.Status != HttpStatusCode.NotFound)
            {
                unreachable.Add($"{host}: {answer.Error}");
            }
        }

        return (null, null, unreachable.Count == 0
            ? $"no known host serves {identity}"
            : $"no known host said it serves {identity}; " + string.Join("; ", unreachable));
    }

    //

    private async Task<PlanGrant> PendingAsync(PlanGrant grant, string error)
    {
        // The attempt this records is attempt grant.Attempts + 1; wait 1, 2, 4... minutes after it
        var wait = TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks,
            FirstRetry.Ticks * (1L << Math.Min(grant.Attempts, 20))));
        logger.LogInformation("Grant {id} for {identity} still pending, retry in {wait}: {error}",
            grant.Id, grant.Identity, wait, error);
        return (await store.MarkPendingAsync(grant.Id, error, Now + (long)wait.TotalMilliseconds, Now))!;
    }

    // What differs between the grant, with its add-ons, and the identity on its host, e.g. "quota 1048576"; null
    // when nothing does
    private static string? Drift(PlanGrant grant, Entitlement entitlement, IdentityOnHost identity)
    {
        var drift = new List<string>();
        if (!string.Equals(identity.PlanId, grant.PlanId, StringComparison.OrdinalIgnoreCase))
        {
            drift.Add($"plan {identity.PlanId ?? "none"}");
        }
        if (entitlement.QuotaBytes != null && identity.QuotaBytes != null && entitlement.QuotaBytes != identity.QuotaBytes)
        {
            drift.Add($"quota {identity.QuotaBytes}");
        }
        if (entitlement.PublicWebPresence != null && identity.PublicWebPresence != null &&
            entitlement.PublicWebPresence != identity.PublicWebPresence)
        {
            drift.Add($"public web presence {identity.PublicWebPresence}");
        }
        return drift.Count == 0 ? null : string.Join(", ", drift);
    }

    private async Task<(HttpStatusCode? Status, IdentityOnHost? Identity, string? Error)> GetPlanAsync(string host,
        string identity)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, IdentityUrl(host, identity));
            request.Headers.Add(ApiKeyHeader, hosts.ApiKey);
            using var response = await Client().SendAsync(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (response.StatusCode, null, await DescribeAsync(response));
            }
            var body = await response.Content.ReadFromJsonAsync<IdentityOnHost>();
            return (HttpStatusCode.OK, body ?? new IdentityOnHost(identity, null, null, null), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, null, e.Message);
        }
    }

    private async Task<(HttpStatusCode? Status, string? Error)> SetPlanAsync(string host, PlanGrant grant)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, IdentityUrl(host, grant.Identity) + "/plan");
            request.Headers.Add(ApiKeyHeader, hosts.ApiKey);
            // The plan with the identity's add-ons on top
            var entitlement = Entitlement.Of(grant, await addOns.ListAsync(grant.Identity));
            request.Content = JsonContent.Create(
                new HostSetPlan(grant.PlanId, entitlement.QuotaBytes, entitlement.PublicWebPresence, grant.Reference));
            using var response = await Client().SendAsync(request);
            return (response.StatusCode, response.IsSuccessStatusCode ? null : await DescribeAsync(response));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, e.Message);
        }
    }

    private HttpClient Client() => httpClientFactory.CreateClient(HttpClientName);

    private static string IdentityUrl(string host, string identity) =>
        $"https://{host}/api/plans/v1/identities/{Uri.EscapeDataString(identity)}";

    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var status = $"{(int)response.StatusCode} {response.StatusCode}";
        return string.IsNullOrWhiteSpace(body) ? status : $"{status}: {(body.Length > 200 ? body[..200] : body)}";
    }


    // A grant from before plans were kept here leaves out the quota and public web presence: the host keeps its own
    private record HostSetPlan(
        string PlanId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? QuotaBytes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PublicWebPresence,
        string Reference);
}

/// <summary>
/// Tries pending grants as they fall due; every hour bills subscriptions due to renew, moves members of subscriptions
/// past their grace (and members whose own grace after leaving is over) to the free plan, and takes away add-ons that
/// ran out; and once a day sets back any applied plan a host no longer has as granted
/// </summary>
public class PlanGrantWorker(PlanService plans, SubscriptionService subscriptions, IConfiguration configuration,
    ILogger<PlanGrantWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("RavenHosting:GrantRetryIntervalSeconds", 60));
        var reconcileEvery = TimeSpan.FromHours(configuration.GetValue("RavenHosting:ReconcileIntervalHours", 24));
        var nextReconcile = DateTimeOffset.UtcNow + reconcileEvery;
        // Paid terms and add-ons that ran out: hourly, so nobody keeps a plan much past their grace
        var expireEvery = TimeSpan.FromMinutes(configuration.GetValue("RavenHosting:ExpiryIntervalMinutes", 60));
        var nextExpiry = DateTimeOffset.UtcNow;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await plans.ApplyDueAsync();
                if (DateTimeOffset.UtcNow >= nextExpiry)
                {
                    nextExpiry = DateTimeOffset.UtcNow + expireEvery;
                    await plans.ExpireAddOnsAsync();
                    var moved = await subscriptions.RunAsync();
                    if (moved.Count > 0)
                    {
                        logger.LogInformation("Moved {count} identities to the free plan", moved.Count);
                    }
                }
                if (DateTimeOffset.UtcNow >= nextReconcile)
                {
                    nextReconcile = DateTimeOffset.UtcNow + reconcileEvery;
                    var corrected = await plans.ReconcileAsync();
                    logger.LogInformation("Reconcile corrected {count} plan(s)", corrected.Count);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Plan grant worker failed; trying again next round");
            }
        }
    }
}

/// <summary>
/// What an identity has now, as its host says (GET /api/plans/v1/identities/{identity}): not necessarily what this
/// service last set, as it may have been changed on the host since. Each field is null from a host too old to say
/// it; anything a newer host adds is kept in <see cref="More"/>, so it is not lost before this service knows it.
/// </summary>
/// <param name="QuotaBytes">The quota in effect: the identity's own, or its host's default</param>
/// <param name="UsedBytes">What it stores, up to a minute old</param>
/// <param name="IsOverQuota">At or over its quota, or marked out of quota: new photos and files are refused</param>
public record IdentityOnHost(
    string? Domain,
    string? PlanId,
    long? QuotaBytes,
    bool? PublicWebPresence,
    long? UsedBytes = null,
    bool? IsOverQuota = null)
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? More { get; init; }

    /// <summary>
    /// The part of <see cref="QuotaBytes"/> that is the identity's storage add-ons, which stay whatever plan it moves
    /// to: set here, not by the host. Plans are compared with the rest.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long ExtraBytes { get; init; }

    /// <summary>The quota its plan gives, without its storage add-ons</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long? PlanQuotaBytes => QuotaBytes - ExtraBytes;

    /// <summary>How full it is, when the host says both what it stores and its quota</summary>
    public StorageUse? Storage => QuotaBytes is { } quota && UsedBytes is { } used ? StorageUse.Of(used, quota) : null;
}
