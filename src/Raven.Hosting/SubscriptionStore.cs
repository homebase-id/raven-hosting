using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Raven.Hosting;

/// <summary>
/// A paid plan someone holds for a term: who pays for it (the owner), what it is, what each term costs, and the term
/// paid for now. The identities it covers are its members (<see cref="SubscriptionMember"/>); the owner need not be one
/// of them. Tracked here, not by a payment provider: a term is paid when its <see cref="Invoice"/> is marked paid, by an
/// admin, the test checkout, or later a provider.
/// </summary>
/// <param name="Seats">How many identities it covers, from the plan when it was bought or last changed</param>
/// <param name="PoolBytes">The plan's quota then: on a group plan, what the owner shares out between its members</param>
/// <param name="AutoRenew">Whether the next term is billed when this one runs out; off, it runs out and goes to its
/// grace, then its members go back to the free plan</param>
/// <param name="Status"><c>active</c>, or <c>ended</c> once its grace is over (or it was replaced)</param>
/// <param name="PeriodStart">When the term paid for began (unix ms)</param>
/// <param name="PeriodEnd">When it runs out (unix ms)</param>
/// <param name="CanceledAt">When auto-renew was last turned off; null while it is on</param>
public record Subscription(
    long Id,
    string Owner,
    string PlanId,
    int Seats,
    long PoolBytes,
    bool PublicWebPresence,
    int TermMonths,
    long PriceCents,
    bool AutoRenew,
    string Status,
    long PeriodStart,
    long PeriodEnd,
    long? CanceledAt,
    long? EndedAt,
    string? EndedReason,
    long CreatedAt,
    long UpdatedAt,
    string? MadeBy,
    string? Note)
{
    public bool IsActive => Status == SubscriptionStatus.Active;

    public bool IsGroup => Seats > 1;

    public PlanPrice Price => new(TermMonths, PriceCents);

    /// <summary>When its grace ends and its members go back to the free plan, unless the next term is paid</summary>
    public long GraceEndsAt => PeriodEnd + (long)PlanService.Grace.TotalMilliseconds;

    /// <summary><c>active</c>; <c>grace</c> once its term ran out, until it is paid or its grace is over; <c>ended</c></summary>
    public string State(long now) => !IsActive ? SubscriptionStatus.Ended : now >= PeriodEnd ? "grace" : SubscriptionStatus.Active;
}

public static class SubscriptionStatus
{
    public const string Active = "active";
    public const string Ended = "ended";
}

/// <summary>
/// An identity a subscription covers, or was asked to cover. The owner names it; it joins with its link, signed in as
/// itself. Its quota is its share of the subscription's pool, set by the owner.
/// </summary>
/// <param name="Status"><see cref="MemberStatus"/>: invited, active, declined, removed, left or ended</param>
/// <param name="QuotaBytes">Its share of the pool; held for it from the moment it is invited</param>
/// <param name="InviteToken">The secret in its join link; null once it joined or the invitation ended. The link only
/// works signed in as the identity named, so it is kept to show the owner again, not hashed</param>
/// <param name="EndedAt">When it was removed or left: it keeps the plan, and its share and seat stay held, for
/// <see cref="PlanService.Grace"/> after</param>
/// <param name="DroppedAt">When it stopped holding a seat: its grace was over, it declined, its invitation was taken
/// back, or it joined another plan</param>
public record SubscriptionMember(
    long Id,
    long SubscriptionId,
    string Identity,
    string Status,
    long QuotaBytes,
    string? InviteToken,
    long InvitedAt,
    long? InviteExpiresAt,
    long? JoinedAt,
    long? EndedAt,
    long? DroppedAt,
    string? EndedBy)
{
    /// <summary>Whether it takes one of the seats and its share of the pool</summary>
    public bool HoldsSeat => Status is MemberStatus.Invited or MemberStatus.Active || IsLeaving;

    /// <summary>Removed or left, but still in its grace: it keeps the plan until <see cref="KeepsUntil"/></summary>
    public bool IsLeaving => Status is MemberStatus.Removed or MemberStatus.Left && DroppedAt == null;

    public long? KeepsUntil => IsLeaving ? EndedAt + (long)PlanService.Grace.TotalMilliseconds : null;

    public bool InviteExpired(long now) => InviteExpiresAt is { } expires && expires <= now;
}

