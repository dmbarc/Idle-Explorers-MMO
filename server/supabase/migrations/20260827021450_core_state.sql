-- ═══════════════════════════════════════════════════════════════════════════════
--  Core state: accounts, characters, and everything a player can gain or lose.
--
--  ══ WHAT THIS SCHEMA ASSUMES ═════════════════════════════════════════════════
--
--  The Unity client NEVER connects here. Cloudflare fronts an ASP.NET Core API,
--  the API holds the only privileged credentials, and anything shipped inside a
--  WebGL build is assumed public. That assumption is what the RLS at the bottom
--  enforces rather than trusts: every table denies everyone, and only the service
--  role -- which lives in server configuration and nowhere else -- bypasses it.
--
--  ══ WHAT IS DELIBERATELY NOT HERE ════════════════════════════════════════════
--
--  No generated `level` column. It would need the xp curve written a second time
--  in plpgsql, and two implementations of a progression formula is exactly the
--  drift the shared C# rules tree exists to prevent. xp is stored; level is
--  computed by IdleExplorers.Rules.Levelling on both sides.
--
--  No game logic. No triggers that grant anything, no functions that decide a
--  reward. The rules are C#. What lives here is shape, constraints, and the few
--  invariants a database enforces better than any application can.
-- ═══════════════════════════════════════════════════════════════════════════════

create extension if not exists "pgcrypto";

-- ── Accounts ──────────────────────────────────────────────────────────────────

-- One row per Supabase Auth user. Separate from auth.users because that table is
-- Supabase's and this one is the game's: everything below points here, and a
-- future migration away from Supabase Auth should not have to rewrite them all.
create table account (
    id            uuid primary key references auth.users (id) on delete cascade,
    display_name  text        not null check (length(trim(display_name)) between 1 and 24),
    account_xp    bigint      not null default 0 check (account_xp >= 0),
    created_at    timestamptz not null default now(),
    banned_at     timestamptz
);

-- Additional ways to sign in to the same account.
--
-- Play Games is the reason this exists. Supabase has no Play Games provider, so
-- the API exchanges the plugin's server auth code with Google itself and links the
-- resulting player id here. Anything else -- Apple, a device id -- lands the same
-- way without another schema change.
create table account_identity (
    account_id       uuid        not null references account (id) on delete cascade,
    provider         text        not null check (provider in ('supabase', 'play_games', 'apple')),
    provider_user_id text        not null,
    linked_at        timestamptz not null default now(),
    primary key (account_id, provider),

    -- One external identity cannot be two accounts. Without this, a re-link races
    -- itself into two characters owning one Play Games profile.
    unique (provider, provider_user_id)
);

-- ── Characters ────────────────────────────────────────────────────────────────

create table character (
    id            uuid        primary key default gen_random_uuid(),
    account_id    uuid        not null references account (id) on delete cascade,
    name          text        not null check (length(trim(name)) between 1 and 20),
    class_id      text        not null default '',
    xp            bigint      not null default 0 check (xp >= 0),
    rename_count  integer     not null default 0 check (rename_count >= 0),
    last_map_id   text        not null default '',
    created_at    timestamptz not null default now(),
    deleted_at    timestamptz,

    -- Names are unique per account and only among the living, so deleting a
    -- character frees the name without a second table of tombstones.
    unique nulls not distinct (account_id, name, deleted_at)
);

create index character_by_account on character (account_id) where deleted_at is null;

create table character_skill (
    character_id uuid   not null references character (id) on delete cascade,
    skill_id     text   not null,
    xp           bigint not null default 0 check (xp >= 0),
    primary key (character_id, skill_id)
);

-- ── What a character is carrying ──────────────────────────────────────────────
--
-- Slot-indexed rather than a bag of rows, because the client draws a grid and slot
-- order is player-visible: an auto-sort that shuffles everything is a feature, and
-- a save that shuffles it on its own is a bug.

create table inventory_slot (
    character_id uuid   not null references character (id) on delete cascade,
    slot_index   int    not null check (slot_index >= 0),
    item_id      text   not null,
    quantity     bigint not null check (quantity > 0),
    primary key (character_id, slot_index)
);

-- The bank is per ACCOUNT, not per character. What one character banks, another
-- can withdraw -- that is the point of it.
create table bank_slot (
    account_id uuid   not null references account (id) on delete cascade,
    slot_index int    not null check (slot_index >= 0),
    item_id    text   not null,
    quantity   bigint not null check (quantity > 0),
    primary key (account_id, slot_index)
);

create table equipment (
    character_id uuid not null references character (id) on delete cascade,
    slot_id      text not null,
    item_id      text not null,
    durability   int  not null default 0 check (durability >= 0),
    primary key (character_id, slot_id)
);

-- Durability remembered for gear that is NOT currently worn.
--
-- Without it, taking a helmet off and putting it back on is a free repair: the
-- inventory has no per-item condition to carry, so the piece returns pristine and
-- durability becomes theatre.
create table stored_durability (
    character_id uuid not null references character (id) on delete cascade,
    item_id      text not null,
    durability   int  not null check (durability >= 0),
    primary key (character_id, item_id)
);

