using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace Raven.Hosting.Tests;

/// <summary>
/// The upgrade page in process: YouAuth sign-in against a faked identity, then choose, confirm, the test checkout,
/// and done, which sets the plan on the faked identity host
/// </summary>
public class UpgradePageTests
{
    private const string Frodo = "frodo.dotyou.cloud";

    private InvitationCodeServer _server = null!;
    private HttpClient _browser = null!;

    [SetUp]
    public void SetUp()
    {
        _server = new InvitationCodeServer();
        _browser = _server.Browser();
        _server.IdentityHosts.Serve(InvitationCodeServer.Hosts[0], Frodo, "free");
    }

    [TearDown]
    public void TearDown()
    {
        _browser.Dispose();
        _server.Dispose();
    }

    [Test]
    public async Task TheServiceSaysWhoItIsToTheIdentity()
    {
        var document = await _browser.GetFromJsonAsync<Dictionary<string, object>>("/.well-known/youauth-client.json");

        Assert.That(document!["name"].ToString(), Is.EqualTo("Test Hosting"));
        Assert.That(document["redirect_uris"].ToString(),
            Does.Contain($"https://{InvitationCodeServer.PublicDomain}/upgrade/youauth-callback"));
    }

    [Test]
    public async Task OpeningThePageSendsTheOwnerToTheirIdentityToSignIn()
    {
        var response = await _browser.GetAsync($"/upgrade?id={Frodo}&host={InvitationCodeServer.Hosts[0]}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!;
        Assert.That(location.GetLeftPart(UriPartial.Path), Is.EqualTo($"https://{Frodo}/api/owner/v1/youauth/authorize"));
        var query = HttpUtility.ParseQueryString(location.Query);
        Assert.That(query["client_id"], Is.EqualTo(InvitationCodeServer.PublicDomain));
        Assert.That(query["client_type"], Is.EqualTo("domain"));
        Assert.That(query["cipher"], Is.EqualTo("aes-gcm"));
        Assert.That(query["redirect_uri"], Is.EqualTo($"https://{InvitationCodeServer.PublicDomain}/upgrade/youauth-callback"));
        Assert.That(query["state"], Is.Not.Empty);
        Assert.That(query["public_key"], Is.Not.Empty);
    }

    [Test]
    public async Task WithoutAnIdentityItAsksForOne()
    {
        var response = await _browser.GetAsync("/upgrade");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.ToString(), Is.EqualTo("/sign-in"));
    }

