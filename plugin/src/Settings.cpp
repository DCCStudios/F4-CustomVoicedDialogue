#include "PCH.h"

#include "Settings.h"

namespace CustomVoicedDialogue::Settings
{
	namespace
	{
		constexpr wchar_t kIniName[]{ L"CustomVoicedDialogue.ini" };

		bool g_enablePlayerLines{ true };
		bool g_enableNPCLines{ false };
		bool g_replaceVoicedPlayerLines{ false };
		bool g_replaceVoicedNPCLines{ false };
		bool g_forceSubtitles{ true };
		bool g_verboseLog{ false };
		std::vector<std::string> g_playerVoiceTypes{ "PlayerVoiceMale01", "PlayerVoiceFemale01" };

		std::string g_serverHost{ "127.0.0.1" };
		std::uint16_t g_serverPort{ 47600 };
		std::uint32_t g_requestTimeoutMs{ 5000 };
		std::uint32_t g_serverRetrySeconds{ 30 };

		std::uint32_t g_wordsPerSecond{ 2 };
		std::uint32_t g_minimumSilenceSeconds{ 1 };
		std::uint32_t g_wideCharactersPerWord{ 3 };
		std::uint32_t g_pendingLineWaitSeconds{ 3 };

		bool g_enablePrefetch{ true };
		std::uint32_t g_menuPollMs{ 500 };
		std::uint32_t g_idlePollMs{ 3000 };
		std::uint32_t g_lookPrefetchMs{ 2000 };

		std::uint32_t g_ttsVolumePercent{ 100 };
		bool g_directAudioPlayback{ false };
		bool g_showGenerationProgress{ true };
		bool g_engineAudioForFreshLines{ true };

		// Remembered so the in-game menu can persist changes.
		std::filesystem::path g_iniPath;

		// MO2-safe INI resolution: locate the module this code lives in and
		// read the INI beside it, never a CWD-relative path.
		[[nodiscard]] std::filesystem::path ResolveIniPath()
		{
			HMODULE module{};
			const auto address = reinterpret_cast<LPCWSTR>(&ResolveIniPath);
			if (!::GetModuleHandleExW(
					GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
					address,
					&module)) {
				logger::warn("Could not resolve the plugin module while locating the settings INI");
				return {};
			}

			std::array<wchar_t, 32768> buffer{};
			const auto length = ::GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
			if (length == 0 || length >= buffer.size()) {
				logger::warn("Could not resolve the plugin path while locating the settings INI");
				return {};
			}

			return std::filesystem::path{ std::wstring_view{ buffer.data(), length } }.replace_filename(kIniName);
		}

		[[nodiscard]] bool ReadBool(const std::filesystem::path& a_ini, const wchar_t* a_section, const wchar_t* a_key, const bool a_default)
		{
			std::array<wchar_t, 32> value{};
			const auto fallback = a_default ? L"1" : L"0";
			::GetPrivateProfileStringW(a_section, a_key, fallback, value.data(), static_cast<DWORD>(value.size()), a_ini.c_str());
			return _wcsicmp(value.data(), L"true") == 0 || _wcsicmp(value.data(), L"yes") == 0 || std::wcstol(value.data(), nullptr, 10) != 0;
		}

		[[nodiscard]] std::uint32_t ReadUInt(const std::filesystem::path& a_ini, const wchar_t* a_section, const wchar_t* a_key, const std::uint32_t a_default)
		{
			const auto value = ::GetPrivateProfileIntW(a_section, a_key, static_cast<INT>(a_default), a_ini.c_str());
			return value < 0 ? a_default : static_cast<std::uint32_t>(value);
		}

		[[nodiscard]] std::string ReadString(const std::filesystem::path& a_ini, const wchar_t* a_section, const wchar_t* a_key, const char* a_default)
		{
			std::array<wchar_t, 512> value{};
			std::wstring fallback;
			for (const char* c = a_default; *c; ++c) {
				fallback.push_back(static_cast<wchar_t>(*c));
			}
			::GetPrivateProfileStringW(a_section, a_key, fallback.c_str(), value.data(), static_cast<DWORD>(value.size()), a_ini.c_str());

			// Settings values are plain ASCII (host names, editor IDs).
			std::string result;
			for (const wchar_t* c = value.data(); *c; ++c) {
				result.push_back(static_cast<char>(*c));
			}
			return result;
		}

		[[nodiscard]] std::vector<std::string> SplitCommaList(const std::string& a_value)
		{
			std::vector<std::string> items;
			std::size_t start = 0;
			while (start <= a_value.size()) {
				auto end = a_value.find(',', start);
				if (end == std::string::npos) {
					end = a_value.size();
				}
				auto item = a_value.substr(start, end - start);
				const auto first = item.find_first_not_of(" \t");
				const auto last = item.find_last_not_of(" \t");
				if (first != std::string::npos) {
					items.push_back(item.substr(first, last - first + 1));
				}
				start = end + 1;
			}
			return items;
		}
	}

