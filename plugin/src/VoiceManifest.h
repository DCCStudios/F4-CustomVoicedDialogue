#pragma once

namespace CustomVoicedDialogue::VoiceManifest
{
	// Persistent record of every voice file this plugin generated, plus the
	// server's voice fingerprints at the time.  When the companion app's
	// player (or NPC) voice configuration changes, the fingerprint from
	// /api/status changes, and the stale files in the matching category are
	// deleted so the lines regenerate with the new voice — no manual cache
	// clearing.  Files another mod or the game shipped are never touched:
	// only paths this plugin wrote are ever listed.

	// Loads the manifest (Data\F4SE\Plugins beside the generated audio via
	// the mod manager's virtual file system) and retries any deletions a
	// previous session could not finish.  Called once from SynthQueue::Start.
	void Init(const std::filesystem::path& a_gameRoot);

	// Records a successfully written voice file (data-relative path).
	void RecordWrite(std::string_view a_voicePath, bool a_isPlayer);

	// True when the path (data-relative, case-insensitive) is a live entry —
	// a file this plugin wrote that has not been invalidated.  Replace-mode
	// playback must only trust tracked files: an untracked loose wav (from a
	// pre-manifest session or another source) may carry a stale voice.
	[[nodiscard]] bool IsTracked(std::string_view a_voicePath);

	// Compares the server's fingerprints against the stored ones; on a
	// mismatch the matching category's files are deleted and the new
	// fingerprint adopted.  Empty fingerprints (no provider configured) are
	// ignored.  Called from the synth worker thread only.
	void ApplyServerFingerprints(std::string_view a_player, std::string_view a_npc);

	// Deletes one generated voice file so the line regenerates on its next
	// encounter.  Used when the companion app produces a new take of a line
	// the game already has audio for.  Only tracked files are touched, so a
	// stray path can never delete something this plugin did not write.
	void Invalidate(std::string_view a_voicePath);
}
