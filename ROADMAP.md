# Idle Explorers — state, lessons, and what is left

Written at the end of a long session so the next one does not have to rediscover any
of it. Three parts: **where things stand**, **the mistakes that keep repeating**, and
**the work that remains**, in the order I would do it.

---

## 1. Where things stand

### Live right now

| Piece | Where | Notes |
|---|---|---|
| Client | https://idle-explorers.pages.dev | Cloudflare Pages, `--branch=main`. Preview URLs are **not** on Supabase's redirect allow-list — Google sign-in silently bounces to the production domain. |
| API | https://api-production-c568.up.railway.app | Railway, project `idle-explorers-api`, region `us-east4`. Hard billing cap **$10/mo**. |
| Database + Auth | Supabase `tmyjwxoadxtlxpdzyesk` | Free tier. RLS enabled **and forced**, no policies, on every table. |

**Deploy sequence** (all three usually needed together):

```bash
cd server && supabase db push --yes          # migrations
cd .. && railway up --detach                 # API
# Unity WebGL build, then:
npx wrangler pages deploy Build/WebGL --project-name=idle-explorers --branch=main --commit-dirty=true
```

Unity headless build (~8–12 min):

```bash
"/c/Program Files/Unity/Hub/Editor/6000.4.6f1/Editor/Unity.exe" -quit -batchmode -nographics \
  -projectPath "C:/Users/danie/Idle Explorers MMO" \
  -executeMethod WebGLBuild.BuildFromCommandLine -logFile /tmp/webgl.log
```

Verify a deploy by comparing `stat -c%s Build/WebGL/Build/WebGL.wasm.unityweb` against
`curl -o /dev/null -w '%{size_download}'` on the live URL. **Do not** trust the
deployment message — it reports the preview alias, not whether production serves it.

### Test suites

```bash
bash Tools/check.sh     # everything: 1876 standalone + 250 rules + 10 content + 84 db + 244 API
bash Tools/verify/verify.sh   # client compile only, ~40s — use this while iterating
```

The API tests need the local Supabase stack (`cd server && supabase db reset --local`).

### Architecture in one paragraph

ASP.NET Core owns every rule. `Assets/Scripts/Rules/` is compiled **twice** — into Unity
and into `server/src/IdleExplorers.Rules` (files linked, not copied) — so a formula
cannot drift between client and server. The client sends **intent** and renders; the
server settles time, grants everything, and is the only writer of anything persistent.
Supabase is Postgres and Auth, nothing more. The client holds no Supabase credential
beyond the publishable key.

### Things that are true and easy to forget

- **JsonUtility** cannot deserialise a `Dictionary` and cannot parse a bare top-level
  array. Wire shapes are arrays of `{k,v}`. It matches on **field name** and silently
  leaves mismatches at their default — no error, ever.
- **System.Text.Json** ignores fields unless `IncludeFields = true` (set globally in
  `Program.cs`). Shared rules types are all fields.
- **Unity WebGL has no usable socket.** `System.Net` is excluded; `ClientWebSocket`
  fails by *hanging*, not by failing to compile. Multiplayer is polled for this reason.
- **`PlayerPrefs` on WebGL is async IndexedDB** and cannot survive a page navigation.
  The OAuth verifier lives in `localStorage` via `IdleOAuth.jslib` for this reason.
- **A SPUM rig's origin is at its feet**, art ≈2 units tall. Every world-space height is
  measured from the feet.
- **`Shader.Find` only finds shaders included in the build.** Always end a fallback
  chain at `Sprites/Default` and always assign the material.
- **`EditorUtility.DisplayDialog` returns false in batch mode** — a build script that
  asks will silently do nothing and report success. Use the `*Silent` entry points.
- Supabase signs **ES256** via JWKS, not HS256.
- Unity ships **every** folder named `Resources`, anywhere, referenced or not.

---

## 2. The mistakes that keep repeating

These cost more time this session than any feature. Read this section before writing a
test or declaring something fixed.