    [Test]
    public async Task NotAnIdentityIsNobodyToSignIn()
    {
        var response = await _browser.GetAsync("/upgrade?id=not%20a%20domain");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task SignedInTheOwnerSeesTheirPlanAndThePlansForSale()
    {
        var page = await SignInAsync();

        Assert.That(page, Does.Contain($"<span class=\"identity\">{Frodo}</span>"), "who is signed in, in the header");
        Assert.That(page, Does.Contain("Your plan now").And.Contain("<h2 id=\"now-heading\">Free</h2>"),
            "free is not for sale, so it is named above the cards, by its name");
        Assert.That(page, Does.Contain("Choose Veterans"));
        Assert.That(page, Does.Contain("Choose Registered"));
        Assert.That(page.IndexOf("Choose Registered"), Is.LessThan(page.IndexOf("Choose Veterans")), "in sort order");
        Assert.That(page, Does.Not.Contain("<h2>Pro</h2>").And.Not.Contain("<h2>Tiny test</h2>"),
            "only the plans for sale are cards");
    }

    [Test]
    public async Task TheCardsSayWhatEachPlanGives()
    {
        var page = await SignInAsync();

        var cards = page.Split("<li class=\"card")[1..];
        Assert.That(cards, Has.Length.EqualTo(2));
        Assert.That(cards[0], Does.Contain("<h2>Registered</h2>").And.Contain("<p class=\"storage\">10 GB</p>")
            .And.Contain("A public home page").And.Contain("<li>$10 / year</li>")
            .And.Not.Contain("save"), "no monthly price to save on");
        Assert.That(cards[1], Does.Contain("<h2>Veterans</h2>").And.Contain("For those who served")
            .And.Contain("<p class=\"storage\">1 TB</p>")
            .And.Contain("<li>$5 / month</li>")
            .And.Contain("<li>$50 / year<span class=\"save\">save 17%</span></li>")
            .And.Contain("<li>$90 / 2 years<span class=\"save\">save 25%</span></li>"));
    }

    [Test]
    public async Task APlanTakenOffSaleLeavesTheCards()
    {
        await SetForSaleAsync("registered", false);

        var page = await SignInAsync();

        Assert.That(page, Does.Not.Contain("Choose Registered"));
        Assert.That((await _browser.GetAsync("/upgrade/confirm?plan=registered")).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task TheCurrentPlansCardIsMarkedAndCannotBeChosen()
    {
        _server.IdentityHosts.Serve(InvitationCodeServer.Hosts[0], Frodo, "veterans");

        var page = await SignInAsync();

        Assert.That(page, Does.Contain("class=\"card current\""));
        Assert.That(page, Does.Contain("Your plan</p>"));
        Assert.That(page, Does.Not.Contain("Choose Veterans"));
        Assert.That(page, Does.Contain("Choose Registered"));
        Assert.That(page, Does.Not.Contain("Your plan is <strong>"), "the current plan is a card, not a line above them");
    }

    [Test]
    public async Task ConfirmNamesTheChange()
    {
        await SignInAsync();

        var response = await _browser.GetAsync("/upgrade/confirm?plan=veterans");
        var html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(html, Does.Contain("Change your plan from <strong>Free</strong>"));
        Assert.That(html, Does.Contain("<h2>Veterans</h2>"));
        Assert.That(html, Does.Contain("How would you like to pay?"));
        Assert.That(html, Does.Contain("""<input type="radio" name="months" value="1" checked /> $5 / month"""),
            "the shortest term, chosen");
        Assert.That(html, Does.Contain("""<input type="radio" name="months" value="12" /> $50 / year"""));
        Assert.That(html, Does.Contain("""<input type="radio" name="months" value="24" /> $90 / 2 years"""));
    }

    [Test]
    public async Task TheCheckoutShowsTheTermChosen()
    {
        await SignInAsync();

        var html = await (await _browser.GetAsync("/upgrade/checkout?plan=veterans&months=24")).Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("<p class=\"price\">$90 / 2 years</p>"));
        Assert.That(html, Does.Contain("""<input type="hidden" name="months" value="24" />"""));
        Assert.That(html, Does.Not.Contain("$50 / year"), "only the term chosen");
    }

    [TestCase("/upgrade/checkout?plan=veterans", TestName = "a checkout of a plan with three terms and none chosen")]
    [TestCase("/upgrade/checkout?plan=veterans&months=6", TestName = "a checkout of a term the plan is not sold for")]
    public async Task ACheckoutNeedsATermThePlanIsSoldFor(string path)
    {
        await SignInAsync();

        var response = await _browser.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("Choose how to pay"));
    }

    [Test]
    public async Task APlanWithOneTermNeedsNoChoice()
    {
        await SignInAsync();

        var html = await (await _browser.GetAsync("/upgrade/checkout?plan=registered")).Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("<p class=\"price\">$10 / year</p>"));
    }

