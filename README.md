# raven-hosting

Raven Hosting's service for Homebase identities: invitation codes for signups, the hosting plans, plan grants,
the upgrade page where owners get a bigger plan, and the site around it at https://ravenhosting.cloud (the plans
and their prices, the terms, the privacy policy and the report form).

The identity hosts (odin-core) call it at signup to check a code and take a use; a host that says who is signing
up has them kept on the use too, for the Channels pages. The hosts have no plans of their own (odin-core #1994):
this service holds them, and sends what a plan gives whenever it gives one. Admins manage codes and plans with
`raven.sh` or curl.

## Run it

```bash
dotnet run --project src/Raven.Hosting     # http://localhost:5246, Development config
dotnet test

./build-docker-image.sh && ./run-attached.sh              # the image, on 127.0.0.1:5246, database in ./data
```

## Command line

`./raven.sh` wraps the API. `./raven.sh setup` saves both keys to `~/.raven-hosting` (outside the
repo, readable only by you); then for plans `plans` (a table; `--json` for the service's answer), `plan-create`,
`plan-edit`, `plan-price`, `plan-delete`; for codes `./raven.sh create --uses 5 --source "spring campaign" --plan registered --owner frodo.demo.rocks`,
`list`, `stats`, `edit`, `disable`, `enable`, `validate`, `use`, `undo`; and for grants `grant --id frodo.demo.rocks --plan veterans`, `grants`, `retry`. `./raven.sh help` explains each command and its arguments.

## Tests

`dotnet test` runs the store tests, every API endpoint over HTTP in process, and `raven.sh` with bash against
the service on a real port. The script tests need `jq`: without it they are skipped locally and fail under CI.
`.github/workflows/test.yml` builds and runs them on every pull request and push to main.

## Deploy

`.github/workflows/build-and-deploy.yml` (run by hand) builds, tests and pushes
`ghcr.io/homebase-id/raven-hosting-internal:<branch>`, then asks the ansible controller to deploy it
(`deploy raven-hosting aux raven-hosting-internal <tag>`) to blackbird-aux, behind Caddy at
https://ravenhosting.cloud (the site and the APIs), the same way homebase-social-sync deploys. The controller only deploys what the
DevOps repo allowlists (`odin-deploy.sh`, `run-playbook.sh`), so that has to be set up first.

The container listens on 8080 and keeps its database in `/data`: mount a volume there.

## A code

| Field | |
|---|---|
| `code` | 12 Crockford base32 characters, shown as `K7QX-M2PD-9FHW`; input is case-insensitive and ignores dashes and spaces |
| `owner` | the identity the code belongs to; optional |
| `source` | free text: where the code came from |
| `enablePublicWebPresence` | **deprecated**: whether an identity that signs up with it gets a public home page, on identity hosts from before odin-core #1974. Later hosts ignore it and take it from the identity's plan. Optional, on by default; still stored and sent for those older hosts |
| `planId` | the hosting plan, and so the quota and public home page, an identity that signs up with it gets; required when a code is made, and can be changed but not removed. It must be one of the [plans](#plans); the code keeps the plan's own Id, whatever case it was given in. `null` only on codes made before it was required, which leave the plan to the identity host |
| `maxUses`, `usesLeft` | how many signups it allows, and how many are left |
| `disabled` | a disabled code cannot be used, whatever uses it has left |

## API (`/api/v1/invitation-codes`, key in the `Odin-Api-Key` header)

| Call | Key | Answer |
|---|---|---|
| `POST /validate {code}` | signup | `{valid}`: exists and has a use left |
| `POST /use {code, identity?}` | signup | `{used, code}`: takes a use, atomically, and returns the code with what its plan gives now, `code.quotaBytes` and `code.publicWebPresence`, which the host stores on the new identity. Both are `null` for a code whose plan is not here (made before plans were), and the host uses its defaults; the service logs a warning. A host that says who is signing up (`identity`) starts their [subscription](#subscriptions) here: a free one of the code's plan, with no end, set on the host by the grant worker once the identity is there. odin-core does not send it yet |
| `POST /undo-use {code}` | signup | 204: gives the latest use back |
| `POST / {owner?, maxUses, source, planId, enablePublicWebPresence?}` | create | the new code |
| `GET /?owner=<odinId>` | create | the codes, all of them without `owner` |
| `GET /stats?sinceDays=<n>` | create | codes and signups in total, by source, by owner, by plan (`name: null` for old codes without one) and by week (Monday, UTC). `sinceDays` limits only the signups counted; the code counts (`neverUsed`, `usedUp`, `disabled`, codes, uses allowed, `usesLeft`) are always about every code as it is now. `usesLeft` on each line is the signups its codes still allow, disabled codes counting none: watch `byPlan[].usesLeft` to see when a plan needs more codes |
| `PATCH /<code> {maxUses?, source?, owner?, enablePublicWebPresence?, planId?, disabled?}` | create | the code as it is now; 404 if there is none. Fields left out stay as they are; `"owner": ""` removes the owner |

Bad input (owner not a domain, a missing `planId` or one that is not a plan, `maxUses` outside 1..10000) is a 400.
Creating a code:

```bash
curl -X POST https://<service>/api/v1/invitation-codes/ \
  -H 'Odin-Api-Key: <create key>' -H 'Content-Type: application/json' \
  -d '{"owner":"frodo.demo.rocks","maxUses":5,"source":"spring campaign","planId":"registered"}'
```

## Plans

A plan is what an identity gets. The identity hosts store each identity's `planId`, `quotaBytes` and
`publicWebPresence` on its registration and enforce them; they do not know what a plan Id means. This service
keeps the plans, and sends all three whenever it gives an identity a plan: with a code at signup, and with every
grant.

| Field | |
|---|---|
| `id` | letters, digits, `.`, `_` and `-`, up to 64; unique whatever its case, and found in any case. Cannot be changed. `grants`, `admin` and `account` are reserved |
| `name`, `description` | what owners see; the name is required |
| `quotaBytes` | bytes an identity on it may store; more than 0, there is no unlimited. `storage` in answers is the same, as people read it (`1 TB`) |
| `publicWebPresence` | whether an identity on it has a public home page |
| `publicEmail` | whether it offers a public email address; `false` by default. Advertised only: shown on its card, not sent to the hosts or enforced |
| `emailAliases` | how many email aliases it offers, 0 or more; `0` by default. Advertised only, like `publicEmail` |
| `seats` | how many identities it covers, 1 to 50; `1` by default. More than 1 makes it a **group plan**: its `quotaBytes` is a pool the one who pays shares out between the people on it, at least 1 GB each, so it must be at least 1 GB per seat ([Subscriptions](#subscriptions)) |
| `forSale` | whether owners can buy it on the upgrade page; `false` by default. A plan for sale has at least one price |
| `prices` | what it costs, one price per billing term, shortest first: `{months, amountCents}` in US cents, `months` from 1 to 120 (1 monthly, 12 yearly, 24 two-yearly...), `amountCents` 0 or more (0 is free). Answers add `amount` (`$50`), `per` (`year`) and `text` (`$50 / year`). Nothing charges them yet: the upgrade page's checkout is a test |
| `recommended` | whether its card is marked "Recommended" for visitors; `false` by default. When more than one is, the first listed is. Someone signed in whose host says their quota is recommended the next size up instead: the smallest plan for sale with more room than they have, or none when no plan has more |
| `sortOrder` | plans are listed, and the upgrade page's cards shown, by this, then by `id`; `0` by default |
| `createdAt`, `updatedAt` | unix ms |

Each plan is kept as one row: its `id`, and a JSON document with everything else, prices included. A new field is
a new property in the document rather than a new column, and a document from before a field existed reads it as
its default. The first plans table, a column per field, is moved into documents the first time the service starts.

Editing a plan changes nothing for identities already on it: a grant keeps what the plan gave when it was made,
and an identity that signed up with a code keeps what it got then. The change applies to the next signup or
purchase. A plan cannot be deleted while a code or a grant uses it; to retire one, mark it not for sale.

### API (`/api/v1/plans`, create key)

| Call | Answer |
|---|---|
| `POST / {id, name, quotaBytes, publicWebPresence, publicEmail?, emailAliases?, seats?, forSale?, description?, prices?: [{months, amountCents}], sortOrder?, recommended?}` | the new plan; 409 if the Id is taken |
| `GET /` | every plan, by `sortOrder`, then `id` |
| `GET /<id>` | the plan; 404 if there is none |
| `PATCH /<id> {name?, description?, quotaBytes?, publicWebPresence?, publicEmail?, emailAliases?, seats?, forSale?, sortOrder?, recommended?}` | the plan as it is now; 404 if there is none. Fields left out stay as they are. `forSale: true` on a plan with no price is a 400 |
| `PUT /<id>/prices/<months> {amountCents}` | the plan, with that term added or its price changed; 404 if there is no such plan |
| `DELETE /<id>/prices/<months>` | the plan, without that term; 404 if it has none; 409 for the last price of a plan for sale |
| `DELETE /<id>` | 204; 404 if there is none; 409 while a code or grant uses it |

Bad input (a bad or reserved Id, no name, a quota of 0 or less, a missing `quotaBytes` or `publicWebPresence`,
`emailAliases` below 0, `seats` outside 1..50 or with less than 1 GB each, a price for 0 or more than 120 months or
below 0, two prices for one term, a plan for sale with no price) is a 400.

Changing a plan or a price changes nothing for subscriptions already bought: each keeps the seats, storage, term and
price it was bought at, and renews at that price.

### Who may buy (`/api/v1/purchases`, create key)

Until real card processing is in, buying on the upgrade page is locked. Everyone can see the plans, sign in and
confirm a plan, but only **testers** can check out; anyone else sees **Coming soon** in place of the checkout button,
and a checkout they reach anyway (`GET` or `POST /upgrade/checkout`) is a 403 "Coming soon" page. A new service
starts closed, with no testers. Admin grants are not affected. `raven.sh purchases` shows and changes it.

| Call | Answer |
|---|---|
| `GET /` | `{open, testers: [{identity, addedAt}]}` |
| `PUT /open {open}` | the same, after opening (`true`: anyone signed in may buy) or closing (`false`: only testers) |
| `PUT /testers/<identity>` | the same, with the identity added; adding one already there changes nothing. 400 for a name that is not a domain |
| `DELETE /testers/<identity>` | the same, without it; 404 if it is not a tester |

### Deploying plans

There are no built-in plans. After deploying, and before anyone signs up, create them: until then every code's
`use` carries its plan Id alone, and new identities get the host's default quota (60 GB) whatever their code's
plan. The starting set, today's plans:

| Id | Quota | Public page | For sale |
|---|---|---|---|
| `free` | 64424509440 (60 GB) | yes | no |
| `free-1gb` | 1073741824 (1 GB) | no | no |
| `free-5gb` | 5368709120 (5 GB) | no | no |
| `registered` | 10737418240 (10 GB) | yes | yes |
| `veterans` | 1099511627776 (1 TB) | yes | yes |
| `tiny-test` | 1048576 (1 MB) | no | no |

```bash
./raven.sh plan-create --id free --name Free --quota-bytes 64424509440 --public-web-presence true
./raven.sh plan-create --id free-1gb --name "Free 1 GB" --quota-bytes 1073741824 --public-web-presence false
./raven.sh plan-create --id free-5gb --name "Free 5 GB" --quota-bytes 5368709120 --public-web-presence false
./raven.sh plan-create --id tiny-test --name "Tiny test" --quota-bytes 1048576 --public-web-presence false

# The plans for sale need their prices: --price <months>:<dollars>, once per term
./raven.sh plan-create --id registered --name Registered --quota-bytes 10737418240 --public-web-presence true \
  --price 1:<dollars> --price 12:<dollars> --for-sale true --sort-order 1
./raven.sh plan-create --id veterans --name Veterans --quota-bytes 1099511627776 --public-web-presence true \
  --price 1:<dollars> --price 12:<dollars> --price 24:<dollars> --for-sale true --sort-order 2
./raven.sh plans
./raven.sh stats      # every plan under "by plan" should be one of the plans above
```

A code whose plan is not one of the plans still works, with the host's defaults; give it a plan with
`./raven.sh edit <code> --plan <plan id>`.

## Subscriptions

Each identity's plan is its latest [grant](#plan-grants); the grants before it are its history. A plan someone pays
for is a **subscription**, tracked here rather than by a payment provider (`SubscriptionStore`, `SubscriptionService`):

| Table | One row per | |
|---|---|---|
| `subscriptions` | plan someone pays for | the **owner** (who pays and manages it), the plan with its `seats` and storage (`pool_bytes`) and the term and price as bought, `auto_renew`, the term paid for now (`period_start`, `period_end`), `status` (`active`, then `ended`) |
| `subscription_members` | identity it names | `invited`, `active`, `declined`, `removed`, `left` or `ended`; its share of the pool (`quota_bytes`); its join link's token while invited; when it left, and when it stopped holding a seat (`dropped_at`) |
| `invoices` | term | the term and amount, `open`, `paid` (when, how: `test-checkout`, `admin:<identity>`, `migration`, later a provider; a reference) or `void` |

What each member gets is still a grant, now carrying the subscription's Id (`subscription_id`): grants are what the
hosts are told. Where an identity's plan came from:

| Grant source | | Until |
|---|---|---|
| `code` | signed up with an invitation code (when the host says who, on `/use`) | no end |
| `admin` | an admin changed its plan; it comes off any subscription it was on | no end |
| `purchase` | the owner, on a subscription they pay for | its term, then its grace |
| `group` | a member of someone else's subscription, who joined it | its term, then its grace; or leaving's grace |
| `expiry` | the service, after a subscription ran out or a member's grace after leaving was over | no end |

- **Buying** (the upgrade page's test checkout, for now) makes a subscription the buyer owns, with its first invoice
  paid. Someone owns one at a time: buying again changes its plan and term, paid from then. Nothing paid for is lost:
  what was left of the old term, by its value (price × time left ÷ term), is added to the new term as extra time at
  the new plan's rate (`SubscriptionService.CarryOver`); checkout shows it, and the plan's history notes it. It is
  refused while it names more people than the new plan has seats, or their shares add up to more than
  its storage. On a plan for one, the buyer takes the seat and the whole quota.
- **Group plans.** The owner always takes a seat: they cannot leave their own plan (to stop, they turn auto-renew
  off), nor join someone else's group plan while theirs runs. They name each person by identity on `/account`; each gets a
  join link (`/join/<token>`, 32 random bytes, working 14 days, and only signed in as the identity named, so it is
  kept to show the owner again, not hashed) and joins after signing in with YouAuth. A share is held from the
  invitation; by default an equal part, in whole GB, of what is unassigned between the seats left. Shares are at
  least 1 GB (`SubscriptionStore.MinimumShareBytes`) and add up to no more than the pool; **Split evenly** divides it
  between everyone but those leaving. A changed share is set on the member's host as a new grant.
- **One plan at a time.** An identity is an active member of one subscription at most (a unique index). Joining
  another leaves the one it was on at once; on a plan for one it owns, auto-renew is turned off.
- **Leaving.** A member removed by the owner, or who leaves, keeps the plan, and its seat and share stay held, for
  **14 days' grace** (`PlanService.Grace`); then it goes to the `free` plan. A cancelled or declined invitation frees
  its seat at once.
- **Renewing.** With auto-renew on, an `open` invoice for the next term is made 7 days before the term runs out
  (`SubscriptionService.InvoiceAhead`), at the price bought. Paid (by an admin's **Mark paid**, or the owner's test
  checkout on `/account`), the term moves on; paid in the grace, the new term still starts when the old one ran out.
  Turning auto-renew off voids an open invoice.
- **Running out.** Unpaid, or with auto-renew off, a subscription keeps going for 14 days' grace after its term. Then,
  hourly (`RavenHosting:ExpiryIntervalMinutes`, 60), it ends and every member still on it is granted the `free` plan
  (an `expiry` grant, noting why), unless an admin gave them another plan meanwhile. Nothing is deleted: an identity
  storing more than the free plan allows is over quota, and its host refuses new files until it upgrades. Admins can
  **extend** one by months (from when it runs out, or from now if that passed), which voids an open invoice.
- **Add-ons** (`plan_add_ons`) are on top of the plan, whatever plan the identity is on, given by admins: more
  storage, a public home page on or off (off is a safety tool), and a public email address and more email aliases,
  which are only recorded until email is offered. Each can end on a date (removed hourly once it passed). The host
  is given the plan's quota (a member's share, on a group plan) plus every storage add-on, and the latest public home
  page add-on in place of the plan's (`Entitlement`); a change is set on the host straight away
  (`PlanService.ReapplyAsync`), and the daily reconcile compares the host with plan plus add-ons. The upgrade page
  compares plans without storage add-ons, which stay.
- **History:** every grant keeps `note` (why), `madeBy` (the admin, on the admin pages), and `reappliedAt` (when it
  was last set on the host again: an add-on changed, or the host drifted); add-ons keep who added and removed them.
- **Before subscriptions**, a purchase was a grant with its price and end (`price_months`, `price_amount_cents`,
  `ends_at`). The first time the service starts, each identity whose latest grant was one becomes the owner and only
  member of a subscription for the rest of that term, with auto-renew off and one invoice paid by `migration`;
  earlier purchases keep their price in their note; then those columns are dropped.

## Plan grants

The service also gives identities plans: a **grant** says identity X is owed plan P, and the service sets it on
the identity host that serves X, with what P gives: its quota and public web presence, as they were when the grant
was made (or, on a group plan, the member's share). P must be one of the [plans](#plans) (400 otherwise). Admins make
grants (`./raven.sh grant`); [subscriptions](#subscriptions) make `purchase`, `group` and `expiry` grants. A plan is
set only through a grant, never by a link.

### Finding the host

The service knows the identity hosts by their provisioning domains (`RavenHosting__IdentityHosts`), and calls only those,
with the one plan key they all take (`RavenHosting__AccountServiceApiKey`; odin-core's `Registry__AccountService__ApiKey`). To find
the host that serves X it asks, in order: the host the request named (`host`, only if it is a known one; owner
consoles send it as `?host=` in the upgrade link), the host that last had X, then every other known host. The
first to answer `200` has it; `404` means not there. The host API (odin-core, on the provisioning domain):

| Call | Answer |
|---|---|
| `GET /api/plans/v1/identities/<identity>` | `{domain, planId, quotaBytes, publicWebPresence}`, the quota being the one in effect; 404 if the host does not serve it |
| `PUT /api/plans/v1/identities/<identity>/plan {planId, quotaBytes, publicWebPresence, reference}` | the same, after setting them; values left out stay as they are. 400 for an empty plan Id or a quota that is not more than 0; 404 as above. Setting what it already has changes nothing |

A grant made before plans were kept here has no quota or public web presence, and sends `planId` and `reference`
alone: the host keeps the identity's own.

### Statuses

| Status | |
|---|---|
| `pending` | not set yet: no host has the identity, a host is down, or it answered something else. Tried again after 1, 2, 4... minutes, at most every 6 hours, for as long as it takes: a paid plan is never given up on |
| `applied` | the host has it; `host` says which, `appliedAt` when |
| `failed` | the host refused the plan (`400`: an empty plan Id or a quota that is not more than 0, which this service never sends). Not retried by itself; once the cause is fixed, `retry` |
| `superseded` | a newer grant for the same identity was made before the host had this one: never set, so it cannot land over the newer plan or share |

A background task tries due grants every minute (`RavenHosting__GrantRetryIntervalSeconds`) and, once a day
(`RavenHosting__ReconcileIntervalHours`), reconciles: for each identity it asks the host for the plan Id, quota and
public web presence it has, and if any differs from its latest applied grant, sets the grant again. Only what both
sides have is compared: a grant from before plans were kept here by its plan Id alone, and a host from before
odin-core #1994, which does not say its quota or public web presence, not on those. So a plan changed by hand on
a host with `odin-admin tenant set-plan` is undone the next day for an identity that has a grant; give such an
identity a new grant instead.

### API (`/api/v1/plans/grants`, create key)

| Call | Answer |
|---|---|
| `POST / {identity, planId, source?, reference?, host?}` | the grant, with the plan's `quotaBytes` and `publicWebPresence` now, after trying to set it straight away; a plan with no end. A `planId` that is not a plan is a 400. Paid plans are [subscriptions](#subscriptions), not made here. `source` is `admin` (the default) or another of the grant sources; `reference` is the caller's id for it, e.g. a payment id, made up when left out. The same `reference` again is the same grant (not set twice); for another identity or plan it is a 409 |
| `GET /?identity=<odinId>&status=<status>` | the grants, newest first; both filters optional |
| `POST /<id>/retry` | tries a pending or failed grant again now; 404 if there is none, 409 if it is already applied |

```bash
curl -X POST https://<service>/api/v1/plans/grants/ \
  -H 'Odin-Api-Key: <create key>' -H 'Content-Type: application/json' \
  -d '{"identity":"frodo.demo.rocks","planId":"veterans","source":"admin"}'
```

## Site

The public pages, in `SitePages.cs`, on the same Razor components and stylesheet as the upgrade page:

- **`/`**: what hosting costs. The free plan (Id `free`, `SitePages.FreePlanId`) first, with **Create a free
  identity**, then the plans for sale with their prices. A visitor gets **Sign in to choose** on each; someone
  signed in gets **Choose** and sees their own plan marked, as on `/upgrade`. If their host cannot be asked just
  then, the page shows without it.
- **`/sign-in?plan=<id>`**: a box for the identity, sent to `/upgrade?id=<identity>&plan=<id>`, which signs them in
  (below) and then goes straight to confirming that plan. The plan rides along in a short cookie on `/upgrade`.
- **`/account`** ("Your plan" in the header, for someone signed in; `/sign-in?next=account` and back otherwise): their
  plan as their host says; invitations waiting for them; the subscription they pay for, with its price, when it
  renews or ends, **auto-renew** on or off, **Change plan**, its invoices (an open one paid with the test checkout,
  for those who may buy), and the people on it with their shares, join links, **New link**, **Remove** and **Cancel
  invitation**, **Save shares**, **Split evenly**, **Add someone**; or the plan someone else pays
  for that they are on, with **Leave this plan**. ([Subscriptions](#subscriptions) has the rules.)
- **`/join/<token>`**: a join link. Not signed in, it signs them in (`/sign-in?next=join/<token>`) and comes back.
  Signed in as the identity named, it shows whose plan, their share, what it gives, and what happens to the plan
  they are on now, with **Join** and **Decline**; signed in as anyone else, or once used, replaced, expired or ended,
  it says why not.
- **`/terms-and-conditions`**, **`/privacy-policy`**: as they were on the old Netlify site (ravenhosting-web).
- **`/report`**: the report form for content that breaks the terms; `/report/content`, where identity hosts link
  (`Host:ReportContentUrl`), redirects to it. Reports are kept in the database (`content_reports`) and read with
  `./raven.sh reports` (`GET /api/v1/reports?sinceDays=<n>`, create key). Nobody is notified of a new one.

**Create a free identity** goes to the visitor's region's sign-up host: the page carries the EU host and
`wwwroot/assets/signup.js` picks by time zone, then language, in step with odin-js `detectRegion`. Every header
has **Sign in**, or the identity signed in and **Sign out**, which goes back to `/`.

## Upgrade page

Where an owner gets a bigger plan. Their owner console sends them to
`https://<RavenHosting__PublicDomain>/upgrade?id=<identity>&host=<provisioning domain>` (set odin-core's
`Registry__UpgradeUrl` to `https://<RavenHosting__PublicDomain>/upgrade`; the host adds `id` and `host`).

1. **Sign in with YouAuth**, as a domain client, done on this server (odin-core
   `docs/youauth-unified-authorization.md`). The service makes a P-384 key pair and a single-use state that
   lasts 10 minutes, kept in the database with the private key sealed by data protection, and sends the browser
   to `https://<identity>/api/owner/v1/youauth/authorize` (`client_id` = the public domain, `client_type=domain`,
   `cipher=aes-gcm`). The owner is usually already signed in to their identity, so there is no password; the
   first time there is a consent screen naming this service.
2. **Callback** (`/upgrade/youauth-callback`): the state is taken once; the service derives the exchange secret,
   trades its digest at `https://<identity>/api/owner/v1/youauth/token`, and opens the token (AES-GCM; an
   identity answering `aes-cbc` is refused as a downgrade). A token that opens is the proof the owner of that
   identity is here. The session is a data-protected cookie for the whole site, for an hour. It also keeps the
   display name on the identity's public profile card (`GET https://<identity>/pub/profile`, odin-core), fetched once
   here and best effort: none when the card has no name of its own, there is no card, or it cannot be reached. The
   header shows the owner as their public photo (`https://<identity>/pub/image`, loaded by the browser; the identity
   serves a placeholder when there is none) beside that name, with the identity under it, or the identity alone.
3. **Choose** (`/upgrade`): the plans marked for sale as cards, by `sortOrder`: name, description, storage
   (`1 TB of storage`), then each feature, ticked with a line on what it is when the plan has it and dashed and
   faded when not (a public home page, a public email address, email aliases), and its prices, each term with what it saves on paying
   monthly when it does (`$50 / year save 17%`, against 12 times the monthly price). Above them, **Your plan now**:
   what the identity's host says it has (`GET /api/plans/v1/identities/{identity}`, asked as grants find it), which
   may differ from what this service last set: the plan's name, how full it is ("12% of 60 GB used", then "7.2 GB
   used · 52.8 GB free", by `StorageUse`, the owner console's rules), its public home page and whether it is full.
   The card with its plan Id is marked "Your plan"; every other says how it compares ("940 GB more than now", "Less
   than you have now (1 TB)"), and one smaller than what the identity stores cannot be chosen, confirmed or bought
   (`PlanFit`). Anything a newer host adds to its answer is kept (`IdentityOnHost.More`).
4. **Confirm** (`/upgrade/confirm?plan=<id>`): "Change your plan from A to B" (on a group plan, a note that the buyer takes
   one of its seats), then **How would you like to pay?**, one choice per term (the
   shortest chosen), and **Continue to checkout**. Someone who already pays for a plan is told this changes it.
5. **Test checkout** (`/upgrade/checkout?plan=<id>&months=<term>`): an order summary with the term's price, and a
   card form under a banner saying
   **no payment is taken**. The card fields have no names, so the browser never sends them; the server reads only
   `planId` and `months` and ignores anything else. **Complete purchase** posts it. A plan sold for one term needs
   no `months`; for several, a term it isn't sold for is a 400 that sends you back to choose.
6. **The stand-in for payment** (`POST /upgrade/checkout`): it buys the plan for the term, a
   [subscription](#subscriptions) with its first invoice paid by `test-checkout`, which sets the plan on the
   buyer's host straight away (or keeps trying), and redirects to done.
7. **Done** (`/upgrade/done?subscription=<id>`, only for the signed-in identity's own): "You're on B", or that the
   change is on its way, or that it failed, with the price and when it renews; then **Back to your Homebase**, or
   on a group plan **Name the people on it** (`/account`).

A plan not for sale cannot be confirmed, checked out or bought (400). The done page names the plan bought even
once it is no longer for sale.

The pages are Razor components (`src/Raven.Hosting/Components`), rendered on the server by the endpoints in
`UpgradePage.cs`, with no JavaScript. They are in Raven Hosting's "Ember Glow" brand (light theme):
`wwwroot/assets/site.css` holds the palette (off-white pages, white cards, Night and Ink text, Ember accents), the type
and the components, checked for WCAG 2.2 AA contrast. Sora and Inter (SIL Open Font License, licences beside them) are
served from `wwwroot/assets/fonts`, so nothing loads from anyone else. The brand's marks are in `wwwroot/assets/brand`
and its icons (favicons, app icons, `site.webmanifest`) at the site root. A plan for sale with no price yet is not shown.

**Back to your Homebase** goes to `https://<identity>/owner/settings/plan` (`UpgradePage.ReturnUrl`, the one
place to change it).

`/.well-known/youauth-client.json` names the service (`RavenHosting__DisplayName`) and its callback, so the
identity's consent screen shows the name and accepts no other callback.

Security:

- `id` and `host` are hints anyone can write into a link. The identity is the one the service stored with the
  state and then completed the token exchange with, never `id` or the callback's `identity`; `host` is used only
  if it is a known host, and only to decide which host to ask first.
- Forms carry an antiforgery token; the session and antiforgery cookies are `Secure` and `HttpOnly`.
- The data-protection keys are in the database (`data_protection_keys`), so sessions and logins in progress
  survive a restart. They are not encrypted at rest (the service logs a warning saying so): protect the
  database file.

## Admin pages

`/admin` on the site does everything `raven.sh` does with the create key, for people rather than a shared key, in
three groups:

- **Subscriptions:** every identity's [subscription](#subscriptions), filterable (paid, free, expiring, in grace,
  host not in sync, with add-ons); each one's page with its plan, until when, what it gets with its add-ons, its host,
  whose paid plan it is on, the subscription it pays for (term, auto-renew on or off, members and shares, invoices
  with **Mark paid** and a reference, **Extend**), add-ons, changing its plan, and its history. **Plans**, their seats
  and their prices.
- **Marketing:** invitation codes (make, change, disable, take or give back a use as a signup would, copy the code
  or its sign-up link); **Channels**, the code stats by source, each channel opening to who signed up with it, with which code, and
  the subscription each has now; **Renewals**, paid subscriptions running out in 7, 30
  or 90 days or in their grace, with their owner, seats, auto-renew and any open invoice, and those moved back to
  free, with a CSV export.
- **Safety:** **Abuse reports**, filed on the site's `/report` page ("Report abuse").

The overview shows how each group stands and the subscriptions that need a look. **Checkout** (who may buy) is a
beta switch, not a tab: while it is closed, a strip under the menu says so and links to it.

- **Who:** admins sign in with their own Homebase identity, the same YouAuth sign-in as the upgrade page (one hour).
  Who is an admin is a list in the database, managed with the create key: `raven.sh admins add <identity>`, `admins
  remove`, `admins` (`/api/v1/admins`: `GET /`, `PUT /<identity>`, `DELETE /<identity>`). There are none at first.
  Someone not signed in is sent to sign in and back (`/sign-in?next=admin`); someone signed in who is not an admin
  gets a 404 "Not found". Admins see an **Admin** link in the site's header.
- **Changes** are forms with the antiforgery token, each answered with a redirect back saying what it did or why
  it was refused; a refused form comes back as it was sent. Each change is logged (`Raven.Hosting.Admin`) with who
  made it.
- **Look:** Tailwind CSS, responsive, its own stylesheet apart from the site's: `src/Raven.Hosting/Styles/admin.css`
  and the classes in `Components/Admin/` build into `wwwroot/assets/admin.css`, which is committed. After changing
  an admin page's classes, run `./build-admin-css.sh` (it fetches Tailwind's standalone CLI, pinned and checksummed,
  into `.tools/`; no Node). CI fails when the committed stylesheet is not what the sources build
  (`./build-admin-css.sh --check`).

## Configuration

| Key | Value |
|---|---|
| `RavenHosting__DatabasePath` | required; e.g. `/data/invitation-codes.db`, on a persistent volume |
| `RavenHosting__SignupApiKey` | required; the signup key, for the identity hosts |
| `RavenHosting__CreateApiKey` | required; the create key, for admins |
| `RavenHosting__RequestsPerMinutePerIp` | optional, default 300; more than that from one client is a 429 |
| `RavenHosting__IdentityHosts__0`, `__1`, ... | optional; the identity hosts' provisioning domains, e.g. `provisioning.demo.dominion.id`. None: grants stay pending |
| `RavenHosting__AccountServiceApiKey` | required with hosts; the plan key, the same on every host (odin-core `Registry__AccountService__ApiKey`). Give it only to Homebase-run hosts |
| `RavenHosting__GrantRetryIntervalSeconds` | optional, default 60; how often due grants are tried |
| `RavenHosting__ReconcileIntervalHours` | optional, default 24 |
| `RavenHosting__PublicDomain` | optional; the service's own public domain, e.g. `ravenhosting.cloud`: its YouAuth client id and where the site and `/upgrade` are. None: both are off (404) |
| `RavenHosting__DisplayName` | optional; what the identity's consent screen calls the service. Defaults to the public domain |

SQLite means **one instance**, and the database file should be backed up.

Against junk traffic: a request without the right key is refused before its body is read, a body over 4 KB
is a 413, and each client IP gets `RequestsPerMinutePerIp`. The client IP comes from Caddy's `X-Forwarded-For`,
which is trusted only from loopback and private addresses. Volume floods have to be stopped before the box,
by a firewall or a CDN.

Keys are 32 random bytes, base64url: `openssl rand -base64 32 | tr '+/' '-_' | tr -d '='`. Give the
signup key to Homebase-run identity hosts only (`Registry__InvitationCodeService__ApiKey` in odin-core),
never to self-hosted servers.

## Known gaps

- Grants are kept in this service: it now holds which identities were given which plans, and why.
- The plan key lets its holder set any identity's plan on every host; it cannot do anything else there.
- The upgrade page takes no payment: completing the test checkout, or paying an invoice on `/account`, marks it paid.
  It must not be turned on for real owners until payment replaces the stand-in. A provider would only mark invoices
  paid (`SubscriptionService.PayAsync`); nobody is told an invoice is open, other than on `/account` and Renewals.
- A change of plan takes effect at once; there is no downgrade at renewal, and no proration.

- Requests are limited per client IP. Only odin-core can limit the people behind its public signup page, so
  that limit belongs there.
- An identity host that crashes between taking a use and registering leaks that use.
- Codes stay valid after their owner is deleted, until someone disables them.
