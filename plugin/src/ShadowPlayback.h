#pragma once

namespace CustomVoicedDialogue::ShadowPlayback
{
	// The engine's resource layer indexes loose files at launch, so a wav
	// written during the session is invisible to ALL engine playback —
	// including BSAudioManager::GetSoundHandleByFile, which then serves the
	// ARCHIVED asset at the same path (the vanilla recording!) or nothing.
	// Session-fresh audio therefore plays through Win32 (winmm/MCI) with the
	// absolute on-disk path — the mod manager's VFS applies and an archive
	// fallback is impossible — while the dialogue line itself carries a
	// duration-matched silence file for engine timing.  Files written in
	// earlier sessions are launch-indexed and may use the engine channel.

	// Creates any missing playback slot files.  The engine indexes loose
	// files at startup, so these exist purely to reserve indexed paths that
	// fresh audio can be copied into and played through the game's own audio
	// system (see Play).  Call once at startup; newly created slots only
	// become usable from the next launch.
	void EnsureStreamSlots();

	// Records a wav written during this session (data-relative path,
	// e.g. "Sound\\Voice\\Fallout4.esm\\...\\0001F604_1.wav").
	void NoteSessionWrite(std::string_view a_voicePath, float a_durationSeconds);

	// True when the path was written this session; yields its duration.
	[[nodiscard]] bool IsSessionFresh(std::string_view a_voicePath, float& a_durationSeconds);

	// Drops a session-write record (the file was invalidated and deleted).
	void Forget(std::string_view a_voicePath);

	// Plays the wav at the data-relative path through the generic audio
	// channel, following the player's 3D position.
	void Play(std::string_view a_voicePath);

	// Reads the audio duration of a wav on disk (data-relative path, read
	// through the game root so mod-manager virtual file systems apply).
	// Returns 0 when the file cannot be read or is not plain PCM.
	[[nodiscard]] float WavDurationOnDisk(std::string_view a_voicePath);
}