### 2.1 A lock is not proof of a key

Every authentication test asked whether something was **refused**: unauthenticated →
401, forged token → 401, expired token → 401. All passed. All kept passing while the
API rejected **every real token**, because it validated HS256 against a project that
signs ES256.

A server that refuses everything passes every refusal test.

**Rule: every refusal test needs a paired acceptance test, in the same file.** Neither
half proves anything alone. This has since caught the party seat race, the shop grant,
and the session guard.

### 2.2 Crediting is not paying; storing is not showing

- The mystic gem got an endpoint and six tests proving seconds were **credited**. Every
  one passed while a gem did nothing, because the settle bailed on `elapsed <= 0` before
  reading the credited column.
- Relic coins were granted correctly and the display read zero, because nothing raised
  `OnRelicCoinsChanged`.
- Equipment applied correctly and the sprite stayed bare, because nothing raised
  `OnEquipmentChanged`.

**Rule: test the observable outcome, not the intermediate write.** "Does the balance
move" is not "does the player see it".

### 2.3 Built and never called

`Session.SignedInChanged` had zero subscribers. `BossPortalController.TryEnterAsync` had
no caller. `BossHealthBar` was never instantiated. `GameBackend.Configure` was never
invoked (fixed in an earlier session). Each looked finished and was unreachable.

**Rule: when adding a component, grep for who constructs or subscribes to it.** If the
answer is nobody, it is not done.

### 2.4 Proximity checks prove nothing

Four separate structural checks passed by finding a guard *near* the code rather than
*on* it — an alternation matched the other branch, a 600-character window reached into
the block above, a file-wide match found a neighbour's guard, a wire-contract check
found a local variable with the right name.

**Rule: slice to the exact construct** (brace matching, the emitted object, the specific
`#if` arm) and then **prove the check fails** by reinstating the bug.

### 2.5 The fix in the wrong place

The spawn-position bug survived two attempts. Both were correct code in the wrong
location: `GoToGame` is one of *three* ways into a map, and the save at map-entry ran
while the rig stood on the spawn point, overwriting the coordinates it was about to
restore.

**Rule: find the single seam every path passes through.** `LocalRewards`,
`ServerActions` and `ZoneManager.EnterMap` exist for this reason.

### 2.6 Shared-database test isolation

Three presence tests and one encounter test passed alone and failed in the suite,
because `presence` and `encounter` are shared tables and the assertions counted other
tests' rows.

**Rule: give each test its own map id / scope the query to its own actors.** Isolation
by construction beats cleanup that can be skipped.

### 2.7 Nothing closed the row

The boss looked broken five different ways in one playtest — the King could not be
targeted, walking in did nothing for the second player, there was no way out, and the
fight was invisible to everyone but whoever got there first.

Four of the five were the same fact: **an encounter row that nothing ever closed.**
Dying did not end an encounter. Nor did closing the tab, nor walking out of the arena
— the fail condition is the enrage clock, so the server had no reason to think
anything had happened. The row stayed live with the player's name on it, and every
engage after that was refused 409 for five minutes. From inside the client that reads
as an arena with no boss in it: engage fails, BossController never starts, and there
are no telegraphs, no health bar, and nothing to hit.

**Rule: for every state a request can enter, name the request that leaves it.** If
there is no answer, the state is a trap, and the symptom will appear somewhere that
looks nothing like the cause.

### 2.8 Two loops that never met

`PlayerController` held its target as a `MonsterController`. `BossController` is
deliberately not one. So there was no path from a click, from auto-mode, or from any
single-target ability to a boss — the King stood in his arena throwing cones at
somebody who could only watch. It compiled, and every method involved was correct.

The tell was already in the codebase: `Bladestorm` had a second loop bolted on beside
the first, sweeping `BossController` separately, because somebody had hit this once
and fixed it locally.

**Rule: a second loop over a parallel type is a missing abstraction, not a fix.** The
next feature will not remember to write itself twice.

### 2.9 The break-verification clobbered the work

