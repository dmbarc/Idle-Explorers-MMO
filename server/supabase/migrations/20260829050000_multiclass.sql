-- The classes a character has, rather than the one it started as.
--
-- ══ WHAT WAS BROKEN ═══════════════════════════════════════════════════════════
--
-- Multi-classing existed on the client and nowhere else. CharacterData carries a
-- classIds LIST; the server had a single class_id column and built the talent trees
-- from it alone.
--
-- So unlocking a second class worked, the tree drew, and spending a point in it came
-- back "no such talent" -- which was the server telling the truth: that node was not
-- in any tree it knew this character had.
--
-- ══ WHY A TABLE AND NOT AN ARRAY COLUMN ═══════════════════════════════════════
--
-- Because a class is going to grow things attached to it -- when it was taken, which
-- ones are eligible for a respec, eventually a per-class level. An array column that
-- has to become a table later is a migration; a table that stays one column is not a
-- cost.
--
-- ══ WHY class_id STAYS ON character ═══════════════════════════════════════════
--
-- It is the PRIMARY class: what the character is called, which rig it wears, what the
-- roster shows. That is a different question from "which trees may this character
-- spend in", and collapsing the two is how the bug above happened in the first place.
create table character_class (
    character_id uuid        not null references character (id) on delete cascade,
    class_id     text        not null,
    added_at     timestamptz not null default now(),

    primary key (character_id, class_id)
);

create index character_class_by_character on character_class (character_id);

-- ══ EVERY EXISTING CHARACTER OWNS THE CLASS IT ALREADY HAS ════════════════════
--
-- Without this, the first read after deploying would find no classes at all and every
-- character would lose the tree they had been spending in -- which is a far louder
-- bug than the one being fixed.
insert into character_class (character_id, class_id, added_at)
select id, class_id, created_at
  from character
 where class_id is not null and class_id <> ''
on conflict do nothing;

alter table character_class enable row level security;
alter table character_class force row level security;

comment on table character_class is
    'Every class a character may spend talent points in. character.class_id remains '
    'the PRIMARY one -- the name and the rig -- which is a different question.';
