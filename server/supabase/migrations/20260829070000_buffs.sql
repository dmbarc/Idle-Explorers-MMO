-- What a potion did to you, and until when.
--
-- ══ WHY THE SERVER OWNS A BUFF AT ALL ═════════════════════════════════════════
--
-- Because it multiplies damage, and damage decides the farm rate and whether the
-- Goblin King's enrage timer is beaten. A buff the client asserted would be a client
-- asserting its own DPS, which is the one thing this whole architecture exists to
-- take away from it.
--
-- ══ WHY expires_at AND NOT seconds_remaining ══════════════════════════════════
--
-- The same reasoning as last_settled_at. A countdown has to be decremented by
-- something, and whatever does the decrementing becomes a second clock that can be
-- stopped, skipped or run twice. An absolute instant needs no maintenance: it is
-- either in the past or it is not, and now() decides.
--
-- Nothing sweeps this table. An expired row is dead weight rather than a correctness
-- problem, because every read filters on expires_at, and the delete below happens
-- naturally the next time the same character drinks the same kind of potion.
--
-- ══ WHY ONE ROW PER STAT ══════════════════════════════════════════════════════
--
-- The primary key is (character_id, stat_id), so drinking a second Draught of Fury
-- REPLACES the first rather than stacking with it. A stacking buff is a currency:
-- fifty potions would be fifty times the damage and the only limit would be how much
-- gold somebody had.
--
-- Refreshing means a potion is worth exactly what it says on it, however many are in
-- the bag -- and it makes the upsert below the whole of the write path.
create table character_buff (
    character_id uuid        not null references character (id) on delete cascade,

    -- One of the three names in Rules/Combat/Buffs.cs. Not an enum: the shared rules
    -- tree is where that vocabulary is decided, and a database enum would be a second
    -- place to change it -- which is the drift the shared tree exists to prevent.
    stat_id      text        not null,

    -- Fraction added, already clamped by Buffs.Clamp on the way in. Stored rather
    -- than recomputed so a content edit cannot retroactively change a buff somebody
    -- already paid for.
    magnitude    real        not null,

    expires_at   timestamptz not null,
    label        text        not null default '',

    primary key (character_id, stat_id)
);

-- The only query this serves: one character's buffs that have not run out.
create index character_buff_live on character_buff (character_id, expires_at);

alter table character_buff enable row level security;
alter table character_buff force row level security;

comment on table character_buff is
    'Timed stat bonuses from potions. One row per stat, so drinking a second of the '
    'same kind refreshes rather than stacks -- a stacking buff is a currency.';