	void Load()
	{
		const auto ini = ResolveIniPath();
		g_iniPath = ini;
		if (ini.empty() || !std::filesystem::exists(ini)) {
			logger::warn("CustomVoicedDialogue.ini was not found beside the plugin; using built-in defaults");
			return;
		}

		g_enablePlayerLines = ReadBool(ini, L"General", L"bEnablePlayerLines", g_enablePlayerLines);
		g_enableNPCLines = ReadBool(ini, L"General", L"bEnableNPCLines", g_enableNPCLines);
		g_replaceVoicedPlayerLines = ReadBool(ini, L"General", L"bReplaceVoicedPlayerLines", g_replaceVoicedPlayerLines);
		g_replaceVoicedNPCLines = ReadBool(ini, L"General", L"bReplaceVoicedNPCLines", g_replaceVoicedNPCLines);
		g_forceSubtitles = ReadBool(ini, L"General", L"bForceSubtitles", g_forceSubtitles);
		g_verboseLog = ReadBool(ini, L"General", L"bVerboseLog", g_verboseLog);
		if (const auto voiceTypes = SplitCommaList(ReadString(ini, L"General", L"sPlayerVoiceTypes", "PlayerVoiceMale01,PlayerVoiceFemale01")); !voiceTypes.empty()) {
			g_playerVoiceTypes = voiceTypes;
		}

		g_serverHost = ReadString(ini, L"Server", L"sHost", g_serverHost.c_str());
		g_serverPort = static_cast<std::uint16_t>(std::clamp<std::uint32_t>(ReadUInt(ini, L"Server", L"iPort", g_serverPort), 1, 65535));
		g_requestTimeoutMs = std::clamp<std::uint32_t>(ReadUInt(ini, L"Server", L"iRequestTimeoutMs", g_requestTimeoutMs), 250, 60000);
		g_serverRetrySeconds = std::clamp<std::uint32_t>(ReadUInt(ini, L"Server", L"iServerRetrySeconds", g_serverRetrySeconds), 5, 3600);

		g_wordsPerSecond = std::clamp<std::uint32_t>(ReadUInt(ini, L"Silence", L"uWordsPerSecond", g_wordsPerSecond), 1, 10);
		g_minimumSilenceSeconds = std::clamp<std::uint32_t>(ReadUInt(ini, L"Silence", L"uMinimumSeconds", g_minimumSilenceSeconds), 1, 10);
		g_wideCharactersPerWord = std::clamp<std::uint32_t>(ReadUInt(ini, L"Silence", L"uWideCharactersPerWord", g_wideCharactersPerWord), 1, 10);
		g_pendingLineWaitSeconds = std::clamp<std::uint32_t>(ReadUInt(ini, L"Silence", L"uPendingLineWaitSeconds", g_pendingLineWaitSeconds), 0, 8);

		g_enablePrefetch = ReadBool(ini, L"Prefetch", L"bEnablePrefetch", g_enablePrefetch);
		g_menuPollMs = std::clamp<std::uint32_t>(ReadUInt(ini, L"Prefetch", L"iMenuPollMs", g_menuPollMs), 100, 10000);
		g_idlePollMs = std::clamp<std::uint32_t>(ReadUInt(ini, L"Prefetch", L"iIdlePollMs", g_idlePollMs), 500, 60000);
		// 0 disables look-ahead prefetch; otherwise how long the crosshair
		// must rest on an NPC before their dialogue starts generating.
		g_lookPrefetchMs = std::clamp<std::uint32_t>(ReadUInt(ini, L"Prefetch", L"iLookPrefetchMs", g_lookPrefetchMs), 0, 30000);

		g_ttsVolumePercent = std::clamp<std::uint32_t>(ReadUInt(ini, L"General", L"iTtsVolumePercent", g_ttsVolumePercent), 0, 150);
		g_directAudioPlayback = ReadBool(ini, L"General", L"bDirectAudioPlayback", g_directAudioPlayback);
		g_showGenerationProgress = ReadBool(ini, L"General", L"bShowGenerationProgress", g_showGenerationProgress);
		g_engineAudioForFreshLines = ReadBool(ini, L"General", L"bEngineAudioForFreshLines", g_engineAudioForFreshLines);

		logger::info(
			"Loaded settings from '{}': playerLines={}, npcLines={}, forceSubtitles={}, verbose={}, server={}:{}, prefetch={}",
			ini.string(),
			g_enablePlayerLines,
			g_enableNPCLines,
			g_forceSubtitles,
			g_verboseLog,
			g_serverHost,
			g_serverPort,
			g_enablePrefetch);
	}

	namespace
	{
		void WriteBoolSetting(const wchar_t* a_section, const wchar_t* a_key, const bool a_value)
		{
			if (!g_iniPath.empty()) {
				::WritePrivateProfileStringW(a_section, a_key, a_value ? L"1" : L"0", g_iniPath.c_str());
			}
		}

