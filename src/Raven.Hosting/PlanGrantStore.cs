using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>
/// A plan an identity is owed: by a code, a purchase, or an admin. The service sets it on the identity host that
/// serves the identity, and retries until that host has it.
/// </summary>
/// <param name="QuotaBytes">The plan's quota when the grant was made, sent to the host with the plan; null on grants
/// made before plans were kept here, which send the plan Id alone. On a group plan, the member's share of its pool</param>
/// <param name="PublicWebPresence">The plan's public web presence when the grant was made; null like QuotaBytes</param>
/// <param name="Host">The provisioning domain of the host the plan was set on; null until it was</param>
/// <param name="HostHint">The host the request said the identity is on; tried first, if it is a known host</param>
/// <param name="Source">Why the identity is owed it: <see cref="PlanGrantSource"/></param>
/// <param name="Reference">The caller's id for it, e.g. a payment id; unique, so the same one twice is one grant</param>
/// <param name="Status"><c>pending</c> until the host has it, then <c>applied</c>; <c>failed</c> when the host
/// refused the plan, which no retry by itself will change; <c>superseded</c> when a newer grant for the identity came
/// before the host had it, so it is never set over the newer one</param>
/// <param name="Attempts">How many times the service tried to set it</param>
/// <param name="NextAttemptAt">When a pending grant is tried again (unix ms)</param>
/// <param name="SubscriptionId">The paid subscription that covers it (<see cref="SubscriptionStore"/>), which says
/// until when; null for a grant that does not end (a code's, an admin's, the free plan after one ran out)</param>
/// <param name="ReappliedAt">When it was last set on the host again after it was applied: an add-on changed, or the
/// host drifted; null until then</param>
/// <param name="Note">Why it was made, as the admin or the service said: "support goodwill", "expired"</param>
/// <param name="MadeBy">The admin who made it on the admin pages; null for the API, codes, purchases and the service</param>
/// <remarks>Each identity's latest grant is its subscription; the ones before it are its history.</remarks>
public record PlanGrant(
    long Id,
    string Identity,
    string PlanId,
    long? QuotaBytes,
    bool? PublicWebPresence,
    string Source,
    string Reference,
    string Status,
    string? Host,
    string? HostHint,
    int Attempts,
    string? LastError,
    long? NextAttemptAt,
    long CreatedAt,
    long UpdatedAt,
    long? AppliedAt,
    long? SubscriptionId = null,
    long? ReappliedAt = null,
    string? Note = null,
    string? MadeBy = null)
{
    /// <summary>When the host was last given it; null until it was</summary>
    public long? LastAppliedAt => ReappliedAt ?? AppliedAt;
}

public static class PlanGrantStatus
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Superseded = "superseded";

    public static readonly string[] All = [Pending, Applied, Failed, Superseded];
}

public static class PlanGrantSource
{
    public const string Code = "code";
    public const string Purchase = "purchase";
    public const string Admin = "admin";

    /// <summary>A member of someone else's group plan, who joined it</summary>
    public const string Group = "group";

    /// <summary>The service, moving an identity whose paid term ran out (and its grace) back to the free plan</summary>
    public const string Expiry = "expiry";

    public static readonly string[] All = [Code, Purchase, Admin, Group, Expiry];
}

/// <summary>
/// A grant request the store refuses: a bad identity, source, reference or host, or a plan that does not exist. The
/// API answers 400.
/// </summary>
public class InvalidPlanGrantRequestException(string message) : InvalidInvitationCodeRequestException(message);

/// <summary>
/// The same reference again, for a different identity or plan. The API answers 409.
/// </summary>
public class PlanGrantConflictException(string message) : Exception(message);

