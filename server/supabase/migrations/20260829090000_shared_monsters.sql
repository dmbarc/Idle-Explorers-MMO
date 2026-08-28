-- The monsters standing in a map, as one population everybody sees.
--
-- ══ WHAT WAS WRONG ════════════════════════════════════════════════════════════
--
-- Every client spawned its own. MonsterSpawner placed a ring of goblins around the
-- player, gave them health, and let them die -- all locally, all invisible to anybody
-- else. Two players standing in the same clearing fought two different sets of
-- goblins occupying the same ground, and neither could see the other's.
--
-- The rewards were never affected: settlement integrates time against server-owned
-- rates and pays from that, so the client's goblins have always been theatre. What
-- was wrong was that the theatre was PRIVATE, in a game whose whole subject is a
-- shared world.
--
-- ══ WHY THE SERVER OWNS HEALTH AND NOT JUST POSITION ══════════════════════════
--
-- Because a shared monster has to die once. If health lived on each client, two
-- players hitting the same goblin would each watch it die on their own screen at
-- their own moment, and the population would disagree about how many were left --
-- which is the private-spawner problem again with extra steps.
--
-- Damage is REPORTED and capped, never trusted: the same ceiling the boss uses,
-- cumulative damage against dps × elapsed. A client that lies can make a goblin
-- fall over slightly early on everybody's screen, and gains nothing by it, because
-- what it is paid still comes from settlement.
--
-- ══ WHY THERE IS NO BACKGROUND JOB ════════════════════════════════════════════
--
-- The population is topped up and respawned lazily, on the presence poll that is
-- already happening every two seconds for every player in the map. A map nobody is
-- standing in does not need monsters, and a timer that maintained one would be a
-- process to run, watch and pay for in exchange for nothing anybody can see.
create table map_monster (
    id           uuid        primary key default gen_random_uuid(),
    map_id       text        not null,

    -- From monster_data.json. The map's defaultMonsterId today; a column rather than
    -- a lookup because a map with two kinds of monster is a content change, and this
    -- is the row that would carry it.
    monster_id   text        not null,

    -- Where it stands. Chosen by the server from the map's own extent, so every
    -- client draws the same goblin in the same place.
    x            real        not null,
    z            real        not null,

    health       double precision not null check (health >= 0),
    max_health   double precision not null check (max_health > 0),

    -- Null while alive. Set when health reaches zero, and the respawn is computed
    -- from it rather than from a countdown somebody has to decrement.
    died_at      timestamptz,

    -- ══ WHO GETS THE CREDIT ═══════════════════════════════════════════════════
    --
    -- The character that has done the most damage to this one. Not a list: the only
    -- question anybody asks of it is "whose kill was that", and a table of
    -- contributions would be a join for an answer nobody needs yet.
    --
    -- It is deliberately NOT what pays. Loot and experience come from settlement,
    -- which integrates each player's own time -- so two people fighting the same
    -- goblin are both paid for the time they spent, and this column only decides
    -- whose name is on the kill.
    tagged_by    uuid        references character (id) on delete set null,
    tagged_damage double precision not null default 0,

    spawned_at   timestamptz not null default now()
);

-- The only query this serves: one map's population, live or recently dead.
create index map_monster_by_map on map_monster (map_id, died_at);

alter table map_monster enable row level security;
alter table map_monster force row level security;

comment on table map_monster is
    'One shared monster population per map. Health is server-owned so a monster dies '
    'once for everybody; loot and xp still come from settlement, so this decides what '
    'is SEEN rather than what is paid.';