public static class MemberStatus
{
    public const string Invited = "invited";
    public const string Active = "active";
    public const string Declined = "declined";
    public const string Removed = "removed";
    public const string Left = "left";

    /// <summary>Its subscription ended</summary>
    public const string Ended = "ended";
}

/// <summary>
/// One term of a subscription, to be paid: made when it is bought, and before each renewal. Paid, it becomes the
/// subscription's term.
/// </summary>
/// <param name="Status"><c>open</c> until paid; <c>paid</c>; <c>void</c> when it will not be (auto-renew turned off,
/// the plan changed, the subscription ended)</param>
/// <param name="DueAt">When it is due: when the term before it runs out</param>
/// <param name="PaidBy">How it was paid: <c>test-checkout</c>, <c>admin:&lt;identity&gt;</c>, <c>migration</c>, later
/// a provider</param>
/// <param name="Reference">A receipt, bank transfer note or provider's charge id; may be empty</param>
public record Invoice(
    long Id,
    long SubscriptionId,
    string PlanId,
    int TermMonths,
    long AmountCents,
    long PeriodStart,
    long PeriodEnd,
    string Status,
    long DueAt,
    long CreatedAt,
    long? PaidAt,
    string? PaidBy,
    string? Reference)
{
    public PlanPrice Price => new(TermMonths, AmountCents);
}

public static class InvoiceStatus
{
    public const string Open = "open";
    public const string Paid = "paid";
    public const string Void = "void";
}

/// <summary>
/// Subscriptions, their members and their invoices, in the same SQLite file as the grants. What each member is given is
/// still a grant (<see cref="PlanGrantStore"/>), with the subscription's Id: the grants are what the hosts are told, and
/// each identity's history.
/// </summary>
public class SubscriptionStore
{
    /// <summary>The smallest share of a group plan's pool a member can have</summary>
    public const long MinimumShareBytes = 1L << 30;

    /// <summary>The most identities one plan can cover</summary>
    public const int MostSeats = 50;

    /// <summary>How long a join link works</summary>
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SubscriptionStore(string databasePath)
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
    // Subscriptions
    //

