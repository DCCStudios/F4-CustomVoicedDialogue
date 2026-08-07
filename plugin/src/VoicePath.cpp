#include "PCH.h"

#include "VoicePath.h"

#include "RE/Dialogue.h"
#include "Settings.h"
#include "ShadowPlayback.h"
#include "VoiceManifest.h"

namespace CustomVoicedDialogue::VoicePath
{
	namespace
	{
		[[nodiscard]] bool EqualsNoCase(std::string_view a_lhs, std::string_view a_rhs) noexcept
		{
			if (a_lhs.size() != a_rhs.size()) {
				return false;
			}
			for (std::size_t i = 0; i < a_lhs.size(); ++i) {
				if (std::tolower(static_cast<unsigned char>(a_lhs[i])) != std::tolower(static_cast<unsigned char>(a_rhs[i]))) {
					return false;
				}
			}
			return true;
		}
	}

	std::string_view ExtractVoiceType(std::string_view a_path) noexcept
	{
		const auto lastSep = a_path.find_last_of("\\/");
		if (lastSep == std::string_view::npos || lastSep == 0) {
			return {};
		}
		const auto prevSep = a_path.find_last_of("\\/", lastSep - 1);
		if (prevSep == std::string_view::npos) {
			return {};
		}
		return a_path.substr(prevSep + 1, lastSep - prevSep - 1);
	}

	std::string_view StripDataPrefix(std::string_view a_path) noexcept
	{
		if (a_path.size() > 5 && EqualsNoCase(a_path.substr(0, 5), "Data\\")) {
			return a_path.substr(5);
		}
		return a_path;
	}

	bool IsPlayerVoiceType(std::string_view a_voiceType)
	{
		for (const auto& playerVoiceType : Settings::PlayerVoiceTypes()) {
			if (EqualsNoCase(a_voiceType, playerVoiceType)) {
				return true;
			}
		}
		return false;
	}

	namespace
	{
		// Probes a data-relative path as a loose file through Win32 — the mod
		// manager's virtual file system applies to that probe, and the
		// engine's resource layer cannot see files written this session.
		[[nodiscard]] bool LooseWavExists(const std::string_view a_relative)
		{
			static const std::filesystem::path gameRoot = [] {
				std::array<wchar_t, 32768> buffer{};
				const auto length = ::GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
				return length > 0 && length < buffer.size()
					? std::filesystem::path{ std::wstring_view{ buffer.data(), length } }.parent_path()
					: std::filesystem::path{};
			}();
			if (!gameRoot.empty()) {
				return ::GetFileAttributesW((gameRoot / "Data" / a_relative).c_str()) != INVALID_FILE_ATTRIBUTES;
			}
			return REX_RE::BSIStream::ResourceExists(std::string{ a_relative }.c_str());
		}
	}

	bool GeneratedWavExists(const char* a_enginePath)
	{
		const std::string_view path{ a_enginePath };
		if (path.size() < 17) {
			return true;
		}
		const auto relative = StripDataPrefix(path);

		// Wavs written this session are ours by definition.
		float duration = 0.0f;
		if (ShadowPlayback::IsSessionFresh(relative, duration)) {
			return true;
		}

		// A loose wav only counts as generated TTS when the manifest tracks
		// it.  Untracked files — pre-manifest sessions, other sources — may
		// carry a stale (or wrong) voice, and a voice change cannot
		// invalidate what was never tracked; treating them as absent makes
		// the line regenerate once and become tracked.
		return VoiceManifest::IsTracked(relative) && LooseWavExists(relative);
	}

	bool VoiceAssetExists(const char* a_enginePath)
	{
		const std::string_view path{ a_enginePath };

		// Paths shorter than "Data\Sound\Voice\" cannot be real voice paths;
		// treat them as valid so the engine's own handling is left alone.
		if (path.size() < 17) {
			return true;
		}

		// Any loose wav counts here — tracked or not — including ones the
		// resource layer cannot see yet because they were written this
		// session.  (Unlike GeneratedWavExists, this asks "is the line
		// voiced at all", not "is this audio ours".)
		const auto relative = StripDataPrefix(path);
		float duration = 0.0f;
		if (ShadowPlayback::IsSessionFresh(relative, duration) || LooseWavExists(relative)) {
			return true;
		}
		if (relative.size() < 4) {
			return true;
		}

		// Swap the extension for each container the resource layer accepts.
		static constexpr std::array<std::string_view, 3> kExtensions{ "wav", "fuz", "xwm" };
		std::string candidate{ relative };
		for (const auto extension : kExtensions) {
			candidate.replace(candidate.size() - 3, 3, extension);
			if (REX_RE::BSIStream::ResourceExists(candidate.c_str())) {
				return true;
			}
		}
		return false;
	}
}
