-- One account, one place at a time.
--
-- ══ WHAT WAS POSSIBLE ═════════════════════════════════════════════════════════
--
-- Signing into the same account in two browsers and playing the SAME CHARACTER in
-- both. Two clients each believing they owned the state, each settling, each setting
-- activities, each writing positions.
--
-- Nothing was duplicated -- settlement holds a row lock and the timestamp is the
-- bookkeeping, so the second settle in an instant pays nothing. But it is a race
-- nobody should have to reason about, the two clients disagree visibly, and it is the
-- shape every duplication exploit starts from.
--
-- ══ WHY A CLAIM AND NOT A CONNECTION ══════════════════════════════════════════
--
-- There is no connection to count. The client polls, so "logged in" is not a socket
-- being open -- it is a recent claim, exactly like presence.
--
-- So a session is a row holding the newest claim on an account. Signing in somewhere
-- else takes the claim; the older client learns it has been displaced the next time
-- it speaks, because the token it presents no longer matches the one recorded.
--
-- ══ WHY THE NEWEST WINS ═══════════════════════════════════════════════════════
--
-- Rather than refusing the second sign-in outright.
--
-- Refusing sounds stricter and is worse. A browser closed without signing out, a
-- crashed tab, a laptop that slept -- every one leaves a claim behind, and a player
-- locked out of their own account by a tab they cannot reach has no way through it
-- except waiting. Support tickets rather than security.
--
-- Displacement has neither problem: the person at the keyboard always gets in, and
-- the stale client stops being able to act.
create table account_session (
    account_id   uuid        primary key references account (id) on delete cascade,

    -- Which client holds the account. Minted by the server at sign-in; the client
    -- echoes it on every request and never chooses it.
    session_id   uuid        not null,

    -- What it last said it was doing, for the message the displaced client shows.
    claimed_at   timestamptz not null default now(),
    last_seen_at timestamptz not null default now()
);

alter table account_session enable row level security;
alter table account_session force row level security;

comment on table account_session is
    'The newest claim on each account. A request carrying a session id that is not '
    'this one is from a client that has been displaced; see SessionGuard.';