Verifying checks by breaking them used `git checkout --` to restore. Three of the
files were **new and therefore untracked**, so the checkout errored, restored nothing,
and nine deliberate breaks stacked on top of each other before anybody noticed. Two
separate sessions have now lost uncommitted work to `git checkout`.

**Rule: restore from a copy taken before the break, never from git.** `git checkout`
knows nothing about work that is not committed, and that is exactly what is at risk.

### 2.10 Tooling traps in this repo

- Bash heredocs eat one backslash level; then Python reads `\b` as a backspace. **Never
  put regex escapes in a heredoc.** Write them via the `Write` tool, or as named
  constants in the C# file.
- `git add -A Assets` sweeps in the entire Kenney pack and times out. Stage named files.
- `perl` eats `$"` when splicing C#.
- A background `check.sh` reading files that a foreground break-verification is
  editing produces a failure that is real, reproducible and entirely imaginary. **Do
  not run the suite and the break loop at the same time.**
- A break loop also leaves a **stale incremental build** behind: the next `check.sh`
  reported a rules test failing against source that was demonstrably correct.
  `dotnet build --no-incremental` cleared it. **After breaking things on purpose,
  force a rebuild before believing the next red.**
- `ScalarAsync<DateTimeOffset?>` throws on a `timestamptz`: Npgsql hands back a
  `DateTime` and `Convert.ChangeType` refuses it. Read timestamps through a reader.
- Npgsql allows **one open reader per connection**. A method-scoped `await using var`
  on a command keeps its reader alive until the method returns, so adding any second
  query below it turns every call into a 500. Brace the first read.

---

## 3. What is left

Ordered as I would do it. Each block is roughly one sitting.

### Phase A — the six small ones — DONE (`fd76863e1`)

Exit portal after the King falls; eleven cosmetics given worn art and the twelfth's
`VFX/aura_ember` path fixed (the resolver prefixes `VFX/` itself, so the only aura in
the game had been looking up `VFX/VFX/aura_ember`); the four anvil cosmetics given
icons; a hover card on every station and gathering node; player drops marked so
auto-pickup leaves them; a chat scrollbar, wheel scrolling, sticky scroll-back, and
speech that rides the next poll instead of trickling out one line per two seconds.

`CosmeticChecks` is new and four checks were added to `BossClientChecks`. Every one
was proven by reinstating the bug it describes.

`wanderers_cape` turned out to have been drawing a placeholder since the day it was
written: every `flag.png` in the project is a plain texture, so the mapping never
bound and nothing said so.

### Phase C — content — DONE

**Craftable class weapons.** One per class, class-locked, each granting an `Equip:`
ability and each drawing on the rig. `ItemData.classReq` and the shared `ClassLock`
are new; the gate is enforced in `EquipmentEndpoints` and mirrored in
`EquipmentManager` so the refusal arrives before the round trip rather than after it.

The trap found on the way: `CharacterAppearance` redrew `EquipmentSlots.Cosmetic()`,
and the hands sit on the FUNCTIONAL side of that divide — so every weapon in the game
would have equipped, hit for its damage, granted its ability, and left the character
holding nothing. `Slot.AffectsAppearance` and `EquipmentSlots.Drawn()` exist for that,
and a check asserts both.

**Shopkeeper and gold potions.** `ShopProduct.goldCost` makes one buy endpoint serve
both currencies; `character_buff` holds timed stat bonuses keyed on
`(character_id, stat_id)`, so a second potion refreshes rather than stacks — a
stacking buff is a currency, and the King's fail state is a DPS check. `Buffs` is a
shared rule applied in `ResolveStatsAsync` and mirrored by `BuffManager`, so the
number on screen is the number the server pays.

NPCs are declared in `zone_data.json` and spawned by `ZoneManager`, **not** placed in
the scene: both maps are generated from a recipe, and anything hand-placed is deleted
the next time somebody edits a layout string.

