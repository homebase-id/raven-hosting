using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>
/// A hosting plan: what an identity on it gets. The identity hosts have no plans of their own (odin-core #1994):
/// whoever gives an identity a plan sends its quota and public web presence with it, and the host stores them.
/// </summary>
/// <param name="Id">The plan Id, e.g. "veterans"; unique whatever its case, and never changed</param>
/// <param name="QuotaBytes">Bytes an identity on it may store; more than 0, there is no unlimited</param>
/// <param name="PublicWebPresence">Whether an identity on it has a public home page</param>
/// <param name="PublicEmail">Whether it offers a public email address; advertised only, the hosts are not told</param>
/// <param name="EmailAliases">How many email aliases it offers, 0 or more; advertised only, like PublicEmail</param>
/// <param name="ForSale">Whether owners can buy it on the upgrade page; a plan for sale has at least one price</param>
/// <param name="Prices">What it costs, one price per billing term, shortest term first; none for a plan not sold</param>
/// <param name="SortOrder">Plans are listed by this, then by Id</param>
/// <param name="Recommended">Whether its card is marked recommended for visitors; someone signed in is recommended the
/// next size up from what they have instead. When more than one is, the first listed is</param>
/// <param name="Seats">How many identities it covers: 1 for one person; more for a group plan, whose quota is a pool the
/// owner shares out between its members (<see cref="SubscriptionStore"/>)</param>
public record Plan(
    string Id,
    string Name,
    string Description,
    long QuotaBytes,
    bool PublicWebPresence,
    bool PublicEmail,
    int EmailAliases,
    bool ForSale,
    IReadOnlyList<PlanPrice> Prices,
    int SortOrder,
    long CreatedAt,
    long UpdatedAt,
    bool Recommended = false,
    int Seats = 1)
{
    /// <summary>Whether it is for more than one identity, with its storage shared out between them</summary>
    public bool IsGroup => Seats > 1;

    /// <summary>The quota as people read it, e.g. "1 TB"</summary>
    public string Storage => StorageSize.Format(QuotaBytes);

    public PlanPrice? PriceFor(int? months) => Prices.FirstOrDefault(p => p.Months == months);

    /// <summary>
    /// How much less a longer term costs than paying monthly for as long, in whole percent; null when the plan has no
    /// monthly price, the term is the monthly one, or it saves less than 1%
    /// </summary>
    public int? Saving(PlanPrice price)
    {
        var monthly = PriceFor(1);
        if (monthly == null || monthly.AmountCents == 0 || price.Months == 1)
        {
            return null;
        }
        var full = (decimal)monthly.AmountCents * price.Months;
        var percent = (int)Math.Round((full - price.AmountCents) * 100 / full, MidpointRounding.AwayFromZero);
        return percent >= 1 ? percent : null;
    }

    // A record compares lists by reference; plans are the same when their prices are
    public virtual bool Equals(Plan? other) =>
        other != null && Id == other.Id && Name == other.Name && Description == other.Description &&
        QuotaBytes == other.QuotaBytes && PublicWebPresence == other.PublicWebPresence &&
        PublicEmail == other.PublicEmail && EmailAliases == other.EmailAliases && ForSale == other.ForSale &&
        Prices.SequenceEqual(other.Prices) && SortOrder == other.SortOrder && CreatedAt == other.CreatedAt &&
        Seats == other.Seats &&
        UpdatedAt == other.UpdatedAt && Recommended == other.Recommended;

    public override int GetHashCode() => HashCode.Combine(Id, QuotaBytes, ForSale, Prices.Count, UpdatedAt);
}

/// <summary>
/// What a plan costs for one billing term, in US dollars
/// </summary>
/// <param name="Months">The term: 1 is monthly, 12 yearly; any number from 1 to 120</param>
/// <param name="AmountCents">What the term costs, in cents; 0 is free</param>
public record PlanPrice(int Months, long AmountCents)
{
    /// <summary>The amount as people read it: "$5", "$4.99", "Free"</summary>
    public string Amount => Money.Format(AmountCents);

    /// <summary>The term as people read it: "month", "year", "2 years", "3 months"</summary>
    public string Per => BillingTerm.Name(Months);

    /// <summary>"$50 / year", or "Free"</summary>
    public string Text => AmountCents == 0 ? Amount : $"{Amount} / {Per}";
}

/// <summary>
/// A price for one billing term, as given to the API
/// </summary>
public record PlanPriceRequest(int? Months, long? AmountCents);

