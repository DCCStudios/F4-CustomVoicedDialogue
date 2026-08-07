#pragma once

namespace CustomVoicedDialogue::TtsClient
{
	struct Response
	{
		// 0 means the request never reached the server (connection failure).
		std::uint32_t status{ 0 };
		std::vector<std::uint8_t> body;
	};

	// Synchronous localhost HTTP.  Only ever called from the SynthQueue
	// worker thread, never from the game thread.
	[[nodiscard]] Response PostJson(std::wstring_view a_path, const std::string& a_body);
	[[nodiscard]] Response Get(std::wstring_view a_path);
}
