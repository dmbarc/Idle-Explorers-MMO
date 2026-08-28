-- Where on the map they were standing.
--
-- ══ WHY THE MAP ALONE WAS NOT ENOUGH ══════════════════════════════════════════
--
-- last_map_id got a character back to the right map and dropped them on its spawn
-- point. Somebody who parked at a mining node in the Hollow came back to the Hollow
-- and then walked to the node again, every time -- which is most of the annoyance of
-- having been sent to the wrong map in the first place.
--
-- ══ WHY IT IS NOT VALIDATED ═══════════════════════════════════════════════════
--
-- Same reasoning as presence. Position is presentation: nothing in this game rewards
-- where a character stands, so a client that lies about it gains nothing. The values
-- are clamped to the same believable range, which is about keeping a broken float out
-- of a column other people's arithmetic reads.
--
-- Zero-zero means "nowhere in particular" and the map's own spawn point is used --
-- which is exactly what every character created before this column reads as, so no
-- backfill is needed.
alter table character
    add column if not exists last_x real not null default 0,
    add column if not exists last_z real not null default 0;

comment on column character.last_x is
    'Where this character was standing when they last left, on last_map_id. Zero-zero '
    'means use the map spawn. Presentation only -- never validated, only clamped.';
