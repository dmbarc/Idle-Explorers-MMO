-- What people say to each other.
--
-- ══ WHY IT IS A TABLE AND NOT A BROADCAST ═════════════════════════════════════
--
-- Same reason presence is. There is no socket to push down — Unity WebGL has none —
-- so a line is written where everybody on that map will read it on their next poll.
--
-- It also means a message survives the two seconds between somebody saying it and
-- everybody else asking, which a fire-and-forget broadcast would not: whoever's poll
-- landed a moment earlier would simply never hear it.
--
-- ══ WHY IT IS KEPT AND NOT DELETED ON READ ════════════════════════════════════
--
-- Because "read" is not a single event when four clients are polling independently.
-- A line deleted by the first reader is a line the other three never see.
--
-- So reads are a time window and nothing is consumed. Old rows are dead weight rather
-- than a correctness problem, and the sweep below keeps them from becoming one.
create table chat_line (
    id           bigserial   primary key,
    character_id uuid        not null references character (id) on delete cascade,
    map_id       text        not null,

    -- Length is capped in the API, not here: the limit is a product decision that
    -- should produce a friendly refusal rather than a constraint violation.
    body         text        not null,

    said_at      timestamptz not null default now()
);

-- The only query this serves: one map, recently, in order.
create index chat_line_by_map on chat_line (map_id, said_at desc);

alter table chat_line enable row level security;
alter table chat_line force row level security;

comment on table chat_line is
    'Local chat, read by polling within a time window. Nothing is consumed on read -- '
    'four clients poll independently and a line deleted by the first is a line the '
    'other three never see.';