    [TestCase("/upgrade/confirm?plan=tiny-test")]
    [TestCase("/upgrade/checkout?plan=tiny-test")]
    [TestCase("/upgrade/confirm")]
    public async Task APlanNotForSaleIsAFriendly400(string path)
    {
        await SignInAsync();

        var response = await _browser.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("That plan is not for sale"));
    }

    [Test]
    public async Task TheCheckoutSaysItIsATest_AndItsCardFieldsAreNeverSent()
    {
        await SignInAsync();

        var html = await (await _browser.GetAsync("/upgrade/checkout?plan=veterans&months=12")).Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("This is a test checkout."));
        Assert.That(html, Does.Contain("No payment is taken."));
        Assert.That(html, Does.Contain("Complete purchase"));
        Assert.That(html, Does.Contain("Card number"));
        var form = Regex.Match(html, "<form method=\"post\" action=\"/upgrade/checkout\".*?</form>", RegexOptions.Singleline).Value;
        var names = Regex.Matches(form, "name=\"([^\"]+)\"").Select(m => m.Groups[1].Value);
        Assert.That(names, Is.EquivalentTo(new[] { "__RequestVerificationToken", "planId", "months", "seat" }),
            "the card fields have no names, so the browser never sends them");
    }

    [Test]
    public async Task ASignInIsUsedOnce()
    {
        var callback = await CallbackAfterConsentAsync();
        Assert.That((await _browser.GetAsync(PathOf(callback))).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        var again = await _browser.GetAsync(PathOf(callback));

        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await again.Content.ReadAsStringAsync(), Does.Contain("expired or was already used"));
    }

    [Test]
    public async Task AnExpiredSignInIsRefused()
    {
        var callback = await CallbackAfterConsentAsync();
        _server.Time.Offset = YouAuthLogin.StateLifetime + TimeSpan.FromSeconds(1);

        var response = await _browser.GetAsync(PathOf(callback));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_server.YouAuthIdentity.TokenRequests, Is.Empty, "no token exchange for an expired sign-in");
    }

    [Test]
    public async Task AnUnknownStateIsRefused()
    {
        var response = await _browser.GetAsync("/upgrade/youauth-callback?state=made-up&public_key=x&salt=AAAA");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task CancellingAtTheIdentityIsNotASignIn()
    {
        var location = (await _browser.GetAsync($"/upgrade?id={Frodo}")).Headers.Location!;
        var state = HttpUtility.ParseQueryString(location.Query)["state"]!;

        var response = await _browser.GetAsync($"/upgrade/youauth-callback?error=cancelled-by-user&state={Uri.EscapeDataString(state)}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("You cancelled"));
    }

    [Test]
    public async Task ATokenTheIdentityDidNotIssueIsNotASignIn()
    {
        var callback = await CallbackAfterConsentAsync();
        // Another salt derives another secret, whose digest the identity has no token for
        var tampered = Regex.Replace(callback, "salt=[^&]+", "salt=" + Uri.EscapeDataString(Convert.ToBase64String(new byte[16])));

        var response = await _browser.GetAsync(PathOf(tampered));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _browser.GetAsync("/upgrade")).StatusCode, Is.EqualTo(HttpStatusCode.Redirect), "not signed in");
    }

    [Test]
    public async Task AnIdentityThatSealsWithAesCbcIsRefused()
    {
        _server.YouAuthIdentity.Cipher = YouAuthCrypto.CipherAesCbc;
        var callback = await CallbackAfterConsentAsync();

        var response = await _browser.GetAsync(PathOf(callback));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task CompletingTheCheckoutSetsThePlanOnTheIdentitysHost()
    {
        await SignInAsync();
        var checkout = await (await _browser.GetAsync("/upgrade/checkout?plan=veterans&months=12")).Content.ReadAsStringAsync();

        // Card values a browser could send anyway are ignored, and kept nowhere
        var response = await PostFormAsync("/upgrade/checkout", checkout, ("planId", "veterans"), ("months", "12"),
            ("cardNumber", "4242424242424242"), ("cvc", "123"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.ToString(), Does.StartWith("/upgrade/done?subscription="));
        Assert.That(_server.IdentityHosts.On(InvitationCodeServer.Hosts[0], Frodo),
            Is.EqualTo(new FakeIdentityHosts.Identity("veterans", 1099511627776, true)), "the plan and what it gives");

        using var admin = _server.ClientWithKey(InvitationCodeServer.CreateKey);
        var grantsJson = await admin.GetStringAsync($"/api/v1/plans/grants?identity={Frodo}");
        var grants = System.Text.Json.JsonSerializer.Deserialize<List<PlanGrant>>(grantsJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.That(grants!.Single().Source, Is.EqualTo(PlanGrantSource.Purchase));
        Assert.That(grants!.Single().Status, Is.EqualTo(PlanGrantStatus.Applied));
        Assert.That(grants!.Single().SubscriptionId, Is.Not.Null, "the subscription bought");
        Assert.That(grantsJson, Does.Not.Contain("4242424242424242"));

        var done = await _browser.GetAsync(response.Headers.Location!.ToString());
        var html = await done.Content.ReadAsStringAsync();
        Assert.That(done.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(html, Does.Contain("You&#x27;re on Veterans").Or.Contain("You're on Veterans"));
        Assert.That(html, Does.Contain("<p class=\"price\">$50 / year</p>"));
        Assert.That(html, Does.Contain("nothing was charged"));
        Assert.That(html, Does.Contain($"href=\"https://{Frodo}/owner/settings/plan\""), "back to the identity's plan page");
    }

    [Test]
    public async Task TheDonePageNamesAPlanNoLongerForSale()
    {
        await SignInAsync();
        var checkout = await (await _browser.GetAsync("/upgrade/checkout?plan=veterans&months=12")).Content.ReadAsStringAsync();
        var bought = await PostFormAsync("/upgrade/checkout", checkout, ("planId", "veterans"), ("months", "12"));
        await SetForSaleAsync("veterans", false);

        var html = await (await _browser.GetAsync(bought.Headers.Location!.ToString())).Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("You&#x27;re on Veterans").Or.Contain("You're on Veterans"));
        Assert.That(html, Does.Contain("1 TB of storage"));
    }

    [Test]
    public async Task APlanNotForSaleCannotBeBought()
    {
        await SignInAsync();
        var checkout = await (await _browser.GetAsync("/upgrade/checkout?plan=veterans&months=12")).Content.ReadAsStringAsync();

        var response = await PostFormAsync("/upgrade/checkout", checkout, ("planId", "tiny-test"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_server.IdentityHosts.PlanOn(InvitationCodeServer.Hosts[0], Frodo), Is.EqualTo("free"));
    }

    [Test]
    public async Task CheckingOutNeedsASignedInOwner()
    {
        var page = await SignInAsync();
        Assert.That((await PostFormAsync("/upgrade/sign-out", page)).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        var response = await PostFormAsync("/upgrade/checkout", page, ("planId", "veterans"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_server.IdentityHosts.PlanOn(InvitationCodeServer.Hosts[0], Frodo), Is.EqualTo("free"));
    }

    [Test]
    public async Task CheckingOutNeedsTheFormsAntiforgeryToken()
    {
        await SignInAsync();

        var response = await _browser.PostAsync("/upgrade/checkout",
            new FormUrlEncodedContent([new("planId", "veterans")]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_server.IdentityHosts.PlanOn(InvitationCodeServer.Hosts[0], Frodo), Is.EqualTo("free"));
    }

    [Test]
    public async Task AnotherIdentitysPurchaseIsNotFound()
    {
        using var admin = _server.ClientWithKey(InvitationCodeServer.CreateKey);
        var other = await admin.PostAsJsonAsync("/api/v1/plans/grants/",
            new { identity = "sam.dotyou.cloud", planId = "veterans", source = "purchase" });
        var grant = (await other.Content.ReadFromJsonAsync<PlanGrant>())!;
        await SignInAsync();

        var response = await _browser.GetAsync($"/upgrade/done?grant={grant.Id}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AKnownHostHintIsAskedFirst_AnUnknownOneNever()
    {
        _server.IdentityHosts.Forget(InvitationCodeServer.Hosts[0], Frodo);
        _server.IdentityHosts.Serve(InvitationCodeServer.Hosts[1], Frodo, "free");

        await SignInAsync(host: InvitationCodeServer.Hosts[1]);
        Assert.That(_server.IdentityHosts.HostsAsked().First(), Is.EqualTo(InvitationCodeServer.Hosts[1]));

        using var other = _server.Browser();
        _browser.Dispose();
        _browser = other;
        await SignInAsync(host: "provisioning.evil.example");
        Assert.That(_server.IdentityHosts.HostsAsked(), Does.Not.Contain("provisioning.evil.example"));
        _browser = _server.Browser();
    }

    [Test]
    public async Task WithoutAPublicDomainThePageIsOff()
    {
        using var server = new InvitationCodeServer(upgradePage: false);
        using var browser = server.Browser();

        Assert.That((await browser.GetAsync($"/upgrade?id={Frodo}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await browser.GetAsync("/.well-known/youauth-client.json")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    //

    private async Task SetForSaleAsync(string planId, bool forSale)
    {
        using var admin = _server.ClientWithKey(InvitationCodeServer.CreateKey);
        var response = await admin.PatchAsJsonAsync($"/api/v1/plans/{planId}", new { forSale });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // Opens the page for Frodo, plays the identity (signed in, consenting), and returns the callback URL it sends
    // the browser back to
    private async Task<string> CallbackAfterConsentAsync(string? host = null)
    {
        var path = $"/upgrade?id={Frodo}" + (host == null ? "" : $"&host={host}");
        var redirect = await _browser.GetAsync(path);
        Assert.That(redirect.StatusCode, Is.EqualTo(HttpStatusCode.Redirect), await redirect.Content.ReadAsStringAsync());
        return _server.YouAuthIdentity.Authorize(redirect.Headers.Location!.ToString());
    }

    // Signs Frodo in, and returns the upgrade page
    private async Task<string> SignInAsync(string? host = null)
    {
        var callback = await CallbackAfterConsentAsync(host);
        var signedIn = await _browser.GetAsync(PathOf(callback));
        Assert.That(signedIn.StatusCode, Is.EqualTo(HttpStatusCode.Redirect), await signedIn.Content.ReadAsStringAsync());
        Assert.That(signedIn.Headers.Location!.ToString(), Is.EqualTo("/upgrade"));

        var page = await _browser.GetAsync("/upgrade");
        var html = await page.Content.ReadAsStringAsync();
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK), html);
        return html;
    }

    private async Task<HttpResponseMessage> PostFormAsync(string path, string page, params (string Name, string Value)[] fields)
    {
        var token = Regex.Match(page, "name=\"(?<name>__RequestVerificationToken)\" value=\"(?<value>[^\"]+)\"");
        Assert.That(token.Success, Is.True, "the page has an antiforgery token");
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new(token.Groups["name"].Value, HttpUtility.HtmlDecode(token.Groups["value"].Value)));
        return await _browser.PostAsync(path, new FormUrlEncodedContent(form));
    }

    // The callback URL is on the service's own domain; the browser client is too
    private static string PathOf(string url) => new Uri(url).PathAndQuery;
}
