-- ══ FIGHTING THE KING AS A GROUP ══════════════════════════════════════════════
--
-- Three things the boss needed before four people could sensibly do it together.
--
-- 1. A CALL, so one person walking into the throne brings the group rather than
--    leaving them in a field. It lives on the party because that is what it is
--    about, and because every client already reads the party row every two
--    seconds -- a call in its own table would need a poll of its own.
--
-- 2. LOOT ROLLS, because a boss that pays every fighter a copy of the same drop is
--    a boss whose table is worthless by the second clear.
--
-- 3. Somewhere to record that somebody LEFT a fight without finishing it, which is
--    handled by deleting their participant row -- no schema needed, but the index
--    below makes "is anybody still in this fight" cheap enough to ask on every
--    departure.

-- ── The call ──────────────────────────────────────────────────────────────────
--
-- Nullable, all four together. A party with call_at set has a live call; one
-- without has never had one, or the last one was cleared. There is deliberately no
-- separate "cancelled" flag: declining is a decision each member makes for
-- themselves, and recording it centrally would let one member cancel everybody's.
alter table party add column call_map_id     text;
alter table party add column call_monster_id text;
alter table party add column call_at         timestamptz;
alter table party add column called_by       uuid references character (id) on delete set null;

-- ── The rolls ─────────────────────────────────────────────────────────────────
--
-- One row per item the boss actually dropped, not one per line of its table: a
-- drop that did not roll is not something anybody rolls for.
create table loot_roll (
    id           uuid        primary key default gen_random_uuid(),
    encounter_id uuid        not null references encounter (id) on delete cascade,

    item_id      text        not null,
    quantity     bigint      not null check (quantity > 0),

    -- Position of this drop within the fight, so the dice can be re-derived from
    -- the encounter's seed months later. Without it two drops of the same item
    -- would share a die.
    roll_index   int         not null,

    offered_at   timestamptz not null default now(),

    -- Set exactly once, when the roll settles. Null means still open; a row with
    -- settled_at and no winner is one everybody passed on.
    settled_at   timestamptz,
    winner_id    uuid        references character (id) on delete set null
);

create index loot_roll_open on loot_roll (encounter_id) where settled_at is null;

-- One answer per fighter per roll, enforced by the primary key rather than by
-- every endpoint remembering to check -- the same reasoning as one party per
-- character and one live encounter per party.
create table loot_roll_choice (
    roll_id      uuid        not null references loot_roll (id) on delete cascade,
    character_id uuid        not null references character (id) on delete cascade,

    choice       text        not null check (choice in ('need', 'greed', 'pass')),

    -- Rolled by the SERVER when the answer arrives, from the encounter seed. The
    -- client never sends a number; a die a client rolls is a die a client chooses.
    roll         int         not null check (roll between 1 and 100),

    answered_at  timestamptz not null default now(),

    primary key (roll_id, character_id)
);

create index loot_roll_choice_by_roll on loot_roll_choice (roll_id);

-- Asked every time somebody leaves a fight, to find out whether the fight is over.
create index encounter_participant_by_encounter on encounter_participant (encounter_id);

-- ══ RLS, AS EVERYWHERE ════════════════════════════════════════════════════════
--
-- Enabled and FORCED with no policies. The client holds no Supabase credential and
-- never will; everything reaches these rows through the API, which connects as the
-- owner. A loot table without this is one accidental anon key away from being a
-- public write of who won the crown.
do $$
declare t text;
begin
    foreach t in array array['loot_roll', 'loot_roll_choice']
    loop
        execute format('alter table %I enable row level security', t);
        execute format('alter table %I force row level security', t);
    end loop;
end $$;