		void WriteUIntSetting(const wchar_t* a_section, const wchar_t* a_key, const std::uint32_t a_value)
		{
			if (!g_iniPath.empty()) {
				::WritePrivateProfileStringW(a_section, a_key, std::to_wstring(a_value).c_str(), g_iniPath.c_str());
			}
		}
	}

	void SetEnablePlayerLines(bool a_value)
	{
		g_enablePlayerLines = a_value;
		WriteBoolSetting(L"General", L"bEnablePlayerLines", a_value);
	}

	void SetEnableNPCLines(bool a_value)
	{
		g_enableNPCLines = a_value;
		WriteBoolSetting(L"General", L"bEnableNPCLines", a_value);
	}

	void SetReplaceVoicedPlayerLines(bool a_value)
	{
		g_replaceVoicedPlayerLines = a_value;
		WriteBoolSetting(L"General", L"bReplaceVoicedPlayerLines", a_value);
	}

	void SetReplaceVoicedNPCLines(bool a_value)
	{
		g_replaceVoicedNPCLines = a_value;
		WriteBoolSetting(L"General", L"bReplaceVoicedNPCLines", a_value);
	}

	void SetForceSubtitles(bool a_value)
	{
		g_forceSubtitles = a_value;
		WriteBoolSetting(L"General", L"bForceSubtitles", a_value);
	}

	void SetVerboseLog(bool a_value)
	{
		g_verboseLog = a_value;
		WriteBoolSetting(L"General", L"bVerboseLog", a_value);
	}

	void SetEnablePrefetch(bool a_value)
	{
		g_enablePrefetch = a_value;
		WriteBoolSetting(L"Prefetch", L"bEnablePrefetch", a_value);
	}

	void SetTtsVolumePercent(std::uint32_t a_value)
	{
		g_ttsVolumePercent = std::clamp<std::uint32_t>(a_value, 0, 150);
		WriteUIntSetting(L"General", L"iTtsVolumePercent", g_ttsVolumePercent);
	}

	void SetDirectAudioPlayback(bool a_value)
	{
		g_directAudioPlayback = a_value;
		WriteBoolSetting(L"General", L"bDirectAudioPlayback", a_value);
	}

	void SetShowGenerationProgress(bool a_value)
	{
		g_showGenerationProgress = a_value;
		WriteBoolSetting(L"General", L"bShowGenerationProgress", a_value);
	}

	void SetPendingLineWaitSeconds(std::uint32_t a_value)
	{
		g_pendingLineWaitSeconds = std::clamp<std::uint32_t>(a_value, 0, 8);
		WriteUIntSetting(L"Silence", L"uPendingLineWaitSeconds", g_pendingLineWaitSeconds);
	}

	std::uint32_t TtsVolumePercent() noexcept { return g_ttsVolumePercent; }
	bool DirectAudioPlayback() noexcept { return g_directAudioPlayback; }
	bool ShowGenerationProgress() noexcept { return g_showGenerationProgress; }
	bool EngineAudioForFreshLines() noexcept { return g_engineAudioForFreshLines; }

	bool EnablePlayerLines() noexcept { return g_enablePlayerLines; }
	bool EnableNPCLines() noexcept { return g_enableNPCLines; }
	bool ReplaceVoicedPlayerLines() noexcept { return g_replaceVoicedPlayerLines; }
	bool ReplaceVoicedNPCLines() noexcept { return g_replaceVoicedNPCLines; }
	bool ForceSubtitles() noexcept { return g_forceSubtitles; }
	bool VerboseLog() noexcept { return g_verboseLog; }
	const std::vector<std::string>& PlayerVoiceTypes() noexcept { return g_playerVoiceTypes; }

	const std::string& ServerHost() noexcept { return g_serverHost; }
	std::uint16_t ServerPort() noexcept { return g_serverPort; }
	std::uint32_t RequestTimeoutMs() noexcept { return g_requestTimeoutMs; }
	std::uint32_t ServerRetrySeconds() noexcept { return g_serverRetrySeconds; }

	std::uint32_t WordsPerSecond() noexcept { return g_wordsPerSecond; }
	std::uint32_t MinimumSilenceSeconds() noexcept { return g_minimumSilenceSeconds; }
	std::uint32_t WideCharactersPerWord() noexcept { return g_wideCharactersPerWord; }
	std::uint32_t PendingLineWaitSeconds() noexcept { return g_pendingLineWaitSeconds; }

	bool EnablePrefetch() noexcept { return g_enablePrefetch; }
	std::uint32_t MenuPollMs() noexcept { return g_menuPollMs; }
	std::uint32_t LookPrefetchMs() noexcept { return g_lookPrefetchMs; }
	std::uint32_t IdlePollMs() noexcept { return g_idlePollMs; }
}
