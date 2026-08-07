#pragma once

namespace CustomVoicedDialogue::Hooks::VoicePathHook
{
	// Replaces the "call BSFixedString::Set" inside DialogueResponse::ctor
	// with a handler that leaves voiced lines untouched, redirects unvoiced
	// lines to a silence file, and queues TTS synthesis for them.
	// Installs nothing (and says so in the log) when the site is unresolved
	// for the running executable or its opcode guard fails.
	void Install();

	[[nodiscard]] bool Installed() noexcept;
}
