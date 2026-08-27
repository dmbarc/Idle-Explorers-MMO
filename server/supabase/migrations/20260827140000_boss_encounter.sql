-- ═══════════════════════════════════════════════════════════════════════════════
-- The boss fight: the one place per-action validation is worth paying for.
--
-- Everything else in this game settles from two timestamps and a rate. The boss does
-- not, because the boss is the only content where WHAT THE PLAYER DID inside the
-- window is supposed to matter -- and a fight resolved from elapsed time alone is not
-- a fight, it is a wait with a health bar.
--
-- ══ WHAT MAKES IT SAFE ANYWAY ══════════════════════════════════════════════════
--
-- One arithmetic bound, held in this table:
--
--     damage_dealt  ≤  frozen_dps × (now − started_at + tolerance)
--
-- frozen_dps is written once, at engage, from what the character was wearing. It is
-- never recomputed and never read from a request. started_at is the database clock.
-- So the ceiling is a function of two server-owned numbers, and no client can move
-- either of them.
--
-- That single bound is why the columns below can be as permissive as they are: the
-- sequence number is for cheap replay rejection, the phase is for telemetry, and the
-- ability cooldowns are for making the fight play correctly. None of them is holding
-- the door.
-- ═══════════════════════════════════════════════════════════════════════════════

create table encounter (
    id           uuid        primary key default gen_random_uuid(),
    character_id uuid        not null references character (id) on delete cascade,
    monster_id   text        not null,

    -- ── Frozen at engage, never rewritten ─────────────────────────────────────
    --
    -- The gear-swap fix. Without it the fight is a puzzle about equipping damage for
    -- the check and armour for the telegraphs, and the damage ceiling would have to
    -- be re-integrated every time a ring changed.
    frozen_dps            double precision not null check (frozen_dps >= 0),
    frozen_attack_seconds double precision not null check (frozen_attack_seconds > 0),

    -- Every roll in this fight is a pure function of (seed, index), so a damage
    -- number in the audit log is re-derivable months later from two integers.
    seed         bigint      not null,

    boss_max_hp  bigint      not null check (boss_max_hp > 0),
    damage_dealt bigint      not null default 0 check (damage_dealt >= 0),

    -- Highest action index accepted. Strictly increasing, so a replayed request is
    -- rejected by comparison rather than by remembering every request ever seen.
    last_sequence bigint     not null default 0 check (last_sequence >= 0),

    -- When each ability was last used, as seconds from the start. A jsonb map rather
    -- than a table: it is read and written together, always, and it is never queried
    -- across encounters.
    ability_used jsonb       not null default '{}'::jsonb,

    started_at   timestamptz not null default now(),
    enrage_at    timestamptz not null,
    ended_at     timestamptz,

    -- Null while the fight is live. Set once, by the server, from its own arithmetic.
    won          boolean,

    -- ══ WHY THIS CHECK ═══════════════════════════════════════════════════════
    --
    -- A finished encounter must say how it finished, and a live one must not. Without
    -- it, "ended_at is not null and won is null" is a state the resolve path can leave
    -- behind on a partial write, and every later query has to decide what it means.
    constraint encounter_ends_with_a_verdict
        check ((ended_at is null and won is null) or (ended_at is not null and won is not null)),

    constraint encounter_enrage_is_after_the_start check (enrage_at > started_at)
);

-- ══ ONE LIVE ENCOUNTER PER CHARACTER ═══════════════════════════════════════════
--
-- A partial unique index, so a character may have any number of FINISHED encounters
-- and exactly one running. This is the constraint that stops the obvious exploit:
-- engage twice, fight both with one set of actions, resolve both, loot twice.
--
-- Enforced here rather than in the handler because a check in a handler is a race
-- unless it holds a lock, and this is true whether or not anybody remembers to.
create unique index encounter_one_live_per_character
    on encounter (character_id)
 where ended_at is null;

create index encounter_by_character on encounter (character_id, started_at desc);

-- ═══════════════════════════════════════════════════════════════════════════════
-- Loot that has been earned but not yet picked up.
--
-- ══ WHY IT IS NOT WRITTEN STRAIGHT INTO THE BAG ════════════════════════════════
--
-- Because the bag can be full, and a boss kill that silently evaporates a drop
-- because slot thirty was holding four logs is the single worst thing this game could
-- do to somebody's evening. Pending loot waits, visibly, until there is room.
--
-- It also separates two questions that have different answers: "did you earn it"
-- is settled by the resolve transaction and can never be undone, while "have you
-- collected it" is a later, retryable operation.
-- ═══════════════════════════════════════════════════════════════════════════════

create table pending_loot (
    id           bigserial   primary key,
    character_id uuid        not null references character (id) on delete cascade,
    encounter_id uuid        references encounter (id) on delete set null,

    item_id      text        not null,
    quantity     bigint      not null check (quantity > 0),

    created_at   timestamptz not null default now(),
    claimed_at   timestamptz
);

create index pending_loot_unclaimed
    on pending_loot (character_id, id)
 where claimed_at is null;

-- ══ Row level security ═════════════════════════════════════════════════════════
--
-- Same as every other table: enabled and FORCED, with no policies, so anon and
-- authenticated are denied outright and only the service role -- which the API holds
-- and the client never sees -- can read a row.
alter table encounter    enable row level security;
alter table encounter    force  row level security;
alter table pending_loot enable row level security;
alter table pending_loot force  row level security;
