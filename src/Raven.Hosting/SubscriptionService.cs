namespace Raven.Hosting;

/// <summary>
/// Something the subscription rules refuse, with a message for the person: a seat too many, shares over the pool, a
/// link that no longer works. The pages show it; the API answers 400.
/// </summary>
public class SubscriptionRefusedException(string message) : InvalidInvitationCodeRequestException(message);

/// <summary>
/// A subscription with everyone it names and its invoices: what /account and the admin pages show
/// </summary>
/// <param name="PlanName">The plan's name; its Id when it is no longer a plan</param>
public record SubscriptionView(Subscription Subscription, List<SubscriptionMember> Members, List<Invoice> Invoices,
    string PlanName)
{
    /// <summary>Who holds a seat: invited, active, or leaving in their grace</summary>
    public List<SubscriptionMember> Holding => Members.Where(m => m.HoldsSeat).ToList();

    public long AssignedBytes => Holding.Sum(m => m.QuotaBytes);

    public long UnassignedBytes => Math.Max(0, Subscription.PoolBytes - AssignedBytes);

    public int SeatsLeft => Math.Max(0, Subscription.Seats - Holding.Count);

    public Invoice? OpenInvoice => Invoices.FirstOrDefault(i => i.Status == InvoiceStatus.Open);

    /// <summary>The owner's own seat, active or leaving; null when they do not use one</summary>
    public SubscriptionMember? OwnerSeat =>
        Members.FirstOrDefault(m => m.Identity == Subscription.Owner && (m.Status == MemberStatus.Active || m.IsLeaving));
}

