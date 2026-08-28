-- One boss, several people hitting it.
--
-- ══ WHY THE FIGHT SPLITS IN TWO ═══════════════════════════════════════════════
--
-- An encounter was one row holding both halves of the fight: what the BOSS is doing
-- (its health, its seed, its enrage deadline) and what ONE CHARACTER is doing (their
-- frozen damage, their action sequence, their cooldowns).
--
-- Those halves belong to different things the moment a second person swings. The
-- boss has one health pool; each fighter has their own frozen snapshot and their own
-- strictly-increasing sequence.
--
-- So: encounter keeps the boss, encounter_participant carries each fighter. Solo is
-- the same shape with one participant, which is what stops group content being a
-- second implementation of the fight.
--
-- ══ WHY THE ANTI-CHEAT SURVIVES THIS ══════════════════════════════════════════
--
-- The invariant is "cumulative damage <= frozen DPS x elapsed, per fighter". Both
-- terms are still per-fighter and still server-owned: the snapshot is frozen when
-- that character joins, the sequence is theirs alone, and the clock is the database's.
--
-- What a shared pool must NOT become is a shared allowance. Four people cannot pool
-- their ceilings and let one of them spend all of it -- so the check stays on the
-- participant row, never on the encounter total.

alter table encounter
    add column if not exists party_id uuid references party (id) on delete set null;

-- ══ THE FIGHTERS ══════════════════════════════════════════════════════════════
create table encounter_participant (
    encounter_id uuid        not null references encounter (id) on delete cascade,
    character_id uuid        not null references character (id) on delete cascade,

    -- Frozen when THIS character joined, not when the fight began. Somebody who
    -- arrives late brings the gear they are wearing, and swapping after that changes
    -- nothing -- which is the same gear-swap fix the solo fight already had.
    frozen_dps            double precision not null check (frozen_dps >= 0),
    frozen_attack_seconds double precision not null check (frozen_attack_seconds > 0),

    damage_dealt  bigint    not null default 0 check (damage_dealt >= 0),

    -- Theirs alone. Two fighters both sending action 7 is ordinary; one fighter
    -- sending action 7 twice is a replay.
    last_sequence bigint    not null default 0 check (last_sequence >= 0),

    ability_used  jsonb     not null default '{}'::jsonb,

    -- When their clock starts. The damage ceiling is measured from here, so a late
    -- arrival cannot claim the seconds before they were in the room.
    joined_at     timestamptz not null default now(),

    primary key (encounter_id, character_id)
);

create index encounter_participant_by_character on encounter_participant (character_id);

-- ══ CARRY THE LIVE FIGHTS ACROSS ══════════════════════════════════════════════
--
-- Anything already running becomes its own single participant, so a fight in progress
-- when this ships does not lose its frozen numbers and answer 404 to the person
-- standing in front of the King.
insert into encounter_participant
    (encounter_id, character_id, frozen_dps, frozen_attack_seconds,
     damage_dealt, last_sequence, ability_used, joined_at)
select id, character_id, frozen_dps, frozen_attack_seconds,
       damage_dealt, last_sequence, ability_used, started_at
  from encounter
on conflict do nothing;

-- ══ ONE LIVE FIGHT PER PARTY ══════════════════════════════════════════════════
--
-- The companion to encounter_one_live_per_character, and for the same reason: a
-- check in a handler is a race unless it holds a lock, and this is true whether or
-- not anybody remembers to hold one.
--
-- Partial on party_id so solo encounters, which have none, are unaffected.
create unique index encounter_one_live_per_party
    on encounter (party_id)
 where ended_at is null and party_id is not null;

alter table encounter_participant enable row level security;
alter table encounter_participant force row level security;

comment on table encounter_participant is
    'One row per fighter in an encounter. Carries the frozen snapshot, sequence and '
    'cooldowns that used to live on encounter itself -- the boss half stayed there.';

comment on column encounter.party_id is
    'The group fighting this, or null for a solo attempt. Both take the same code '
    'path; solo is a fight with one participant.';
