-- Seeing other people, and standing with them.
--
-- ══ WHY PRESENCE IS A TABLE AND NOT A CONNECTION ══════════════════════════════
--
-- Because there is no connection. Unity WebGL has no usable socket -- System.Net is
-- excluded from the player, so ClientWebSocket is unavailable and fails by hanging
-- rather than by refusing to compile -- so the world is read by polling.
--
-- That turns "who is online" from a question about sockets into a question about
-- rows: everybody writes where they are every couple of seconds, and everybody reads
-- the rows written recently enough to believe. A row that stops being updated stops
-- being a player, with no disconnect event required, which is also what makes a
-- closed laptop behave correctly for free.
--
-- ══ WHY IT IS NOT VALIDATED ═══════════════════════════════════════════════════
--
-- Position is presentation. Nothing in this game rewards where a character stands --
-- no loot, no rates, no combat advantage -- so a client that lies about its
-- coordinates gains exactly nothing and the server spends nothing checking. The
-- coordinates are clamped to a believable range in the API, which is about protecting
-- other clients' arithmetic rather than about cheating.
create table presence (
    character_id uuid        primary key references character (id) on delete cascade,
    account_id   uuid        not null references account (id) on delete cascade,
    map_id       text        not null,
    x            real        not null default 0,
    z            real        not null default 0,
    updated_at   timestamptz not null default now()
);

-- The only query this table serves: everybody on one map, recently. Ordered by the
-- timestamp so the staleness cutoff is an index range rather than a filter.
create index presence_by_map on presence (map_id, updated_at desc);

-- ══ PARTIES ═══════════════════════════════════════════════════════════════════
--
-- A party is a row and a set of members. The leader is a member like any other; the
-- column exists so there is somebody to answer for the group when it has to make one
-- decision, such as starting the encounter before it is full.
create table party (
    id                  uuid        primary key default gen_random_uuid(),
    leader_character_id uuid        not null references character (id) on delete cascade,
    created_at          timestamptz not null default now()
);

-- character_id is the PRIMARY KEY, not part of a composite. That is the whole
-- "one party at a time" rule, enforced by the shape of the table rather than by
-- every endpoint remembering to check -- which is the same reasoning as the unique
-- constraint on a purchase token.
create table party_member (
    party_id     uuid        not null references party (id) on delete cascade,
    character_id uuid        primary key references character (id) on delete cascade,
    joined_at    timestamptz not null default now()
);

create index party_member_by_party on party_member (party_id);

-- ══ RLS, AS EVERYWHERE ════════════════════════════════════════════════════════
--
-- Enabled and FORCED with no policies, like every other table. The client holds no
-- Supabase credential and never will; everything reaches these rows through the API,
-- which connects as the owner. A table without this is one accidental anon key away
-- from being a public read of where every player is standing.
do $$
declare t text;
begin
    foreach t in array array['presence', 'party', 'party_member']
    loop
        execute format('alter table %I enable row level security', t);
        execute format('alter table %I force row level security', t);
    end loop;
end $$;

comment on table presence is
    'Where each character was last seen, refreshed by polling. Rows older than the '
    'staleness window are treated as absent; see IdleExplorers.Rules.Presence.';

comment on table party_member is
    'One row per character, so a character cannot be in two parties. The primary key '
    'IS the rule.';
