# OldenPedia

A Civilopedia-style in-game encyclopedia for **Heroes of Might and Magic: Olden
Era**, built as a **BepInEx 6 IL2CPP** plugin. It reads the game's own data,
localization, and visual style **live at runtime** (so it survives patches) and
shows units, heroes, artifacts, and skills in a themed, in-game window that
matches the game's own look.

Current version: **0.75.0**.

> This README doubles as the project's engineering memory. The hard part of this
> mod was never the UI — it was reverse-engineering an obfuscated IL2CPP game and
> discovering, by trial and error, how its data, localization, language setting,
> visual style, and **input** work. Those findings are written down here so they
> aren't lost.

---

## Features

- **Live data extraction** — units, heroes, artifacts, and skills are read from
  the game's in-memory config registry every run; nothing is hardcoded.
- **Localized names & descriptions** — pulled from the game's own translation
  files, in the player's language (auto-detected — see Localization below).
- **Themed uGUI window** — master/detail: faction/group accordion on the left,
  variant tabs + scrollable detail on the right, restyled to match the game's
  own UI (see Visual style below). Centered "OldenPedia <version>" header, plus
  a live **search box** that filters the left-hand list as you type.
- **3D unit portraits** — renders each unit's own in-game prefab (idle pose,
  best-available LOD, background-prewarmed for the whole roster) into a frozen
  snapshot shown in the detail panel. Opt-in (`Show3DUnitPreview`).
- **Portraits** for heroes, artifacts, and skills (sized to ~40% of the detail
  panel), resolved from the game's own loaded sprites by name.
- **Resource-bar button** (the gold "?") plus a rebindable **`.`** toggle.
- **Input blocking** — while the window is open, the map underneath ignores
  clicks, scroll-zoom, and hotkeys (see the Input section — this was one of the
  hardest things in the project).
- **Effects**: artifact stat bonuses (including full set-combination bonuses)
  and skill effects + perks are formatted from the game's own bonus data, not
  guessed prose.
- **Skills** are grouped one collapsible entry per logical skill (matching the
  unit-faction/artifact-set accordion style), with Basic/Upgrade tiers nested
  inside; skills that read identically across Normal/Arena/Campaign are merged
  into one entry instead of showing near-duplicates.

---

## Environment (confirmed)

- **BepInEx 6 IL2CPP**, pinned to `6.0.0-be.755`. Not Mono — the game is IL2CPP.
- **Unity 6000.0.66f1**, .NET 6 runtime. Project targets `net6.0` (so
  `System.IO.Compression` is available for reading the localization zip).
- **Game logic is in `Hex.dll`**, not `Assembly-CSharp.dll`.
- **Obfuscated** with GUPS.Obfuscator: most member/type names are 2–4 letters and
  **change per patch**. Two stable exceptions we rely on: **`Hex.Configs.*`** and
  **`Hex.Loc.*`** names are *not* obfuscated.
- Interop assemblies live in `<GameDir>/BepInEx/interop/`. Beyond the obvious
  ones, this project also references `UnityEngine.PhysicsModule`,
  `UnityEngine.ParticleSystemModule`, `UnityEngine.AudioModule`, and
  `UnityEngine.AnimationModule` (needed for the 3D unit preview's safe-instance
  stripping).
- Player's game path (dev machine, for reference): a Steam/Proton install on
  Linux; probe output lands in `<GameDir>/BepInEx/OldenPedia/*.txt` on the real
  filesystem (outside the Proton prefix).
- Confirmed Steam App ID: **3105440**.

---

## Data architecture (as actually found)

> The original theory that game data was MessagePack DTOs was **wrong** for the
> pedia's purposes. The real content lives in plain config objects.

- A **master registry singleton** (obfuscated type, e.g. `cjw`, with a static
  self-field) holds many **catalog fields** of type `cjv\`2<TValue, String>`
  (a keyed dictionary/list of config objects). One catalog per content type.