/// <summary>
/// The rules of paid subscriptions, tracked here rather than by a payment provider (README, "Subscriptions"):
/// <list type="bullet">
/// <item>Buying a plan makes a subscription the buyer owns, paid for one term by an invoice. Someone owns one at a
/// time: buying again changes its plan, from now. Nothing paid for is lost: what was left of the old term, by its value,
/// is added to the new one as extra time (<see cref="CarryOver"/>).</item>
/// <item>It covers its members, up to the plan's seats. The owner is always one of them: they cannot leave their own
/// plan, nor join someone else's group plan while theirs runs. Each member gets its share of the pool; on a plan for
/// one, the whole of it. Shares are at least <see cref="SubscriptionStore.MinimumShareBytes"/>.</item>
/// <item>The owner names members; each joins with its link, signed in as itself. An identity is an active member of one
/// subscription at most: joining another leaves the one it was on, and turns auto-renew off on a plan for one it owns.</item>
/// <item>A member removed, or who leaves, keeps the plan and its seat for <see cref="PlanService.Grace"/>, then goes to
/// the free plan.</item>
/// <item>With auto-renew on, an invoice for the next term is made <see cref="InvoiceAhead"/> before the term runs out;
/// paid, the term moves on. Unpaid, or with auto-renew off, the subscription runs out, keeps going through its grace,
/// then ends and its members go to the free plan.</item>
/// </list>
/// What each member gets is set on its host as a grant, with the subscription's Id (<see cref="PlanService"/>).
/// </summary>
public class SubscriptionService(
    SubscriptionStore store,
    PlanGrantStore grants,
    PlanService plans,
    PlanStore planStore,
    TimeProvider time,
    ILogger<SubscriptionService> logger)
{
    /// <summary>How long before a term runs out the invoice for the next one is made</summary>
    public static readonly TimeSpan InvoiceAhead = TimeSpan.FromDays(7);

    // One change at a time, so two pages cannot both take the last seat
    private readonly SemaphoreSlim _lock = new(1, 1);

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    //
    // Reading
    //

    public async Task<SubscriptionView?> ViewAsync(long id)
    {
        var subscription = await store.GetAsync(id);
        return subscription == null ? null : await ViewOfAsync(subscription);
    }

    public async Task<SubscriptionView> ViewOfAsync(Subscription subscription) =>
        new(subscription, await store.MembersAsync(subscription.Id), await store.InvoicesAsync(subscription.Id),
            (await planStore.GetAsync(subscription.PlanId))?.Name ?? subscription.PlanId);

    /// <summary>The active subscription the identity pays for, if any</summary>
    public async Task<SubscriptionView?> OwnedByAsync(string owner) =>
        await store.ActiveOwnedByAsync(owner) is { } subscription ? await ViewOfAsync(subscription) : null;

    /// <summary>The subscription that covers the identity now, and its place on it: active, or leaving in its grace</summary>
    public async Task<(SubscriptionView View, SubscriptionMember Member)?> CoveringAsync(string identity)
    {
        foreach (var member in await store.MembershipsOfAsync(identity))
        {
            if ((member.Status == MemberStatus.Active || member.IsLeaving) &&
                await store.GetAsync(member.SubscriptionId) is { IsActive: true } subscription)
            {
                return (await ViewOfAsync(subscription), member);
            }
        }
        return null;
    }

    /// <summary>Invitations waiting for the identity, on subscriptions still running</summary>
    public async Task<List<(SubscriptionView View, SubscriptionMember Member)>> InvitationsForAsync(string identity)
    {
        var found = new List<(SubscriptionView, SubscriptionMember)>();
        foreach (var member in await store.MembershipsOfAsync(identity))
        {
            if (member.Status == MemberStatus.Invited && !member.InviteExpired(Now) &&
                await store.GetAsync(member.SubscriptionId) is { IsActive: true } subscription)
            {
                found.Add((await ViewOfAsync(subscription), member));
            }
        }
        return found;
    }

    /// <summary>For every identity a subscription covers now, its place on it and the subscription</summary>
    public async Task<Dictionary<string, (Subscription Subscription, SubscriptionMember Member)>> CoverageAsync()
    {
        var subscriptions = (await store.ListAsync(activeOnly: true)).ToDictionary(s => s.Id);
        var coverage = new Dictionary<string, (Subscription, SubscriptionMember)>();
        foreach (var member in await store.HoldingAsync())
        {
            if (member.Status != MemberStatus.Invited && subscriptions.TryGetValue(member.SubscriptionId, out var s))
            {
                coverage[member.Identity] = (s, member);
            }
        }
        return coverage;
    }

    //
    // Buying
    //

    /// <summary>
    /// The owner buys the plan for the term, paid now (the test checkout, an admin): a new subscription, or the plan of
    /// the one they own changed, from now. The owner always takes a seat. Changing to a plan with fewer seats than
    /// members, or less storage than their shares, is refused.
    /// </summary>
    public async Task<Subscription> BuyAsync(string owner, Plan plan, PlanPrice price, string? host, string paidBy,
        string? reference = null, string? madeBy = null)
    {
        await _lock.WaitAsync();
        try
        {
            var existing = await store.ActiveOwnedByAsync(owner);
            if (existing == null)
            {
                var created = await store.CreateAsync(owner, plan, price, autoRenew: true, Now, madeBy, null);
                await PaidInvoiceAsync(created, price, created.PeriodStart, paidBy, reference);
                logger.LogInformation("{owner} bought {planId} for {term} (subscription {id}, {paidBy})", owner, plan.Id,
                    BillingTerm.Name(price.Months), created.Id, paidBy);
                await AddSeatAsync(created, owner, ShareForNewSeat(await ViewOfAsync(created)), host,
                    $"Bought {plan.Name}, {price.Text}");
                return created;
            }

            // A plan change: the members it has must fit the new one
            var view = await ViewOfAsync(existing);
            var holding = view.Holding;
            var addsOwner = holding.All(m => m.Identity != owner);
            var seatsNeeded = holding.Count + (addsOwner ? 1 : 0);
            if (seatsNeeded > plan.Seats)
            {
                throw new SubscriptionRefusedException(
                    $"{plan.Name} is for {People(plan.Seats)}, and your plan names {People(holding.Count)}. Remove some first.");
            }
            if (plan.IsGroup && view.AssignedBytes + (addsOwner ? MinimumShare : 0) > plan.QuotaBytes)
            {
                throw new SubscriptionRefusedException(
                    $"Your members' shares add up to {StorageSize.Format(view.AssignedBytes)}, and {plan.Name} has " +
                    $"{plan.Storage}. Lower them first.");
            }

            // What was left of the term paid for becomes extra time on the new plan, at the new plan's rate
            var carry = CarryOver(existing, price, Now);
            var oldName = (await planStore.GetAsync(existing.PlanId))?.Name ?? existing.PlanId;
            var changed = (await store.ChangePlanAsync(existing.Id, plan, price, Now))!;
            if (carry.ExtraMs > 0)
            {
                changed = (await store.SetPeriodAsync(changed.Id, changed.PeriodStart, changed.PeriodEnd + carry.ExtraMs, Now))!;
                logger.LogInformation("Subscription {id}: {value} left of {from} carried over to {planId} as {extra}",
                    existing.Id, Money.Format(carry.ValueCents), existing.PlanId, plan.Id, Describe(carry.ExtraMs));
            }
            await store.VoidOpenInvoicesAsync(existing.Id);
            await PaidInvoiceAsync(changed, price, changed.PeriodStart, paidBy, reference);
            logger.LogInformation("{owner} changed subscription {id} from {from} to {planId} for {term} ({paidBy})",
                owner, existing.Id, existing.PlanId, plan.Id, BillingTerm.Name(price.Months), paidBy);

            // On a plan for one, its one member has the whole of it
            if (!plan.IsGroup && holding.Count == 1)
            {
                await store.SetShareAsync(holding[0].Id, plan.QuotaBytes, Now);
            }
            foreach (var member in await store.MembersAsync(changed.Id))
            {
                if (member.Status == MemberStatus.Active || member.IsLeaving)
                {
                    await GrantAsync(changed, member,
                        $"Plan changed to {plan.Name}" + (carry.ExtraMs > 0 ? $"; {Describe(carry.ExtraMs)} carried over from {oldName}" : ""),
                        member.Identity == owner ? host : null);
                }
            }
            if (addsOwner)
            {
                await AddSeatAsync(changed, owner, ShareForNewSeat(await ViewOfAsync(changed)), host,
                    $"Bought {plan.Name}, {price.Text}");
            }
            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    //
    // Members
    //

    /// <summary>
    /// The owner names an identity on their subscription. Its share is what was asked, or by default an equal part of
    /// what is unassigned between the seats left. The owner naming themselves takes the seat at once; anyone else is
    /// invited, with a link to join.
    /// </summary>
    public async Task<SubscriptionMember> AddMemberAsync(long subscriptionId, string owner, string? identity,
        long? quotaBytes, string? host = null)
    {
        var normalized = DomainNames.Normalize(identity) ??
                         throw new SubscriptionRefusedException($"'{identity}' is not a Homebase identity, like frodo.homebase.id");
        await _lock.WaitAsync();
        try
        {
            var view = await OwnViewAsync(subscriptionId, owner);
            if (view.Holding.Any(m => m.Identity == normalized))
            {
                throw new SubscriptionRefusedException($"{normalized} is already on your plan.");
            }
            if (view.SeatsLeft == 0)
            {
                throw new SubscriptionRefusedException(
                    $"All {view.Subscription.Seats} seats are taken. Remove someone first, or choose a bigger plan.");
            }

            var share = view.Subscription.IsGroup ? quotaBytes ?? ShareForNewSeat(view) : view.Subscription.PoolBytes;
            CheckShare(share);
            if (share > view.UnassignedBytes)
            {
                throw new SubscriptionRefusedException(
                    $"Only {StorageSize.Format(view.UnassignedBytes)} is unassigned. Lower someone's share first.");
            }

            if (normalized == owner)
            {
                return await AddSeatAsync(view.Subscription, owner, share, host, "Took a seat on their own plan");
            }
            var member = await store.AddMemberAsync(subscriptionId, normalized, share, active: false, Now);
            logger.LogInformation("{owner} invited {identity} to subscription {id} with {share}", owner, normalized,
                subscriptionId, StorageSize.Format(share));
            return member;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A new join link for an invitation; the old one stops working</summary>
    public async Task<SubscriptionMember> NewLinkAsync(long subscriptionId, string owner, long memberId)
    {
        var view = await OwnViewAsync(subscriptionId, owner);
        var member = view.Members.FirstOrDefault(m => m.Id == memberId && m.Status == MemberStatus.Invited) ??
                     throw new SubscriptionRefusedException("That invitation is no longer open.");
        return (await store.NewLinkAsync(member.Id, Now))!;
    }

    /// <summary>
    /// The owner takes someone off their plan: an invitation ends at once; a member keeps the plan for its grace
    /// </summary>
    public async Task<SubscriptionMember> RemoveAsync(long subscriptionId, string owner, long memberId)
    {
        await _lock.WaitAsync();
        try
        {
            var view = await OwnViewAsync(subscriptionId, owner);
            var member = view.Members.FirstOrDefault(m => m.Id == memberId) ??
                         throw new SubscriptionRefusedException("They are not on your plan.");
            if (member.Identity == owner && member.Status == MemberStatus.Active)
            {
                throw new SubscriptionRefusedException(OwnerStays);
            }
            var removed = member.Status switch
            {
                MemberStatus.Invited => await store.EndMemberAsync(member.Id, MemberStatus.Removed, owner, dropNow: true, Now),
                MemberStatus.Active => await store.EndMemberAsync(member.Id, MemberStatus.Removed, owner, dropNow: false, Now),
                _ => throw new SubscriptionRefusedException($"{member.Identity} is already off your plan."),
            };
            logger.LogInformation("{owner} took {identity} off subscription {id}", owner, member.Identity, subscriptionId);
            return removed!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A member leaves the plan it is on; it keeps it for its grace</summary>
    public async Task<SubscriptionMember> LeaveAsync(string identity)
    {
        await _lock.WaitAsync();
        try
        {
            var member = (await store.MembershipsOfAsync(identity)).FirstOrDefault(m => m.Status == MemberStatus.Active) ??
                         throw new SubscriptionRefusedException("You are not on a plan you can leave.");
            if (await store.GetAsync(member.SubscriptionId) is { } subscription && subscription.Owner == identity)
            {
                throw new SubscriptionRefusedException(OwnerStays);
            }
            logger.LogInformation("{identity} left subscription {id}", identity, member.SubscriptionId);
            return (await store.EndMemberAsync(member.Id, MemberStatus.Left, identity, dropNow: false, Now))!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The invitation behind a join link, for the identity signed in; refused when the link no longer works, or is
    /// someone else's
    /// </summary>
    public async Task<(SubscriptionView View, SubscriptionMember Member)> InvitationAsync(string? token, string identity)
    {
        var member = await store.MemberByTokenAsync(token);
        if (member is not { Status: MemberStatus.Invited })
        {
            throw new SubscriptionRefusedException(
                "This link no longer works: it was used, or replaced by a newer one. Ask whoever sent it for a new link.");
        }
        var subscription = await store.GetAsync(member.SubscriptionId);
        if (subscription is not { IsActive: true })
        {
            throw new SubscriptionRefusedException("The plan this link is for has ended.");
        }
        if (member.InviteExpired(Now))
        {
            throw new SubscriptionRefusedException($"This link has expired. Ask {subscription.Owner} for a new one.");
        }
        if (member.Identity != identity)
        {
            throw new SubscriptionRefusedException(
                $"This invitation is for {member.Identity}, and you are signed in as {identity}. Sign out, then open the link again.");
        }
        // Someone who pays for a group plan is on it, so cannot move to another
        if (await store.ActiveOwnedByAsync(identity) is { IsGroup: true } own && own.Id != subscription.Id)
        {
            throw new SubscriptionRefusedException(
                $"You pay for a {(await planStore.GetAsync(own.PlanId))?.Name ?? own.PlanId} plan and are on it, so you cannot join another. " +
                "Turn its auto-renew off, and you can join once it has ended.");
        }
        return (await ViewOfAsync(subscription), member);
    }

    /// <summary>
    /// The identity signed in joins with its link: it leaves whatever plan it was on, and gets its share of this one
    /// </summary>
    public async Task<(SubscriptionView View, SubscriptionMember Member)> JoinAsync(string? token, string identity,
        string? host)
    {
        await _lock.WaitAsync();
        try
        {
            var (view, member) = await InvitationAsync(token, identity);
            await LeaveOthersAsync(identity, view.Subscription.Id);
            var joined = (await store.JoinAsync(member.Id, Now))!;
            await GrantAsync(view.Subscription, joined, $"Joined {view.Subscription.Owner}'s {view.PlanName}", host);
            logger.LogInformation("{identity} joined subscription {id}", identity, view.Subscription.Id);
            return (await ViewOfAsync(view.Subscription), joined);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// An admin gave the identity another plan by hand: it comes off any paid plan it was on, at once, freeing its seat
    /// </summary>
    public async Task DetachAsync(string identity, string by)
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var member in await store.MembershipsOfAsync(identity))
            {
                if (member.Status == MemberStatus.Active)
                {
                    await store.EndMemberAsync(member.Id, MemberStatus.Removed, by, dropNow: true, Now);
                }
                else if (member.IsLeaving)
                {
                    await store.DropAsync(member.Id, Now);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeclineAsync(string? token, string identity)
    {
        await _lock.WaitAsync();
        try
        {
            var (_, member) = await InvitationAsync(token, identity);
            await store.EndMemberAsync(member.Id, MemberStatus.Declined, identity, dropNow: true, Now);
        }
        finally
        {
            _lock.Release();
        }
    }

    //
    // Shares
    //

    /// <summary>
    /// The owner sets members' shares of the pool: each at least the minimum, all of them (with the shares held by
    /// members leaving) no more than the pool. Members on the plan get their new quota on their hosts.
    /// </summary>
    public async Task<SubscriptionView> SetSharesAsync(long subscriptionId, string owner, IReadOnlyDictionary<long, long> shares)
    {
        await _lock.WaitAsync();
        try
        {
            var view = await OwnViewAsync(subscriptionId, owner);
            return await ApplySharesAsync(view, shares);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Divides the pool equally, in whole GB, between everyone named but those leaving, whose shares stay</summary>
    public async Task<SubscriptionView> SplitEvenlyAsync(long subscriptionId, string owner)
    {
        await _lock.WaitAsync();
        try
        {
            var view = await OwnViewAsync(subscriptionId, owner);
            var sharing = view.Holding.Where(m => !m.IsLeaving).ToList();
            if (sharing.Count == 0)
            {
                throw new SubscriptionRefusedException("There is nobody on your plan to share it between yet.");
            }
            var available = view.Subscription.PoolBytes - view.Holding.Where(m => m.IsLeaving).Sum(m => m.QuotaBytes);
            var each = WholeGb(available / sharing.Count);
            return await ApplySharesAsync(view, sharing.ToDictionary(m => m.Id, _ => each));
        }
        finally
        {
            _lock.Release();
        }
    }

    //
    // Billing
    //

    /// <summary>
    /// Turns auto-renew on or off. Off, an invoice made for the next term is voided, and the plan runs out at the end
    /// of this one.
    /// </summary>
    public async Task<Subscription> SetAutoRenewAsync(long subscriptionId, string owner, bool on)
    {
        var view = await OwnViewAsync(subscriptionId, owner);
        var changed = (await store.SetAutoRenewAsync(view.Subscription.Id, on, Now))!;
        if (!on)
        {
            await store.VoidOpenInvoicesAsync(view.Subscription.Id);
        }
        logger.LogInformation("{owner} turned auto-renew {onOff} on subscription {id}", owner, on ? "on" : "off",
            subscriptionId);
        return changed;
    }

    /// <summary>
    /// An open invoice is paid: its term becomes the subscription's. Paid in the grace, the term still starts when the
    /// one before ran out.
    /// </summary>
    public async Task<(Invoice Invoice, Subscription Subscription)> PayAsync(long invoiceId, string paidBy,
        string? reference = null, string? owner = null)
    {
        await _lock.WaitAsync();
        try
        {
            var invoice = await store.GetInvoiceAsync(invoiceId);
            var subscription = invoice == null ? null : await store.GetAsync(invoice.SubscriptionId);
            if (invoice is not { Status: InvoiceStatus.Open } || subscription == null ||
                (owner != null && subscription.Owner != owner))
            {
                throw new SubscriptionRefusedException("That invoice is not open.");
            }
            if (!subscription.IsActive)
            {
                throw new SubscriptionRefusedException("That plan has ended: choose a plan again instead.");
            }

            var paid = (await store.MarkPaidAsync(invoice.Id, paidBy, reference, Now))!;
            var renewed = (await store.SetPeriodAsync(subscription.Id, paid.PeriodStart, paid.PeriodEnd, Now))!;
            logger.LogInformation("Invoice {invoice} paid ({paidBy}): subscription {id} runs until {end}", invoice.Id,
                paidBy, subscription.Id, DateTimeOffset.FromUnixTimeMilliseconds(renewed.PeriodEnd).ToString("yyyy-MM-dd"));
            return (paid, renewed);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// An admin adds months to the term, free of charge: from when it runs out, or from now if that has passed. An
    /// invoice already made for the next term is voided; the next one is made for the new end.
    /// </summary>
    public async Task<Subscription> ExtendAsync(long subscriptionId, int months)
    {
        if (months is < 1 or > BillingTerm.LongestMonths)
        {
            throw new SubscriptionRefusedException($"Extend by 1 to {BillingTerm.LongestMonths} months");
        }
        var subscription = await store.GetAsync(subscriptionId);
        if (subscription is not { IsActive: true })
        {
            throw new SubscriptionRefusedException("Only a running subscription can be extended.");
        }
        await store.VoidOpenInvoicesAsync(subscription.Id);
        return (await store.SetPeriodAsync(subscription.Id, subscription.PeriodStart,
            PlanGrantStore.TermEnd(Math.Max(subscription.PeriodEnd, Now), months), Now))!;
    }

    //
    // The worker
    //

    /// <summary>
    /// Bills subscriptions due to renew; ends those whose grace is over, moving their members to the free plan; and
    /// moves members whose own grace after leaving is over. Returns the identities moved.
    /// </summary>
    public async Task<List<string>> RunAsync()
    {
        foreach (var due in await store.DueForInvoiceAsync(Now + (long)InvoiceAhead.TotalMilliseconds))
        {
            var invoice = await store.AddInvoiceAsync(due.Id, due.PlanId, due.Price, due.PeriodEnd, due.PeriodEnd, Now);
            logger.LogInformation("Invoice {invoice} made for subscription {id}: {price}, due {due}", invoice.Id, due.Id,
                invoice.Price.Text, DateTimeOffset.FromUnixTimeMilliseconds(invoice.DueAt).ToString("yyyy-MM-dd"));
        }

        var moved = new List<string>();
        var ranOut = await store.RanOutAsync(Now - (long)PlanService.Grace.TotalMilliseconds);
        var leaving = await store.LeavingDueAsync(Now - (long)PlanService.Grace.TotalMilliseconds);
        if (ranOut.Count == 0 && leaving.Count == 0)
        {
            return moved;
        }
        if (await planStore.GetAsync(SitePages.FreePlanId) == null)
        {
            logger.LogError("{count} subscription(s) and {members} member(s) are past their grace, but there is no " +
                            "'{free}' plan to move them to", ranOut.Count, leaving.Count, SitePages.FreePlanId);
            return moved;
        }

        await _lock.WaitAsync();
        try
        {
            foreach (var subscription in ranOut)
            {
                var when = Day(subscription.PeriodEnd);
                foreach (var member in await store.MembersAsync(subscription.Id))
                {
                    if (member.Status == MemberStatus.Active || member.IsLeaving)
                    {
                        if (await ToFreeAsync(member, $"{subscription.PlanId} ran out on {when}; {PlanService.Grace.Days} days' grace over"))
                        {
                            moved.Add(member.Identity);
                        }
                    }
                    if (member.Status is MemberStatus.Active or MemberStatus.Invited)
                    {
                        await store.EndMemberAsync(member.Id, MemberStatus.Ended, "expiry", dropNow: true, Now);
                    }
                    else if (member.IsLeaving)
                    {
                        await store.DropAsync(member.Id, Now);
                    }
                }
                await store.VoidOpenInvoicesAsync(subscription.Id);
                await store.EndAsync(subscription.Id, "ran out", Now);
                logger.LogInformation("Subscription {id} ({owner}'s {planId}) ran out on {when} and ended", subscription.Id,
                    subscription.Owner, subscription.PlanId, when);
            }

            foreach (var member in leaving)
            {
                if (await ToFreeAsync(member, $"Off the plan since {Day(member.EndedAt!.Value)}; {PlanService.Grace.Days} days' grace over"))
                {
                    moved.Add(member.Identity);
                }
                await store.DropAsync(member.Id, Now);
            }
        }
        finally
        {
            _lock.Release();
        }
        return moved;
    }

    //

    /// <summary>
    /// What is left of a running subscription's paid term, by its value, as time on a new plan bought now: the unused
    /// part of what was paid (price × time left ÷ term), turned into time at the new price's rate. None for a free plan,
    /// a term already run out, or a new price of 0.
    /// </summary>
    /// <returns>The value left, in cents, and the time it buys on the new plan, in milliseconds</returns>
    public static (long ValueCents, long ExtraMs) CarryOver(Subscription? from, PlanPrice to, long now)
    {
        if (from is not { IsActive: true } || from.PriceCents <= 0 || to.AmountCents <= 0 || now >= from.PeriodEnd ||
            from.PeriodEnd <= from.PeriodStart)
        {
            return (0, 0);
        }
        var left = (decimal)(from.PeriodEnd - now) / (from.PeriodEnd - from.PeriodStart);
        var value = from.PriceCents * left;
        var newTerm = PlanGrantStore.TermEnd(now, to.Months) - now;
        return ((long)decimal.Round(value), (long)decimal.Round(value / to.AmountCents * newTerm));
    }

    /// <summary>A stretch of time as people read it: "12 days", "about 3 months"</summary>
    public static string Describe(long ms)
    {
        var days = ms / 86_400_000d;
        if (days < 1)
        {
            return "less than a day";
        }
        if (days < 45)
        {
            var whole = (int)Math.Round(days);
            return whole == 1 ? "1 day" : $"{whole} days";
        }
        var months = (int)Math.Round(days / 30.44);
        return months == 1 ? "about a month" : $"about {months} months";
    }

    private static long MinimumShare => SubscriptionStore.MinimumShareBytes;

    private const string OwnerStays =
        "You pay for this plan, so you stay on it. To stop, turn auto-renew off: it then ends with its term.";

    // Their own subscription, or refused
    private async Task<SubscriptionView> OwnViewAsync(long subscriptionId, string owner)
    {
        var subscription = await store.GetAsync(subscriptionId);
        if (subscription == null || subscription.Owner != owner)
        {
            throw new SubscriptionRefusedException("That is not your plan.");
        }
        if (!subscription.IsActive)
        {
            throw new SubscriptionRefusedException("That plan has ended.");
        }
        return await ViewOfAsync(subscription);
    }

    // A seat taken at once by someone signed in (the owner): they leave whatever plan they were on
    private async Task<SubscriptionMember> AddSeatAsync(Subscription subscription, string identity, long share,
        string? host, string note)
    {
        await LeaveOthersAsync(identity, subscription.Id);
        var member = await store.AddMemberAsync(subscription.Id, identity, share, active: true, Now);
        await GrantAsync(subscription, member, note, host);
        return member;
    }

    // An identity joining a plan leaves any other it is on, at once: it has the new plan now. A plan for one it owns
    // stops renewing, as nobody would be on it
    private async Task LeaveOthersAsync(string identity, long joining)
    {
        foreach (var member in await store.MembershipsOfAsync(identity))
        {
            if (member.SubscriptionId == joining || !(member.Status == MemberStatus.Active || member.IsLeaving))
            {
                continue;
            }
            if (member.Status == MemberStatus.Active)
            {
                await store.EndMemberAsync(member.Id, MemberStatus.Left, identity, dropNow: true, Now);
            }
            else
            {
                await store.DropAsync(member.Id, Now);
            }
            if (await store.GetAsync(member.SubscriptionId) is { IsActive: true, IsGroup: false, AutoRenew: true } own &&
                own.Owner == identity)
            {
                await store.SetAutoRenewAsync(own.Id, false, Now);
                await store.VoidOpenInvoicesAsync(own.Id);
                logger.LogInformation("{identity} joined another plan: auto-renew turned off on their own subscription {id}",
                    identity, own.Id);
            }
        }
    }

    private async Task<SubscriptionView> ApplySharesAsync(SubscriptionView view, IReadOnlyDictionary<long, long> shares)
    {
        if (!view.Subscription.IsGroup)
        {
            throw new SubscriptionRefusedException("A plan for one person has nothing to share.");
        }
        foreach (var (id, share) in shares)
        {
            var member = view.Members.FirstOrDefault(m => m.Id == id);
            if (member is not { HoldsSeat: true } || member.IsLeaving)
            {
                throw new SubscriptionRefusedException("Someone in that list is not on your plan now.");
            }
            CheckShare(share);
        }
        var total = view.Holding.Sum(m => shares.TryGetValue(m.Id, out var share) ? share : m.QuotaBytes);
        if (total > view.Subscription.PoolBytes)
        {
            throw new SubscriptionRefusedException(
                $"Those shares add up to {StorageSize.Format(total)}; your plan has {StorageSize.Format(view.Subscription.PoolBytes)}.");
        }

        foreach (var member in view.Holding)
        {
            if (!shares.TryGetValue(member.Id, out var share) || share == member.QuotaBytes)
            {
                continue;
            }
            var changed = (await store.SetShareAsync(member.Id, share, Now))!;
            if (changed.Status == MemberStatus.Active)
            {
                await GrantAsync(view.Subscription, changed, $"Share set to {StorageSize.Format(share)}", null);
            }
        }
        return await ViewOfAsync(view.Subscription);
    }

    // A new seat's share: an equal part, in whole GB, of what is unassigned between the seats left; all of it on a
    // plan for one
    private static long ShareForNewSeat(SubscriptionView view)
    {
        if (!view.Subscription.IsGroup)
        {
            return view.Subscription.PoolBytes;
        }
        var each = WholeGb(view.UnassignedBytes / Math.Max(1, view.SeatsLeft));
        return each >= MinimumShare ? each : WholeGb(view.UnassignedBytes);
    }

    private static void CheckShare(long share)
    {
        if (share < MinimumShare)
        {
            throw new SubscriptionRefusedException($"Each person gets at least {StorageSize.Format(MinimumShare)}.");
        }
    }

    private static long WholeGb(long bytes) => bytes / MinimumShare * MinimumShare;

    private async Task PaidInvoiceAsync(Subscription subscription, PlanPrice price, long periodStart, string paidBy,
        string? reference)
    {
        var invoice = await store.AddInvoiceAsync(subscription.Id, subscription.PlanId, price, periodStart, periodStart, Now);
        await store.MarkPaidAsync(invoice.Id, paidBy, reference, Now);
    }

    // What the member gets, set on its host: the plan, with its share as its quota
    private async Task GrantAsync(Subscription subscription, SubscriptionMember member, string note, string? host) =>
        await plans.GrantAsync(member.Identity, subscription.PlanId,
            member.Identity == subscription.Owner ? PlanGrantSource.Purchase : PlanGrantSource.Group,
            $"sub-{subscription.Id}-member-{member.Id}-{Guid.NewGuid():N}", host, note,
            quotaBytes: member.QuotaBytes, subscriptionId: subscription.Id);

    // Back to the free plan, unless the identity's plan is no longer this subscription's (an admin changed it)
    private async Task<bool> ToFreeAsync(SubscriptionMember member, string note)
    {
        var current = await grants.CurrentForAsync(member.Identity);
        if (current?.SubscriptionId != member.SubscriptionId)
        {
            return false;
        }
        await plans.GrantAsync(member.Identity, SitePages.FreePlanId, PlanGrantSource.Expiry,
            $"expiry-sub-{member.SubscriptionId}-member-{member.Id}", current.Host, note);
        logger.LogInformation("{identity} moved to the free plan: {note}", member.Identity, note);
        return true;
    }

    private static string People(int count) => count == 1 ? "1 person" : $"{count} people";

    private static string Day(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy-MM-dd");
}
