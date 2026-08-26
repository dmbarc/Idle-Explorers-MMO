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
| `tests/IdleExplorers.Rules.Tests/` | xUnit over the shared rules. |
| `supabase/` | Local stack config and, later, SQL migrations. |

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

Neither needs Docker, a database, or Unity to be open.

## Prerequisites

| Tool | Needed for | Status |
|---|---|---|
| .NET SDK 10 | everything here | installed |
| Supabase CLI | migrations, local stack, deploys | installed at `~/.supabase/bin`, on PATH |
| **Docker Desktop** | `supabase start` — the local Postgres and Auth containers | **not installed** |
| Fly CLI | deploying the API | not installed, not needed yet |

Docker is the only outstanding one, and it is deliberately not installed automatically: it
needs elevation, enables Windows features, and has a licence to accept. Install it with

```bash
winget install --id Docker.DockerDesktop --source winget
```

Until then the rules layer is fully testable — it is pure C# and touches no database.

## Local stack (once Docker exists)

```bash
supabase start
```

Brings up Postgres on `54322`, the API on `54321`, Studio on `54323` and the mail catcher
on `54324`. Realtime and vector storage are switched off in `config.toml`: this is an idle
game, settlement-on-read plus a heartbeat is enough, and Unity WebGL excludes
`System.Net.WebSockets` outright — a client that tried to use Realtime would hang rather
than fail cleanly.

## Rules that are not negotiable

- **The service-role key never leaves the server.** Anything shipped inside a WebGL build
  is public. The Unity client gets the API base URL and whatever token Auth issues it, and
  nothing else.
- **Never trust a client-supplied timestamp.** Not for settlement, not for cooldowns, not
  in tests. The test suite advances an injected clock instead.
- **Every mutating endpoint takes a `request_id`.** WebGL tabs get suspended mid-request
  constantly, and a retried craft that consumes twice is a support ticket.