- Config types extend **`Hex.Configs.ConfigElementBase\`1<String>`** (which
  provides the inherited `id`). Key ones:
  - **`UnitLogicConfig`** — `baseSid`, `upgradeSid`, `tier`, `fraction`, `stats`
    (→ `UnitStat`: hp/damageMin/damageMax/offence/defence/speed/initiative),
    `abilities` (`AbilityLogicConfig[]`), `passives`.
  - **`UnitViewConfig`** — `name_` loc key, `narrativeDescription_`, `mesh`,
    prefab. (Most unit `name_` are empty; names come from loc keys instead.)
  - **`HeroConfig`** — `name`, `description`, `classType`, `costGold`, `fraction`,
    `startLevel`, `mesh`, `icon`, **`stats`** (`HeroStat`:
    offence/defence/spellPower/intelligence + luck/moral + derived `…Per` floats).
  - **`ItemConfig`** = **artifacts** — `icon`, `name`, `description`,
    `narrativeDescription`, `rarity`, `slot_`, `costBase`, `maxLevel`, `itemSet`,
    **`bonuses`** (`BonusConfig[]`).
  - **`SkillConfig`** — `icon`, `name`, `desc`, `isPseudoSkill`, `skillType`,
    **`parametersPerLevel`** (`SkillParameter[]`).
  - **`BonusConfig`** — `type` (String, e.g. `heroStat`), **`parameters`**
    (`String[]`), plus targeting fields.
  - **`SkillParameter`** — `name`, `desc`, **`bonuses`** (`BonusConfig[]`),
    **`subSkills`** (`String[]` — perk ids).
  - **`BuffConfig`** — real `id` field, plus `cngp`/`cngq` properties that
    resolve to a loc *name*/*desc* key — but these are **computed**, not a fixed
    `<id>_name`/`<id>_description` pattern (confirmed while reverse-engineering
    ability names, below). Used for artifact/skill bonuses that reference a buff
    rather than a plain stat.
  - **`Hex.Settings.Data.SettingsData`** — holds the live `language` string, but
    is only reachable as an *instance* field (`bric`) handed out lazily to
    several settings-UI section objects; see Localization for the full story.

### Unit upgrade families
Each unit family is 3 entries: base (`baseSid=null`, `upgradeSid=X_upg`), `X_upg`,
`X_upg_alt`. Group by stripping `_upg_alt` then `_upg`; variant rank base=0,
_upg=1, _upg_alt=2.

### Unit ability names — solved
Units reference abilities via an `AbilityID` struct with no usable string id
(its `ToString()` returns the exact same garbage value for every ability across
every unit — a boxed-value-type pitfall, see IL2CPP lessons below). The real key
is one level deeper: an ability's `rank` (a plain `Int32` field) combines with
the unit's **family base id** (same stripping as unit grouping) to form
`"<familyBaseId>_ability_<rank>_name"` / `"_description"` — confirmed against
the real Lang files. A handful of shared/generic abilities don't follow this
convention and are skipped rather than guessed.

**Unit passives remain unsolved** — `UnitPassiveConfig.id` is always null and
its nested `BuffData` carries no id/name field either.

### Artifact sets
`ItemConfig.itemSet` groups artifacts into sets. The set itself has a real
loc-resolvable name (`<set>_item_set`) and one or more combination-bonus tiers
at `<set>_item_set_description_<n>` (n = a 1-based tier index, **not** a piece
count — a 6-item set can have just 2 tiers, so it isn't a simple sequence).
These are shown both as a synthetic "Set Bonus" list entry and appended directly
onto every individual piece's own page (a separate entry alone proved easy to
miss during testing).

---

## Localization

### Loading the text (disk-based, solved early)
Reflection into the game's loc tables was a dead end (the live instances were
never reachable). The working approach reads the game's own translation files
directly from disk:

- `Application.streamingAssetsPath/Core.zip` → `Lang/<language>/texts/<category>.json`
- Language folders are **full words** and are enumerated live from whatever's
  actually in the zip (not a hardcoded list) — so new languages the game adds
  later are picked up automatically.
- File format: `{"tokens":[{"sid":"...","text":"..."}, ...]}`.

#### ⚠️ Parser gotcha (cost several builds)
Effect strings contain `{0}`/`{1}` placeholders — which include `}`. The parser
**must not split records on `}`**. It uses one regex to grab each `sid`+`text`
pair, tolerating braces and newlines in values. (Symptom of the bug: *names*
resolve but *effect descriptions* come back empty.)

### Which language to load — the real saga
Getting the *content* was easy. Figuring out **which language the player is
actually using**, automatically, took many rounds of reverse-engineering.
Everything tried, in order, and what actually worked:

1. ❌ **Unity's own Localization package** — this game doesn't use it at all
   (`LocalizationSettings` type isn't present).
2. ❌ **PlayerPrefs, guessed key names** — no match on any common key name.
3. ❌ **A settings file in `Application.persistentDataPath`** — text-file scan
   found nothing (wrong assumption about where settings live for this game).
4. ⚠️ **`Hex.Settings.Data.SettingsData.language`** (a plain `String` field) —
   found via a broad reflection sweep of every `Hex.*` type for anything
   `lang`/`locale`-named. It's the right field, but it's an **instance** field,
   not behind any static registry — distributed instead as an instance field
   (`bric`) on several settings-UI section objects (`BhGeneralSection`,
   `BhSettingsSection`, etc.), and it's only **populated once the player has
   visited that specific in-game Settings tab** this session. Confirmed via a
   live-object search (`Resources.FindObjectsOfTypeAll`) that these objects
   exist from launch but the field stays `null` until then — even the parent
   controller managing the whole Settings screen doesn't have it early.
   Attempting to trigger it ourselves (calling the section's own `Show(...)`
   method with a null argument) confirmed it needs the real data as an *input*,
   not a trigger — a dead end, safely (the call just throws, caught cleanly).
   **Still used** as the highest-priority source when available, since it's the
   most current value if the player *has* visited Settings this session.
5. ❌ **Windows/Proton registry (PlayerPrefs storage)** — Unity's PlayerPrefs on
   Windows/Proton live in the Wine prefix's `user.reg`, not a settings file;
   confirmed reachable from inside the game process via Wine's `Z: -> /`
   drive mapping (the game's own `GameRootPath` is a **Wine virtual drive
   path**, e.g. `S:\...`, and does *not* lead anywhere on the real filesystem).
   A key literally named `"language"` was found and read correctly — but
   proven **stale**: it read `"english"` while the game was actually running a
   different language, and the identical `"english"` value showed up across
   *every other game's* Wine prefix on the same machine. It's a Unity default
   written once, not a live-tracked setting. **Disabled** (kept only as a
   documented dead end in `LangLoader.cs`) — a confident wrong answer is worse
   than falling through.
6. ✅ **Read what's actually displayed on screen — the real fix.** Sidesteps the
   whole "where is the setting stored" question by instead asking "what
   language is the game showing right now." Builds a reverse lookup of every
   available language's translation for **8 independent reference keys** from
   `menu.json` (deliberately mixing short and long phrases — `button_exit`
   ("Exit"), `button_start_game`, `button_cancel`, `button_apply`,
   `navigationPanel_matchmaking`, `lobby_find_placeholder`, `fractions`,
   `tab_players_num`), then scans every currently-loaded `TMP_Text` for exact
   matches, and the folder with the **most matching votes** wins. Voting (not
   "first match") is the redundancy against future languages colliding on any
   one key's translation — a collision on one key is logged and simply
   outvoted by the others. UI text is set once when a screen is built and often
   stays in memory even while hidden, so this **does not require the player to
   have visited any specific screen** this session, unlike source #4 above.

**Final detection order** (`LangLoader.DetectLanguage`): live `GameSettings` (#4,
most current when available) → UI text voting (#6, works from the very first
pedia open) → system language (final fallback). If the language was only
detected via a fallback (not `GameSettings`), the pedia automatically re-checks
and upgrades — and **rebuilds all extracted data** — the next time it's opened,
in case the player has since visited Settings.

### Loc key conventions
- **Units**: `<ownId>_name`, `<ownId>_narrativeDescription` (in `unitsAbility.json`).
- **Heroes**: the **name IS the id** (`human_hero_1` → "Ister"); desc `<id>_description`.
- **Artifacts**: `<id>_artifact_name`; the `<id>_artifact_description` key can be
  **flavor text, mechanical effect text with a `{0}`, or both** — don't assume
  either way; always try to fill placeholders and fall back to omitting a line
  rather than showing a raw unfilled `{0}`.
- **Skills**: `skill_<id>_name`, `skill_<id>_desc`; levels `skill_<id>_name_1..5`;
  perks `sub_skill_<id>_<n>_name` / `_desc` (with `_alt`/`_new` variants).
- **Artifact sets**: `<set>_item_set` (name), `<set>_item_set_description_<n>`
  (combination bonus tiers).

### Effects come from bonuses, not description text
- **Artifacts**: the effect is the **`bonuses`** array. `heroStat` bonuses have
  `parameters = [statName, value]` → e.g. "Spell Power +12"; `…Per` stats are
  fractions → percents. Non-`heroStat` bonuses reference a buff via
  `BuffConfig` (its own `cngp`/`cngq` give the real name/desc key — computed,
  not a fixed pattern from the buff's `id`). Any bonus that can't be resolved to
  real text is **omitted**, never shown as a raw type/parameter dump — showing
  the underlying mechanic verbatim was explicitly flagged as unhelpful.
- **Placeholder filling is percent-aware**: a `{0}%` slot fed a stored 0–1
  fraction is multiplied by 100 first (confirmed bug: "Music Sheet" showed
  "+0.40%" instead of the game's own "+40%" before this fix).
- **Skills**: each `SkillConfig` has `parametersPerLevel` (Basic/Advanced/Expert).
  A tier that resolves to only **one** real perk option isn't a genuine choice —
  it's folded into the main skill's own description instead of becoming its own
  menu entry. Pseudo skills (`isPseudoSkill`, ids `skill_pseudo_*`), and skills
  with an `arena_skill_*`/`campaign_skill_*` id prefix (mode-specific duplicates
  of the same skill), are filtered out entirely.

---

## Visual style — matched to the game, not guessed

A dedicated probe (**`Y`** key, `StyleProbe.cs`) walks every currently-visible
UI element and dumps its sprite name, color (hex), Simple/Sliced/Tiled type,
size, and (for text) font name/size/color — run it with whatever game screen
you want to copy the look of open. This is how the pedia's panel, buttons, and
fonts were matched to the game's own tavern/settings screens instead of
approximated:

- **Panel background** uses the game's own `universal_back` sprite (confirmed
  as a generic window frame — it showed up at a dozen different sizes across
  multiple game screens), not a flat color + drawn border. The sprite has
  visible internal padding baked into its texture, so it's drawn as a separate,
  larger layer *behind* the panel (oversized by a fixed margin) rather than
  directly on the panel's own rect, so its visible frame actually reaches the
  gold-bordered elements sitting on top of it instead of looking inset.
- **Dropdown, search box, and tabs** use the game's own `TextField_Big` sliced
  sprite instead of flat rectangles (tabs are tinted gold/dark for
  selected/unselected state, same texture).
- **Close button** uses the game's own `Button_EskBig` sprite.
- **Font** is `LT-Regular` (confirmed as the game's own body-text font via the
  probe), resolved by name via `FontLoader.cs` (mirrors `IconLoader.cs`'s
  by-name sprite resolution, but for `TMP_FontAsset`).
- **Colors**: primary text `#EFEFEF`, gold accent `#D9C08E` — both taken
  directly from the probe's output, not approximated.

Sliced sprites with rounded/tapered edges need noticeably wider text margins
than a plain rectangle would — text positioned for a flat box will spill past
the visible curve of a pill-shaped sprite.

---

## 3D unit portraits

Renders a unit's own in-game prefab to a texture for the detail panel.

- Instantiated under an **inactive** holder first (Unity defers Awake/OnEnable),
  every `Behaviour`-except-`Animator`/`Collider`/`Rigidbody`/`ParticleSystem`/
  `AudioSource` is stripped via `DestroyImmediate` while still inactive, then
  placed on a private layer far from any gameplay position, and only then
  activated. What's left is pure render data with no game logic to misfire.
- **Animator is kept** (not stripped) so the unit settles into its idle pose
  instead of the T-pose bind pose — captured after a short settle delay so the
  Animator has actually applied that pose before the camera reads bounds
  (capturing too early measures the wider bind-pose bounds, causing undersized/
  miscentered results).
- **`LODGroup.ForceLOD(0)`** is applied — Unity picks mesh/texture detail based
  on how much of the camera's view an object fills, and this camera's framing
  distance was landing on a lower LOD than the game's own close-ups use, which
  was the actual cause of "renders look low-res" (not render *texture*
  resolution, which had no effect once this was found).
- Camera framing uses proper trigonometry (FOV + the larger of width/height)
  rather than a diagonal-bounds-magnitude guess, which mis-sized non-cube-shaped
  bodies (a tall skeleton vs. a wide dragon) inconsistently.
- Captured **once per unit, in memory only** (a disk-cache version was tried and
  rolled back — a stale cached file from before a rendering fix would keep
  showing the old result forever, silently hiding whether a fix even worked).
  A bounded LRU cache (48 textures) keeps memory in check; the whole roster is
  background-prewarmed one at a time at idle priority so browsing feels instant
  without needing to reopen a unit to see its portrait.
- Camera/render texture settings (MSAA, HDR) follow the game's own
  `QualitySettings`, not arbitrary values.
- **Known limitation**: orientation is not fixable from our side if a unit's own
  prefab was authored facing a different direction than others — the code never
  rotates the instantiated prefab, so whatever direction it faces is exactly how
  it was modeled.

---

## Input blocking (the hard-won part)

**The game reads gameplay/camera input through LEGACY `UnityEngine.Input`**, not
the new Input System. This is the key fact. Everything that failed, failed
because it targeted the new system:

- ❌ Marking new-system events `handled` — ignored.
- ❌ Rewriting new-system event values — too late / not the read path.
- ❌ `InputSystem.DisableDevice` on Mouse/Keyboard — the game doesn't read them
  for the map, so it changed nothing (and froze *our* reads).
- ❌ `onAfterUpdate` hook / `[DefaultExecutionOrder]` — not available/usable in
  this interop.

**What works:** use **HarmonyX** (ships with BepInEx; confirmed patchable here by
the community cursor-fix mod) to patch the **legacy `Input` reads** —
`mouseScrollDelta`, `GetMouseButton/Down/Up`, `GetKey/Down/Up`, `GetAxis/Raw` — to
return neutral **only while the pedia is open**. Meanwhile:

- the **pedia reads the NEW Input System** (`Mouse/Keyboard.current`), which the
  patches don't touch, so its own clicking/scrolling/Esc keep working;
- `Input.mousePosition` is left **unpatched** so the (legacy-based) cursor overlay
  keeps tracking.

The **search box's text entry** also reads the new Input System — but since
`Keyboard.onTextInput` turned out not to be present in this game's interop
binding (see IL2CPP lessons below), it's implemented as direct per-key polling
(letters/digits/space/a few punctuation marks), the same proven-safe pattern
already used for Backspace/Escape elsewhere.

Requires two references most IL2CPP mods omit: **`UnityEngine.InputLegacyModule.dll`**
(interop) and **`0Harmony.dll`** (`BepInEx/core`). Missing the legacy module is
why `Input` first failed to compile.

---

## IL2CPP reflection & compiler lessons (apply everywhere)

- `GetType()` returns a useless wrapper — match on **`FieldType.FullName`** strings.
- `GetGenericArguments()` / `.FullName` on **open generics crashes natively** —
  avoid.
- Iterating an Il2Cpp `List<T>`: call `ToArray()` via reflection, `TryCast` to
  `Il2CppSystem.Array`, walk `Length`/`GetValue`. Arrays (`T[]`) likewise via
  `Il2CppSystem.Array`; get element type from `FieldType.GetElementType()`.
- Value-type field reads return boxed pointer garbage via `ToString()` — you must
  `Unbox<T>()`, switched on `FieldType.Name`.
- **Native crashes are uncatchable** by managed `try/catch`. So always-on code
  paths must not invoke arbitrary static methods/getters; only the manual F-key
  probes do risky discovery — and even a deliberate, user-requested method
  invocation (`Show(null)` while chasing the language setting) should be
  isolated, logged heavily, and wrapped so a thrown *managed* exception (the
  likely/actual outcome) is caught safely.
- `BindingFlags` 62 includes `DeclaredOnly` (hides inherited members); targeted
  reads use 60 + a `GetProperty` fallback (exposed as `DataExtractor.FR`, reused
  project-wide rather than redefined per file).
- **IL2CPP interop only generates bindings for members the game itself
  references.** This bit us more than once: `Vector3.one`/`.up` weren't always
  available (use `new Vector3(...)` literals instead), and
  `Keyboard.onTextInput` isn't present at all in this game's binding (this game
  apparently never calls it) — direct per-key polling was the fix, not a
  workaround to avoid.
- **C# named value tuples can fail to compile** with
  `error CS0656: Missing compiler required member
  'System.Runtime.CompilerServices.NullableAttribute..ctor'` — happened twice in
  this project. `<Nullable>disable</Nullable>` in the csproj does *not*
  prevent it; the actual trigger seems tied to matching against an
  already-nullable-annotated API in a referenced module. Two-part fix, both
  present in this project: (1) avoid named tuples entirely — use a small plain
  class instead (see `BuiltSkill`/`SkillOption` in `DataExtractor.cs`); (2) as a
  robust general safety net regardless of the exact trigger, `NullablePolyfill.cs`
  defines the missing attribute types (`NullableAttribute`,
  `NullableContextAttribute`, `NullablePublicOnlyAttribute`) so the compiler has
  something to bind to no matter what construct needs them.
- `Resources.FindObjectsOfTypeAll(Il2CppSystem.Type)` works with a type obtained
  via **reflection** (not just `Il2CppType.Of<T>()`), which is how live
  instances of the game's own obfuscated/unreferenced types (settings-UI
  sections, etc.) get found without needing a compile-time reference to them.
- uGUI click handling: poll the pointer and use
  `RectTransformUtility.RectangleContainsScreenPoint(rect, pointer, null)` — a
  **null camera is correct** for a ScreenSpaceOverlay canvas.
- Proton reports tiny scroll-wheel deltas (±1) vs Windows (±120) — normalize by
  **sign** (notches), with a keyboard fallback (↑/↓, PageUp/Down).
- **From inside the game process (running under Wine/Proton), `GameRootPath` is
  a Wine virtual drive path** (e.g. `S:\...`), not a real filesystem path —
  walking "up" from it will never reach `steamapps`/`compatdata`, since that
  structure isn't exposed through the game's own drive letter at all. Wine's
  conventional `Z:` drive (when configured) maps directly to the real Linux
  root (`/`) and can reach the same files a different way, if needed.

---

## Prerequisites

1. **.NET SDK 6.0+**.
2. The **community BepInEx 6 IL2CPP pack for Olden Era** (with the deobfuscation
   config) installed into the game root.
3. **Launch the game once** after installing BepInEx to generate
   `BepInEx/interop/`.
4. *(Recommended)* **UnityExplorer (IL2CPP build)** for live inspection.

## Build

1. Set `<GameDir>` in `OldenPedia.csproj` to your install path.
2. `dotnet build -c Release`
3. Copy `bin/Release/net6.0/OldenPedia.dll` into `<GameDir>/BepInEx/plugins/`.
4. Launch the game.

The csproj references the game interop DLLs, **`UnityEngine.InputLegacyModule.dll`**,
**`0Harmony.dll`** (`$(GameDir)/BepInEx/core`), and the
Physics/ParticleSystem/Audio/Animation Unity modules (for the 3D unit preview).
On Linux these paths are case-sensitive — match `ls BepInEx/interop` casing.

### Linux (Proton / Steam Deck)
BepInEx runs *inside* Proton, so the plugin behaves as on Windows. Set the
doorstop override in Steam launch options:
```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```
Launch once to generate `BepInEx/interop/`, then build natively (or use
`./build.sh "<GameDir>"`, which also checks that interop exists).

---

## Config

`<GameDir>/BepInEx/config/oldenpedia.civilopedia.cfg`, under `[General]`:

- **`ToggleKey`** — default `Period` (`.`). Accepts a new-Input-System `Key` name.
- **`Language`** — default `auto`. A folder name (`german`, `polish`, …) or code
  (`de`, `pl`, …) forces a language; `auto` runs the full detection chain above.
- **`BlockMapInput`** — default `true`. Turns the Harmony input blocking on/off.
- **`Show3DUnitPreview`** — default `false`. Opt-in 3D unit portrait rendering.

---

## Dev probes (keys)

These are discovery tools; they write to `<GameDir>/BepInEx/OldenPedia/*.txt`.
Function keys were originally used throughout, but **`F1`** conflicts with this
game's own bug-report overlay and **`F12`** with Steam/OS screenshot capture —
newer probes use plain letter keys instead, which is actually *safer* here:
these checks only run while the pedia window is already open, and at that
point the game receives no input at all (see Input blocking), so a letter key
can't collide with any gameplay hotkey.

| Key | Probe | Output |
|-----|-------|--------|
| `Y` | Visual style probe — colors/sprites/fonts of whatever screen is open | `style_probe.txt` |
| `L` | Language-setting probe — the full reverse-engineering trail for the game's language setting | `lang_setting_probe.txt` |
| F2 | Ability probe | `ability_probe.txt` |
| F3 | Data extractor | `units_dump.txt` |
| F4 | Type detail | `type_detail.txt` (targets in `[General] InspectTypes`) |
| F5–F9 | Container/Type/Data/GameData/UI probes | various |
| F10 | Loc probe | `loc_probe.txt` |
| F11 | LangLoader probe | `lang_probe.txt` |

`DataExtractor` also writes `effects_dump.txt` (artifact bonuses + skill
`parametersPerLevel`) when categories build, which is how the effect/skill work
was reverse-engineered.

---

## Known limitations / TODO

- **Unit passives remain unsolved** — no discovered path to a passive's real
  name (see Data architecture above).
- **Unit portrait orientation** can't be corrected from our side if different
  prefabs were authored facing different directions — this is a property of
  the source assets, not something the render code controls.
- **Artifact set-tier piece counts** (e.g. "requires 3 of 6 pieces") aren't
  shown per-tier, since the loc data doesn't expose a reliable mapping from
  tier index to piece count (confirmed: a 6-item set can have just 2 bonus
  tiers). Tiers are honestly labeled "Partial set bonus" / "Full set bonus"
  instead of guessing a specific wrong number.
- **`{0}` value filling** is generally solved (percent-aware, falls back to
  omitting a line rather than showing a raw placeholder) but still relies on
  positional/heuristic matching of bonus parameters in a few paths — a
  mismatch here would show a *plausible but wrong* number rather than an
  obviously-broken one, worth double-checking if a specific effect ever looks
  suspicious.
- **"Tavern screen" style matching** covered the highest-impact elements
  (panel, dropdown/search/tabs, close button, font, two colors) via the `Y`
  probe; smaller elements (list rows, portrait frame) were deliberately left
  as-is rather than guessing further sprite matches without more probe data.

---

## Files

| File | Purpose |
|------|---------|
| `src/Plugin.cs` | BepInEx entry; config; Harmony init; injects the behaviour |
| `src/DataExtractor.cs` | Live data → category model; effects; skill grouping/merging |
| `src/PediaWindow.cs` | Themed window, master/detail, tabs, portraits, search box, header |
| `src/PediaBehaviour.cs` | Input loop / toggle / probe keys (reads the new Input System) |
| `src/PediaButton.cs` | Resource-bar "?" button |
| `src/InputBlocker.cs` | Harmony patches on legacy `Input` (the blocking) |
| `src/LangLoader.cs` | Core.zip localization loader + full language-detection chain |
| `src/Localizer.cs` | Token/pattern key resolution |
| `src/IconLoader.cs` | Resolves icon strings → loaded `Sprite`s by name |
| `src/FontLoader.cs` | Resolves font names → loaded `TMP_FontAsset`s by name |
| `src/UnitModel.cs` | Unit prefab access |
| `src/UnitPreviewRenderer.cs` | 3D unit portrait rendering (safe-instance build, LOD force, capture) |
| `src/NullablePolyfill.cs` | Compiler workaround — see IL2CPP lessons above |
| `src/Il2CppUtil.cs`, `src/FileLogListener.cs` | Interop helpers, file logging |
| `src/AbilityProbe.cs`, `src/StyleProbe.cs`, `src/LangSettingProbe.cs`, `src/*Probe.cs` | Dev discovery probes (see Dev probes table) |
| `OldenPedia.csproj`, `build.sh`, `nuget.config` | Build config |

## Legal / etiquette

This project's own source code is licensed under the **MIT License** (see
`LICENSE`) — use, fork, and modify it freely.

That license covers **this repository's code only**. It does not extend to
`Hex.dll`, the game's other assemblies, or any extracted game assets, all of
which remain the property of their original developers. This is a
single-player QoL/reference mod: it only reads and displays data the player
already owns, and this repo does not redistribute `Hex.dll` or any extracted
assets. Credit the BepInEx pack authors and mark it as requiring their pack.