/// <summary>
/// A new plan. Description, public email (false), email aliases (0), prices, sort order and recommended (false) may
/// be left out; the rest is required. A plan for sale needs at
/// least one price.
/// </summary>
public record PlanRequest(
    string? Id,
    string? Name,
    long? QuotaBytes,
    bool? PublicWebPresence,
    bool? ForSale = null,
    string? Description = null,
    IReadOnlyList<PlanPriceRequest>? Prices = null,
    int? SortOrder = null,
    bool? PublicEmail = null,
    int? EmailAliases = null,
    bool? Recommended = null,
    int? Seats = null);

/// <summary>
/// A change to a plan; null leaves a field as it is. The Id cannot be changed; prices are changed one term at a time.
/// </summary>
public record PlanChange(
    string? Name = null,
    string? Description = null,
    long? QuotaBytes = null,
    bool? PublicWebPresence = null,
    bool? ForSale = null,
    int? SortOrder = null,
    bool? PublicEmail = null,
    int? EmailAliases = null,
    bool? Recommended = null,
    int? Seats = null);

/// <summary>
/// A plan request the store refuses: a bad Id, a missing name, a quota that is not more than 0, fewer than 0 email
/// aliases, a bad price, a plan
/// for sale without one. The API answers 400.
/// </summary>
public class InvalidPlanRequestException(string message) : InvalidInvitationCodeRequestException(message);

/// <summary>
/// A plan Id that is taken, a plan that codes or grants still use, or the last price of a plan for sale. The API
/// answers 409.
/// </summary>
public class PlanConflictException(string message) : Exception(message);

/// <summary>
/// What a plan Id may look like, for plans, codes and grants: letters, digits, '.', '_' and '-', up to 64
/// </summary>
public static class PlanIds
{
    // "grants" would be /api/v1/plans/grants, the grants' own route; "admin" and "account" are what /sign-in?next=
    // takes to go on to the admin pages and /account, where a plan Id goes on to confirming it
    public static readonly string[] Reserved = ["grants", "admin", "account"];

    private static readonly Regex Format = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    public static bool IsValid(string? planId) => planId != null && Format.IsMatch(planId);
}

/// <summary>
/// Bytes as people read them: 1024-based, labelled TB, GB, MB and KB, at most one decimal
/// </summary>
public static class StorageSize
{
    private static readonly (long Bytes, string Unit)[] Units =
        [(1L << 40, "TB"), (1L << 30, "GB"), (1L << 20, "MB"), (1L << 10, "KB")];

    public static string Format(long bytes)
    {
        foreach (var (size, unit) in Units)
        {
            if (bytes >= size)
            {
                var value = Math.Round((double)bytes / size, 1);
                return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit;
            }
        }
        return bytes == 1 ? "1 byte" : $"{bytes} bytes";
    }
}

/// <summary>
/// US dollars as people read them: "$5", "$4.99", "$1,200", "Free"
/// </summary>
public static class Money
{
    public static string Format(long cents) => cents == 0
        ? "Free"
        : cents % 100 == 0
            ? "$" + (cents / 100).ToString("N0", CultureInfo.InvariantCulture)
            : "$" + (cents / 100m).ToString("N2", CultureInfo.InvariantCulture);
}

/// <summary>
/// A billing term as people read it: 1 month is "month", 12 "year", 24 "2 years", 3 "3 months"
/// </summary>
public static class BillingTerm
{
    public const int LongestMonths = 120;

    public static string Name(int months) => months switch
    {
        1 => "month",
        12 => "year",
        _ when months % 12 == 0 => $"{months / 12} years",
        _ => $"{months} months",
    };
}

