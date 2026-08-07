#pragma once

namespace CustomVoicedDialogue::Menu
{
	// Registers the in-game settings page with the F4SE Menu Framework.
	// Call at kPostLoad or later (the framework DLL loads after this one);
	// silently does nothing when the framework is not installed.
	void Register();
}