-- ── What a character is doing ─────────────────────────────────────────────────

create table activity (
    character_id       uuid        primary key references character (id) on delete cascade,

    kind               text        not null default 'idle'
                                   check (kind in ('idle', 'gather', 'craft', 'combat')),
    skill_id           text        not null default '',
    node_id            text        not null default '',
    target_item_id     text        not null default '',
    recipe_id          text        not null default '',
    monster_id         text        not null default '',

    -- Resolved rates, mirroring IdleExplorers.Rules.ActivityState. Stored rather
    -- than recomputed per settlement so a talent respec mid-session cannot
    -- retroactively re-rate hours already elapsed.
    seconds_per_action real        not null default 3    check (seconds_per_action > 0),
    active_rate_multi  real        not null default 1    check (active_rate_multi > 0),
    afk_rate_multi     real        not null default 0.6  check (afk_rate_multi >= 0),
    xp_per_action      real        not null default 0    check (xp_per_action >= 0),
    special_chance     real        not null default 0    check (special_chance between 0 and 1),

    -- A FRACTION of an action, never leftover seconds. See Settlement: a window can
    -- straddle the moment a tab closed, and seconds banked at the active rate would
    -- silently be spent at the offline one.
    progress           double precision not null default 0
                       check (progress >= 0 and progress < 1),

    started_at         timestamptz not null default now(),

    -- The only clock that matters. Never a client timestamp, at any point, for any
    -- reason. Guarded by a trigger below, because settling a window twice is the
    -- cheapest possible duplication exploit.
    last_settled_at    timestamptz not null default now(),

    -- Set by the 20-second heartbeat while the tab is focused. Seconds covered by
    -- one earn the active rate; the rest earn the offline rate.
    last_heartbeat_at  timestamptz,

    -- Drained by settlement, never added to last_settled_at. A mystic gem granting
    -- 72 hours ADDS here; rewinding a timestamp instead would be a currency printer.
    credited_seconds   bigint      not null default 0 check (credited_seconds >= 0)
);

-- Time only ever moves forward.
create or replace function activity_settled_at_only_advances()
returns trigger
language plpgsql
as $$
begin
    if new.last_settled_at < old.last_settled_at then
        raise exception
            'last_settled_at may not move backwards (% -> %) for character %',
            old.last_settled_at, new.last_settled_at, old.character_id;
    end if;

    return new;
end;
$$;

create trigger activity_settled_at_forward_only
    before update on activity
    for each row
    execute function activity_settled_at_only_advances();

-- ── Kills, and the gate they open ─────────────────────────────────────────────

create table kill_counter (
    character_id uuid   not null references character (id) on delete cascade,
    monster_id   text   not null,

    -- Only the SUPERVISED portion of a settlement window credits this. The boss
    -- portal counts active kills, because a gate an idle character walks through on
    -- its own is not a gate.
    active_kills bigint not null default 0 check (active_kills >= 0),
    afk_kills    bigint not null default 0 check (afk_kills >= 0),
    primary key (character_id, monster_id)
);

-- Unlocks that persist. The portal has to still be open after a disconnect, which
-- means the unlock is a row and not session state.
create table unlock (
    character_id uuid        not null references character (id) on delete cascade,
    unlock_id    text        not null,
    unlocked_at  timestamptz not null default now(),
    primary key (character_id, unlock_id)
);

-- ── Money ─────────────────────────────────────────────────────────────────────
--
-- Per ACCOUNT, because relic coins are bought with real money and a player who
-- deletes a character must not delete their purchases.

create table wallet (
    account_id uuid   not null references account (id) on delete cascade,
    currency   text   not null check (currency in ('coins', 'relic_coins')),
    balance    bigint not null default 0 check (balance >= 0),
    primary key (account_id, currency)
);

-- Every movement of every currency, append-only.
--
-- The invariant this exists for: SUM(delta) = balance, per account, per currency.
-- Checked after every scenario test. A balance that cannot be explained by its
-- ledger is either a bug or a compromise, and both need finding the same day.
create table wallet_ledger (
    id          bigserial   primary key,
    account_id  uuid        not null references account (id) on delete cascade,
    currency    text        not null check (currency in ('coins', 'relic_coins')),
    delta       bigint      not null,
    reason      text        not null,
    character_id uuid       references character (id) on delete set null,
    request_id  text,
    created_at  timestamptz not null default now()
);

create index wallet_ledger_by_account on wallet_ledger (account_id, currency, id);

