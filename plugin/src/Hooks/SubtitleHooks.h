#pragma once

namespace CustomVoicedDialogue::Hooks::SubtitleHooks
{
	// Forces subtitles on for lines whose audio this plugin replaced, by
	// patching the four places SubtitleManager reads its cached
	// bDialogueSubtitles / bGeneralSubtitles booleans.  Each site installs
	// independently and only when its opcode guard verifies; the feature is
	// cosmetic, so partial installation just means fewer forced subtitles.
	void Install();
}