/// <summary>
/// The hosting plans, in the same SQLite file as the codes and grants: one row per plan, its Id and a JSON document
/// with everything else, prices included. A new field is a new property in the document, not a new column; a
/// document written before a field existed reads it as its default. There are no built-in plans: the operator
/// creates them (README, "Plans").
/// </summary>
public class PlanStore
{
    private const int SqliteConstraintError = 19;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public PlanStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
    }

    //

    public async Task<Plan> CreateAsync(PlanRequest request)
    {
        var id = request.Id?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            throw new InvalidPlanRequestException("id is required");
        }
        if (!PlanIds.IsValid(id))
        {
            throw new InvalidPlanRequestException("Invalid id: letters, digits, '.', '_' and '-', up to 64");
        }
        if (PlanIds.Reserved.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidPlanRequestException($"'{id}' cannot be a plan id");
        }
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidPlanRequestException("name is required");
        }
        var quotaBytes = request.QuotaBytes ?? throw new InvalidPlanRequestException("quotaBytes is required");
        CheckQuota(quotaBytes);
        var publicWebPresence = request.PublicWebPresence ??
                                throw new InvalidPlanRequestException("publicWebPresence is required");
        var emailAliases = request.EmailAliases ?? 0;
        CheckEmailAliases(emailAliases);
        var seats = request.Seats ?? 1;
        CheckSeats(seats, quotaBytes);

        var prices = (request.Prices ?? []).Select(p => CheckPrice(
                p.Months ?? throw new InvalidPlanRequestException("Each price needs months"),
                p.AmountCents ?? throw new InvalidPlanRequestException("Each price needs amountCents")))
            .OrderBy(p => p.Months)
            .ToList();
        if (prices.Select(p => p.Months).Distinct().Count() != prices.Count)
        {
            throw new InvalidPlanRequestException("Two prices for the same number of months");
        }
        var forSale = request.ForSale ?? false;
        if (forSale && prices.Count == 0)
        {
            throw new InvalidPlanRequestException("A plan for sale needs at least one price");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var plan = new Plan(id, name, request.Description?.Trim() ?? "", quotaBytes, publicWebPresence,
            request.PublicEmail ?? false, emailAliases, forSale, prices, request.SortOrder ?? 0, now, now,
            request.Recommended ?? false, seats);

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO plans (id, json) VALUES (@id, @json);";
        command.Parameters.AddWithValue("@id", plan.Id);
        command.Parameters.AddWithValue("@json", Write(plan));
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintError)
        {
            throw new PlanConflictException($"There is already a plan '{id}'");
        }
        return plan;
    }

    //

    /// <summary>
    /// The plan with this Id, in any case; null when there is none, or the Id cannot be one
    /// </summary>
    public async Task<Plan?> GetAsync(string? id)
    {
        var trimmed = id?.Trim();
        if (!PlanIds.IsValid(trimmed))
        {
            return null;
        }

        await using var connection = await OpenAsync();
        return await GetAsync(connection, trimmed!);
    }

    /// <summary>
    /// Every plan, by sort order, then Id
    /// </summary>
    public async Task<List<Plan>> ListAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, json FROM plans;";
        await using var reader = await command.ExecuteReaderAsync();
        var plans = new List<Plan>();
        while (await reader.ReadAsync())
        {
            plans.Add(Read(reader.GetString(0), reader.GetString(1)));
        }
        return plans.OrderBy(p => p.SortOrder).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    //

    /// <summary>
    /// Changes a plan and returns it as it is now; null if there is no such plan. Identities already on it keep
    /// what they were given.
    /// </summary>
    public Task<Plan?> UpdateAsync(string? id, PlanChange change)
    {
        if (change.Name != null && change.Name.Trim() == "")
        {
            throw new InvalidPlanRequestException("name cannot be empty");
        }
        if (change.QuotaBytes != null)
        {
            CheckQuota(change.QuotaBytes.Value);
        }
        if (change.EmailAliases != null)
        {
            CheckEmailAliases(change.EmailAliases.Value);
        }
        if (change is { Name: null, Description: null, QuotaBytes: null, PublicWebPresence: null, PublicEmail: null,
                EmailAliases: null, ForSale: null, SortOrder: null, Recommended: null, Seats: null })
        {
            throw new InvalidPlanRequestException("Nothing to change");
        }

        return ChangeAsync(id, plan =>
        {
            if (change.ForSale == true && plan.Prices.Count == 0)
            {
                throw new InvalidPlanRequestException(
                    $"A plan for sale needs at least one price; give {plan.Id} one first (raven.sh plan-price)");
            }
            CheckSeats(change.Seats ?? plan.Seats, change.QuotaBytes ?? plan.QuotaBytes);
            return plan with
            {
                Name = change.Name?.Trim() ?? plan.Name,
                Description = change.Description?.Trim() ?? plan.Description,
                QuotaBytes = change.QuotaBytes ?? plan.QuotaBytes,
                PublicWebPresence = change.PublicWebPresence ?? plan.PublicWebPresence,
                PublicEmail = change.PublicEmail ?? plan.PublicEmail,
                EmailAliases = change.EmailAliases ?? plan.EmailAliases,
                ForSale = change.ForSale ?? plan.ForSale,
                SortOrder = change.SortOrder ?? plan.SortOrder,
                Recommended = change.Recommended ?? plan.Recommended,
                Seats = change.Seats ?? plan.Seats,
            };
        });
    }

    /// <summary>
    /// Sets what the plan costs for this many months, adding the term or changing its amount; null if there is no
    /// such plan
    /// </summary>
    public Task<Plan?> SetPriceAsync(string? id, int months, long amountCents)
    {
        var price = CheckPrice(months, amountCents);
        return ChangeAsync(id, plan => plan with
        {
            Prices = plan.Prices.Where(p => p.Months != months).Append(price).OrderBy(p => p.Months).ToList(),
        });
    }

    /// <summary>
    /// Takes away the plan's price for this many months. Found is false when there is no such plan or price; the last
    /// price of a plan for sale cannot be taken away.
    /// </summary>
    public async Task<(Plan? Plan, bool Found)> RemovePriceAsync(string? id, int months)
    {
        var found = false;
        var plan = await ChangeAsync(id, plan =>
        {
            if (plan.PriceFor(months) == null)
            {
                return null;
            }
            if (plan.ForSale && plan.Prices.Count == 1)
            {
                throw new PlanConflictException(
                    $"{plan.Id} is for sale and this is its only price; mark it not for sale first, or add another price");
            }
            found = true;
            return plan with { Prices = plan.Prices.Where(p => p.Months != months).ToList() };
        });
        return (plan, found);
    }

    /// <summary>
    /// Deletes a plan; false if there is none. The caller checks no code or grant uses it.
    /// </summary>
    public async Task<bool> DeleteAsync(string? id)
    {
        var trimmed = id?.Trim();
        if (!PlanIds.IsValid(trimmed))
        {
            return false;
        }

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plans WHERE id = @id;";
        command.Parameters.AddWithValue("@id", trimmed);
        return await command.ExecuteNonQueryAsync() == 1;
    }

    //

    // Reads the plan, changes it, and writes it back with a new updatedAt, holding the write lock throughout so two
    // changes cannot overwrite each other. Null from the change leaves the plan as it is. Returns the plan as it is
    // now; null if there is no such plan.
    private async Task<Plan?> ChangeAsync(string? id, Func<Plan, Plan?> change)
    {
        var trimmed = id?.Trim();
        if (!PlanIds.IsValid(trimmed))
        {
            return null;
        }

        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var plan = await GetAsync(connection, trimmed!);
        if (plan == null)
        {
            return null;
        }

        var changed = change(plan);
        if (changed == null)
        {
            return plan;
        }
        changed = changed with { UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plans SET json = @json WHERE id = @id;";
        command.Parameters.AddWithValue("@id", plan.Id);
        command.Parameters.AddWithValue("@json", Write(changed));
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return changed;
    }

    private static async Task<Plan?> GetAsync(SqliteConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, json FROM plans WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader.GetString(0), reader.GetString(1)) : null;
    }

    // What is kept for a plan: everything but its Id, which is the row's
    private record Stored(
        string Name = "",
        string Description = "",
        long QuotaBytes = 0,
        bool PublicWebPresence = false,
        bool ForSale = false,
        List<StoredPrice>? Prices = null,
        int SortOrder = 0,
        long CreatedAt = 0,
        long UpdatedAt = 0,
        bool PublicEmail = false,
        int EmailAliases = 0,
        bool Recommended = false,
        int Seats = 1);

    private record StoredPrice(int Months, long AmountCents);

    private static string Write(Plan plan) => JsonSerializer.Serialize(new Stored(plan.Name, plan.Description,
        plan.QuotaBytes, plan.PublicWebPresence, plan.ForSale,
        plan.Prices.Select(p => new StoredPrice(p.Months, p.AmountCents)).ToList(), plan.SortOrder, plan.CreatedAt,
        plan.UpdatedAt, plan.PublicEmail, plan.EmailAliases, plan.Recommended, plan.Seats), Json);

    private static Plan Read(string id, string json)
    {
        var stored = JsonSerializer.Deserialize<Stored>(json, Json) ?? new Stored();
        return new Plan(id, stored.Name, stored.Description, stored.QuotaBytes, stored.PublicWebPresence,
            stored.PublicEmail, stored.EmailAliases, stored.ForSale,
            (stored.Prices ?? []).Select(p => new PlanPrice(p.Months, p.AmountCents)).OrderBy(p => p.Months).ToList(),
            stored.SortOrder, stored.CreatedAt, stored.UpdatedAt, stored.Recommended, Math.Max(1, stored.Seats));
    }

    private static PlanPrice CheckPrice(int months, long amountCents)
    {
        if (months is < 1 or > BillingTerm.LongestMonths)
        {
            throw new InvalidPlanRequestException($"months must be between 1 and {BillingTerm.LongestMonths}");
        }
        if (amountCents < 0)
        {
            throw new InvalidPlanRequestException("amountCents cannot be negative; 0 is free");
        }
        return new PlanPrice(months, amountCents);
    }

    private static void CheckQuota(long quotaBytes)
    {
        if (quotaBytes <= 0)
        {
            throw new InvalidPlanRequestException("quotaBytes must be more than 0; there is no unlimited");
        }
    }

    // Every member of a group plan gets at least SubscriptionStore.MinimumShareBytes of its pool; a plan for one may be
    // any size
    private static void CheckSeats(int seats, long quotaBytes)
    {
        if (seats is < 1 or > SubscriptionStore.MostSeats)
        {
            throw new InvalidPlanRequestException($"seats must be between 1 and {SubscriptionStore.MostSeats}");
        }
        if (seats > 1 && quotaBytes < seats * SubscriptionStore.MinimumShareBytes)
        {
            throw new InvalidPlanRequestException(
                $"A plan for {seats} needs at least {StorageSize.Format(seats * SubscriptionStore.MinimumShareBytes)}: " +
                $"{StorageSize.Format(SubscriptionStore.MinimumShareBytes)} each");
        }
    }

    private static void CheckEmailAliases(int emailAliases)
    {
        if (emailAliases < 0)
        {
            throw new InvalidPlanRequestException("emailAliases cannot be fewer than 0");
        }
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        await InitializeAsync();
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // Creates the table on first use, next to the codes' and grants'
    private async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync();
        try
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

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync();
            }
            await MoveColumnsToJsonAsync(connection);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS plans (
                        id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                        json TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    // Before documents, plans were a column per field, and their prices a plan_prices table (before that, a free-text
    // price column, which is not a price and is left behind). The plans and their prices move into documents as they
    // were, for sale or not, and both tables are replaced by the Id-and-JSON one.
    private static async Task MoveColumnsToJsonAsync(SqliteConnection connection)
    {
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('plans') WHERE name = 'quota_bytes';";
            if (Convert.ToInt64(await columns.ExecuteScalarAsync()) == 0)
            {
                return;
            }
        }

        await using var transaction = connection.BeginTransaction(deferred: false);

        var prices = new Dictionary<string, List<PlanPrice>>(StringComparer.OrdinalIgnoreCase);
        await using (var table = connection.CreateCommand())
        {
            table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'plan_prices';";
            if (Convert.ToInt64(await table.ExecuteScalarAsync()) == 1)
            {
                await using var select = connection.CreateCommand();
                select.CommandText = "SELECT plan_id, months, amount_cents FROM plan_prices ORDER BY months;";
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!prices.TryGetValue(reader.GetString(0), out var list))
                    {
                        prices[reader.GetString(0)] = list = [];
                    }
                    list.Add(new PlanPrice(reader.GetInt32(1), reader.GetInt64(2)));
                }
            }
        }

        var plans = new List<Plan>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                "SELECT id, name, description, quota_bytes, public_web_presence, for_sale, sort_order, created_at, " +
                "updated_at FROM plans;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                plans.Add(new Plan(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    reader.GetBoolean(4), PublicEmail: false, EmailAliases: 0, reader.GetBoolean(5),
                    prices.TryGetValue(reader.GetString(0), out var planPrices) ? planPrices : [],
                    reader.GetInt32(6), reader.GetInt64(7), reader.GetInt64(8)));
            }
        }

        await using (var replace = connection.CreateCommand())
        {
            replace.CommandText =
                """
                DROP TABLE IF EXISTS plan_prices;
                DROP TABLE plans;
                CREATE TABLE plans (
                    id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    json TEXT NOT NULL
                );
                """;
            await replace.ExecuteNonQueryAsync();
        }
        foreach (var plan in plans)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO plans (id, json) VALUES (@id, @json);";
            insert.Parameters.AddWithValue("@id", plan.Id);
            insert.Parameters.AddWithValue("@json", Write(plan));
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
