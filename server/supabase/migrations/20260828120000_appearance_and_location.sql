-- Character appearance, and where the character was standing when they left.
--
-- ══ WHY APPEARANCE IS SERVER STATE ════════════════════════════════════════════
--
-- Because it was nowhere else. A player customised hair, skin and eyes, went to
-- character select, came back, and found the default face -- the look existed only
-- in the client's local save, which under an authoritative server is not the source
-- of anything and is not carried between devices.
--
-- It is not progression and nothing about the game's balance depends on it, so it is
-- stored rather than validated: the server keeps what it is given and hands the same
-- thing back. The reason it belongs here anyway is durability, not anti-cheat. A
-- cheated hairstyle costs nobody anything; a hairstyle that evaporates on every
-- character select costs the player the character they made.
--
-- ══ WHY jsonb AND NOT COLUMNS ═════════════════════════════════════════════════
--
-- SpumSaveData lives in the shared rules tree, so both sides already agree on its
-- shape and neither can drift from the other. Spreading its fields across a dozen
-- columns would mean a migration every time an art pack adds a slot, to store data
-- the server never reads a single field of.
alter table character
    add column if not exists appearance jsonb not null default '{}'::jsonb;

-- ══ WHERE THEY LEFT OFF ═══════════════════════════════════════════════════════
--
-- last_map_id already existed and was already returned in the roster. Nothing ever
-- wrote to it, so every character loaded into the starting map for ever -- including
-- one that had walked to another zone thirty seconds earlier.
--
-- The default stays, because a character that has never played has genuinely not
-- been anywhere and the starting map is the honest answer for them.
comment on column character.last_map_id is
    'Where this character was when they last left. Written on map change and on exit; '
    'read when they next enter the world.';

comment on column character.appearance is
    'SpumSaveData as the client authored it. Stored, never interpreted -- the server '
    'has no opinion about hair.';
