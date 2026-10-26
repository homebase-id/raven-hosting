using System.Net;
using System.Net.Http.Json;

namespace Raven.Hosting.Tests;

/// <summary>
/// The plan grant endpoints over HTTP, in process, with the identity hosts faked: who may call them, and what they
/// answer.
/// </summary>
public class PlanGrantApiTests
{
    private const string Root = "/api/v1/plans/grants";
    private const string Frodo = "frodo.dotyou.cloud";

    private InvitationCodeServer _server = null!;
    private HttpClient _admin = null!;

    [SetUp]
    public void SetUp()
    {
        _server = new InvitationCodeServer();
        _admin = _server.ClientWithKey(InvitationCodeServer.CreateKey);
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _server.Dispose();
    }

    private static IEnumerable<TestCaseData> EveryEndpoint()
    {
        yield return new TestCaseData(HttpMethod.Post, "/", new { identity = Frodo, planId = "veterans" }).SetName("grant");
        yield return new TestCaseData(HttpMethod.Get, "/", null).SetName("grants");
        yield return new TestCaseData(HttpMethod.Post, "/1/retry", null).SetName("retry");
    }

    [TestCaseSource(nameof(EveryEndpoint))]
    public async Task EveryEndpointNeedsTheCreateKey(HttpMethod method, string path, object? body)
    {
        foreach (var key in new[] { null, "wrong", InvitationCodeServer.SignupKey, FakeIdentityHosts.PlanKey })
        {
            using var client = _server.ClientWithKey(key);
            var request = new HttpRequestMessage(method, Root + path);
            if (body != null)
            {
                request.Content = JsonContent.Create(body);
            }
            var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), $"key: {key ?? "(none)"}");
        }
        Assert.That(_server.IdentityHosts.Requests, Is.Empty, "no host was called");
    }

    [Test]
    public async Task AGrantIsSetOnItsHostStraightAway()
    {
        _server.IdentityHosts.Serve(InvitationCodeServer.Hosts[1], Frodo);

        var response = await _admin.PostAsJsonAsync(Root + "/",
            new { identity = "FRODO.dotyou.cloud", planId = "veterans", source = "purchase", reference = "pay_9",
                host = InvitationCodeServer.Hosts[1] });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var grant = (await response.Content.ReadFromJsonAsync<PlanGrant>())!;
        Assert.That(grant with { Id = 0, CreatedAt = 0, UpdatedAt = 0, AppliedAt = null }, Is.EqualTo(new PlanGrant(
            0, Frodo, "veterans", 1099511627776, true, "purchase", "pay_9", "applied", InvitationCodeServer.Hosts[1],
            InvitationCodeServer.Hosts[1], 1, null, null, 0, 0, null)));
        Assert.That(_server.IdentityHosts.PlanOn(InvitationCodeServer.Hosts[1], Frodo), Is.EqualTo("veterans"));
        Assert.That(_server.IdentityHosts.HostsAsked()[0], Is.EqualTo(InvitationCodeServer.Hosts[1]), "the hint first");
    }

    [Test]
    public async Task AGrantNoHostCanTakeYetIsPending()
    {
        var grant = await GrantAsync(new { identity = Frodo, planId = "veterans" });

        Assert.That(grant.Status, Is.EqualTo("pending"));
        Assert.That(grant.Source, Is.EqualTo("admin"), "the default");
        Assert.That(grant.Reference, Does.StartWith("grant-"), "made up");
        Assert.That(grant.LastError, Does.Contain("no known host serves"));
    }

    [Test]
    public async Task TheSameReferenceForAnotherPlanIsAConflict()
    {
        await GrantAsync(new { identity = Frodo, planId = "veterans", reference = "pay_1" });

        var response = await _admin.PostAsJsonAsync(Root + "/", new { identity = Frodo, planId = "registered", reference = "pay_1" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [TestCase("not a domain", "veterans", null, "Invalid identity")]
    [TestCase(Frodo, "", null, "planId is required")]
    [TestCase(Frodo, "has space", null, "There is no plan")]
    [TestCase(Frodo, "gold", null, "There is no plan 'gold'")]
    [TestCase(Frodo, "veterans", "gift", "source must be one of")]
    public async Task BadInputIsA400(string identity, string planId, string? source, string message)
    {
        var response = await _admin.PostAsJsonAsync(Root + "/", new { identity, planId, source });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(message));
    }

    [Test]
    public async Task AGrantHasNoEnd()
    {
        var grant = await GrantAsync(new { identity = Frodo, planId = "veterans", source = "purchase" });

        Assert.That(grant.SubscriptionId, Is.Null, "paid plans are subscriptions, not made here");
    }

    [Test]
    public async Task ANewGrantSupersedesOnesTheHostDoesNotHaveYet()
    {
        var first = await GrantAsync(new { identity = Frodo, planId = "veterans" });
        await GrantAsync(new { identity = Frodo, planId = "registered" });

        var grants = await _admin.GetFromJsonAsync<List<PlanGrant>>($"{Root}/?identity={Frodo}");
        Assert.That(grants!.Single(g => g.Id == first.Id).Status, Is.EqualTo(PlanGrantStatus.Superseded),
            "never set over the newer one");
    }

        [Test]
    public async Task ListFiltersByIdentityAndStatus()
    {
        _server.IdentityHosts.Serve(InvitationCodeServer.Hosts[0], Frodo);
        var applied = await GrantAsync(new { identity = Frodo, planId = "veterans" });
        var pending = await GrantAsync(new { identity = "sam.dotyou.cloud", planId = "veterans" });

        Assert.That((await ListAsync("")).Select(g => g.Id), Is.EqualTo(new[] { pending.Id, applied.Id }), "newest first");
        Assert.That((await ListAsync($"?identity={Frodo}")).Single().Id, Is.EqualTo(applied.Id));
        Assert.That((await ListAsync("?status=pending")).Single().Id, Is.EqualTo(pending.Id));
        Assert.That((await _admin.GetAsync(Root + "/?status=lost")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task RetryTriesAgainNow()
    {
        var grant = await GrantAsync(new { identity = Frodo, planId = "veterans" });
        _server.IdentityHosts.Serve(InvitationCodeServer.Hosts[0], Frodo);

        var retried = await _admin.PostAsync($"{Root}/{grant.Id}/retry", null);

        Assert.That(retried.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await retried.Content.ReadFromJsonAsync<PlanGrant>())!.Status, Is.EqualTo("applied"));
        Assert.That((await _admin.PostAsync($"{Root}/{grant.Id}/retry", null)).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await _admin.PostAsync($"{Root}/999999/retry", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    //

    private async Task<PlanGrant> GrantAsync(object body)
    {
        var response = await _admin.PostAsJsonAsync(Root + "/", body);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PlanGrant>())!;
    }

    private async Task<List<PlanGrant>> ListAsync(string query) =>
        (await _admin.GetFromJsonAsync<List<PlanGrant>>(Root + "/" + query))!;
}
