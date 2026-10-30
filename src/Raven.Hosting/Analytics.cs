using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>
/// One page request as it came in, before it is written: the address and user agent are only ever in memory, until
/// the worker turns them into the day's visitor hash
/// </summary>
public record PageHit(long At, string Path, int Status, string? Referrer, string? Campaign, string Ip, string UserAgent);

/// <summary>
/// The site's own traffic counts (README, "Traffic"): no scripts, no cookies, nobody else. The middleware notes each
/// page request in memory; <see cref="AnalyticsWorker"/> writes them to their own SQLite file every few seconds.
/// Admin pages, the APIs and anything that is not a GET are not counted.
/// </summary>
public partial class Analytics
{
    /// <summary>More than this waiting to be written, and new ones are dropped rather than held</summary>
    public const int MaxWaiting = 10_000;

    private static readonly string[] Skipped =
        [AdminPages.Root, "/api/", "/ping", "/.well-known/", "/dev/", $"{UpgradePage.Root}/youauth-callback"];

    private readonly Channel<PageHit> _hits = Channel.CreateBounded<PageHit>(
        new BoundedChannelOptions(MaxWaiting) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private readonly TimeProvider _time;
    private long _dropped;

    public Analytics(TimeProvider time) => _time = time;

    public ChannelReader<PageHit> Hits => _hits.Reader;

    /// <summary>How many were dropped since last asked, with the queue full</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    /// <summary>Notes the request once it is answered; never fails it</summary>
    public async Task MiddlewareAsync(HttpContext context, RequestDelegate next)
    {
        await next(context);

        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || Skipped.Any(s => request.Path.StartsWithSegments(s.TrimEnd('/'))))
        {
            return;
        }

        var hit = new PageHit(_time.GetUtcNow().ToUnixTimeMilliseconds(), PathOf(request.Path.Value ?? "/"),
            context.Response.StatusCode, ReferrerOf(request), Short(request.Query["utm_source"], 100),
            context.Connection.RemoteIpAddress?.ToString() ?? "", Short(request.Headers.UserAgent, 500) ?? "");
        if (!_hits.Writer.TryWrite(hit))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>mobile, desktop, or bot for crawlers, scripts and no user agent at all</summary>
    public static string DeviceOf(string userAgent) =>
        userAgent.Length == 0 || BotPattern().IsMatch(userAgent) ? "bot"
        : userAgent.Contains("Mobi", StringComparison.Ordinal) || userAgent.Contains("Android", StringComparison.Ordinal) ? "mobile"
        : "desktop";

    // A join link's token is a key to someone's plan: it is not kept
    private static string PathOf(string path) =>
        path.StartsWith("/join/", StringComparison.OrdinalIgnoreCase) ? "/join/…" : path.Length > 200 ? path[..200] : path;

    // Only the site it came from, and not this one
    private static string? ReferrerOf(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }
        var host = uri.Host.ToLowerInvariant();
        host = host.StartsWith("www.") ? host[4..] : host;
        return string.Equals(host, request.Host.Host, StringComparison.OrdinalIgnoreCase) ? null : host;
    }

