#include "PCH.h"

#include "SilenceFallback.h"

#include "Settings.h"

namespace CustomVoicedDialogue::SilenceFallback
{
	std::uint32_t EstimateSeconds(
		const std::string_view a_responseText,
		const std::uint32_t a_wordsPerSecond,
		const std::uint32_t a_minimumSeconds,
		const std::uint32_t a_wideCharactersPerWord) noexcept
	{
		constexpr std::uint32_t kMaximumSeconds = 10;

		std::uint32_t wordCount = 0;
		std::uint32_t wideCharCount = 0;
		std::uint32_t continuationBytes = 0;

		// Count words by spaces; count UTF-8 multi-byte sequences separately
		// because ideographic scripts have no spaces between words.
		for (const char character : a_responseText) {
			if (continuationBytes > 0) {
				--continuationBytes;
				continue;
			}
			const auto byte = static_cast<unsigned char>(character);
			if ((byte & 0xE0) == 0xE0) {
				continuationBytes = (byte & 0x10) != 0 ? 3 : 2;
				++wideCharCount;
			} else if (character == ' ') {
				++wordCount;
			}
		}

		wordCount += wideCharCount / std::max<std::uint32_t>(a_wideCharactersPerWord, 1);
		const auto seconds = wordCount / std::max<std::uint32_t>(a_wordsPerSecond, 1) + 1;
		return std::clamp(seconds, std::max<std::uint32_t>(a_minimumSeconds, 1), kMaximumSeconds);
	}

	std::string Pick(const std::string_view a_responseText)
	{
		const auto seconds = EstimateSeconds(
			a_responseText,
			Settings::WordsPerSecond(),
			Settings::MinimumSilenceSeconds(),
			Settings::WideCharactersPerWord());
		return std::format("Data\\Sound\\Voice\\CustomVoicedDialogue\\Silence_{}.wav", seconds);
	}

	std::string PickForSeconds(const float a_seconds)
	{
		const auto seconds = std::clamp<std::uint32_t>(
			static_cast<std::uint32_t>(std::lround(a_seconds)), 1, 10);
		return std::format("Data\\Sound\\Voice\\CustomVoicedDialogue\\Silence_{}.wav", seconds);
	}
}