-- Item creation, destruction and economic transfer. NOT slot shuffling.
--
-- The scope is the point. This answers "where did this value come from?", while
-- the tables above answer "what does this player have right now?". Logging every
-- equip and every drag between slots turns an idle game into an event-sourcing
-- system nobody wants to operate, and buries the twenty rows that matter.
create table item_ledger (
    id           bigserial   primary key,
    account_id   uuid        not null references account (id) on delete cascade,
    character_id uuid        references character (id) on delete set null,
    item_id      text        not null,
    delta        bigint      not null,
    reason       text        not null check (reason in (
                     'gather', 'craft_output', 'craft_input', 'loot', 'boss_loot',
                     'shop_purchase', 'shop_sale', 'trade_in', 'trade_out',
                     'consume', 'fuse_input', 'fuse_output', 'admin'
                 )),
    request_id   text,
    created_at   timestamptz not null default now()
);

create index item_ledger_by_account on item_ledger (account_id, id);
create index item_ledger_by_item    on item_ledger (item_id, id);

-- ── Purchases ─────────────────────────────────────────────────────────────────

-- Transaction-id first: the fundamental operation is not "purchase, then grant",
-- it is "record this store transaction, and grant only if it is new". The unique
-- constraint is what makes sending the same receipt thirty-seven times grant once.
create table purchase (
    id             bigserial   primary key,
    account_id     uuid        not null references account (id) on delete cascade,
    provider       text        not null check (provider in ('google_play', 'apple', 'web')),
    purchase_token text        not null,
    product_id     text        not null,
    granted        boolean     not null default false,
    refunded_at    timestamptz,
    created_at     timestamptz not null default now(),

    unique (provider, purchase_token)
);

-- ── Idempotency ───────────────────────────────────────────────────────────────

-- Every mutating endpoint takes a request_id. WebGL tabs are suspended mid-request
-- constantly, and a retried craft that consumes twice is a support ticket.
--
-- Enforced by middleware rather than by each handler remembering, so a new endpoint
-- is covered by default instead of by discipline.
create table idempotency_record (
    account_id  uuid        not null references account (id) on delete cascade,
    request_id  text        not null,
    endpoint    text        not null,

    -- Zero means claimed but not yet answered. The claim is taken BEFORE the work
    -- runs, using this table's primary key as the lock: two copies of one request
    -- arriving together must not both execute, and a check-then-insert lets them.
    status_code int         not null,

    -- TEXT, not jsonb, and deliberately. The promise is that a retry gets back the
    -- ORIGINAL answer -- and jsonb normalises whitespace, reorders keys and can
    -- reformat numbers, so a round trip through it returns something equivalent
    -- rather than something identical. Nothing queries inside this column; it is
    -- replayed verbatim or it is nothing.
    response    text        not null,

    created_at  timestamptz not null default now(),
    primary key (account_id, request_id)
);

create index idempotency_expiry on idempotency_record (created_at);

-- ── Operations ────────────────────────────────────────────────────────────────

-- Turning something off without shipping a Unity build. During a fast-iterating
-- playtest this is the difference between "we disabled it in four minutes" and
-- "we took the game down for a day".
create table feature_flag (
    flag       text        primary key,
    enabled    boolean     not null default false,
    value      jsonb       not null default '{}'::jsonb,
    updated_at timestamptz not null default now()
);

-- What are players doing? Deliberately coarse: no per-gather-tick and no
-- per-attack events, which is the difference between a stream you can read and one
-- you cannot.
create table telemetry_event (
    id           bigserial   primary key,
    account_id   uuid        references account (id) on delete set null,
    character_id uuid        references character (id) on delete set null,
    event        text        not null,
    payload      jsonb       not null default '{}'::jsonb,
    occurred_at  timestamptz not null default now()
);

create index telemetry_by_event on telemetry_event (event, occurred_at);

-- Is someone trying to break the game? A separate stream, because the questions
-- are different and mixing them means neither is queryable.
create table security_event (
    id          bigserial   primary key,
    account_id  uuid        references account (id) on delete set null,
    kind        text        not null,
    detail      jsonb       not null default '{}'::jsonb,
    occurred_at timestamptz not null default now()
);

create index security_by_kind on security_event (kind, occurred_at);

-- ── Row level security ────────────────────────────────────────────────────────
--
-- Enabled everywhere, with NO policies. That denies anon and authenticated
-- outright; the service role bypasses RLS and is the only thing the API uses.
--
-- Defence in depth rather than the actual boundary -- the client has no Supabase
-- credentials at all. But "the client cannot reach the database" is an assertion
-- about deployment, and deployments change. This is the same assertion in a place
-- that cannot be reconfigured by accident.

do $$
declare
    t text;
begin
    foreach t in array array[
        'account', 'account_identity', 'character', 'character_skill',
        'inventory_slot', 'bank_slot', 'equipment', 'stored_durability',
        'activity', 'kill_counter', 'unlock',
        'wallet', 'wallet_ledger', 'item_ledger', 'purchase',
        'idempotency_record', 'feature_flag', 'telemetry_event', 'security_event'
    ]
    loop
        execute format('alter table %I enable row level security', t);
        execute format('alter table %I force row level security', t);
    end loop;
end;
$$;