/// <summary>
/// Plan grants, and which host was last seen serving each identity, in the same SQLite file as the codes
/// </summary>
public class PlanGrantStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public PlanGrantStore(string databasePath)
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

    /// <summary>
    /// Makes a pending grant of the plan with what it gives now, or returns the one already made with this reference.
    /// A reference reused for another identity or plan is refused.
    /// </summary>
    /// <param name="quotaBytes">The quota to give, when not the plan's: a member's share of a group plan</param>
    /// <param name="subscriptionId">The paid subscription it comes from, if any</param>
    /// <returns>The grant, and whether this call made it</returns>
    public async Task<(PlanGrant Grant, bool Created)> CreateAsync(string? identity, Plan plan, string? source,
        string? reference, string? hostHint, long now, string? note = null, string? madeBy = null,
        long? quotaBytes = null, long? subscriptionId = null)
    {
        var normalizedIdentity = DomainNames.Normalize(identity) ??
                                 throw new InvalidPlanGrantRequestException("Invalid identity");
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? PlanGrantSource.Admin : source.Trim().ToLowerInvariant();
        if (!PlanGrantSource.All.Contains(normalizedSource))
        {
            throw new InvalidPlanGrantRequestException($"source must be one of {string.Join(", ", PlanGrantSource.All)}");
        }

        var normalizedReference = string.IsNullOrWhiteSpace(reference) ? $"grant-{Guid.NewGuid():N}" : reference.Trim();
        if (!ReferenceFormat.IsMatch(normalizedReference))
        {
            throw new InvalidPlanGrantRequestException("Invalid reference");
        }

        string? normalizedHint = null;
        if (!string.IsNullOrWhiteSpace(hostHint))
        {
            normalizedHint = DomainNames.Normalize(hostHint) ?? throw new InvalidPlanGrantRequestException("Invalid host");
        }

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plan_grants (identity, plan_id, quota_bytes, public_web_presence, source, reference, status,
                                     host_hint, attempts, next_attempt_at, created_at, updated_at, subscription_id,
                                     note, made_by)
            VALUES (@identity, @planId, @quotaBytes, @publicWebPresence, @source, @reference, 'pending', @hostHint, 0,
                    @now, @now, @now, @subscriptionId, @note, @madeBy)
            ON CONFLICT (reference) DO NOTHING;
            SELECT changes();
            """;
        // A grant not yet on its host gives way to a newer one for the same identity: retried later, it would set the
        // older plan or share over the newer
        await using var supersede = connection.CreateCommand();
        supersede.CommandText =
            """
            UPDATE plan_grants SET status = 'superseded', next_attempt_at = NULL, updated_at = @now
            WHERE identity = @identity AND status IN ('pending', 'failed')
              AND id < (SELECT id FROM plan_grants WHERE reference = @reference);
            """;
        supersede.Parameters.AddWithValue("@identity", normalizedIdentity);
        supersede.Parameters.AddWithValue("@reference", normalizedReference);
        supersede.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@identity", normalizedIdentity);
        command.Parameters.AddWithValue("@planId", plan.Id);
        command.Parameters.AddWithValue("@quotaBytes", quotaBytes ?? plan.QuotaBytes);
        command.Parameters.AddWithValue("@publicWebPresence", plan.PublicWebPresence);
        command.Parameters.AddWithValue("@source", normalizedSource);
        command.Parameters.AddWithValue("@reference", normalizedReference);
        command.Parameters.AddWithValue("@hostHint", (object?)normalizedHint ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@subscriptionId", (object?)subscriptionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@note", (object?)note?.Trim() is "" or null ? DBNull.Value : note!.Trim());
        command.Parameters.AddWithValue("@madeBy", (object?)madeBy ?? DBNull.Value);
        var created = Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
        if (created)
        {
            await supersede.ExecuteNonQueryAsync();
        }

        var grant = (await GetByReferenceAsync(connection, normalizedReference))!;
        if (!created && (grant.Identity != normalizedIdentity || !string.Equals(grant.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PlanGrantConflictException(
                $"Reference {normalizedReference} is already a grant of {grant.PlanId} to {grant.Identity}");
        }
        return (grant, created);
    }

    //

    public async Task<PlanGrant?> GetAsync(long id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return await ReadOneAsync(command);
    }

    /// <summary>
    /// Grants, newest first; only an identity's, or only those in a status, when given
    /// </summary>
    public async Task<List<PlanGrant>> ListAsync(string? identity, string? status)
    {
        var where = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        if (identity != null)
        {
            where.Add("identity = @identity");
            command.Parameters.AddWithValue("@identity",
                DomainNames.Normalize(identity) ?? throw new InvalidPlanGrantRequestException("Invalid identity"));
        }
        if (status != null)
        {
            if (!PlanGrantStatus.All.Contains(status))
            {
                throw new InvalidPlanGrantRequestException(
                    $"status must be one of {string.Join(", ", PlanGrantStatus.All)}");
            }
            where.Add("status = @status");
            command.Parameters.AddWithValue("@status", status);
        }

        command.CommandText = Select + (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) +
                              " ORDER BY id DESC;";
        return await ReadAllAsync(command);
    }

    /// <summary>
    /// Pending grants whose next attempt is due, oldest first
    /// </summary>
    public async Task<List<PlanGrant>> DueAsync(long now, int limit)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Select +
                              " WHERE status = 'pending' AND next_attempt_at <= @now ORDER BY next_attempt_at, id LIMIT @limit;";
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@limit", limit);
        return await ReadAllAsync(command);
    }

    /// <summary>
    /// For each identity, the grant applied last: the plan it should have now
    /// </summary>
    public async Task<List<PlanGrant>> LatestAppliedAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Select +
                              """
                               WHERE id IN (
                                   SELECT g.id FROM plan_grants g
                                   WHERE g.status = 'applied'
                                     AND g.id = (SELECT g2.id FROM plan_grants g2
                                                 WHERE g2.identity = g.identity AND g2.status = 'applied'
                                                 ORDER BY g2.applied_at DESC, g2.id DESC LIMIT 1)
                               ) ORDER BY identity;
                              """;
        return await ReadAllAsync(command);
    }

    /// <summary>
    /// Every identity's subscription: its latest grant, whatever its status. Newest first.
    /// </summary>
    public async Task<List<PlanGrant>> CurrentAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Select +
                              " WHERE id IN (SELECT MAX(id) FROM plan_grants GROUP BY identity) ORDER BY id DESC;";
        return await ReadAllAsync(command);
    }

    /// <summary>The identity's subscription: its latest grant; null when it has none</summary>
    public async Task<PlanGrant?> CurrentForAsync(string identity)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE identity = @identity ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("@identity", identity);
        return await ReadOneAsync(command);
    }

    /// <summary>When a term of this many months from <paramref name="from"/> (unix ms) runs out</summary>
    public static long TermEnd(long from, int months) =>
        DateTimeOffset.FromUnixTimeMilliseconds(from).AddMonths(months).ToUnixTimeMilliseconds();

    /// <summary>
    /// How many grants are of this plan, whatever its case
    /// </summary>
    public async Task<int> CountWithPlanAsync(string planId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM plan_grants WHERE plan_id = @planId COLLATE NOCASE;";
        command.Parameters.AddWithValue("@planId", planId.Trim());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    //

    public Task<PlanGrant?> MarkAppliedAsync(long id, string host, long now) =>
        UpdateAsync(id, "status = 'applied', host = @host, applied_at = @now, last_error = NULL, next_attempt_at = NULL, attempts = attempts + 1",
            now, ("@host", host));

    /// <summary>An applied grant set on its host again: an add-on changed, or the host drifted</summary>
    public Task<PlanGrant?> MarkReappliedAsync(long id, string host, long now) =>
        UpdateAsync(id, "host = @host, reapplied_at = @now", now, ("@host", host));

    public Task<PlanGrant?> MarkFailedAsync(long id, string? host, string error, long now) =>
        UpdateAsync(id, "status = 'failed', host = COALESCE(@host, host), last_error = @error, next_attempt_at = NULL, attempts = attempts + 1",
            now, ("@host", (object?)host ?? DBNull.Value), ("@error", error));

    public Task<PlanGrant?> MarkPendingAsync(long id, string error, long nextAttemptAt, long now) =>
        UpdateAsync(id, "status = 'pending', last_error = @error, next_attempt_at = @next, attempts = attempts + 1",
            now, ("@error", error), ("@next", nextAttemptAt));

    /// <summary>
    /// Makes a pending or failed grant due now, without counting an attempt; null for an applied grant or none
    /// </summary>
    public async Task<PlanGrant?> MakeDueAsync(long id, long now)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE plan_grants SET status = 'pending', next_attempt_at = @now, updated_at = @now " +
            "WHERE id = @id AND status IN ('pending', 'failed');" +
            Select + " WHERE id = @id AND changes() = 1;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", now);
        return await ReadOneAsync(command);
    }

    //

    /// <summary>
    /// The host that last said it serves the identity; null when none has
    /// </summary>
    public async Task<string?> GetKnownHostAsync(string identity)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT host FROM plan_identity_hosts WHERE identity = @identity;";
        command.Parameters.AddWithValue("@identity", identity);
        return await command.ExecuteScalarAsync() as string;
    }

    public async Task SetKnownHostAsync(string identity, string host, long now)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plan_identity_hosts (identity, host, seen_at) VALUES (@identity, @host, @now)
            ON CONFLICT (identity) DO UPDATE SET host = excluded.host, seen_at = excluded.seen_at;
            """;
        command.Parameters.AddWithValue("@identity", identity);
        command.Parameters.AddWithValue("@host", host);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ForgetKnownHostAsync(string identity)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plan_identity_hosts WHERE identity = @identity;";
        command.Parameters.AddWithValue("@identity", identity);
        await command.ExecuteNonQueryAsync();
    }

    //

    private const string Select =
        """
        SELECT id, identity, plan_id, quota_bytes, public_web_presence, source, reference, status, host, host_hint,
               attempts, last_error, next_attempt_at, created_at, updated_at, applied_at, subscription_id, reapplied_at,
               note, made_by
        FROM plan_grants
        """;

    // The caller's id, e.g. a payment provider's: printable, no spaces, not too long
    private static readonly Regex ReferenceFormat = new(@"^[A-Za-z0-9._:-]{1,128}$", RegexOptions.Compiled);

    private async Task<PlanGrant?> UpdateAsync(long id, string set, long now, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE plan_grants SET {set}, updated_at = @now WHERE id = @id;" +
                              Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", now);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await ReadOneAsync(command);
    }

    private static async Task<PlanGrant?> GetByReferenceAsync(SqliteConnection connection, string reference)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE reference = @reference;";
        command.Parameters.AddWithValue("@reference", reference);
        return await ReadOneAsync(command);
    }

    private static async Task<PlanGrant?> ReadOneAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    private static async Task<List<PlanGrant>> ReadAllAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var grants = new List<PlanGrant>();
        while (await reader.ReadAsync())
        {
            grants.Add(Read(reader));
        }
        return grants;
    }

    private static PlanGrant Read(SqliteDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
        bool? Flag(int i) => reader.IsDBNull(i) ? null : reader.GetBoolean(i);

        return new PlanGrant(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            Number(3),
            Flag(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            Text(8),
            Text(9),
            reader.GetInt32(10),
            Text(11),
            Number(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            Number(15),
            Number(16),
            Number(17),
            Text(18),
            Text(19));
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        await InitializeAsync();
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // Creates the tables on first use, next to the codes'
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
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS plan_grants (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    identity TEXT NOT NULL,
                    plan_id TEXT NOT NULL,
                    quota_bytes INTEGER NULL,
                    public_web_presence INTEGER NULL,
                    source TEXT NOT NULL,
                    reference TEXT NOT NULL UNIQUE,
                    status TEXT NOT NULL,
                    host TEXT NULL,
                    host_hint TEXT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NULL,
                    next_attempt_at INTEGER NULL,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL,
                    applied_at INTEGER NULL,
                    subscription_id INTEGER NULL,
                    reapplied_at INTEGER NULL,
                    note TEXT NULL,
                    made_by TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_plan_grants_identity ON plan_grants(identity);
                CREATE INDEX IF NOT EXISTS ix_plan_grants_due ON plan_grants(status, next_attempt_at);
                CREATE TABLE IF NOT EXISTS plan_identity_hosts (
                    identity TEXT NOT NULL PRIMARY KEY,
                    host TEXT NOT NULL,
                    seen_at INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();

            // Grants made before plans were kept here lack what the plan gave
            await AddColumnIfMissingAsync(connection, "quota_bytes", "INTEGER NULL");
            await AddColumnIfMissingAsync(connection, "public_web_presence", "INTEGER NULL");
            // and when the host was last given it again, why and by whom it was made, and the subscription it is from
            await AddColumnIfMissingAsync(connection, "reapplied_at", "INTEGER NULL");
            await AddColumnIfMissingAsync(connection, "note", "TEXT NULL");
            await AddColumnIfMissingAsync(connection, "made_by", "TEXT NULL");
            await AddColumnIfMissingAsync(connection, "subscription_id", "INTEGER NULL");
            await SubscriptionStore.EnsureTablesAsync(connection);
            await MovePurchasesToSubscriptionsAsync(connection);

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    // Before subscriptions, a purchase was a grant with its price and term (price_months, price_amount_cents, ends_at).
    // Each identity whose latest grant is one becomes the owner and only member of a subscription for what is left of
    // it, paid by one invoice; earlier purchases keep their price in their note. Then those columns go.
    private static async Task MovePurchasesToSubscriptionsAsync(SqliteConnection connection)
    {
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('plan_grants') WHERE name = 'price_months';";
            if (Convert.ToInt64(await columns.ExecuteScalarAsync()) == 0)
            {
                return;
            }
        }

        // The oldest purchases had no end kept: they ran out their term's months after they were bought
        await AddColumnIfMissingAsync(connection, "ends_at", "INTEGER NULL");
        await using var transaction = connection.BeginTransaction(deferred: false);
        var purchases = new List<(long Id, string Identity, string PlanId, long Quota, bool Web, int Months, long Cents,
            long CreatedAt, long EndsAt, bool Current)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT g.id, g.identity, g.plan_id, COALESCE(g.quota_bytes, 0), COALESCE(g.public_web_presence, 0),
                       g.price_months, COALESCE(g.price_amount_cents, 0), g.created_at,
                       g.ends_at, g.id = (SELECT MAX(id) FROM plan_grants c WHERE c.identity = g.identity)
                FROM plan_grants g WHERE g.price_months IS NOT NULL;
                """;
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                purchases.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    reader.GetBoolean(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetInt64(7),
                    reader.IsDBNull(8) ? TermEnd(reader.GetInt64(7), reader.GetInt32(5)) : reader.GetInt64(8),
                    reader.GetBoolean(9)));
            }
        }

        foreach (var p in purchases)
        {
            var price = new PlanPrice(p.Months, p.Cents);
            await using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("@id", p.Id);
            command.Parameters.AddWithValue("@note", $"Bought, {price.Text}");
            if (!p.Current)
            {
                command.CommandText = "UPDATE plan_grants SET note = COALESCE(note, @note) WHERE id = @id;";
                await command.ExecuteNonQueryAsync();
                continue;
            }

            command.CommandText =
                """
                INSERT INTO subscriptions (owner, plan_id, seats, pool_bytes, public_web_presence, term_months,
                                           price_cents, auto_renew, status, period_start, period_end, created_at,
                                           updated_at, note)
                VALUES (@identity, @planId, 1, @quota, @web, @months, @cents, 0, 'active', @start, @end, @start, @start,
                        'Bought before subscriptions');
                INSERT INTO subscription_members (subscription_id, identity, status, quota_bytes, invited_at, joined_at)
                VALUES (last_insert_rowid(), @identity, 'active', @quota, @start, @start);
                INSERT INTO invoices (subscription_id, plan_id, term_months, amount_cents, period_start, period_end,
                                      status, due_at, created_at, paid_at, paid_by)
                SELECT subscription_id, @planId, @months, @cents, @start, @end, 'paid', @start, @start, @start, 'migration'
                FROM subscription_members WHERE id = last_insert_rowid();
                UPDATE plan_grants SET note = COALESCE(note, @note),
                    subscription_id = (SELECT subscription_id FROM invoices WHERE id = last_insert_rowid())
                WHERE id = @id;
                """;
            command.Parameters.AddWithValue("@identity", p.Identity);
            command.Parameters.AddWithValue("@planId", p.PlanId);
            command.Parameters.AddWithValue("@quota", p.Quota);
            command.Parameters.AddWithValue("@web", p.Web);
            command.Parameters.AddWithValue("@months", p.Months);
            command.Parameters.AddWithValue("@cents", p.Cents);
            command.Parameters.AddWithValue("@start", p.CreatedAt);
            command.Parameters.AddWithValue("@end", p.EndsAt);
            await command.ExecuteNonQueryAsync();
        }

        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText =
                """
                ALTER TABLE plan_grants DROP COLUMN price_months;
                ALTER TABLE plan_grants DROP COLUMN price_amount_cents;
                ALTER TABLE plan_grants DROP COLUMN ends_at;
                """;
            await drop.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    // Whether it added it
    private static async Task<bool> AddColumnIfMissingAsync(SqliteConnection connection, string column, string definition)
    {
        await using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('plan_grants') WHERE name = @column;";
        columns.Parameters.AddWithValue("@column", column);
        if (Convert.ToInt64(await columns.ExecuteScalarAsync()) == 0)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE plan_grants ADD COLUMN {column} {definition};";
            await alter.ExecuteNonQueryAsync();
            return true;
        }
        return false;
    }
}
