-- ═══════════════════════════════════════════════════════════════════════════════
-- Two things that were Critical-tier and still lived only on the client.
--
-- Talent points change a character's stats, and stats decide how fast they farm and
-- whether they beat the boss enrage timer. The bank holds items. Both were being
-- edited by the game and written to a JSON file the player owns.
--
-- Neither is exotic. They are here because they were missed, and because "the client
-- decides nothing that persists" is only true when it is true of everything.
-- ═══════════════════════════════════════════════════════════════════════════════

create table talent (
    character_id uuid   not null references character (id) on delete cascade,
    node_id      text   not null,

    -- Rank rather than a boolean: a node can be bought several times, and the point
    -- cost is per rank. A row exists only once a point has gone in.
    rank         integer not null check (rank > 0),

    primary key (character_id, node_id)
);

-- ══ WHY THE BANK IS ITS OWN TABLE AND NOT A BIGGER INVENTORY ═══════════════════
--
-- bank_slot already existed and nothing used it. It stays as it is, and this migration
-- only adds what the endpoints need to be safe.
--
-- The bank is per ACCOUNT while the inventory is per CHARACTER, which is the whole
-- reason it is worth having: it is how a player moves ore from the character who mined
-- it to the one who smiths. That also makes it the one place two of a player's own
-- characters can race each other, so every bank operation takes the ACCOUNT lock
-- rather than a character lock.

-- Slots are bounded. Without this a client could deposit into slot 2,000,000,000 and
-- the bank would be a sparse array with a scroll bar to the horizon.
alter table bank_slot
    add constraint bank_slot_index_is_in_range
    check (slot_index >= 0 and slot_index < 200);

-- Same for the character's own bag, which had the same hole: the settlement code
-- writes slots 0..29, but nothing stopped an endpoint being asked for slot 40.
alter table inventory_slot
    add constraint inventory_slot_index_is_in_range
    check (slot_index >= 0 and slot_index < 30);

alter table talent enable row level security;
alter table talent force  row level security;
