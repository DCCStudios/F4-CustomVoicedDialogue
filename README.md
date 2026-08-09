# F4-CustomVoicedDialogue

**In-engine dynamic TTS replacement for player-voiced dialogue. No patching
required.**

Voices Fallout 4 dialogue with text-to-speech, live, as you play — including
a fully custom voice for your own character. Nothing is pre-generated and no
plugin/ESP edits are needed: lines are synthesized on demand and played
through the game's own dialogue system.

Two parts work together:

- **F4SE plugin** (`plugin/`, C++) — hooks the engine where it assigns a
  dialogue line's voice file. Lines that already have recorded audio are
  left alone (unless you ask for replacement). Lines without audio play a
  length-matched silence file so subtitles stay readable and the
  conversation never skips, while the line is synthesized in the
  background; once the audio exists the line speaks.
- **Companion app** (`server/`, C# / WPF) — a desktop app hosting a
  localhost server the plugin talks to. First-run wizard, 16 TTS services,
  DPAPI-protected API keys, automatic per-NPC voice assignment, preview
  buttons everywhere, and a diagnostics page.

The app runs outside the game; the plugin runs inside it. They talk over
`127.0.0.1:47600` and nothing leaves your machine except the TTS requests
themselves.

---

## Table of contents

- [What works](#what-works)
- [Install](#install)
- [Using the companion app](#using-the-companion-app)
- [Plugin settings](#plugin-settings-customvoiceddialogueini)
- [In-game settings menu](#in-game-settings-menu)
- [TTS providers](#tts-providers)
- [Emotion auto-tagging (Inworld)](#emotion-auto-tagging-inworld)
- [Accents](#accents)
- [How a line gets its voice](#how-a-line-gets-its-voice)
- [Mod compatibility](#mod-compatibility)
- [Game version support](#game-version-support)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [HTTP API](#http-api)
- [Credits & license](#credits--license)

---

## What works

**Voicing**
- Unvoiced player dialogue is spoken in the voice you choose.
- Unvoiced NPC dialogue, behind a toggle (`bEnableNPCLines`).
- **Custom player voice** (`bReplaceVoicedPlayerLines`) — TTS replaces the
  vanilla voice acting everywhere, giving your character a completely
  different voice with no other mods required.
- **Full NPC re-voicing** (`bReplaceVoicedNPCLines`) for total-conversion
  style setups.
- Automatic, deterministic voice assignment per NPC voice type, with a
  per-voice-type override grid. The same NPC keeps the same voice across
  sessions.
- **Accents** — 17 of them, applied through hand-written IPA pronunciation
  lexicons so the voice actually speaks with the accent, with a slider for
  how much the accent slips (see [Accents](#accents)).

**Timing and playback**
- Dialogue holds for the length of the generated line, then advances — no
  talking over yourself, no conversations racing ahead.
- Fresh audio plays through the game's own audio system where possible
  (3D positioning, the game's volume sliders, normal mixing and ducking),
  with an automatic fallback that always produces sound.
- Every generated line is loudness-levelled, so lines don't jump in volume
  between takes or providers.
- Subtitles stay up for the full line.

**Speed**
- The whole dialogue wheel is generated concurrently when the menu opens,
  so the option you pick usually speaks immediately.
- **Look-ahead prefetch**: rest the crosshair on an NPC for a couple of
  seconds and their dialogue starts generating before you even talk to them.
- Server-side audio cache keyed by content, so a line already spoken (or
  the same line on another NPC) is instant and costs nothing.

**Live configuration**
- Change your voice, provider, or provider settings in the app and the
  affected lines regenerate automatically — no restart, no cache clearing.
- Key plugin options are adjustable in-game via the F4SE Menu Framework.

**Not implemented yet**
- Lip-sync files. Player dialogue is first-person so there is no face to
  animate; NPCs get generic mouth movement.
- Combat barks / grunts are silent under a custom player voice (they are
  dispatched too fast to synthesize, and the vanilla voice is muted).

---

## Install

**Requirements**: Fallout 4 with [F4SE](https://f4se.silverlock.org/), and a
TTS service — either a cloud API key or a locally running TTS server.

1. Install the mod zip (`CustomVoicedDialogue-Mod-<version>.zip`) with MO2
   or Vortex. It is a standard FOMOD.
2. Unzip `CustomVoicedDialogue-App-<version>.zip` anywhere outside the game
   folder and run `CustomVoicedDialogue.exe`.
3. The setup wizard walks you through picking a TTS service, entering the
   API key (cloud) or endpoint (local), a mandatory listening test, and
   picking your character's voice.
4. Leave the app running and launch the game through F4SE.

Generated audio is written under `Data\Sound\Voice\...` from inside the game
process, so with Mod Organizer 2 it lands in **Overwrite**. That is normal;
you can move it into a mod folder of its own at any time.

---

## Using the companion app

The app must be running while you play. It sits in the tray and starts its
server automatically by default.

### Server tab
Start/stop the local server, change the port (match `iPort` in the plugin
INI if you do), watch the live log, and see three status lights:

| Light | Meaning |
|---|---|
| **Server** | The localhost server is listening |
| **Provider** | The last test or synthesis succeeded |
| **Game** | When the plugin last contacted the app |

### TTS Provider tab
Pick your service and configure it. The settings panel is generated from
each provider's own schema, so every option a service supports is exposed
with its default and a description.

- **Test connection** — cheapest possible call, reports latency and a
  specific reason on failure (bad key vs unreachable vs quota).
- **Test synthesis box** — type any text, synthesize with the current
  settings, hear it, and see the provider latency, decoded format,
  validator verdict, cache key, and output path. Use this to audition
  settings before committing.

### Player Voice tab
Pick your character's voice from the service's voice list, with instant
preview. **Changing this regenerates your character's existing lines
automatically** — you do not need to clear anything.

### NPC Voices tab
A grid of every NPC voice type the plugin has seen, showing the voice each
one was automatically assigned, with a ▶ preview per row and an override
dropdown. Assignment is a stable hash of the voice type, so it never
shuffles between sessions. Player voice types are excluded from this grid.

### Diagnostics tab
- **Synthesis history** — the last requests with text, voice, provider,
  timing, validator result, replay button, and open-containing-folder.
- **Simulate game request** — issues the exact HTTP sequence the plugin
  would (`POST /api/synth` → poll `/api/result`) so you can prove the whole
  pipeline works without launching the game.

### Updates tab
Checks GitHub releases and can install a newer version.

---

## Plugin settings (`CustomVoicedDialogue.ini`)

Located at `Data\F4SE\Plugins\CustomVoicedDialogue.ini`.

### `[General]`

| Setting | Default | What it does |
|---|---|---|
| `bEnablePlayerLines` | `1` | Voice the player's lines that have no recorded audio. |
| `bEnableNPCLines` | `0` | Voice NPC lines that have no recorded audio. |
| `bReplaceVoicedPlayerLines` | `0` | **Custom player voice.** Replace the player's vanilla voice acting with TTS everywhere. |
| `bReplaceVoicedNPCLines` | `0` | Same for NPCs (also needs `bEnableNPCLines=1`). Full re-voicing. |
| `bForceSubtitles` | `1` | Force subtitles for lines this plugin silenced or replaced. |
| `bVerboseLog` | `0` | Log every dialogue line observed (text, path, voice type). Turn on when reporting a problem. |
| `sPlayerVoiceTypes` | `PlayerVoiceMale01,PlayerVoiceFemale01` | Voice type editor IDs treated as "the player". |
| `iTtsVolumePercent` | `100` | Volume applied as audio is generated (0–150). Only affects newly generated lines. |
| `bDirectAudioPlayback` | `0` | Always play generated player lines outside the engine's voice-file system. |
| `bEngineAudioForFreshLines` | `1` | Play freshly generated lines through the game's audio system (3D, volume sliders, ducking) using the pre-indexed slot files. Falls back automatically. |
| `bShowGenerationProgress` | `0` | Small corner progress bar while a conversation's audio generates. Off by default; turn it on to watch generation happen. |

### `[Server]`

| Setting | Default | What it does |
|---|---|---|
| `sHost` | `127.0.0.1` | Where the companion app listens. |
| `iPort` | `47600` | Must match the app. |
| `iRequestTimeoutMs` | `5000` | Per-request timeout. |
| `iServerRetrySeconds` | `30` | How often to re-check an **unreachable** server. (A reachable server is polled every 5 s so voice changes apply promptly.) |

### `[Silence]`
While a line's audio is not ready, it plays a silence file sized from the
text so the subtitle stays readable.

| Setting | Default | What it does |
|---|---|---|
| `uWordsPerSecond` | `2` | Reading speed used to size the silence. |
| `uMinimumSeconds` | `1` | Shortest silence. |
| `uWideCharactersPerWord` | `3` | Multi-byte characters counted per word. |
| `uPendingLineWaitSeconds` | `3` | Extra seconds a picked line is held open while its audio finishes generating (0 = never wait). |

### `[Prefetch]`

| Setting | Default | What it does |
|---|---|---|
| `bEnablePrefetch` | `1` | Generate dialogue options ahead of time. |
| `iMenuPollMs` | `500` | Result poll interval while the dialogue menu is open. |
| `iIdlePollMs` | `3000` | Poll interval outside dialogue. |
| `iLookPrefetchMs` | `2000` | Crosshair dwell before an NPC's dialogue starts generating. **Raise this if you are generating lines for NPCs you never talk to** (cloud TTS costs money); `0` disables it. |

---

## In-game settings menu

With [F4SE Menu Framework](https://www.nexusmods.com/fallout4/mods/105090)
([source](https://github.com/DCCStudios/F4SEMenuFramework))
installed, a **CustomVoicedDialogue** section appears in its menu with live
toggles that write straight back to the INI:

- TTS volume (0–150 %)
- Voice unvoiced player lines / NPC lines
- Custom player voice (replace vanilla acting) / Re-voice NPCs
- Wait for voice (0–8 s)
- Direct audio playback
- Show generation progress bar
- Force subtitles
- Prefetch dialogue options
- Verbose log

---

## TTS providers

16 services are supported. Every option each service exposes is available
in the app, rendered from the provider's schema.

**Cloud (API key)**

| Provider | Notes |
|---|---|
| ElevenLabs | Stability, similarity, style, speed, speaker boost, v3 audio tags |
| OpenAI | `tts-1`, `gpt-4o-mini-tts` (supports an instructions/style field) |
| Azure Speech | SSML with prosody + `mstts:express-as` moods, rate/volume/contour |
| Inworld | Voice cloning, `inworld-tts-2`, **emotion auto-tagging** (see below) |
| Cartesia | `sonic-3`, speed presets |
| Deepgram Aura | Aura voice models |

**Local (free, runs on your PC)**

> **These are separate programs you install and run yourself.** This app does
> not bundle, download, install, or launch any of them — it only talks HTTP
> to a server that is already listening on your machine. Install the one you
> want from its own project page below, start it, and then pick it in the
> wizard.

| Provider | Default port | Get it from |
|---|---|---|
| xVASynth — **native Fallout 4 voice models** (`f4_*`) | 8008 | [xVASynth v3 (Nexus)](https://www.nexusmods.com/skyrimspecialedition/mods/44184) |
| Piper | 5000 | [OHF-Voice/piper1-gpl](https://github.com/OHF-Voice/piper1-gpl) |
| Kokoro | 8880 | [remsky/Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) |
| XTTS (voice cloning) | 8020 | [daswer123/xtts-api-server](https://github.com/daswer123/xtts-api-server) |
| PocketTTS | 8086 / 8024 | [CHIM (Nexus)](https://www.nexusmods.com/skyrimspecialedition/mods/126330) |
| Chatterbox | 8023 | [resemble-ai/chatterbox](https://github.com/resemble-ai/chatterbox) |
| OmniVoice | 8021 | [CHIM (Nexus)](https://www.nexusmods.com/skyrimspecialedition/mods/126330) |
| MeloTTS | 8084 | [myshell-ai/MeloTTS](https://github.com/myshell-ai/MeloTTS) |
| Mimic 3 | 59125 | [MycroftAI/mimic3](https://github.com/MycroftAI/mimic3) |
| KoboldCpp | 5001 | [LostRuins/koboldcpp](https://github.com/LostRuins/koboldcpp) |

Most of these are Python servers with their own dependency setup — follow
the instructions on the project's own page. Once the service is listening,
the wizard does the rest:

- It **probes all of these ports** when it opens and badges anything it
  finds as `● running — ready to use`, so a service you already have up is
  one click away.
- It **pre-fills the endpoint** with the default port above, so there is
  usually nothing to type.
- Each provider's settings panel links straight to its setup page.
- The wizard **will not let you continue until a real test synthesis
  succeeds**, so a service that isn't running fails there rather than
  silently in-game.

xVASynth is the standout for lore-friendly results: it ships actual
Fallout 4 voice models, and NPC voice types are matched to their `f4_*`
model automatically.

**Prefer zero setup?** The cloud providers need nothing installed — paste
an API key and you are done.

---

## Emotion auto-tagging (Inworld)

With `inworld-tts-2` and `auto_tag` enabled, each line is passed through a
small language model that adds performance direction before synthesis —
`[challenging, confident] Let's see what you've got.` — so delivery varies
with the content of the line instead of being read flat.

- Spoken words are never altered; only bracketed direction is added.
- Written actions in asterisks (`*sighs*`) become real non-verbal sounds
  instead of being read aloud.
- Each line gets a deterministic "take" number, so similar lines get
  different deliveries while any given line always tags identically (which
  keeps the audio cache stable).
- Pacing is biased toward a natural conversational tempo: speed words
  (slowly, drawn-out, hesitant, weary…) genuinely stretch delivery, so they
  are reserved for lines whose impact needs them.
- `tag_model` selects the tagging model. Cheap models are fine here.

---

## Accents

Any voice can be performed with an accent, set on the **Player Voice** tab
for your character and per voice type in the **NPC Voices** grid. The
default, **American (neutral)**, adds nothing at all and leaves lines
exactly as written.

Accents work through a **hand-written pronunciation lexicon**: for each
accent, the words that actually carry it are mapped to exact IPA
pronunciations, substituted into the line before synthesis — "Put the gun
down" becomes "Put the gun /duːn/" for Scottish — using Inworld's inline
custom-pronunciation support. The substitution happens in code,
deterministically, so the accent lands on every line regardless of which
tagging model is configured. (Simply naming an accent in a steering tag
tends to produce a caricature or nothing at all, and having the tagging
model respell lines proved unreliable — small models mangle spellings.)

| | |
|---|---|
| **American** | neutral (default), Southern, Deep South, Boston / New England, New York, Mid-Atlantic (1940s radio) |
| **British Isles** | Received Pronunciation (posh), Cockney, Northern England, Scottish, Welsh, Irish |
| **Other** | Spanish (Mexican), Australian, Russian, French, German, Italian |

Boston and Mid-Atlantic are the two that sit most naturally in the
Commonwealth — one is where the game is set, the other is the 1940s
newsreel diction the whole setting is built on.

### Accent imperfection

A slider (default 15 %) controlling how often a line is performed with the
accent easing off. Real speakers are not perfectly consistent, and a
flawless accent on every single line sounds synthetic. At 0 every line is
performed identically; higher values scatter lighter lines through a
conversation, where only a word or two carries the accent. Even at 100 the
accent wobbles rather than disappearing.

Which lines slip is derived from the line's own identity, so a given line
always performs the same way — the audio cache depends on that.

### Notes and limits

- Requires the **Inworld** provider with `inworld-tts-2` (the model with
  inline IPA support). It adds no latency and no extra cost — the
  substitution is a local string operation, and it works even with
  emotion auto-tagging turned off.
- The spoken words never change — only their pronunciation. The tagging
  model is never asked to respell anything; if it tries anyway, the real
  words are restored before the lexicon is applied.
- Each accent's lexicon covers its signature features grounded in real
  phonology: rhoticity (`worn` → `/wɔːn/` for RP, kept for Scottish),
  the marker vowel shifts (Cockney MOUTH `down` → `/daːn/`, Scottish
  `down` → `/duːn/`), and consonant changes (Cockney th-fronting `think`
  → `/fɪŋk/`, Russian `what` → `/vʌt/`). Words outside the lexicon are
  spoken normally — an accent only needs to show on the words that carry
  it.
- Emotion tagging still colours the delivery: with auto-tagging on, the
  steering instruction also carries the accent's rhythm and melody.
- Southern is deliberately mild (drawled `I'm`, pen/pin merger,
  dropped g's); **Deep South** adds the heavier shifts (flattened `right`,
  fronted `down`/`house`) — pick by how thick you want it.
- Changing an accent or the imperfection level regenerates the affected
  lines automatically.

---

## How a line gets its voice

```
Game (F4SE plugin)                          Companion app (localhost:47600)
──────────────────────────────────          ────────────────────────────────
dialogue line is built
  ├ has recorded audio? ── yes ─► play it unchanged (vanilla untouched)
  └ no (or replace mode)
      ├ substitute a length-matched silence   POST /api/synth
      │  so the subtitle/timing is right ───►   cache hit → 200 wav
      └ queue the line                          else 202 + background job:
                                                  provider → 48 kHz mono 16-bit
  poll GET /api/result ◄───────────────────       + 150 ms pad → loudness level
    write the wav into Data\Sound\Voice\...       → validate (decode, silence,
    play it, hold the line for its length           clipping, duration sanity)
```

The server's audio cache key is `SHA256(provider | voice | options | text)`,
so no settings change can serve stale audio. The plugin's job key is the
engine's own voice path, so prefetch and in-conversation requests for the
same line deduplicate naturally.

---

## Mod compatibility

**F4z Ro D'oh** — can stay installed. This plugin patches the same site,
positively identifies F4z's hooks, supersedes them cleanly, and logs that
it did. It includes the same silent-voice behaviour.

**Silent Protagonist** (OG version) — can stay installed, and is
recommended if you want combat barks silenced. This plugin keeps its player
mute (that is what stops the vanilla voice) and replaces only its two timer
patches, which otherwise skip the speak-wait and make dialogue race ahead.
If Silent Protagonist is absent, an equivalent mute is applied
automatically. The separate NG variant is detected and warned about, not
superseded.

**XDI (Extended Dialogue Interface)** — compatible. XDI hooks the dialogue
UI and option enumeration; this plugin hooks voice-file assignment and
playback timing.

**Player voice replacer mods** (e.g. Danse Player Voice) — these ship
recorded audio at the player's voice paths, which means the game has real
audio for those lines and you will hear *that* voice, not your TTS voice.
Disable them if you want a TTS player voice.

---

## Game version support

| Runtime | Status |
|---|---|
| OG 1.10.163 | Supported and live-tested. Addresses are resolved at runtime by in-memory signature scan (the on-disk exe is encrypted, so offline verification is impossible). Forced subtitles are unavailable on this runtime. |
| NG 1.10.984 | All patch sites verified offline (byte-checked, call targets proven). |
| AE 1.11.x (verified against 1.11.221) | Same as NG. |

Every patch site is opcode-guarded before a byte is written. On a mismatch —
a future game patch, an unexpected mod — the plugin disables that feature
and says so in the log instead of patching blind.

---

## Troubleshooting

The log is at
`Documents\My Games\Fallout4\F4SE\CustomVoicedDialogue.log`.
Set `bVerboseLog=1` for per-line detail.

| Symptom | Cause / fix |
|---|---|
| Lines are silent | Is the app running? Check the **Game** light in the app. The log says whether the server is reachable. |
| I hear the vanilla voice, not mine | A player-voice replacer mod is installed (real audio exists at those paths), or `bReplaceVoicedPlayerLines=0`. |
| First line of a conversation is silent | It was not prefetched in time. Raise `uPendingLineWaitSeconds`, or lower `iLookPrefetchMs` so NPCs generate before you talk to them. |
| Lines are quiet / vary in volume | Levelling only applies to newly generated audio. Delete the generated wavs to regenerate them. |
| Voice change didn't apply | Should apply within ~5 s. If not, check the app is running and the log shows the invalidation line. |
| Generating lines for NPCs I never talk to | Raise `iLookPrefetchMs` or set it to `0`. |
| Dialogue advances too fast / too slow | `uPendingLineWaitSeconds` controls the extra hold for a line still generating. |

---

## Building from source

Clone **with submodules** (CommonLibF4 is a submodule):

```
git clone --recurse-submodules https://github.com/DCCStudios/F4-CustomVoicedDialogue.git
```

Already cloned without them:

```
git submodule update --init --recursive
```

**Plugin** — MSVC 2022 (v143, C++23), xmake ≥ 3.0:

```
cd plugin
xmake f -p windows -a x64 -m releasedbg
xmake build          # stages DLL/PDB/INI into ../Compile/F4SE/Plugins
```

Set `COMMONLIBF4_PATH` to build against a CommonLibF4 checkout kept
elsewhere instead of the submodule.

**Server + app + tests** — .NET 8 SDK:

```
cd server
dotnet test          # 41 tests: provider request shapes, audio pipeline,
                     # levelling, validator, cache, voice mapping, e2e HTTP
dotnet build -c Release
```

**Everything, packaged for release**:

```
powershell -File packaging\build-package.ps1 -Version x.y.z
```

Builds the plugin, runs the GuardCheck gate against the game executables,
regenerates the silence and playback-slot assets, and writes both zips into
`packaging/dist/`. Publishing a GitHub release with a higher tag is what
the app's update check looks for.

### Offline verification tooling (`tools/`)

`CvdTools` (C#): `resolve` / `reverse` parse the F4SE Address Library V0
databases; `scan` / `bytes` / `calltarget` / `xref` operate on the game
executables; `guardcheck` verifies every hook site in
`tools/guardcheck.manifest.json` against the real exes — run it the day a
new game patch drops, before anything ships.

`SilenceGen/generate.ps1` generates the silence carriers (1–10 s) and the
24 playback slot files. No third-party assets are shipped.

---

## HTTP API

Loopback only, port 47600 by default. Useful if you want to drive the
server from something other than the plugin.

| Endpoint | Purpose |
|---|---|
| `GET /api/status` | Version, provider, readiness, queue depth, voice fingerprints |
| `POST /api/synth` | `{text, voicePath, voiceType, isPlayer, wait?}` → `200` wav, `202` queued, or `422` failed |
| `GET /api/result?voicePath=…&waitMs=…` | Poll (or long-poll) for a queued line |
| `POST /api/prefetch` | `{lines:[…]}` → queue a batch |
| `GET /api/voices` | The current provider's voice list |

---

## Credits & license

- **Plugin: GPL-3.0.** The interception design, struct layouts, and
  silence-fallback behaviour derive from
  [F4z Ro D'oh](https://github.com/shadeMe/F4z-Ro-D-oh) (GPL-3) by shadeMe.
- **Server, app, tools: MIT.** The provider architecture is a from-scratch
  C# port of concepts from
  [HerikaServer / CHIM](https://github.com/abeiro/HerikaServer) (MIT).
- [CommonLibF4](https://github.com/Dear-Modding-FO4/commonlibf4) by Ryan
  McKenzie, the Dear-Modding team, and contributors.
- Reference material: **Silent Protagonist** (player voice-play hook
  points), **AudioUtil** by crajjjj (loose-file playback and envelope
  lipsync patterns), **FPGunplayOverhaul** (`GetSoundHandleByFile` recipe),
  and **XDI** by reg2k (dialogue option enumeration).

See [LICENSE](LICENSE) for full terms.
