#pragma once

namespace CustomVoicedDialogue::Settings
{
	// Loads CustomVoicedDialogue.ini from beside the plugin DLL.  Values are
	// read once at plugin load; the companion app edits the INI only while
	// the game is not running.
	void Load();

	[[nodiscard]] bool EnablePlayerLines() noexcept;
	[[nodiscard]] bool EnableNPCLines() noexcept;
	[[nodiscard]] bool ReplaceVoicedPlayerLines() noexcept;
	[[nodiscard]] bool ReplaceVoicedNPCLines() noexcept;
	[[nodiscard]] bool ForceSubtitles() noexcept;
	// Send a short description of the scene (in combat, sneaking, a hostile
	// listener) with each line, so the delivery can suit the situation.
	// Costs nothing measurable in latency; turn it off to keep every line
	// performed purely on its own words.
	[[nodiscard]] bool SendSceneContext() noexcept;
	[[nodiscard]] bool VerboseLog() noexcept;
	[[nodiscard]] const std::vector<std::string>& PlayerVoiceTypes() noexcept;
	// Gain applied to TTS audio as it is written (percent, 0-150; 100 = as
	// generated).  Existing files keep the loudness they were written with.
	[[nodiscard]] std::uint32_t TtsVolumePercent() noexcept;
	// Always play generated player TTS through the generic audio channel
	// (the HerikaServer approach) instead of the engine's voice file layer.
	[[nodiscard]] bool DirectAudioPlayback() noexcept;
	// Show the on-screen progress bar while a conversation's lines generate.
	[[nodiscard]] bool ShowGenerationProgress() noexcept;
	// Play freshly generated lines through the game's own audio system (via
	// startup-indexed slot paths) instead of direct Win32 audio, so they get
	// 3D positioning, the game's volume sliders, and normal mixing.  Falls
	// back automatically whenever the engine cannot be shown to be playing
	// exactly the audio just written.
	[[nodiscard]] bool EngineAudioForFreshLines() noexcept;
	// How long the crosshair must rest on an NPC before their dialogue is
	// prefetched (0 disables look-ahead prefetch).
	[[nodiscard]] std::uint32_t LookPrefetchMs() noexcept;

	// Live setters used by the in-game settings menu.  Each updates the
	// running value and persists it to the INI immediately.
	void SetEnablePlayerLines(bool a_value);
	void SetEnableNPCLines(bool a_value);
	void SetReplaceVoicedPlayerLines(bool a_value);
	void SetReplaceVoicedNPCLines(bool a_value);
	void SetForceSubtitles(bool a_value);
	void SetVerboseLog(bool a_value);
	void SetEnablePrefetch(bool a_value);
	void SetTtsVolumePercent(std::uint32_t a_value);
	void SetDirectAudioPlayback(bool a_value);
	void SetShowGenerationProgress(bool a_value);
	void SetPendingLineWaitSeconds(std::uint32_t a_value);

	[[nodiscard]] const std::string& ServerHost() noexcept;
	[[nodiscard]] std::uint16_t ServerPort() noexcept;
	[[nodiscard]] std::uint32_t RequestTimeoutMs() noexcept;
	[[nodiscard]] std::uint32_t ServerRetrySeconds() noexcept;

	[[nodiscard]] std::uint32_t WordsPerSecond() noexcept;
	[[nodiscard]] std::uint32_t MinimumSilenceSeconds() noexcept;
	[[nodiscard]] std::uint32_t WideCharactersPerWord() noexcept;
	// Extra seconds of silence granted to a player line whose TTS is not
	// ready when it is picked, so the audio can arrive and still play
	// within the line (bounded wait-for-voice; 0 = never wait).
	[[nodiscard]] std::uint32_t PendingLineWaitSeconds() noexcept;

	[[nodiscard]] bool EnablePrefetch() noexcept;
	[[nodiscard]] std::uint32_t MenuPollMs() noexcept;
	[[nodiscard]] std::uint32_t IdlePollMs() noexcept;
}