    private static string? Short(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    [GeneratedRegex(@"bot|crawl|spider|slurp|curl|wget|python|go-http|java/|okhttp|httpclient|axios|node-fetch|headless|lighthouse|preview|facebookexternalhit|monitor|uptime|scan|feed",
        RegexOptions.IgnoreCase)]
    private static partial Regex BotPattern();
}

/// <summary>Writes the noted requests every few seconds, and the rest when the service stops; hourly, rolls up and
/// clears out</summary>
public class AnalyticsWorker(Analytics analytics, AnalyticsStore store, TimeProvider time, ILogger<AnalyticsWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TidyEvery = TimeSpan.FromHours(1);
    private const int Batch = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextTidy = time.GetUtcNow();
        using var timer = new PeriodicTimer(FlushEvery, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync();
                if (time.GetUtcNow() >= nextTidy)
                {
                    nextTidy = time.GetUtcNow() + TidyEvery;
                    await TidyAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        // What came in before the stop
        await FlushAsync();
    }

    private async Task FlushAsync()
    {
        try
        {
            var batch = new List<PageHit>(Batch);
            while (true)
            {
                batch.Clear();
                while (batch.Count < Batch && analytics.Hits.TryRead(out var hit))
                {
                    batch.Add(hit);
                }
                if (batch.Count == 0)
                {
                    break;
                }
                await store.AddAsync(batch);
            }

            if (analytics.TakeDropped() is > 0 and var dropped)
            {
                logger.LogWarning("Dropped {count} page views with the queue full", dropped);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not write page views; they are lost");
        }
    }

    private async Task TidyAsync()
    {
        try
        {
            await store.TidyAsync(time.GetUtcNow());
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not roll up or clear out page views; trying again next hour");
        }
    }
}

/// <summary>One line of a traffic breakdown: a page, referrer or campaign</summary>
public record TrafficLine(string Name, long Views, long Visitors);

/// <summary>One day, or month, of traffic</summary>
public record TrafficPeriod(string Name, long Views, long Visitors);

/// <summary>A step towards buying: how many visitors reached the page</summary>
public record FunnelStep(string Label, string Path, long Visitors);

/// <summary>
/// The traffic over the last <see cref="Days"/> days, today included. A view is a page answered 2xx to someone not
/// a bot; visitors are counted afresh each day (the hash changes daily), so over several days they are summed.
/// </summary>
public record TrafficStats(
    int Days,
    long Views,
    long Visitors,
    long BotViews,
    long NotFound,
    List<TrafficPeriod> ByDay,
    List<TrafficLine> Pages,
    List<TrafficLine> Referrers,
    List<TrafficLine> Campaigns,
    List<TrafficLine> Devices,
    List<TrafficLine> Missing,
    List<FunnelStep> Funnel,
    List<TrafficPeriod> ByMonth);

/// <summary>
/// Page views, in their own SQLite file apart from the business data (<c>RavenHosting:AnalyticsDatabasePath</c>):
/// written in batches, never in the way of a purchase, and safe to lose. Each one keeps the path, status, referring
/// site, utm_source and device, and a visitor hash of the address and user agent with a salt that changes every
/// day (UTC) and is then deleted, so a visitor can be counted for a day and never followed. Each view is kept
/// <see cref="KeepDays"/> days; every finished day is rolled up into daily totals, kept for good.
/// </summary>
public class AnalyticsStore
{
    public const int KeepDays = 90;

    /// <summary>The steps towards buying, in order</summary>
    public static readonly (string Label, string Path)[] FunnelSteps =
    [
        ("Home", "/"),
        ("Sign in", SitePages.SignIn),
        ("Confirm", $"{UpgradePage.Root}/confirm"),
        ("Checkout", $"{UpgradePage.Root}/checkout"),
        ("Done", $"{UpgradePage.Root}/done"),
    ];

    private const int Top = 20;

    // A view: answered 2xx to someone not a bot
    private const string IsView = "device <> 'bot' AND status BETWEEN 200 AND 299";

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Lock _initializeLock = new();
    private bool _initialized;

    public AnalyticsStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>Writes a batch in one transaction, hashing each visitor with its day's salt</summary>
    public async Task AddAsync(IReadOnlyList<PageHit> hits)
    {
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction();
        var salts = new Dictionary<string, byte[]>();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO page_views (at, day, path, status, referrer, campaign, visitor, device)
            VALUES (@at, @day, @path, @status, @referrer, @campaign, @visitor, @device);
            """;
        var at = command.Parameters.Add("@at", SqliteType.Integer);
        var day = command.Parameters.Add("@day", SqliteType.Text);
        var path = command.Parameters.Add("@path", SqliteType.Text);
        var status = command.Parameters.Add("@status", SqliteType.Integer);
        var referrer = command.Parameters.Add("@referrer", SqliteType.Text);
        var campaign = command.Parameters.Add("@campaign", SqliteType.Text);
        var visitor = command.Parameters.Add("@visitor", SqliteType.Text);
        var device = command.Parameters.Add("@device", SqliteType.Text);

        foreach (var hit in hits)
        {
            var hitDay = DayOf(DateTimeOffset.FromUnixTimeMilliseconds(hit.At));
            if (!salts.TryGetValue(hitDay, out var salt))
            {
                salt = salts[hitDay] = await SaltAsync(connection, hitDay);
            }

            at.Value = hit.At;
            day.Value = hitDay;
            path.Value = hit.Path;
            status.Value = hit.Status;
            referrer.Value = (object?)hit.Referrer ?? DBNull.Value;
            campaign.Value = (object?)hit.Campaign ?? DBNull.Value;
            visitor.Value = VisitorHash(salt, hit.Ip, hit.UserAgent);
            device.Value = Analytics.DeviceOf(hit.UserAgent);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Rolls every finished day not yet rolled up into the daily totals, then deletes views older than
    /// <see cref="KeepDays"/> and the salts of days gone by
    /// </summary>
    public async Task TidyAsync(DateTimeOffset now)
    {
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT OR REPLACE INTO daily_pages (day, path, views, visitors)
            SELECT day, path, COUNT(*), COUNT(DISTINCT visitor) FROM page_views
            WHERE {IsView} AND day < @today AND day NOT IN (SELECT day FROM daily_totals)
            GROUP BY day, path;

            INSERT OR REPLACE INTO daily_totals (day, views, visitors, bot_views, not_found)
            SELECT day,
                   COUNT(CASE WHEN {IsView} THEN 1 END),
                   COUNT(DISTINCT CASE WHEN {IsView} THEN visitor END),
                   COUNT(CASE WHEN device = 'bot' THEN 1 END),
                   COUNT(CASE WHEN status = 404 THEN 1 END)
            FROM page_views WHERE day < @today AND day NOT IN (SELECT day FROM daily_totals)
            GROUP BY day;

            DELETE FROM page_views WHERE day < @oldest;
            DELETE FROM visitor_salts WHERE day < @yesterday;
            """;
        command.Parameters.AddWithValue("@today", DayOf(now));
        command.Parameters.AddWithValue("@yesterday", DayOf(now.AddDays(-1)));
        command.Parameters.AddWithValue("@oldest", DayOf(now.AddDays(-(KeepDays - 1))));
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    /// <summary>The traffic over the last <paramref name="days"/> days, today included (at most <see cref="KeepDays"/>)</summary>
    public async Task<TrafficStats> StatsAsync(int days, DateTimeOffset now)
    {
        days = Math.Clamp(days, 1, KeepDays);
        var from = DayOf(now.AddDays(-(days - 1)));
        await using var connection = Open();

        long views = 0, visitors = 0, botViews = 0, notFound = 0;
        await using (var command = Command(connection,
            $"""
            SELECT COUNT(CASE WHEN {IsView} THEN 1 END),
                   COUNT(DISTINCT CASE WHEN {IsView} THEN day || visitor END),
                   COUNT(CASE WHEN device = 'bot' THEN 1 END),
                   COUNT(CASE WHEN status = 404 THEN 1 END)
            FROM page_views WHERE day >= @from;
            """, from))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                (views, visitors, botViews, notFound) =
                    (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            }
        }

        var counted = (await LinesAsync(connection,
                $"SELECT day, COUNT(*), COUNT(DISTINCT visitor) FROM page_views WHERE {IsView} AND day >= @from GROUP BY day;", from))
            .ToDictionary(l => l.Name);
        var byDay = Enumerable.Range(0, days)
            .Select(i => DayOf(now.AddDays(-i)))
            .Select(d => counted.TryGetValue(d, out var l) ? new TrafficPeriod(d, l.Views, l.Visitors) : new TrafficPeriod(d, 0, 0))
            .ToList();

        var pages = await LinesAsync(connection, Breakdown("path"), from);
        var referrers = await LinesAsync(connection, Breakdown("referrer"), from);
        var campaigns = await LinesAsync(connection, Breakdown("campaign"), from);
        var devices = await LinesAsync(connection, Breakdown("device"), from);
        var missing = await LinesAsync(connection,
            $"""
            SELECT path, COUNT(*), COUNT(DISTINCT day || visitor) FROM page_views
            WHERE status = 404 AND day >= @from GROUP BY path ORDER BY 2 DESC LIMIT {Top};
            """, from);

        var reached = (await LinesAsync(connection,
                $"""
                SELECT path, COUNT(*), COUNT(DISTINCT day || visitor) FROM page_views
                WHERE {IsView} AND day >= @from AND path IN ({string.Join(", ", FunnelSteps.Select((_, i) => $"@step{i}"))})
                GROUP BY path;
                """, from, FunnelSteps.Select(s => s.Path).ToArray()))
            .ToDictionary(l => l.Name);
        var funnel = FunnelSteps
            .Select(s => new FunnelStep(s.Label, s.Path, reached.TryGetValue(s.Path, out var l) ? l.Visitors : 0))
            .ToList();

        // Every month there is, from the daily totals and the days not yet rolled up
        var byMonth = (await LinesAsync(connection,
                $"""
                SELECT substr(day, 1, 7), SUM(views), SUM(visitors) FROM (
                    SELECT day, views, visitors FROM daily_totals
                    UNION ALL
                    SELECT day, COUNT(CASE WHEN {IsView} THEN 1 END), COUNT(DISTINCT CASE WHEN {IsView} THEN visitor END)
                    FROM page_views WHERE day NOT IN (SELECT day FROM daily_totals) GROUP BY day
                ) GROUP BY 1 ORDER BY 1 DESC LIMIT 24;
                """, from))
            .Select(l => new TrafficPeriod(l.Name, l.Views, l.Visitors))
            .ToList();

        return new TrafficStats(days, views, visitors, botViews, notFound, byDay, pages, referrers, campaigns, devices,
            missing, funnel, byMonth);
    }

    //

    // The views by one column, most visitors first, leaving out those without it
    private static string Breakdown(string column) =>
        $"""
        SELECT {column}, COUNT(*), COUNT(DISTINCT day || visitor) FROM page_views
        WHERE {IsView} AND day >= @from AND {column} IS NOT NULL GROUP BY {column} ORDER BY 3 DESC, 2 DESC LIMIT {Top};
        """;

    private static SqliteCommand Command(SqliteConnection connection, string sql, string from, string[]? steps = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@from", from);
        for (var i = 0; i < (steps?.Length ?? 0); i++)
        {
            command.Parameters.AddWithValue($"@step{i}", steps![i]);
        }
        return command;
    }

    private static async Task<List<TrafficLine>> LinesAsync(SqliteConnection connection, string sql, string from,
        string[]? steps = null)
    {
        await using var command = Command(connection, sql, from, steps);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<TrafficLine>();
        while (await reader.ReadAsync())
        {
            lines.Add(new TrafficLine(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }
        return lines;
    }

    // The day's salt, made the first time it is needed
    private static async Task<byte[]> SaltAsync(SqliteConnection connection, string day)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO visitor_salts (day, salt) VALUES (@day, @salt) ON CONFLICT (day) DO NOTHING;
            SELECT salt FROM visitor_salts WHERE day = @day;
            """;
        command.Parameters.AddWithValue("@day", day);
        command.Parameters.AddWithValue("@salt", RandomNumberGenerator.GetBytes(32));
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static string VisitorHash(byte[] salt, string ip, string userAgent) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes($"{ip}\n{userAgent}"))[..8]);

    private static string DayOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        Initialize();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Creates the tables on first use, in their own file
    private void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initializeLock)
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS page_views (
                    id INTEGER PRIMARY KEY,
                    at INTEGER NOT NULL,
                    day TEXT NOT NULL,
                    path TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    referrer TEXT,
                    campaign TEXT,
                    visitor TEXT NOT NULL,
                    device TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS page_views_day ON page_views (day);
                CREATE TABLE IF NOT EXISTS visitor_salts (
                    day TEXT PRIMARY KEY,
                    salt BLOB NOT NULL
                );
                CREATE TABLE IF NOT EXISTS daily_totals (
                    day TEXT PRIMARY KEY,
                    views INTEGER NOT NULL,
                    visitors INTEGER NOT NULL,
                    bot_views INTEGER NOT NULL,
                    not_found INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS daily_pages (
                    day TEXT NOT NULL,
                    path TEXT NOT NULL,
                    views INTEGER NOT NULL,
                    visitors INTEGER NOT NULL,
                    PRIMARY KEY (day, path)
                );
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }
}
