# Idle Explorers — game server

The authoritative half of the game. Unity renders and sends intent; everything a player
can gain, spend, lose or unlock is decided here.

```
              Unity (WebGL)          presentation + intent
                    │
                 Cloudflare          edge, TLS, WAF, CDN
                    │
          ASP.NET Core (Fly.io)      THE GAME SERVER
                    │
        ┌───────────┴───────────┐
   Supabase Auth          Supabase Postgres
   identity only          state · economy · telemetry
```

Supabase is infrastructure, not the game server. Edge Functions and plpgsql RPCs are
kept to the few things that genuinely need them — outbound HTTPS for store receipt
verification, and the Play Games token exchange. The rules live in C#.

## Layout

| Path | What it is |
|---|---|
| `src/IdleExplorers.Rules/` | **No source of its own.** Every file is linked from `../Assets/Scripts/Rules`. |
| `src/IdleExplorers.Content/` | Reads the twelve StreamingAssets files into the shared catalogue. |
| `tests/IdleExplorers.Rules.Tests/` | xUnit over the shared rules. |
| `tests/IdleExplorers.Content.Tests/` | Imports the real shipping content and validates it. |
| `tests/IdleExplorers.Database.Tests/` | Schema invariants and the RLS boundary, against a real Postgres. |
| `supabase/` | Local stack config and SQL migrations. |

### Why content loading is NOT shared

The catalogue is: `GameContent` indexes and validates, and both hosts call it. The
loading is not, because the two hosts genuinely cannot share it — Unity has no
filesystem on the web and fetches the same files over HTTP with `JsonUtility`, while
this side reads bytes with `System.Text.Json`.

The two parsers agree on exactly one thing, and the shared content classes are written
to stay inside it: **public fields, matched by name**. `System.Text.Json` ignores fields
unless `IncludeFields` is set — and without it every file parses successfully into a
catalogue of empty objects. Nothing throws; the server just quietly believes the game
has no items in it. There is a test named for that failure.

### The shared rules project

`src/IdleExplorers.Rules` compiles the *same files* Unity compiles. Not a copy — a link.

That is the answer to the one risk that otherwise recurs forever in a server-authoritative
migration: the server says the swing did 47 and the client drew 52, on every balance
change, for the life of the project. Two implementations pinned together by test vectors
treats the symptom. One implementation removes it.

The arrangement survives only while that tree stays host-agnostic, so three things are
forbidden in it and asserted by `RulesPurity` in `../Tools/tests`:

- **No `UnityEngine`** — the server has no engine to link against.
- **No clock.** Elapsed time is an argument. On the server the database owns "now", and a
  rule that reads `DateTime.Now` cannot be tested by advancing time either.
- **No unseeded randomness.** Use `CounterRandom`, which makes every value a pure function
  of `(seed, index)`, so a disputed hit is re-derivable from two columns months later.

## Running the checks

Everything, from the repository root:

```bash
./Tools/check.sh
```

That runs three layers: both Unity assemblies compiling with their DLLs actually on disk,
the standalone content and economy suite, and this solution's tests.

Just the server:

```bash
dotnet test server/IdleExplorers.slnx
```

Neither needs Unity to be open. The schema tests need the local stack, and **skip
themselves** when it is not running rather than failing — so the rest stays runnable
without Docker. `check.sh` prints a loud reminder when anything skipped, because a check
that quietly stops existing is worse than one that was never written.

## Prerequisites

| Tool | Needed for | Status |
|---|---|---|
| .NET SDK 10 | everything here | installed |
| Supabase CLI | migrations, local stack, deploys | installed at `~/.supabase/bin`, on PATH |
| Docker Desktop | `supabase start` — the local Postgres and Auth containers | installed and running |
| Fly CLI | deploying the API | not installed, not needed yet |

Docker needs WSL, which needs the `VirtualMachinePlatform` Windows feature and a reboot:
`wsl --install` from an elevated prompt. Worth recording because "virtualisation support
not detected" is what Docker Desktop reports for a *missing WSL*, which sends you to the
BIOS for no reason.

Everything except `tests/IdleExplorers.Database.Tests` runs without it.

## Local stack (once Docker exists)

```bash
supabase start
```

Brings up Postgres on `54322`, the API on `54321`, Studio on `54323` and the mail catcher
on `54324`. Realtime and vector storage are switched off in `config.toml`: this is an idle
game, settlement-on-read plus a heartbeat is enough, and Unity WebGL excludes
`System.Net.WebSockets` outright — a client that tried to use Realtime would hang rather
than fail cleanly.

Apply migrations from scratch:

```bash
supabase db reset
```

### Row level security

Every game table has RLS **enabled and forced**, with **no policies at all**. That denies
`anon` and `authenticated` outright; `service_role` has `BYPASSRLS` and is the only role
the API uses.

This is defence in depth rather than the real boundary — the client has no Supabase
credentials whatsoever. But "the client cannot reach the database" is an assertion about
deployment, and deployments change. `RowLevelSecurityTests` asserts it somewhere a config
change cannot quietly undo, including the `FORCE`: without it the table owner is exempt,
and a migration running as the owner leaves a hole nobody sees.

## Rules that are not negotiable

- **The service-role key never leaves the server.** Anything shipped inside a WebGL build
  is public. The Unity client gets the API base URL and whatever token Auth issues it, and
  nothing else.
- **Never trust a client-supplied timestamp.** Not for settlement, not for cooldowns, not
  in tests. The test suite advances an injected clock instead.
- **Every mutating endpoint takes a `request_id`.** WebGL tabs get suspended mid-request
  constantly, and a retried craft that consumes twice is a support ticket.
