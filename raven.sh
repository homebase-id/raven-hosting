#!/bin/bash
#
# Talk to Raven Hosting's service: invitation codes, plans and plan grants.
#
# Keys come from the environment, or from ~/.raven-hosting (never from this repo):
#   RAVEN_HOSTING_CREATE_KEY=...
#   RAVEN_HOSTING_SIGNUP_KEY=...
#   RAVEN_HOSTING_URL=https://ravenhosting.cloud   (optional; this is the default)
# Run `./raven.sh setup` once to write that file.
#
set -euo pipefail

CONFIG="$HOME/.raven-hosting"
[[ -f "$CONFIG" ]] && source "$CONFIG"
URL="${RAVEN_HOSTING_URL:-https://ravenhosting.cloud}"
API="$URL/api/v1/invitation-codes"
PLANS="$URL/api/v1/plans"
GRANTS="$URL/api/v1/plans/grants"
REPORTS="$URL/api/v1/reports"
PURCHASES="$URL/api/v1/purchases"
ADMINS="$URL/api/v1/admins"

usage() {
  cat <<EOF
raven.sh - manage Raven Hosting's invitation codes, plans and plan grants at $URL

An invitation code lets people sign up for a Homebase identity. Each code can be used a set number
of times. Codes look like K7QX-M2PD-9FHW; you can type them in any case, with or without dashes.

FIRST TIME
  setup
      Asks for the service's API keys, checks each one with the service, and saves them to
      $CONFIG (only you can read it). The keys are not passwords you make up: they are in
      the Hetzner vault in DevOps, under secrets.raven_hosting. Setup says which is which.
      Run it again to change a key; Enter keeps the one already saved.

CHECK THE SERVICE
  ping
      Prints "pong" if the service is up.

HOSTING PLANS  (create key)
  A plan is what an identity gets: a quota and whether it has a public home page. The identity
  hosts have no plans of their own; this service sends what a plan gives whenever it gives one.
  A plan also says whether it has a public email address and how many email aliases it has;
  those are only shown on its card, and the hosts are not told.
  There are no built-in plans: create them before making codes for them.

  plans [--json]
      Lists the plans as a table: id, name, storage, public page, public email, email aliases,
      for sale, prices and sort order.
        --json              print the service's JSON instead of the table (the table needs jq)

  plan <plan id> [--json]
      Shows everything about one plan: name, description, storage, public page, public email,
      email aliases, for sale, recommended, each price, sort order, and when it was made and last
      changed. Any case finds it.
        --json              print the service's JSON instead (the readable form needs jq)

  plan-create --id <plan id> --name <text> --quota-bytes <n> --public-web-presence true|false
              [--public-email true|false] [--email-aliases <n>] [--seats <n>]
              [--price <months>:<dollars>]... [--for-sale true|false] [--sort-order <n>]
              [--recommended true|false] [--description <text>]
      Makes a new plan and prints it.
        --id <plan id>                 required: letters, digits, '.', '_' and '-', e.g. veterans.
                                       Any case finds it; it cannot be changed later
        --name <text>                  required: what owners see, e.g. "Veterans"
        --quota-bytes <n>              required: bytes it may store, more than 0, e.g. 1099511627776
                                       for 1 TB. There is no unlimited
        --public-web-presence true|false   required: whether it has a public home page
        --public-email true|false      optional, false by default: whether it has a public email
                                       address. Shown on its card only
        --email-aliases <n>            optional, 0 by default: how many email aliases it has.
                                       Shown on its card only
        --seats <n>                    optional, 1 by default: how many identities it covers. More
                                       than 1 makes it a group plan, whose storage the one who pays
                                       shares out between them, at least 1 GB each
        --price <months>:<dollars>     optional, once per billing term: what it costs, in US
                                       dollars, for that many months (1 to 120), e.g.
                                       --price 1:5 --price 12:50 --price 24:90 for \$5 a month,
                                       \$50 a year or \$90 for two years. 0 is free. Owners pick a
                                       term at checkout; the page shows what longer terms save
        --for-sale true|false          optional, false by default: whether owners can buy it on
                                       the upgrade page. A plan for sale needs a price
        --sort-order <n>               optional, 0 by default: plans are listed by it, then by id
        --recommended true|false       optional, false by default: mark its card "Recommended" for
                                       visitors. Someone signed in is recommended the next size up
                                       from what they have instead
        --description <text>           optional: a line shown on its card

  plan-edit <plan id> [--name <text>] [--quota-bytes <n>] [--public-web-presence true|false]
            [--public-email true|false] [--email-aliases <n>] [--seats <n>] [--for-sale true|false]
            [--sort-order <n>] [--recommended true|false] [--description <text>]
      Changes only what you give. Identities already on the plan keep what they were given; the
      change applies to the next signup or purchase. To retire a plan, use --for-sale false.
      Prices are changed with plan-price.

  plan-price <plan id> <months> <dollars>
      Sets what the plan costs for that many months: adds the term, or changes its price.
      e.g. ./raven.sh plan-price veterans 12 45 for \$45 a year. Purchases already made keep the
      price they were made at.
  plan-price <plan id> <months> --remove
      Stops selling the plan for that term. [409] means it is the last price of a plan for
      sale: mark it not for sale first, or add another price.

  plan-delete <plan id>
      Deletes a plan no code or grant uses. [409] means one does: mark it not for sale instead.

CREATE AND LIST CODES  (create key)
  create --uses <n> --source <text> --plan <plan id> [--owner <identity>]
      Makes a new code and prints it.
        --uses <n>             required: how many people can sign up with it (1-10000)
        --source <text>        required: free text noting where the code came from,
                               e.g. --source "spring campaign"
        --owner <identity>     optional: the identity the code belongs to, e.g. frodo.demo.rocks
        --plan <plan id>       required: the hosting plan people who sign up with this code get,
                               e.g. --plan registered. It must be one of the plans (see plans);
                               it decides their quota and whether they get a public home page.

  list [--owner <identity>] [--json]
      Lists codes as a table: plan, uses left of uses allowed, owner, source, and whether each is
      disabled. All of them, or just one owner's.
        --json              print the service's JSON instead of the table (the table needs jq)

SEE HOW CODES ARE DOING  (create key)
  stats [--since <days>] [--json]
      Counts codes, signups and the signups still left: in total, by source, by owner, by plan,
      and per week. "left" is what the codes still allow now; disabled codes count none.
        --since <days>      count only signups from the last <days> days. The code counts
                            (codes, uses allowed, left, never used, used up, disabled) are always
                            about every code as it is now.
        --json              print the service's JSON instead of the report (the report needs jq)

CHANGE A CODE  (create key)
  edit <code> [--uses <n>] [--source <text>] [--owner <identity> | --no-owner]
              [--plan <plan id>]
      Changes only what you give; everything else stays as it is.
        --uses <n>          the new total number of signups allowed (1-10000). Uses already taken
                            still count, so lowering it below them leaves none left.
        --source <text>     new note on where the code came from
        --owner <identity>  give the code to this identity
        --no-owner          take the code away from its owner
        --plan <plan id>    people who sign up with it from now on get this hosting plan; it
                            must be one of the plans

  disable <code>
      Stops the code working, whatever uses it has left. Nothing else changes.
  enable <code>
      Lets a disabled code work again.

SUBSCRIPTIONS  (create key)
  An identity's subscription is the latest plan granted to it; the admin pages (/admin) show them,
  with add-ons, extending paid ones, and renewals.

  grant --id <identity> --plan <plan id> [--host <provisioning domain>] [--reference <ref>]
      Owes an identity a plan and sets it on the identity host that serves it, straight away. If no
      host can take it yet, the grant stays pending and the service keeps trying.
        --id <identity>        required: e.g. frodo.demo.rocks
        --plan <plan id>       required: one of the plans, e.g. veterans. The grant keeps what the
                               plan gives now and sets all of it on the host
        --host <domain>        optional: the provisioning domain of the identity's host, tried
                               first; only hosts the service knows are ever called
        --reference <ref>      optional: your id for it; the same reference twice is one grant

  grants [--id <identity>] [--status pending|applied|failed] [--json]
      Lists grants, newest first, as a table: id, identity, plan, status, host, attempts and the
      last error. "failed" means the host refused the plan (an empty plan id or a quota that is
      not more than 0); "pending" is retried by itself.
        --json              print the service's JSON instead of the table (the table needs jq)

  retry <grant id>
      Tries a pending or failed grant again now, e.g. once the identity is on a host.

ADMIN PAGES  (create key)
  The admin pages, at /admin on the site, do everything this script does with the create key, for
  admins signed in with their own Homebase identity. Nobody is an admin until added here.

  admins [--json]
      Lists the admins.
  admins add <identity>
      Lets this identity use the admin pages, e.g. ./raven.sh admins add toddmitchell.me
  admins remove <identity>
      Takes it off the admins.

CHECKOUT, WHO MAY BUY  (create key)
  Until real card processing is in, buying a plan on the upgrade page is locked: everyone can see
  the plans and sign in, but only testers can check out. A new service starts locked, with no
  testers. Admin grants (grant) are not affected.

  purchases [--json]
      Shows whether purchases are open, and the testers.
  purchases open | close
      Lets everyone buy, or only the testers.
  purchases allow <identity>
      Lets this identity buy while purchases are closed.
  purchases disallow <identity>
      Takes it off the testers.

ABUSE REPORTS  (create key)
  reports [--since <days>] [--json]
      Prints the reports filed on https://ravenhosting.cloud/report, newest first: who sent it and
      how to reach them, what and who it is about, what is wrong, where it is, and anything else
      they wrote. Nobody is told when one comes in: check here.
        --since <days>      only those from the last <days> days
        --json              print the service's JSON instead (the readable form needs jq)

TEST A CODE  (signup key; odin-core does these during a real signup)
  validate <code>
      Says whether the code exists, is not disabled, and still has a use left. Does not use it up.
  use <code>
      Takes one use of the code, exactly as a real signup would. Careful on real codes.
  undo <code>
      Gives back the code's most recent use.

EXAMPLES
  ./raven.sh plan-create --id veterans --name Veterans --quota-bytes 1099511627776
                          --public-web-presence true --price 1:5 --price 12:50 --for-sale true
      a 1 TB plan with a public home page, for sale at \$5 a month or \$50 a year

  ./raven.sh plan-price veterans 24 90
      also sell it for two years at \$90

  ./raven.sh plan-edit veterans --for-sale false
      stop selling it; identities on it keep it

  ./raven.sh create --uses 5 --source "spring campaign" --plan free --owner frodo.demo.rocks
      5 signups on the free plan, owned by frodo

  ./raven.sh create --uses 10 --source "conference booth" --plan registered
      10 signups on the registered plan, no owner

  ./raven.sh list --owner frodo.demo.rocks

  ./raven.sh stats --since 30
      signups in the last 30 days, by source, owner, plan and week

  ./raven.sh edit K7QX-M2PD-9FHW --uses 20 --plan veterans
      allow 20 signups in total, on the veterans plan from now on

  ./raven.sh disable K7QX-M2PD-9FHW
  ./raven.sh validate K7QX-M2PD-9FHW

  ./raven.sh grant --id frodo.demo.rocks --plan veterans
      give frodo the veterans plan on whichever host serves frodo

  ./raven.sh grants --status pending

Every command prints the service's answer and the HTTP status, e.g. [200]. [401] means a wrong or
missing key; [400] means bad input, and the message says what; [404] on edit means no such code
or plan; [409] on plan-create means the id is taken, on plan-delete that the plan is in use, on
grant that the reference is another grant's, on retry that the grant is already applied.
EOF
}

