using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Raven.Hosting.Tests;

/// <summary>
/// raven.sh, run with bash against the service on a real port. Needs bash, curl and jq; without jq the
/// tests are ignored locally, and fail under CI so a runner without it cannot pass them silently.
/// </summary>
[NonParallelizable]
[UnsupportedOSPlatform("windows")] // bash, and Unix file modes
public class RavenCliTests
{
    private InvitationCodeServer _server = null!;
    private string _url = null!;
    private string _home = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!OnPath("jq"))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
            {
                Assert.Fail("jq is not installed; raven.sh needs it");
            }
            Assert.Ignore("jq is not installed");
        }

        _server = new InvitationCodeServer();
        _url = _server.StartOnKestrel();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _server?.Dispose();

    [SetUp]
    public void SetUp()
    {
        // A home of its own, so a real ~/.raven-hosting is never read
        _home = Directory.CreateTempSubdirectory("raven-cli-").FullName;
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_home, recursive: true);

    //

    [Test]
    public async Task HelpExplainsEveryCommand()
    {
        var result = await RunAsync("help");

        Assert.That(result.ExitCode, Is.EqualTo(0));
        foreach (var command in new[] { "setup", "ping", "plans", "plan-create", "plan-edit", "plan-price", "plan-delete", "create", "list", "stats", "edit", "disable", "enable", "grant", "grants", "retry", "validate", "use", "undo" })
        {
            Assert.That(result.Output, Does.Contain($"  {command}"), command);
        }
    }

    [Test]
    public async Task AnUnknownCommandPrintsHelpAndFails()
    {
        var result = await RunAsync("frobnicate");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Output, Does.Contain("CREATE AND LIST CODES"));
    }

    [Test]
    public async Task AMissingKeySaysToRunSetup()
    {
        var result = await RunAsync(new Options(WithKeys: false), "list");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain("RAVEN_HOSTING_CREATE_KEY is not set; run ./raven.sh setup"));
    }

    [Test]
    public async Task SetupSavesTheKeysForOnlyTheUserToRead()
    {
        var result = await RunAsync(new Options(WithKeys: false, Stdin: $"{CreateKey}\n{SignupKey}\n"), "setup");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.Error);
        Assert.That(await File.ReadAllTextAsync(ConfigPath),
            Is.EqualTo($"RAVEN_HOSTING_CREATE_KEY={CreateKey}\nRAVEN_HOSTING_SIGNUP_KEY={SignupKey}\n"));
        Assert.That(File.GetUnixFileMode(ConfigPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.That(result.Output, Does.Contain("Saved to"));
    }

    [Test]
    public async Task SetupSaysWhatTheKeysAreAndWhereToFindThem()
    {
        var result = await RunAsync(new Options(WithKeys: false, Stdin: $"{CreateKey}\n\n"), "setup");

        Assert.That(result.Output, Does.Contain("not passwords you make up"));
        Assert.That(result.Output, Does.Contain("ansible-vault view ansible-vault.yml | grep -A2 raven_hosting"));
        Assert.That(result.Output, Does.Contain("secrets.raven_hosting.create_api_key"));
        Assert.That(result.Output, Does.Contain("secrets.raven_hosting.signup_api_key"));
        Assert.That(result.Output, Does.Contain("OK: the service accepted it."));
    }

    [Test]
    public async Task SetupSkipsTheSignupKey()
    {
        var result = await RunAsync(new Options(WithKeys: false, Stdin: $"{CreateKey}\n\n"), "setup");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.Error);
        Assert.That(result.Output, Does.Contain("Skipped."));
        Assert.That(await File.ReadAllTextAsync(ConfigPath), Is.EqualTo($"RAVEN_HOSTING_CREATE_KEY={CreateKey}\n"));
    }

    [TestCase("wrong-key", "not the create key", TestName = "SetupRefusesAWrongCreateKey")]
    [TestCase(SignupKey, "not the create key", TestName = "SetupRefusesTheSignupKeyAsTheCreateKey")]
    [TestCase("", "No create key given", TestName = "SetupNeedsACreateKey")]
    public async Task SetupRefusesABadCreateKey(string typed, string message)
    {
        var result = await RunAsync(new Options(WithKeys: false, Stdin: typed + "\n"), "setup");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain(message));
        Assert.That(File.Exists(ConfigPath), Is.False, "nothing saved");
    }

    [Test]
    public async Task SetupRefusesTheCreateKeyAsTheSignupKey()
    {
        var result = await RunAsync(new Options(WithKeys: false, Stdin: $"{CreateKey}\n{CreateKey}\n"), "setup");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain("not the signup key"));
        Assert.That(File.Exists(ConfigPath), Is.False, "nothing saved");
    }

    [Test]
    public async Task SetupKeepsTheSavedKeysOnEnter()
    {
        await RunAsync(new Options(WithKeys: false, Stdin: $"{CreateKey}\n{SignupKey}\n"), "setup");
        var saved = await File.ReadAllTextAsync(ConfigPath);

        var again = await RunAsync(new Options(WithKeys: false, Stdin: "\n\n"), "setup");

        Assert.That(again.ExitCode, Is.EqualTo(0), again.Error);
        Assert.That(again.Output, Does.Contain("One is saved already; press Enter to keep it."));
        Assert.That(await File.ReadAllTextAsync(ConfigPath), Is.EqualTo(saved));
    }

    [Test]
    public async Task SetupSavesTheKeysWhenTheServiceCannotBeAsked()
    {
        var result = await RunAsync(new Options(WithKeys: false, Stdin: "some-key\n\n", Url: "http://127.0.0.1:9"), "setup");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.Error);
        Assert.That(result.Output, Does.Contain("Could not check it"));
        Assert.That(await File.ReadAllTextAsync(ConfigPath), Is.EqualTo("RAVEN_HOSTING_CREATE_KEY=some-key\n"));
    }

    [Test]
    public async Task TheSavedKeysAreUsed()
    {
        await RunAsync(new Options(WithKeys: false, Stdin: $"{InvitationCodeServer.CreateKey}\n{InvitationCodeServer.SignupKey}\n"), "setup");

        var result = await RunAsync(new Options(WithKeys: false), "list", "--json");

        Assert.That(result.Status, Is.EqualTo(200), result.Output);
    }

    [Test]
    public async Task Ping()
    {
        var result = await RunAsync("ping");

        Assert.That(result.Output, Is.EqualTo("pong\n[200]\n"));
    }

    //

    [Test]
    public async Task CreateMakesACodeWithWhatItIsGiven()
    {
        var result = await RunAsync("create", "--uses", "4", "--source", "spring \"quoted\" campaign", "--owner",
            "frodo.dotyou.cloud", "--plan", "pro-100gb");

        Assert.That(result.Status, Is.EqualTo(200), result.Output);
        var code = result.Json<InvitationCode>();
        Assert.That(code, Is.EqualTo(new InvitationCode(code.Code, "frodo.dotyou.cloud", "spring \"quoted\" campaign", true, "pro-100gb", 4, 4, false)));
    }

    [Test]
    public async Task CreateWithoutAnOwnerMakesAnOwnerlessCode()
    {
        var code = (await RunAsync("create", "--uses", "1", "--source", "booth", "--plan", "free")).Json<InvitationCode>();

        Assert.That(code.Owner, Is.Null);
        Assert.That(code.PlanId, Is.EqualTo("free"));
    }

    // The plan decides public web presence now (odin-core #1974), so the CLI no longer sets it
    [TestCase("create", "--uses", "1", "--source", "x", "--plan", "free", "--no-public-page")]
    [TestCase("edit", "K7QX-M2PD-9FHW", "--public-page")]
    public async Task ThePublicPageFlagsAreGone(params string[] args)
    {
        var result = await RunAsync(args);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Output + result.Error, Does.Contain("unknown option"));
    }

    [TestCase(new[] { "--source", "x", "--plan", "free" }, "usage: ./raven.sh create", TestName = "create without --uses")]
    [TestCase(new[] { "--uses", "1", "--plan", "free" }, "usage: ./raven.sh create", TestName = "create without --source")]
    [TestCase(new[] { "--uses", "1", "--source", "x" }, "usage: ./raven.sh create", TestName = "create without --plan")]
    [TestCase(new[] { "--uses", "five", "--source", "x", "--plan", "free" }, "--uses must be a number", TestName = "create with a non-number")]
    [TestCase(new[] { "--uses", "1", "--source", "x", "--plan" }, "usage: ./raven.sh create", TestName = "create with --plan and no plan")]
    [TestCase(new[] { "--uses", "1", "--source", "x", "--plan", "free", "--bogus" }, "unknown option: --bogus", TestName = "create with an unknown option")]
    public async Task CreateRefusesBadArguments(string[] arguments, string message)
    {
        var result = await RunAsync(["create", .. arguments]);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain(message));
    }

    [Test]
    public async Task TheServicesRefusalIsShownWithItsStatus()
    {
        var result = await RunAsync("create", "--uses", "1", "--source", "x", "--plan", "free", "--owner", "not-a-domain");

        Assert.That(result.Status, Is.EqualTo(400));
        Assert.That(result.Output, Does.Contain("Invalid owner"));
    }

    [Test]
    public async Task ListShowsCodesAllOrOneOwners()
    {
        var mine = await CreateAsync(owner: "list-owner.dotyou.cloud");

        var all = await RunAsync("list", "--json");
        var owners = await RunAsync("list", "--owner", "LIST-owner.dotyou.cloud", "--json");

        Assert.That(all.Json<List<InvitationCode>>(), Does.Contain(mine));
        Assert.That(owners.Json<List<InvitationCode>>(), Is.EqualTo(new[] { mine }));
        Assert.That((await RunAsync("list", "--owner")).ExitCode, Is.EqualTo(1));
        Assert.That((await RunAsync("list", "--bogus")).Error, Does.Contain("unknown option: --bogus"));
    }

    [Test]
    public async Task ListPrintsATable()
    {
        var mine = await CreateAsync(maxUses: 3, owner: "table-owner.dotyou.cloud", source: "table source", planId: "pro-100gb");
        var other = await CreateAsync(maxUses: 2, owner: "table-owner.dotyou.cloud", source: "other");
        await RunAsync("use", mine.Code);
        await RunAsync("disable", other.Code);

        var result = await RunAsync("list", "--owner", "table-owner.dotyou.cloud");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.Error);
        var lines = result.Output.Split('\n');
        Assert.That(lines[0], Does.Match(@"^CODE +PLAN +LEFT +OWNER +SOURCE$"));
        Assert.That(lines[1], Does.Match($@"^{mine.Code} +pro-100gb +2/3 +table-owner\.dotyou\.cloud +table source$"));
        Assert.That(lines[2], Does.Match($@"^{other.Code} +free +2/2 +table-owner\.dotyou\.cloud +other +disabled$"));
        // The columns line up
        Assert.That(lines[1].IndexOf("pro-100gb"), Is.EqualTo(lines[0].IndexOf("PLAN")));
        Assert.That(result.Output, Does.EndWith("\n2 codes, 2 uses left on those not disabled\n"));
    }

    [Test]
    public async Task ListWithNoCodesSaysSo()
    {
        var result = await RunAsync("list", "--owner", "nobody-at-all.dotyou.cloud");

        Assert.That(result.Output, Is.EqualTo("(no codes)\n"));
    }

    [Test]
    public async Task ListWithAWrongKeyShowsTheRefusal()
    {
        var result = await RunAsync(new Options(CreateKey: "wrong"), "list");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Output, Does.EndWith("[401]\n"));
    }

    [Test]
    public async Task EditChangesWhatItIsGiven()
    {
        var code = await CreateAsync(owner: "frodo.dotyou.cloud");

        var edited = (await RunAsync("edit", code.Code, "--uses", "9", "--source", "autumn", "--no-owner"))
            .Json<InvitationCode>();
        Assert.That(edited, Is.EqualTo(code with { MaxUses = 9, UsesLeft = 9, Source = "autumn", Owner = null }));

        var reowned = (await RunAsync("edit", code.Code.ToLowerInvariant(), "--owner", "sam.dotyou.cloud"))
            .Json<InvitationCode>();
        Assert.That(reowned, Is.EqualTo(edited with { Owner = "sam.dotyou.cloud" }));

        var planned = (await RunAsync("edit", code.Code, "--plan", "pro-100gb")).Json<InvitationCode>();
        Assert.That(planned, Is.EqualTo(reowned with { PlanId = "pro-100gb" }));

        Assert.That((await RunAsync("edit", code.Code, "--no-plan")).Error, Does.Contain("unknown option: --no-plan"));
    }

    [Test]
    public async Task EditRefusesBadArguments()
    {
        var code = await CreateAsync();

        Assert.That((await RunAsync("edit", code.Code)).Error, Does.Contain("nothing to change"));
        Assert.That((await RunAsync("edit", code.Code, "--uses", "x")).Error, Does.Contain("--uses must be a number"));
        Assert.That((await RunAsync("edit", "K7QX/../stats", "--uses", "1")).Error, Does.Contain("usage: ./raven.sh edit"));
        Assert.That((await RunAsync("edit", "K7QX-M2PD-9FHW", "--uses", "1")).Status, Is.EqualTo(404));
    }

    [Test]
    public async Task DisableAndEnable()
    {
        var code = await CreateAsync();

        Assert.That((await RunAsync("disable", code.Code)).Json<InvitationCode>().Disabled, Is.True);
        Assert.That((await RunAsync("validate", code.Code)).Output, Does.StartWith("{\"valid\":false}"));

        Assert.That((await RunAsync("enable", code.Code)).Json<InvitationCode>().Disabled, Is.False);
        Assert.That((await RunAsync("validate", code.Code)).Output, Does.StartWith("{\"valid\":true}"));
    }

    [Test]
    public async Task ValidateUseAndUndo()
    {
        var code = await CreateAsync(maxUses: 1);

        Assert.That((await RunAsync("use", code.Code)).Json<UseResponse>(), Is.EqualTo(new UseResponse(true,
            UsedInvitationCode.From(code with { UsesLeft = 0 }, null) with
                { QuotaBytes = 64424509440, PublicWebPresence = true })));
        Assert.That((await RunAsync("validate", code.Code)).Json<ValidateResponse>().Valid, Is.False);

        Assert.That((await RunAsync("undo", code.Code)).Status, Is.EqualTo(204));
        Assert.That((await RunAsync("validate", code.Code)).Json<ValidateResponse>().Valid, Is.True);
    }

    //

    [Test]
    public async Task StatsPrintsAReport()
    {
        await CreatePlanAsync("stats-plan");
        var code = await CreateAsync(maxUses: 3, owner: "stats-owner.dotyou.cloud", source: "stats-source", planId: "stats-plan");
        await RunAsync("use", code.Code);

        var result = await RunAsync("stats");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.Error);
        Assert.That(result.Output, Does.StartWith("Signups of all time\n"));
        Assert.That(result.Output, Does.Match(@"\n  stats-source +1 +3 +1 +2\n"));
        Assert.That(result.Output, Does.Match(@"\n  stats-owner\.dotyou\.cloud +1 +3 +1 +2\n"));
        Assert.That(result.Output, Does.Match(@"\nby plan\n(  .*\n)*  stats-plan +1 +3 +1 +2\n"));
        Assert.That(result.Output, Does.Match(@"\nsignups per week, from Monday\n  \d{4}-\d{2}-\d{2} +\d+  #+\n"));
        Assert.That(result.Output, Does.Match(@"\ncodes: \d+ never used, \d+ used up, \d+ disabled\n$"));
    }

    [Test]
    public async Task StatsSinceSaysFromWhen()
    {
        var result = await RunAsync("stats", "--since", "30");

        var expected = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        Assert.That(result.Output, Does.StartWith($"Signups since {expected}\n").Or.StartWith(
            $"Signups since {DateTime.UtcNow.AddDays(-30).AddMinutes(-1):yyyy-MM-dd}\n"));
    }

    [Test]
    public async Task StatsJsonIsTheServicesAnswer()
    {
        var result = await RunAsync("stats", "--json", "--since", "7");

        Assert.That(result.Status, Is.EqualTo(200));
        Assert.That(result.Json<InvitationCodeStats>().Since, Is.Not.Null);
    }

    [TestCase(new[] { "--since", "0" }, "--since must be a number of days", TestName = "stats since zero days")]
    [TestCase(new[] { "--since", "week" }, "--since must be a number of days", TestName = "stats since a non-number")]
    [TestCase(new[] { "--bogus" }, "unknown option: --bogus", TestName = "stats with an unknown option")]
    public async Task StatsRefusesBadArguments(string[] arguments, string message)
    {
        var result = await RunAsync(["stats", .. arguments]);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain(message));
    }

    [Test]
    public async Task StatsWithAWrongKeyShowsTheRefusal()
    {
        var result = await RunAsync(new Options(CreateKey: "wrong"), "stats");

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Output, Does.EndWith("[401]\n"));
    }

    //
    // Plans
    //

    [Test]
    public async Task PlanCreatePrintsThePlan()
    {
        var result = await RunAsync("plan-create", "--id", "cli-gold", "--name", "Gold", "--quota-bytes", "5368709120",
            "--public-web-presence", "false", "--price", "1:2", "--price", "12:19.5", "--for-sale", "true",
            "--sort-order", "-1", "--description", "A small plan");

        var plan = result.Json<Plan>();
        Assert.That(plan with { CreatedAt = 0, UpdatedAt = 0 }, Is.EqualTo(new Plan("cli-gold", "Gold", "A small plan",
            5368709120, false, false, 0, true, [new(1, 200), new(12, 1950)], -1, 0, 0)));
    }

    [Test]
    public async Task PlansPrintsATable()
    {
        await CreatePlanAsync("cli-table", "--name", "Table plan", "--for-sale", "true", "--price", "1:1", "--price", "12:10");

        var result = await RunAsync("plans");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.Error);
        var lines = result.Output.Split('\n');
        Assert.That(lines[0], Does.Match(@"^ID +NAME +STORAGE +SEATS +PUBLIC PAGE +PUBLIC EMAIL +ALIASES +FOR SALE +PRICES +ORDER$"));
        Assert.That(lines, Has.Some.Match(@"^cli-table +Table plan +1 TB +1 +yes +no +0 +yes +\$1 / month, \$10 / year +0$"));
        Assert.That(lines, Has.Some.Match(@"^tiny-test +Tiny test +1 MB +1 +no +no +0 +no +- +0$"));
        Assert.That((await RunAsync("plans", "--json")).Json<List<Plan>>().Select(p => p.Id), Does.Contain("cli-table"));
    }

    [Test]
    public async Task PlanEditChangesOnlyWhatIsGiven()
    {
        var plan = await CreatePlanAsync("cli-edit", "--for-sale", "true", "--price", "1:3");

        var result = await RunAsync("plan-edit", "CLI-EDIT", "--for-sale", "false", "--quota-bytes", "2199023255552");

        var edited = result.Json<Plan>();
        Assert.That(edited with { UpdatedAt = 0 }, Is.EqualTo(plan with
            { ForSale = false, QuotaBytes = 2199023255552, UpdatedAt = 0 }));
        Assert.That((await RunAsync("plan-edit", "cli-edit")).Error, Does.Contain("nothing to change"));
        Assert.That((await RunAsync("plan-edit", "cli-nothing", "--name", "x")).Status, Is.EqualTo(404));
        Assert.That((await RunAsync("plan-edit", "cli-edit", "--price", "1:3")).Error, Does.Contain("unknown option: --price"),
            "prices are changed with plan-price");
    }

    [Test]
    public async Task PlanPriceSetsAndRemovesATerm()
    {
        await CreatePlanAsync("cli-priced", "--price", "1:3", "--for-sale", "true");

        var yearly = (await RunAsync("plan-price", "cli-priced", "12", "30")).Json<Plan>();
        Assert.That(yearly.Prices, Is.EqualTo(new[] { new PlanPrice(1, 300), new PlanPrice(12, 3000) }));

        var changed = (await RunAsync("plan-price", "cli-priced", "12", "29.99")).Json<Plan>();
        Assert.That(changed.PriceFor(12), Is.EqualTo(new PlanPrice(12, 2999)));

        var removed = (await RunAsync("plan-price", "cli-priced", "1", "--remove")).Json<Plan>();
        Assert.That(removed.Prices, Is.EqualTo(new[] { new PlanPrice(12, 2999) }));

        var last = await RunAsync("plan-price", "cli-priced", "12", "--remove");
        Assert.That(last.Status, Is.EqualTo(409), "the only price of a plan for sale");
    }

    [TestCase(new[] { "cli-x", "12" }, "usage: ./raven.sh plan-price", TestName = "plan-price without an amount")]
    [TestCase(new[] { "cli-x", "twelve", "30" }, "usage: ./raven.sh plan-price", TestName = "plan-price with months not a number")]
    [TestCase(new[] { "cli-x", "12", "$30" }, "<dollars> must be an amount in dollars", TestName = "plan-price with a dollar sign")]
    [TestCase(new[] { "cli-x", "12", "30.999" }, "<dollars> must be an amount in dollars", TestName = "plan-price with fractions of a cent")]
    public async Task PlanPriceRefusesBadArguments(string[] arguments, string message)
    {
        var result = await RunAsync(["plan-price", .. arguments]);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain(message));
    }

    [TestCase("12", TestName = "plan-create with a price without a term")]
    [TestCase("12:", TestName = "plan-create with a price without an amount")]
    [TestCase("12:$50", TestName = "plan-create with a price with a dollar sign")]
    public async Task PlanCreateRefusesABadPrice(string price)
    {
        var result = await RunAsync("plan-create", "--id", "cli-bad", "--name", "Bad", "--quota-bytes", "1",
            "--public-web-presence", "true", "--price", price);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain("--price must be <months>:<dollars>"));
    }

    [Test]
    public async Task PlanDeleteRefusesAPlanInUse()
    {
        await CreatePlanAsync("cli-used");
        await CreatePlanAsync("cli-unused");
        await CreateAsync(planId: "cli-used");

        var inUse = await RunAsync("plan-delete", "cli-used");
        Assert.That(inUse.Status, Is.EqualTo(409));
        Assert.That(inUse.Output, Does.Contain("mark it not for sale instead"));

        Assert.That((await RunAsync("plan-delete", "cli-unused")).Status, Is.EqualTo(204));
        Assert.That((await RunAsync("plan-delete", "cli-unused")).Status, Is.EqualTo(404));
    }

    [TestCase(new[] { "--name", "X", "--quota-bytes", "1", "--public-web-presence", "true" }, "usage: ./raven.sh plan-create", TestName = "plan-create without --id")]
    [TestCase(new[] { "--id", "x", "--quota-bytes", "1", "--public-web-presence", "true" }, "usage: ./raven.sh plan-create", TestName = "plan-create without --name")]
    [TestCase(new[] { "--id", "x", "--name", "X", "--public-web-presence", "true" }, "usage: ./raven.sh plan-create", TestName = "plan-create without --quota-bytes")]
    [TestCase(new[] { "--id", "x", "--name", "X", "--quota-bytes", "1" }, "usage: ./raven.sh plan-create", TestName = "plan-create without --public-web-presence")]
    [TestCase(new[] { "--id", "x", "--name", "X", "--quota-bytes", "1 TB", "--public-web-presence", "true" }, "--quota-bytes must be a number", TestName = "plan-create with a quota that is not a number")]
    [TestCase(new[] { "--id", "x", "--name", "X", "--quota-bytes", "1", "--public-web-presence", "yes" }, "--public-web-presence must be true or false", TestName = "plan-create with a public page that is not true or false")]
    [TestCase(new[] { "--id", "x", "--name", "X", "--quota-bytes", "1", "--public-web-presence", "true", "--bogus" }, "unknown option: --bogus", TestName = "plan-create with an unknown option")]
    public async Task PlanCreateRefusesBadArguments(string[] arguments, string message)
    {
        var result = await RunAsync(["plan-create", .. arguments]);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain(message));
    }

    [Test]
    public async Task PlanCreateSaysWhatTheServiceRefuses()
    {
        var zero = await RunAsync("plan-create", "--id", "cli-zero", "--name", "Zero", "--quota-bytes", "0",
            "--public-web-presence", "true");
        Assert.That(zero.Status, Is.EqualTo(400));
        Assert.That(zero.Output, Does.Contain("there is no unlimited"));

        var taken = await RunAsync("plan-create", "--id", "VETERANS", "--name", "Again", "--quota-bytes", "1",
            "--public-web-presence", "true");
        Assert.That(taken.Status, Is.EqualTo(409));
    }

    [Test]
    public async Task ACodeNeedsAnExistingPlan()
    {
        var result = await RunAsync("create", "--uses", "1", "--source", "cli", "--plan", "cli-no-such-plan");

        Assert.That(result.Status, Is.EqualTo(400));
        Assert.That(result.Output, Does.Contain("There is no plan 'cli-no-such-plan'"));
    }

    //
    // Plan grants
    //

    [Test]
    public async Task GrantSetsThePlanOnTheIdentitysHost()
    {
        var host = InvitationCodeServer.Hosts[1];
        _server.IdentityHosts.Serve(host, "grant-frodo.dotyou.cloud");

        var result = await RunAsync("grant", "--id", "grant-frodo.dotyou.cloud", "--plan", "veterans", "--host", host,
            "--reference", "cli-pay-1");

        Assert.That(result.Status, Is.EqualTo(200), result.Output);
        var grant = result.Json<PlanGrant>();
        Assert.That((grant.Status, grant.Host, grant.Reference), Is.EqualTo(("applied", host, "cli-pay-1")));
        Assert.That(_server.IdentityHosts.PlanOn(host, "grant-frodo.dotyou.cloud"), Is.EqualTo("veterans"));
    }

    [TestCase(new[] { "--plan", "veterans" }, "usage: ./raven.sh grant", TestName = "grant without --id")]
    [TestCase(new[] { "--id", "frodo.dotyou.cloud" }, "usage: ./raven.sh grant", TestName = "grant without --plan")]
    [TestCase(new[] { "--id", "frodo.dotyou.cloud", "--plan" }, "usage: ./raven.sh grant", TestName = "grant with --plan and no plan")]
    [TestCase(new[] { "--id", "frodo.dotyou.cloud", "--plan", "veterans", "--bogus" }, "unknown option: --bogus", TestName = "grant with an unknown option")]
    public async Task GrantRefusesBadArguments(string[] arguments, string message)
    {
        var result = await RunAsync(["grant", .. arguments]);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain(message));
    }

    [Test]
    public async Task GrantsPrintsATableAndRetryTriesAgain()
    {
        var pending = (await RunAsync("grant", "--id", "grants-sam.dotyou.cloud", "--plan", "registered")).Json<PlanGrant>();
        Assert.That(pending.Status, Is.EqualTo("pending"));

        var table = await RunAsync("grants", "--id", "grants-sam.dotyou.cloud");
        var lines = table.Output.Split('\n');
        Assert.That(lines[0], Does.Match(@"^ID +IDENTITY +PLAN +STATUS +HOST +TRIES +LAST ERROR$"));
        Assert.That(lines[1], Does.Match($@"^{pending.Id} +grants-sam\.dotyou\.cloud +registered +pending +- +1 +no known host serves"));

        _server.IdentityHosts.Serve(InvitationCodeServer.Hosts[0], "grants-sam.dotyou.cloud");
        var retried = await RunAsync("retry", pending.Id.ToString());
        Assert.That(retried.Json<PlanGrant>().Status, Is.EqualTo("applied"));

        var applied = await RunAsync("grants", "--status", "applied", "--json");
        Assert.That(applied.Json<List<PlanGrant>>().Select(g => g.Id), Does.Contain(pending.Id));
        Assert.That((await RunAsync("grants", "--status", "lost")).Error, Does.Contain("--status must be pending, applied or failed"));
        Assert.That((await RunAsync("retry", "x")).Error, Does.Contain("usage: ./raven.sh retry"));
    }

    //

    // A plan, 1 TB with a public home page unless the options say otherwise
    private async Task<Plan> CreatePlanAsync(string id, params string[] options)
    {
        List<string> arguments = ["plan-create", "--id", id];
        if (!options.Contains("--name")) arguments.AddRange(["--name", id]);
        if (!options.Contains("--quota-bytes")) arguments.AddRange(["--quota-bytes", "1099511627776"]);
        if (!options.Contains("--public-web-presence")) arguments.AddRange(["--public-web-presence", "true"]);
        var result = await RunAsync([.. arguments, .. options]);
        return result.Json<Plan>();
    }

    private async Task<InvitationCode> CreateAsync(int maxUses = 2, string? owner = null, string source = "cli-test",
        string planId = "free")
    {
        List<string> arguments = ["create", "--uses", maxUses.ToString(), "--source", source, "--plan", planId];
        if (owner != null)
        {
            arguments.AddRange(["--owner", owner]);
        }
        var result = await RunAsync(arguments.ToArray());
        Assert.That(result.Status, Is.EqualTo(200), result.Output + result.Error);
        return result.Json<InvitationCode>();
    }

    /// <param name="WithKeys">Pass the right keys in the environment</param>
    /// <param name="Stdin">What to type</param>
    /// <param name="CreateKey">A create key of its own, instead of the right one</param>
    /// <param name="Url">The service's URL, instead of the test server's</param>
    private record Options(bool WithKeys = true, string? Stdin = null, string? CreateKey = null, string? Url = null);

    private const string CreateKey = InvitationCodeServer.CreateKey;
    private const string SignupKey = InvitationCodeServer.SignupKey;

    private string ConfigPath => Path.Combine(_home, ".raven-hosting");

    private Task<CliResult> RunAsync(params string[] arguments) => RunAsync(new Options(), arguments);

    private async Task<CliResult> RunAsync(Options options, params string[] arguments)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Script);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["HOME"] = _home;
        start.Environment["RAVEN_HOSTING_URL"] = options.Url ?? _url;
        start.Environment.Remove("RAVEN_HOSTING_CREATE_KEY");
        start.Environment.Remove("RAVEN_HOSTING_SIGNUP_KEY");
        if (options.WithKeys)
        {
            start.Environment["RAVEN_HOSTING_CREATE_KEY"] = InvitationCodeServer.CreateKey;
            start.Environment["RAVEN_HOSTING_SIGNUP_KEY"] = InvitationCodeServer.SignupKey;
        }
        if (options.CreateKey != null)
        {
            start.Environment["RAVEN_HOSTING_CREATE_KEY"] = options.CreateKey;
        }

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(options.Stdin ?? "");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static readonly string Script = FindScript();

    private static string FindScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var script = Path.Combine(directory.FullName, "raven.sh");
            if (File.Exists(script))
            {
                return script;
            }
        }
        throw new FileNotFoundException("raven.sh not found above " + AppContext.BaseDirectory);
    }

    private static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Any(directory => File.Exists(Path.Combine(directory, program)));

    /// <summary>
    /// What a run printed. Commands that call the service print its answer, then the HTTP status as [200].
    /// </summary>
    private record CliResult(int ExitCode, string Output, string Error)
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public int? Status =>
            Output.TrimEnd().Split('\n').Last() is var last && last.StartsWith('[') && last.EndsWith(']')
                ? int.Parse(last[1..^1])
                : null;

        public T Json<T>()
        {
            Assert.That(Status, Is.EqualTo(200), Output + Error);
            return JsonSerializer.Deserialize<T>(Output.TrimEnd().Split('\n')[0], Web)!;
        }
    }
}