**Potions are a pre-fight decision, deliberately.** The boss freezes a stat snapshot
at engage — that is what stops mid-fight gear swapping, and it cannot tell a potion
from a sword. Said in every description and once more in the shop header, because a
player who discovers it at twenty percent health has been misled by the shop.

### Phase B — shared world state — MONSTERS DONE, DROPS DEFERRED

**The population is the server's.** `map_monster` holds one set per map: id, position,
health, alive. It is topped up and respawned lazily on the presence poll that was
already happening, so a map nobody stands in costs nothing and there is no background
job to run or watch. `MonsterSpawner` stands down whenever the server owns the
population and is otherwise unchanged, which keeps the editor playable offline.

Damage is **reported and capped**, never simulated: `Population.DamageCeiling` is the
boss's own `dps × elapsed` with three seconds of slack. The cap is there to stop one
client emptying everybody else's screen — griefing, not cheating — because a fast kill
earns exactly what a slow one does. `MonsterController` predicts locally so the bar
moves under the swing, and the server's number wins on the next poll.

Only **our own** kills drop loot and move the kill count. A shared monster can fall
because somebody else landed the last blow, and dropping for that would put items on
the ground this player never earned.

`PopulationTests` is new (eight), and `SharedWorldChecks` guards the four wires that
compile perfectly whether or not anybody strung them. All proven by breakage.

#### The fork still open: DROPS

The user asked for drops to be per-map rather than per-player. That is **not** done,
and it is a decision rather than an oversight.

Combat loot is granted by **settlement**, which integrates each player's own time
against server-owned rates. Client-side drops are pure theatre the next pull
overwrites — `InventoryManager.AddUpTo` writes only to the local mirror. So a drop
that everybody can see and anybody can collect needs loot moved OUT of settlement and
ONTO pickup, and that changes what active play is: you would have to walk over your
loot to have it, where today it arrives whether you do or not.

Three ways forward, in the order I would consider them:

1. **Leave it.** Drops stay the killer's private theatre. Cheapest, and the world is
   already shared in the way that shows.
2. **Visible but owned.** Everyone sees the drop with the owner's name on it; only the
   owner can take it, and taking it is still theatre. Looks like an MMO, changes no
   economy.
3. **Loot on pickup.** Genuinely shared drops, granted by the server when collected,
   with combat loot removed from settlement for supervised kills only. The honest
   version, and a real change to how the game plays.

### Phase B, remaining — NEXT

- **Drops**, once the fork above is decided.
- **Monster movement.** The server owns where a monster SPAWNS; each client then walks
  it about with its own wander and chase, so two players see the same goblin drift to
  different places. Nothing depends on it and nobody has complained, but it is the
  visible half of "shared" that is still only half true. Reporting position on the
  poll is the obvious fix and it is a payload decision, not a hard one.
- **`tagged_by` is written and never read.** The column records who did the most damage
  to a monster; nothing asks it yet. It exists because whichever way the drops fork
  goes, that is the question it will need answered.

### Standing constraints

- **No real money.** `ShopManager` purchasing is a test grant gated on the
  `shop_test_grants` feature flag, which must be **explicitly** true — unknown flags are
  ON in this system, which is right for gameplay and catastrophic for a currency tap.
  Real purchasing needs store registration and server-side receipt validation first.
- **Nothing has been pushed.** Twenty commits sit on `server-authoritative` locally.
  Push has never been authorised.
- **I do not handle passwords, tokens or payment details.** The owner runs those.
- **One monster type per zone** — a new map means authoring a new monster.
- **Supabase Pro and a documented restore drill** before any external playtest. Free
  tier pauses after 7 days idle and has no backups.

### Known-unverified

- The Goblin Throne arena was built from a recipe that had never been run. The fight
  logic is well tested server-side; the **room** has had no eyes on it.
- Chat bubbles were reported not working, then reported working; the current build has
  them larger and higher but the placement has not been confirmed by a human.
- `SpriteFacing.DefaultFacing` was flipped to `-1` on report; not re-confirmed.
