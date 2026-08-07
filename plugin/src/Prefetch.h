#pragma once

namespace CustomVoicedDialogue::Prefetch
{
	// Registers the DialogueMenu open/close sink (call once, at
	// kGameDataReady).  While the menu is open, the visible player dialogue
	// options are enumerated and queued for synthesis so their audio is on
	// disk before the player picks one.
	void Register();
}
