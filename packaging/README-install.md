# CustomVoicedDialogue — install guide

Voices unvoiced Fallout 4 dialogue with text-to-speech.

## What you need

- Fallout 4 (any of: OG 1.10.163, next-gen 1.10.984, or 1.11.x)
- [F4SE](https://f4se.silverlock.org/) matching your game version
- The **Address Library for F4SE Plugins** matching your game version
  (all versions, including OG 1.10.163)
- One TTS service:
  - **Cloud** (API key, best quality): ElevenLabs, OpenAI, Azure,
    Inworld, Cartesia, Deepgram
  - **Local** (free): xVASynth (has real Fallout 4 NPC voices!), Piper,
    Kokoro, XTTS, PocketTTS, Chatterbox, OmniVoice, MeloTTS, Mimic3,
    KoboldCpp

## Steps

1. **Mod**: install `CustomVoicedDialogue-Mod-<version>.zip` with
   Mod Organizer 2 or Vortex like any other mod.
2. **App**: unzip `CustomVoicedDialogue-App-<version>.zip` to any folder
   (e.g. `C:\Games\CustomVoicedDialogue`) and run
   `CustomVoicedDialogue.exe`.
3. Follow the setup wizard: pick your TTS service, fill in its key or
   endpoint, press **Test synthesis** (you must hear a spoken line to
   continue), pick your character's voice, done.
4. Keep the app running (it can start minimized) and launch the game
   through F4SE.

That's it — dialogue options you pick that have no recorded voice now
speak. The very first time a line is encountered it may play silently
while its audio is generated; it speaks from then on.

## Options

- **NPC lines**: off by default. Set `bEnableNPCLines=1` in
  `Data\F4SE\Plugins\CustomVoicedDialogue.ini` to voice unvoiced NPC
  dialogue too. Voices are auto-assigned per voice type; pin specific
  voices in the app's *NPC Voices* tab.
- **Custom player voice**: set `bReplaceVoicedPlayerLines=1` to have TTS
  replace even the vanilla-voiced player lines — your character speaks
  with the voice you picked in the app everywhere. You do NOT need
  Silent Protagonist for this; on OG it may stay installed (it is
  detected and superseded automatically), on NG remove it — it would
  mute the TTS.
- **Port**: both sides default to `47600`. If you change it in the app,
  change `iPort` in the INI to match.
- Silence timing, subtitles forcing, and logging are also in the INI.

## Compatibility

- **F4z Ro D'oh**: can stay installed on every game version —
  CustomVoicedDialogue detects it, supersedes its hooks, and includes its
  silent-voice behaviour (it becomes redundant but harmless). In the
  unlikely case the plugin cannot do this safely, it stays inactive and
  the log asks you to remove F4z Ro D'oh rather than risking a guess.
- Vanilla-voiced dialogue (including the voiced player) is never touched;
  the mod only acts on lines that have no audio at all.

## Troubleshooting

Open the app — the three lights at the top tell you what's wrong
(server not running / provider not working / game not connected). The
*Diagnostics* tab's **Simulate game request** proves the whole pipeline
without launching the game. The plugin logs to
`Documents\My Games\Fallout4\F4SE\CustomVoicedDialogue.log`.
