#pragma once

namespace CustomVoicedDialogue::VoicePath
{
	// Engine voice paths look like:
	//   Data\Sound\Voice\<PluginFile.esp>\<VoiceTypeEDID>\<basename>.wav
	// The engine always builds the extension as .wav; the resource layer
	// resolves .wav/.fuz/.xwm from loose files and BA2 archives.

	// Returns the voice type segment (second-to-last path component), or an
	// empty view when the path does not look like a voice path.
	[[nodiscard]] std::string_view ExtractVoiceType(std::string_view a_path) noexcept;

	// Strips a leading "Data\" (any case) so the remainder is the
	// Data-relative resource path used for existence checks, server keys,
	// and file writes.
	[[nodiscard]] std::string_view StripDataPrefix(std::string_view a_path) noexcept;

	// True when the voice type matches one of the configured player voice
	// types (case-insensitive).
	[[nodiscard]] bool IsPlayerVoiceType(std::string_view a_voiceType);

	// True when a voice asset (wav/fuz/xwm) exists for the given engine-built
	// path, checked through the game's own resource layer.
	[[nodiscard]] bool VoiceAssetExists(const char* a_enginePath);

	// True when a .wav specifically exists for the path.  Used by the
	// replace-voiced-lines mode: generated TTS audio is always a loose
	// .wav, while shipped voice acting is .fuz/.xwm, so this distinguishes
	// "our audio is ready" from "the line has vanilla audio".
	[[nodiscard]] bool GeneratedWavExists(const char* a_enginePath);
}