die() { printf '%s\n' "$@" >&2; exit 1; }

need() { [[ -n "${!1:-}" ]] || die "$1 is not set; run ./raven.sh setup"; }

check_uses() { [[ "$1" =~ ^[0-9]+$ ]] || die "--uses must be a number, e.g. --uses 5"; }

# A plan id for use in the URL
check_plan_id() { [[ "$1" =~ ^[A-Za-z0-9._-]{1,64}$ ]] || die "$2"; }

plan_fields() { # usage [option value]...; the plan options as JSON fields, in the array fields
  local usage="$1"; shift
  fields=()
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --name|--description)
        [[ $# -ge 2 ]] || die "$usage"
        fields+=("\"${1#--}\":$(json_string "$2")"); shift 2 ;;
      --quota-bytes)
        [[ "${2:-}" =~ ^[0-9]+$ ]] || die "--quota-bytes must be a number of bytes, e.g. --quota-bytes 1099511627776"
        fields+=("\"quotaBytes\":$2"); shift 2 ;;
      --sort-order)
        [[ "${2:-}" =~ ^-?[0-9]+$ ]] || die "--sort-order must be a whole number, e.g. --sort-order 1"
        fields+=("\"sortOrder\":$2"); shift 2 ;;
      --email-aliases)
        [[ "${2:-}" =~ ^[0-9]+$ ]] || die "--email-aliases must be a number, 0 or more, e.g. --email-aliases 5"
        fields+=("\"emailAliases\":$((10#$2))"); shift 2 ;;
      --seats)
        [[ "${2:-}" =~ ^[0-9]+$ && $((10#$2)) -ge 1 ]] || die "--seats must be a number, 1 or more, e.g. --seats 5"
        fields+=("\"seats\":$((10#$2))"); shift 2 ;;
      --public-web-presence|--public-email|--for-sale|--recommended)
        [[ "${2:-}" =~ ^(true|false)$ ]] || die "$1 must be true or false"
        case "$1" in
          --public-web-presence) name=publicWebPresence ;;
          --public-email) name=publicEmail ;;
          --recommended) name=recommended ;;
          *) name=forSale ;;
        esac
        fields+=("\"$name\":$2"); shift 2 ;;
      *) die "unknown option: $1" "$usage" ;;
    esac
  done
}

