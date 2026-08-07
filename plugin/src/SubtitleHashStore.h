#pragma once

namespace CustomVoicedDialogue::SubtitleHashStore
{
	// Tracks response texts whose audio this plugin replaced so the subtitle
	// hooks can force those subtitles on even when the user plays without
	// subtitles enabled.  The store is purged on a timer because the same
	// text can belong to both voiced and unvoiced INFO records.

	void Add(std::string_view a_responseText);
	[[nodiscard]] bool Contains(std::string_view a_responseText);

	// Starts the periodic purge thread (call once, at kPostLoad).
	void StartPurgeThread();
}
