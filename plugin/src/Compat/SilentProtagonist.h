#pragma once

namespace CustomVoicedDialogue::Compat::SilentProtagonist
{
	// Captures the original bytes at Silent Protagonist's OG patch sites.
	// Must run at plugin load, before Silent Protagonist's own load-time
	// patches (F4SE loads plugins alphabetically, C before S).
	void Snapshot();

	// Takes control of the OG player-voice line timing.  Silent
	// Protagonist's getter patch (which swaps the player's voice file for a
	// nonexistent placeholder, so native playback never happens) is kept
	// as-is — or byte-identically applied when it is absent — and only its
	// two timer patches (which skip the speak wait and rush dialogue) are
	// replaced with thunks of the same proven shape that hold each player
	// line for this plugin's known duration.  TTS itself plays through the
	// generic audio channel.  Call at kPostLoad, before the dialogue hooks
	// install.
	void Supersede();

	// Records the carrier substituted for a player line; its "_<N>.wav"
	// suffix is the seconds the timer thunks hold the line for.  Both
	// paths are engine format ("Data\\Sound\\Voice\\...").
	void NotePlayerLineCarrier(std::string_view a_enginePath, std::string_view a_carrierPath);
}