# Dollars as typed (5, 4.99, 4.5) in cents; dies with the message given for anything else
to_cents() { # dollars message
  [[ "$1" =~ ^([0-9]+)(\.([0-9]{1,2}))?$ ]] || die "$2"
  local cents="${BASH_REMATCH[3]:-00}"
  [[ ${#cents} -eq 1 ]] && cents="${cents}0"
  printf '%d' $(( 10#${BASH_REMATCH[1]} * 100 + 10#$cents ))
}

price_usage="--price must be <months>:<dollars>, e.g. --price 12:50 for \$50 a year"

# A JSON string, quotes included
json_string() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g')"; }

call() { # method path [body]; sends $key, to $base (the codes API unless set)
  local method="$1" path="$2" body="${3:-}"
  local args=(-sS -X "$method" -H "Odin-Api-Key: $key" -w '\n[%{http_code}]\n')
  [[ -n "$body" ]] && args+=(-H 'Content-Type: application/json' -d "$body")
  curl "${args[@]}" "${base:-$API}$path"
}

# The HTTP status the service answers a key with: 200 it is the right key, 401 it is not, 000 no answer
key_check() { # key method url [body]
  local args=(-s -o /dev/null -w '%{http_code}' -X "$2" -H "Odin-Api-Key: $1")
  [[ -n "${4:-}" ]] && args+=(-H 'Content-Type: application/json' -d "$4")
  curl "${args[@]}" "$3" || true
}

code_body() { printf '{"code":%s}' "$(json_string "$1")"; }

render() { # as_json path jq-program; GETs path and prints the answer through jq, or as is with --json or no jq
  local as_json="$1" path="$2" program="$3" response status body
  if [[ "$as_json" == true ]] || ! command -v jq >/dev/null; then
    call GET "$path"
    return
  fi
  response="$(call GET "$path")"
  status="${response##*$'\n'}"; body="${response%$'\n'*}"
  [[ "$status" == "[200]" ]] || { printf '%s\n%s\n' "$body" "$status"; exit 1; }
  printf '%s' "$body" | jq -r "$program"
}

# A code for use in the URL: spaces dropped, nothing but letters, digits and dashes
path_code() {
  local code="${1// /}"
  [[ "$code" =~ ^[A-Za-z0-9-]+$ ]] || return 1
  printf '%s' "$code"
}

cmd="${1:-}"
shift || true

case "$cmd" in
  plans|plan|plan-create|plan-edit|plan-price|plan-delete|create|list|stats|edit|disable|enable|grant|grants|retry|reports|purchases|admins) need RAVEN_HOSTING_CREATE_KEY; key="$RAVEN_HOSTING_CREATE_KEY" ;;
  validate|use|undo) need RAVEN_HOSTING_SIGNUP_KEY; key="$RAVEN_HOSTING_SIGNUP_KEY" ;;
esac

case "$cmd" in
  setup)
    cat <<INTRO
raven.sh needs the service's API keys to talk to Raven Hosting at $URL.

They are not passwords you make up: they were made when the service was set up, and are kept in
the Hetzner vault in the DevOps repo. To see them (needs the vault password):
  cd DevOps/hetzner/ansible
  ansible-vault view ansible-vault.yml | grep -A2 raven_hosting
If you don't have the vault password, ask whoever runs the deploys.

Paste each key and press Enter. What you type is not shown.

INTRO
    saved_create="${RAVEN_HOSTING_CREATE_KEY:-}"; saved_signup="${RAVEN_HOSTING_SIGNUP_KEY:-}"

    echo "1. The create key (secrets.raven_hosting.create_api_key) - you need this one."
    echo "   For managing the service: plans, codes, stats and grants."
    [[ -n "$saved_create" ]] && echo "   One is saved already; press Enter to keep it."
    read -rsp "   Create key: " create; echo
    create="${create:-$saved_create}"
    [[ -n "$create" ]] || die "   No create key given; nothing changed."
    case "$(key_check "$create" GET "$PLANS/")" in
      200) echo "   OK: the service accepted it." ;;
      401) die "   The service refused it: that is not the create key." \
               "   Check you copied create_api_key (not signup_api_key), all of it. Nothing changed." ;;
      *)   echo "   Could not check it: the service did not answer. Saving it anyway." ;;
    esac
    echo

    echo "2. The signup key (secrets.raven_hosting.signup_api_key) - optional."
    echo "   Only for testing a code the way a signup does: validate, use and undo."
    echo "   Most people don't need it; press Enter to skip."
    [[ -n "$saved_signup" ]] && echo "   One is saved already; press Enter to keep it."
    read -rsp "   Signup key: " signup; echo
    signup="${signup:-$saved_signup}"
    if [[ -n "$signup" ]]; then
      case "$(key_check "$signup" POST "$API/validate" '{"code":"0000-0000-0000"}')" in
        200) echo "   OK: the service accepted it." ;;
        401) die "   The service refused it: that is not the signup key." \
                 "   Check you copied signup_api_key (not create_api_key), all of it. Nothing changed." ;;
        *)   echo "   Could not check it: the service did not answer. Saving it anyway." ;;
      esac
    else
      echo "   Skipped."
    fi
    echo

    umask 077
    {
      printf 'RAVEN_HOSTING_CREATE_KEY=%q\n' "$create"
      if [[ -n "$signup" ]]; then printf 'RAVEN_HOSTING_SIGNUP_KEY=%q\n' "$signup"; fi
    } > "$CONFIG"
    echo "Saved to $CONFIG (only you can read it). Try: ./raven.sh plans"
    ;;
  ping)
    curl -sS -w '\n[%{http_code}]\n' "$URL/ping"
    ;;
  plans)
    [[ "${1:-}" == "--json" || -z "${1:-}" ]] || die "usage: ./raven.sh plans [--json]"
    as_json=false; [[ "${1:-}" == "--json" ]] && as_json=true
    base="$PLANS" render "$as_json" "/" '
      def pad(n): . + (" " * ([n - length, 0] | max));
      def clip(n): if length > n then .[0:n - 3] + "..." else . end;
      . as $plans
      | ["ID", "NAME", "STORAGE", "SEATS", "PUBLIC PAGE", "PUBLIC EMAIL", "ALIASES", "FOR SALE", "PRICES", "ORDER"] as $head
      | [$plans[] | [.id, (.name | clip(30)), .storage, "\(.seats)", (if .publicWebPresence then "yes" else "no" end),
                     (if .publicEmail then "yes" else "no" end), "\(.emailAliases)",
                     (if .forSale then "yes" else "no" end),
                     (if (.prices | length) == 0 then "-" else [.prices[].text] | join(", ") end),
                     "\(.sortOrder)"]] as $rows
      | ([$head] + $rows) as $table
      | [range(0; $head | length) as $i | [$table[][$i] | length] | max] as $widths
      | if ($rows | length) == 0 then "(no plans; make one with ./raven.sh plan-create)"
        else ($table[] | [range(0; length) as $i | .[$i] | pad($widths[$i])] | join("  ") | sub(" +$"; ""))
        end'
    ;;
  plan)
    plan_usage="usage: ./raven.sh plan <plan id> [--json]"
    check_plan_id "${1:-}" "$plan_usage"
    as_json=false
    case "${2:-}" in
      "") ;;
      --json) as_json=true ;;
      *) die "$plan_usage" ;;
    esac
    base="$PLANS" render "$as_json" "/$1" '
      def when: . / 1000 | floor | strftime("%Y-%m-%d %H:%M UTC");
      def yes: if . then "yes" else "no" end;
      "\(.name) (\(.id))",
      (if .description == "" then "(no description)" else .description end),
      "",
      "  storage       \(.storage) (\(.quotaBytes) bytes)",
      "  seats         \(.seats)",
      "  public page   \(.publicWebPresence | yes)",
      "  public email  \(.publicEmail | yes)",
      "  email aliases \(.emailAliases)",
      "  for sale      \(.forSale | yes)",
      "  recommended   \(.recommended | yes)",
      "  prices        " + (if (.prices | length) == 0 then "none"
                            else [.prices[] | .text + (if .months == 1 or .months == 12 then "" else " (\(.months) months)" end)]
                                 | join("\n                ") end),
      "  sort order    \(.sortOrder)",
      "  created       \(.createdAt | when)",
      "  changed       \(.updatedAt | when)"'
    ;;
  plan-create)
    plan_create_usage="usage: ./raven.sh plan-create --id <plan id> --name <text> --quota-bytes <n> --public-web-presence true|false [--public-email true|false] [--email-aliases <n>] [--seats <n>] [--price <months>:<dollars>]... [--for-sale true|false] [--sort-order <n>] [--recommended true|false] [--description <text>]  (see ./raven.sh help)"
    plan_id=""; rest=(); prices=()
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --id) [[ -n "${2:-}" ]] || die "$plan_create_usage"
              plan_id="$2"; shift 2 ;;
        --price) [[ "${2:-}" =~ ^([0-9]+):(.+)$ ]] || die "$price_usage"
                 months="${BASH_REMATCH[1]}"
                 cents="$(to_cents "${BASH_REMATCH[2]}" "$price_usage")" || exit 1
                 prices+=("{\"months\":$((10#$months)),\"amountCents\":$cents}"); shift 2 ;;
        *) rest+=("$1"); shift ;;
      esac
    done
    plan_fields "$plan_create_usage" ${rest[@]+"${rest[@]}"}
    joined=" ${fields[*]-} "
    [[ -n "$plan_id" && "$joined" == *'"name":'* && "$joined" == *'"quotaBytes":'* &&
       "$joined" == *'"publicWebPresence":'* ]] || die "$plan_create_usage"
    fields+=("\"id\":$(json_string "$plan_id")")
    [[ ${#prices[@]} -gt 0 ]] && fields+=("\"prices\":[$(IFS=,; echo "${prices[*]}")]")
    base="$PLANS" call POST / "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  plan-edit)
    plan_edit_usage="usage: ./raven.sh plan-edit <plan id> [--name <text>] [--quota-bytes <n>] [--public-web-presence true|false] [--public-email true|false] [--email-aliases <n>] [--seats <n>] [--for-sale true|false] [--sort-order <n>] [--recommended true|false] [--description <text>]  (prices: plan-price)"
    plan_id="${1:-}"
    check_plan_id "$plan_id" "$plan_edit_usage"
    shift
    plan_fields "$plan_edit_usage" "$@"
    [[ ${#fields[@]} -gt 0 ]] || die "nothing to change" "$plan_edit_usage"
    base="$PLANS" call PATCH "/$plan_id" "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  plan-price)
    plan_price_usage="usage: ./raven.sh plan-price <plan id> <months> <dollars>, or <plan id> <months> --remove"
    check_plan_id "${1:-}" "$plan_price_usage"
    [[ "${2:-}" =~ ^[0-9]+$ && $# -eq 3 ]] || die "$plan_price_usage"
    if [[ "$3" == "--remove" ]]; then
      base="$PLANS" call DELETE "/$1/prices/$((10#$2))"
    else
      cents="$(to_cents "$3" "<dollars> must be an amount in dollars, e.g. 50 or 4.99")" || exit 1
      base="$PLANS" call PUT "/$1/prices/$((10#$2))" "{\"amountCents\":$cents}"
    fi
    ;;
  plan-delete)
    check_plan_id "${1:-}" "usage: ./raven.sh plan-delete <plan id>"
    base="$PLANS" call DELETE "/$1"
    ;;
  create)
    create_usage="usage: ./raven.sh create --uses <n> --source <text> --plan <plan id> [--owner <identity>]  (see ./raven.sh help)"
    uses=""; source_text=""; plan_id=""; owner_json=null
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --uses) uses="${2:-}"; shift 2 ;;
        --source) source_text="${2:-}"; shift 2 ;;
        --owner) [[ -n "${2:-}" ]] && owner_json="$(json_string "$2")"; shift 2 ;;
        --plan) [[ -n "${2:-}" ]] || die "$create_usage"
                plan_id="$2"; shift 2 ;;
        *) die "unknown option: $1" "$create_usage" ;;
      esac
    done
    [[ -n "$uses" && -n "$source_text" && -n "$plan_id" ]] || die "$create_usage"
    check_uses "$uses"
    call POST / \
      "{\"owner\":$owner_json,\"maxUses\":$uses,\"source\":$(json_string "$source_text"),\"planId\":$(json_string "$plan_id")}"
    ;;
  list)
    list_usage="usage: ./raven.sh list [--owner <identity>] [--json]"
    query=""; as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --owner) [[ -n "${2:-}" ]] || die "$list_usage"
                 query="?owner=$(printf '%s' "$2" | jq -sRr @uri 2>/dev/null || printf '%s' "$2")"; shift 2 ;;
        --json) as_json=true; shift ;;
        *) die "unknown option: $1" "$list_usage" ;;
      esac
    done
    render "$as_json" "/$query" '
      def pad(n): . + (" " * ([n - length, 0] | max));
      def clip(n): if length > n then .[0:n - 3] + "..." else . end;
      . as $codes
      | ["CODE", "PLAN", "LEFT", "OWNER", "SOURCE", ""] as $head
      | [$codes[] | [.code, (.planId // "-"), "\(.usesLeft)/\(.maxUses)", (.owner // "-"),
                     (if .source == "" then "-" else .source | clip(40) end),
                     (if .disabled then "disabled" else "" end)]] as $rows
      | ([$head] + $rows) as $table
      | [range(0; $head | length) as $i | [$table[][$i] | length] | max] as $widths
      | [$codes[] | select(.disabled | not) | .usesLeft] as $left
      | if ($rows | length) == 0 then "(no codes)"
        else
          ($table[] | [range(0; length) as $i | .[$i] | pad($widths[$i])] | join("  ") | sub(" +$"; "")),
          "",
          "\($rows | length) code\(if ($rows | length) == 1 then "" else "s" end), \($left | add // 0) uses left on those not disabled"
        end'
    ;;
  stats)
    stats_usage="usage: ./raven.sh stats [--since <days>] [--json]"
    query=""; as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --since) [[ "${2:-}" =~ ^[0-9]+$ && "$2" -ge 1 ]] || die "--since must be a number of days, e.g. --since 30"
                 query="?sinceDays=$2"; shift 2 ;;
        --json) as_json=true; shift ;;
        *) die "unknown option: $1" "$stats_usage" ;;
      esac
    done
    render "$as_json" "/stats$query" '
      def left(n): tostring | . + (" " * ([n - length, 0] | max));
      def right(n): tostring | (" " * ([n - length, 0] | max)) + .;
      def row(name): "  " + (name | left(34)) + (.codes | right(7)) + (.usesAllowed | right(9)) + (.signups | right(9)) + (.usesLeft | right(7));
      (([.byWeek[].signups] | max) // 1) as $most
      | "Signups " + (if .since then "since " + (.since / 1000 | floor | strftime("%Y-%m-%d")) else "of all time" end),
        "",
        "  " + ("" | left(34)) + ("codes" | right(7)) + ("allowed" | right(9)) + ("signups" | right(9)) + ("left" | right(7)),
        (.totals | row("total")),
        "",
        "by source",
        (.bySource[] | row(if .name == "" then "(no source)" else .name end)),
        "",
        "by owner",
        (if (.byOwner | length) == 0 then "  (no code has an owner)" else (.byOwner[] | row(.name)) end),
        "",
        "by plan",
        (.byPlan[] | row(.name // "(no plan)")),
        "",
        "signups per week, from Monday",
        (if (.byWeek | length) == 0 then "  (none)"
         else (.byWeek[] | "  " + .week + (.signups | right(6)) + "  " + ("#" * ((.signups * 40 / $most) | ceil) // ""))
         end),
        "",
        "codes: \(.neverUsed) never used, \(.usedUp) used up, \(.disabled) disabled"'
    ;;
  edit)
    edit_usage="usage: ./raven.sh edit <code> [--uses <n>] [--source <text>] [--owner <identity> | --no-owner] [--plan <plan id>]"
    code="$(path_code "${1:-}")" || die "$edit_usage"
    shift
    fields=()
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --uses) check_uses "${2:-}"; fields+=("\"maxUses\":$2"); shift 2 ;;
        --source) [[ -n "${2:-}" ]] || die "$edit_usage"
                  fields+=("\"source\":$(json_string "$2")"); shift 2 ;;
        --owner) [[ -n "${2:-}" ]] || die "$edit_usage"
                 fields+=("\"owner\":$(json_string "$2")"); shift 2 ;;
        --no-owner) fields+=("\"owner\":\"\""); shift ;;
        --plan) [[ -n "${2:-}" ]] || die "$edit_usage"
                fields+=("\"planId\":$(json_string "$2")"); shift 2 ;;
        *) die "unknown option: $1" "$edit_usage" ;;
      esac
    done
    [[ ${#fields[@]} -gt 0 ]] || die "nothing to change" "$edit_usage"
    call PATCH "/$code" "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  disable|enable)
    code="$(path_code "${1:-}")" || die "usage: ./raven.sh $cmd <code>"
    disabled=false; [[ "$cmd" == disable ]] && disabled=true
    call PATCH "/$code" "{\"disabled\":$disabled}"
    ;;
  grant)
    grant_usage="usage: ./raven.sh grant --id <identity> --plan <plan id> [--host <provisioning domain>] [--reference <ref>]  (see ./raven.sh help)"
    fields=()
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --id|--plan|--host|--reference)
          [[ -n "${2:-}" ]] || die "$grant_usage"
          case "$1" in
            --id) name=identity ;; --plan) name=planId ;; --host) name=host ;; --reference) name=reference ;;
          esac
          fields+=("\"$name\":$(json_string "$2")"); shift 2 ;;
        *) die "unknown option: $1" "$grant_usage" ;;
      esac
    done
    [[ " ${fields[*]-} " == *'"identity":'* && " ${fields[*]-} " == *'"planId":'* ]] || die "$grant_usage"
    base="$GRANTS" call POST / "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  admins)
    admins_usage="usage: ./raven.sh admins [--json] | add <identity> | remove <identity>"
    case "${1:-}" in
      ""|--json)
        as_json=false; [[ "${1:-}" == "--json" ]] && as_json=true
        base="$ADMINS" render "$as_json" "/" '
          def when: . / 1000 | floor | strftime("%Y-%m-%d %H:%M UTC");
          if length == 0 then "(no admins; add one with ./raven.sh admins add <identity>)"
          else .[] | "\(.identity)  (added \(.addedAt | when))" end'
        ;;
      add|remove)
        [[ $# -eq 2 && "$2" =~ ^[A-Za-z0-9.-]+$ ]] || die "$admins_usage"
        method=PUT; [[ "$1" == remove ]] && method=DELETE
        base="$ADMINS" call "$method" "/$2"
        ;;
      *) die "$admins_usage" ;;
    esac
    ;;
  purchases)
    purchases_usage="usage: ./raven.sh purchases [--json] | open | close | allow <identity> | disallow <identity>"
    case "${1:-}" in
      ""|--json)
        as_json=false; [[ "${1:-}" == "--json" ]] && as_json=true
        base="$PURCHASES" render "$as_json" "/" '
          def when: . / 1000 | floor | strftime("%Y-%m-%d %H:%M UTC");
          (if .open then "Purchases are OPEN: anyone signed in can buy a plan."
           else "Purchases are CLOSED: only the testers can buy a plan." end),
          "",
          (if (.testers | length) == 0 then "(no testers)"
           else "Testers:", (.testers[] | "  \(.identity)  (added \(.addedAt | when))") end)'
        ;;
      open|close)
        [[ $# -eq 1 ]] || die "$purchases_usage"
        open=false; [[ "$1" == open ]] && open=true
        base="$PURCHASES" call PUT /open "{\"open\":$open}"
        ;;
      allow|disallow)
        [[ $# -eq 2 && "$2" =~ ^[A-Za-z0-9.-]+$ ]] || die "$purchases_usage"
        method=PUT; [[ "$1" == disallow ]] && method=DELETE
        base="$PURCHASES" call "$method" "/testers/$2"
        ;;
      *) die "$purchases_usage" ;;
    esac
    ;;
  reports)
    reports_usage="usage: ./raven.sh reports [--since <days>] [--json]"
    qs=""; as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --since) [[ "${2:-}" =~ ^[0-9]+$ ]] || die "--since must be a number of days, e.g. --since 7"
                 qs="?sinceDays=$2"; shift 2 ;;
        --json) as_json=true; shift ;;
        *) die "unknown option: $1" "$reports_usage" ;;
      esac
    done
    base="$REPORTS" render "$as_json" "$qs" '
      def when: . / 1000 | floor | strftime("%Y-%m-%d %H:%M UTC");
      if length == 0 then "(no reports)"
      else [.[] |
        "#\(.id)  \(.createdAt | when)",
        "  from       \(.contactInfo)",
        "  about      \(.contentType), by \(.contentAuthor)",
        "  problem    \(.violation)",
        "  where      \(.evidence | gsub("\n"; "\n             "))",
        (if .additional == "" then empty else "  also       \(.additional | gsub("\n"; "\n             "))" end),
        ""] | .[:-1] | join("\n")
      end'
    ;;
  grants)
    grants_usage="usage: ./raven.sh grants [--id <identity>] [--status pending|applied|failed] [--json]"
    query=(); as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --id) [[ -n "${2:-}" ]] || die "$grants_usage"
              query+=("identity=$(printf '%s' "$2" | jq -sRr @uri 2>/dev/null || printf '%s' "$2")"); shift 2 ;;
        --status) [[ "${2:-}" =~ ^(pending|applied|failed)$ ]] || die "--status must be pending, applied or failed"
                  query+=("status=$2"); shift 2 ;;
        --json) as_json=true; shift ;;
        *) die "unknown option: $1" "$grants_usage" ;;
      esac
    done
    qs=""; [[ ${#query[@]} -gt 0 ]] && qs="?$(IFS='&'; echo "${query[*]}")"
    base="$GRANTS" render "$as_json" "/$qs" '
      def pad(n): . + (" " * ([n - length, 0] | max));
      def clip(n): if length > n then .[0:n - 3] + "..." else . end;
      . as $grants
      | ["ID", "IDENTITY", "PLAN", "STATUS", "HOST", "TRIES", "LAST ERROR"] as $head
      | [$grants[] | ["\(.id)", .identity, .planId, .status, (.host // .hostHint // "-"), "\(.attempts)",
                      ((.lastError // "") | clip(60))]] as $rows
      | ([$head] + $rows) as $table
      | [range(0; $head | length) as $i | [$table[][$i] | length] | max] as $widths
      | if ($rows | length) == 0 then "(no grants)"
        else ($table[] | [range(0; length) as $i | .[$i] | pad($widths[$i])] | join("  ") | sub(" +$"; ""))
        end'
    ;;
  retry)
    [[ "${1:-}" =~ ^[0-9]+$ ]] || die "usage: ./raven.sh retry <grant id>"
    base="$GRANTS" call POST "/$1/retry"
    ;;
  validate|use|undo)
    path="/$cmd"; [[ "$cmd" == undo ]] && path=/undo-use
    call POST "$path" "$(code_body "${1:?code is required}")"
    ;;
  *)
    usage
    [[ -z "$cmd" || "$cmd" == "help" || "$cmd" == "-h" ]] || exit 1
    ;;
esac
