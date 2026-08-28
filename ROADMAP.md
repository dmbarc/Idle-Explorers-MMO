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
bash Tools/check.sh     # everything: 1290 standalone + 236 rules + 10 content + 84 db + 207 API
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

### 2.7 Tooling traps in this repo

- Bash heredocs eat one backslash level; then Python reads `\b` as a backspace. **Never
  put regex escapes in a heredoc.** Write them via the `Write` tool, or as named
  constants in the C# file.
- `git add -A Assets` sweeps in the entire Kenney pack and times out. Stage named files.
- `perl` eats `$"` when splicing C#.

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

### Phase B — shared world state (the big one) — NEXT

Monsters and drops are per-client today: every player spawns their own `MonsterSpawner`
population as theatre over a server that only counts kills.

Needed:
- Server owns monster instances per map: id, position, health, alive.
- Drops belong to the map, not the killer, and everyone sees them.
- Damage tagging, so credit and loot go to whoever fought.
- Client `MonsterSpawner` becomes a *renderer* of server state, the way
  `RemotePlayerView` renders presence.

Reuse the presence design: one poll carries position and reads the world. The seam is
already there — `PresenceSync` is the file a socket would later replace.

**This is comparable in size to the whole multiplayer piece.** Do not start it in the
same sitting as anything else.

### Standing constraints

- **No real money.** `ShopManager` purchasing is a test grant gated on the
  `shop_test_grants` feature flag, which must be **explicitly** true — unknown flags are
  ON in this system, which is right for gameplay and catastrophic for a currency tap.
  Real purchasing needs store registration and server-side receipt validation first.
- **Nothing has been pushed.** Nineteen commits sit on `server-authoritative` locally.
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