    public async Task<Subscription> CreateAsync(string owner, Plan plan, PlanPrice price, bool autoRenew, long now,
        string? madeBy, string? note)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO subscriptions (owner, plan_id, seats, pool_bytes, public_web_presence, term_months, price_cents,
                                       auto_renew, status, period_start, period_end, created_at, updated_at, made_by, note)
            VALUES (@owner, @planId, @seats, @pool, @web, @months, @cents, @autoRenew, 'active', @now, @end, @now, @now,
                    @madeBy, @note)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("@owner", owner);
        command.Parameters.AddWithValue("@planId", plan.Id);
        command.Parameters.AddWithValue("@seats", plan.Seats);
        command.Parameters.AddWithValue("@pool", plan.QuotaBytes);
        command.Parameters.AddWithValue("@web", plan.PublicWebPresence);
        command.Parameters.AddWithValue("@months", price.Months);
        command.Parameters.AddWithValue("@cents", price.AmountCents);
        command.Parameters.AddWithValue("@autoRenew", autoRenew);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@end", PlanGrantStore.TermEnd(now, price.Months));
        command.Parameters.AddWithValue("@madeBy", (object?)madeBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim());
        var id = (long)(await command.ExecuteScalarAsync())!;
        return (await GetAsync(id))!;
    }

    public async Task<Subscription?> GetAsync(long id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSubscription + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return (await ReadSubscriptionsAsync(command)).FirstOrDefault();
    }

    /// <summary>The active subscription the identity pays for; null when it has none. Someone owns one at a time.</summary>
    public async Task<Subscription?> ActiveOwnedByAsync(string owner)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSubscription + " WHERE owner = @owner AND status = 'active' ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("@owner", owner);
        return (await ReadSubscriptionsAsync(command)).FirstOrDefault();
    }

    /// <summary>Subscriptions, newest first; only active ones when asked</summary>
    public async Task<List<Subscription>> ListAsync(bool activeOnly)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSubscription + (activeOnly ? " WHERE status = 'active'" : "") + " ORDER BY id DESC;";
        return await ReadSubscriptionsAsync(command);
    }

    /// <summary>Every subscription the identity pays or paid for, newest first</summary>
    public async Task<List<Subscription>> OwnedByAsync(string owner)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSubscription + " WHERE owner = @owner ORDER BY id DESC;";
        command.Parameters.AddWithValue("@owner", owner);
        return await ReadSubscriptionsAsync(command);
    }

    /// <summary>Active subscriptions with auto-renew on whose term runs out before <paramref name="before"/> and that
    /// have no invoice for their next term yet</summary>
    public async Task<List<Subscription>> DueForInvoiceAsync(long before)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSubscription +
                              """
                               WHERE status = 'active' AND auto_renew = 1 AND period_end < @before
                                 AND NOT EXISTS (SELECT 1 FROM invoices i WHERE i.subscription_id = subscriptions.id
                                                 AND i.period_start = subscriptions.period_end AND i.status <> 'void')
                               ORDER BY period_end;
                              """;
        command.Parameters.AddWithValue("@before", before);
        return await ReadSubscriptionsAsync(command);
    }

    /// <summary>Active subscriptions whose term ran out before <paramref name="before"/></summary>
    public async Task<List<Subscription>> RanOutAsync(long before)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSubscription + " WHERE status = 'active' AND period_end < @before ORDER BY period_end;";
        command.Parameters.AddWithValue("@before", before);
        return await ReadSubscriptionsAsync(command);
    }

    /// <summary>Puts it on another plan and term, paid from now: what was left of the term before is not carried over</summary>
    public Task<Subscription?> ChangePlanAsync(long id, Plan plan, PlanPrice price, long now) =>
        UpdateSubscriptionAsync(id,
            "plan_id = @planId, seats = @seats, pool_bytes = @pool, public_web_presence = @web, term_months = @months, " +
            "price_cents = @cents, period_start = @now, period_end = @end", now,
            ("@planId", plan.Id), ("@seats", plan.Seats), ("@pool", plan.QuotaBytes), ("@web", plan.PublicWebPresence),
            ("@months", price.Months), ("@cents", price.AmountCents), ("@end", PlanGrantStore.TermEnd(now, price.Months)));

    public Task<Subscription?> SetPeriodAsync(long id, long start, long end, long now) =>
        UpdateSubscriptionAsync(id, "period_start = @start, period_end = @end", now, ("@start", start), ("@end", end));

    public Task<Subscription?> SetAutoRenewAsync(long id, bool on, long now) =>
        UpdateSubscriptionAsync(id, "auto_renew = @on, canceled_at = CASE WHEN @on = 1 THEN NULL ELSE @now END", now,
            ("@on", on));

    public Task<Subscription?> EndAsync(long id, string reason, long now) =>
        UpdateSubscriptionAsync(id, "status = 'ended', ended_at = @now, ended_reason = @reason, auto_renew = 0", now,
            ("@reason", reason));

    //
    // Members
    //

    /// <summary>Names an identity on a subscription: invited, with a join link that works for
    /// <see cref="InviteLifetime"/>; or active straight away (the owner taking a seat, a plan for one)</summary>
    public async Task<SubscriptionMember> AddMemberAsync(long subscriptionId, string identity, long quotaBytes,
        bool active, long now)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO subscription_members (subscription_id, identity, status, quota_bytes, invite_token, invited_at,
                                              invite_expires_at, joined_at)
            VALUES (@subscription, @identity, @status, @quota, @token, @now, @expires, @joined)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("@subscription", subscriptionId);
        command.Parameters.AddWithValue("@identity", identity);
        command.Parameters.AddWithValue("@status", active ? MemberStatus.Active : MemberStatus.Invited);
        command.Parameters.AddWithValue("@quota", quotaBytes);
        command.Parameters.AddWithValue("@token", active ? DBNull.Value : NewToken());
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@expires", active ? DBNull.Value : now + (long)InviteLifetime.TotalMilliseconds);
        command.Parameters.AddWithValue("@joined", active ? now : DBNull.Value);
        var id = (long)(await command.ExecuteScalarAsync())!;
        return (await GetMemberAsync(id))!;
    }

    public async Task<SubscriptionMember?> GetMemberAsync(long id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectMember + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return (await ReadMembersAsync(command)).FirstOrDefault();
    }

    /// <summary>The invitation with this join link's token; null when there is none (or no longer one)</summary>
    public async Task<SubscriptionMember?> MemberByTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
        {
            return null;
        }
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectMember + " WHERE invite_token = @token;";
        command.Parameters.AddWithValue("@token", token);
        return (await ReadMembersAsync(command)).FirstOrDefault();
    }

    /// <summary>Everyone a subscription names, past and present, in the order they were named</summary>
    public async Task<List<SubscriptionMember>> MembersAsync(long subscriptionId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectMember + " WHERE subscription_id = @subscription ORDER BY id;";
        command.Parameters.AddWithValue("@subscription", subscriptionId);
        return await ReadMembersAsync(command);
    }

    /// <summary>Every place the identity is or was named, newest first</summary>
    public async Task<List<SubscriptionMember>> MembershipsOfAsync(string identity)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectMember + " WHERE identity = @identity ORDER BY id DESC;";
        command.Parameters.AddWithValue("@identity", identity);
        return await ReadMembersAsync(command);
    }

    /// <summary>Every member that holds a seat now, by identity: who is covered by which subscription</summary>
    public async Task<List<SubscriptionMember>> HoldingAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectMember +
                              " WHERE status IN ('invited', 'active') OR (status IN ('removed', 'left') AND dropped_at IS NULL)" +
                              " ORDER BY id;";
        return await ReadMembersAsync(command);
    }

    /// <summary>Members removed, or who left, whose grace ended before <paramref name="before"/></summary>
    public async Task<List<SubscriptionMember>> LeavingDueAsync(long before)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectMember +
                              " WHERE status IN ('removed', 'left') AND dropped_at IS NULL AND ended_at < @before ORDER BY ended_at;";
        command.Parameters.AddWithValue("@before", before);
        return await ReadMembersAsync(command);
    }

    public Task<SubscriptionMember?> JoinAsync(long id, long now) =>
        UpdateMemberAsync(id, "status = 'active', joined_at = @now, invite_token = NULL, invite_expires_at = NULL", now);

    /// <summary>Ends a member: <c>removed</c> or <c>left</c> keep the plan through their grace; an invitation, or
    /// <c>declined</c> and <c>ended</c>, stop holding a seat at once</summary>
    public Task<SubscriptionMember?> EndMemberAsync(long id, string status, string? by, bool dropNow, long now) =>
        UpdateMemberAsync(id,
            "status = @status, ended_at = @now, ended_by = @by, invite_token = NULL, invite_expires_at = NULL" +
            (dropNow ? ", dropped_at = @now" : ""), now, ("@status", status), ("@by", (object?)by ?? DBNull.Value));

    /// <summary>A member stops holding its seat: its grace is over, or it joined another plan</summary>
    public Task<SubscriptionMember?> DropAsync(long id, long now) =>
        UpdateMemberAsync(id, "dropped_at = COALESCE(dropped_at, @now)", now);

    public Task<SubscriptionMember?> SetShareAsync(long id, long quotaBytes, long now) =>
        UpdateMemberAsync(id, "quota_bytes = @quota", now, ("@quota", quotaBytes));

    /// <summary>A new join link for an invitation, working for <see cref="InviteLifetime"/>; the old one stops working</summary>
    public Task<SubscriptionMember?> NewLinkAsync(long id, long now) =>
        UpdateMemberAsync(id, "invite_token = @token, invite_expires_at = @expires", now, ("@token", NewToken()),
            ("@expires", now + (long)InviteLifetime.TotalMilliseconds));

    //
    // Invoices
    //

    public async Task<Invoice> AddInvoiceAsync(long subscriptionId, string planId, PlanPrice price, long periodStart,
        long dueAt, long now)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO invoices (subscription_id, plan_id, term_months, amount_cents, period_start, period_end, status,
                                  due_at, created_at)
            VALUES (@subscription, @planId, @months, @cents, @start, @end, 'open', @due, @now)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("@subscription", subscriptionId);
        command.Parameters.AddWithValue("@planId", planId);
        command.Parameters.AddWithValue("@months", price.Months);
        command.Parameters.AddWithValue("@cents", price.AmountCents);
        command.Parameters.AddWithValue("@start", periodStart);
        command.Parameters.AddWithValue("@end", PlanGrantStore.TermEnd(periodStart, price.Months));
        command.Parameters.AddWithValue("@due", dueAt);
        command.Parameters.AddWithValue("@now", now);
        var id = (long)(await command.ExecuteScalarAsync())!;
        return (await GetInvoiceAsync(id))!;
    }

    public async Task<Invoice?> GetInvoiceAsync(long id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectInvoice + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return (await ReadInvoicesAsync(command)).FirstOrDefault();
    }

    /// <summary>A subscription's invoices, newest first</summary>
    public async Task<List<Invoice>> InvoicesAsync(long subscriptionId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectInvoice + " WHERE subscription_id = @subscription ORDER BY id DESC;";
        command.Parameters.AddWithValue("@subscription", subscriptionId);
        return await ReadInvoicesAsync(command);
    }

    /// <summary>Every open invoice, by subscription</summary>
    public async Task<Dictionary<long, Invoice>> OpenInvoicesAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectInvoice + " WHERE status = 'open' ORDER BY id;";
        return (await ReadInvoicesAsync(command)).GroupBy(i => i.SubscriptionId).ToDictionary(g => g.Key, g => g.Last());
    }

    /// <summary>Marks an open invoice paid; null when there is no such open one</summary>
    public async Task<Invoice?> MarkPaidAsync(long id, string paidBy, string? reference, long now)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE invoices SET status = 'paid', paid_at = @now, paid_by = @by, reference = @reference " +
            "WHERE id = @id AND status = 'open';" + SelectInvoice + " WHERE id = @id AND changes() = 1;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@by", paidBy);
        command.Parameters.AddWithValue("@reference", string.IsNullOrWhiteSpace(reference) ? DBNull.Value : reference.Trim());
        return (await ReadInvoicesAsync(command)).FirstOrDefault();
    }

    public async Task VoidOpenInvoicesAsync(long subscriptionId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE invoices SET status = 'void' WHERE subscription_id = @subscription AND status = 'open';";
        command.Parameters.AddWithValue("@subscription", subscriptionId);
        await command.ExecuteNonQueryAsync();
    }

    //

    private const string SelectSubscription =
        """
        SELECT id, owner, plan_id, seats, pool_bytes, public_web_presence, term_months, price_cents, auto_renew, status,
               period_start, period_end, canceled_at, ended_at, ended_reason, created_at, updated_at, made_by, note
        FROM subscriptions
        """;

    private const string SelectMember =
        """
        SELECT id, subscription_id, identity, status, quota_bytes, invite_token, invited_at, invite_expires_at, joined_at,
               ended_at, dropped_at, ended_by
        FROM subscription_members
        """;

    private const string SelectInvoice =
        """
        SELECT id, subscription_id, plan_id, term_months, amount_cents, period_start, period_end, status, due_at,
               created_at, paid_at, paid_by, reference
        FROM invoices
        """;

    // 32 random bytes as URL-safe base64: a join link's secret
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<Subscription?> UpdateSubscriptionAsync(long id, string set, long now,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE subscriptions SET {set}, updated_at = @now WHERE id = @id;" +
                              SelectSubscription + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", now);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (await ReadSubscriptionsAsync(command)).FirstOrDefault();
    }

    private async Task<SubscriptionMember?> UpdateMemberAsync(long id, string set, long now,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE subscription_members SET {set} WHERE id = @id;" + SelectMember + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", now);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (await ReadMembersAsync(command)).FirstOrDefault();
    }

    private static async Task<List<Subscription>> ReadSubscriptionsAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<Subscription>();
        while (await reader.ReadAsync())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
            list.Add(new Subscription(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.GetInt64(4), reader.GetBoolean(5), reader.GetInt32(6), reader.GetInt64(7), reader.GetBoolean(8),
                reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11), Number(12), Number(13), Text(14),
                reader.GetInt64(15), reader.GetInt64(16), Text(17), Text(18)));
        }
        return list;
    }

    private static async Task<List<SubscriptionMember>> ReadMembersAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<SubscriptionMember>();
        while (await reader.ReadAsync())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
            list.Add(new SubscriptionMember(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), Text(5), reader.GetInt64(6), Number(7), Number(8), Number(9),
                Number(10), Text(11)));
        }
        return list;
    }

    private static async Task<List<Invoice>> ReadInvoicesAsync(SqliteCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<Invoice>();
        while (await reader.ReadAsync())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
            list.Add(new Invoice(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8),
                reader.GetInt64(9), Number(10), Text(11), Text(12)));
        }
        return list;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        await InitializeAsync();
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

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
            await EnsureTablesAsync(connection);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    /// <summary>
    /// Creates the subscription tables if they are not there. The grant store calls it too, before it moves purchases
    /// made before subscriptions into them, whichever store opens the database first.
    /// </summary>
    internal static async Task EnsureTablesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS subscriptions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                owner TEXT NOT NULL,
                plan_id TEXT NOT NULL,
                seats INTEGER NOT NULL,
                pool_bytes INTEGER NOT NULL,
                public_web_presence INTEGER NOT NULL,
                term_months INTEGER NOT NULL,
                price_cents INTEGER NOT NULL,
                auto_renew INTEGER NOT NULL,
                status TEXT NOT NULL,
                period_start INTEGER NOT NULL,
                period_end INTEGER NOT NULL,
                canceled_at INTEGER NULL,
                ended_at INTEGER NULL,
                ended_reason TEXT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                made_by TEXT NULL,
                note TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_subscriptions_owner ON subscriptions(owner);
            CREATE INDEX IF NOT EXISTS ix_subscriptions_status ON subscriptions(status, period_end);
            CREATE TABLE IF NOT EXISTS subscription_members (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                subscription_id INTEGER NOT NULL REFERENCES subscriptions(id),
                identity TEXT NOT NULL,
                status TEXT NOT NULL,
                quota_bytes INTEGER NOT NULL,
                invite_token TEXT NULL UNIQUE,
                invited_at INTEGER NOT NULL,
                invite_expires_at INTEGER NULL,
                joined_at INTEGER NULL,
                ended_at INTEGER NULL,
                dropped_at INTEGER NULL,
                ended_by TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_subscription_members_subscription ON subscription_members(subscription_id);
            CREATE INDEX IF NOT EXISTS ix_subscription_members_identity ON subscription_members(identity);
            -- An identity is an active member of one subscription at most
            CREATE UNIQUE INDEX IF NOT EXISTS ux_subscription_members_active ON subscription_members(identity)
                WHERE status = 'active';
            CREATE TABLE IF NOT EXISTS invoices (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                subscription_id INTEGER NOT NULL REFERENCES subscriptions(id),
                plan_id TEXT NOT NULL,
                term_months INTEGER NOT NULL,
                amount_cents INTEGER NOT NULL,
                period_start INTEGER NOT NULL,
                period_end INTEGER NOT NULL,
                status TEXT NOT NULL,
                due_at INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                paid_at INTEGER NULL,
                paid_by TEXT NULL,
                reference TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_invoices_subscription ON invoices(subscription_id);
            """;
        await command.ExecuteNonQueryAsync();
    }
}
