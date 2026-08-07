#include "PCH.h"

#include "VoiceManifest.h"

#include "ShadowPlayback.h"

#include <fstream>
#include <map>

namespace CustomVoicedDialogue::VoiceManifest
{
	namespace
	{
		// Manifest line flags: P = player line, N = NPC line, D = stale file
		// pending deletion (kept until the delete actually succeeds so a
		// locked file survives into the next session's retry).
		enum class Kind : char
		{
			kPlayer = 'P',
			kNpc = 'N',
			kDoomed = 'D',
		};

		constexpr std::string_view kHeader = "CVDMANIFEST 1";

		std::mutex g_lock;
		std::filesystem::path g_gameRoot;
		std::filesystem::path g_manifestPath;
		std::string g_playerFingerprint;
		std::string g_npcFingerprint;
		// Data-relative voice path -> flag.  A rewrite of the same path
		// (re-synthesis) just keeps its entry.
		std::map<std::string, Kind> g_entries;

		void Load()
		{
			std::ifstream stream{ g_manifestPath };
			if (!stream) {
				return;
			}
			std::string line;
			if (!std::getline(stream, line) || !line.starts_with(kHeader)) {
				return;
			}
			while (std::getline(stream, line)) {
				if (line.empty()) {
					continue;
				}
				if (line.starts_with("player=")) {
					g_playerFingerprint = line.substr(7);
				} else if (line.starts_with("npc=")) {
					g_npcFingerprint = line.substr(4);
				} else if (line.size() > 2 && line[1] == '|' &&
						   (line[0] == 'P' || line[0] == 'N' || line[0] == 'D')) {
					g_entries[line.substr(2)] = static_cast<Kind>(line[0]);
				}
			}
		}

		// The manifest is small (one short line per generated file), so a
		// full rewrite per change is cheap and keeps the format trivial.
		void Save()
		{
			std::error_code ec;
			std::filesystem::create_directories(g_manifestPath.parent_path(), ec);
			std::ofstream stream{ g_manifestPath, std::ios::trunc };
			if (!stream) {
				logger::warn("Could not write the voice manifest at '{}'", g_manifestPath.string());
				return;
			}
			stream << kHeader << '\n';
			stream << "player=" << g_playerFingerprint << '\n';
			stream << "npc=" << g_npcFingerprint << '\n';
			for (const auto& [path, kind] : g_entries) {
				stream << static_cast<char>(kind) << '|' << path << '\n';
			}
		}

		// Attempts to delete every doomed file; entries disappear once the
		// file is confirmed gone.  Returns how many were removed.
		[[nodiscard]] std::size_t DeleteDoomed()
		{
			std::size_t deleted = 0;
			for (auto it = g_entries.begin(); it != g_entries.end();) {
				if (it->second != Kind::kDoomed) {
					++it;
					continue;
				}
				const auto target = g_gameRoot / "Data" / it->first;
				if (::DeleteFileW(target.c_str())) {
					ShadowPlayback::Forget(it->first);
					++deleted;
					it = g_entries.erase(it);
					continue;
				}
				const auto error = ::GetLastError();
				if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) {
					// Already gone (user cleared the folder) — settled.
					it = g_entries.erase(it);
					continue;
				}
				logger::warn("Could not delete stale voice file '{}' (error {}); retrying later", it->first, error);
				++it;
			}
			return deleted;
		}

		// Adopts a fingerprint; a change dooms that category's files.
		// Returns true when anything changed.
		[[nodiscard]] bool ApplyFingerprint(std::string& a_stored, const std::string_view a_server, const Kind a_kind, const char* a_label)
		{
			if (a_server.empty() || a_stored == a_server) {
				return false;
			}
			if (!a_stored.empty()) {
				std::size_t doomed = 0;
				for (auto& [path, kind] : g_entries) {
					if (kind == a_kind) {
						kind = Kind::kDoomed;
						++doomed;
					}
				}
				logger::info(
					"The {} voice configuration changed in the companion app; invalidating {} generated file(s) so they regenerate with the new voice",
					a_label,
					doomed);
			}
			a_stored = a_server;
			return true;
		}
	}

	void Init(const std::filesystem::path& a_gameRoot)
	{
		const std::scoped_lock lock{ g_lock };
		g_gameRoot = a_gameRoot;
		g_manifestPath = a_gameRoot / "Data" / "F4SE" / "Plugins" / "CustomVoicedDialogue.manifest.txt";
		Load();
		if (DeleteDoomed() > 0) {
			Save();
		}
		logger::info(
			"Voice manifest loaded: {} generated file(s) tracked",
			g_entries.size());
	}

	void RecordWrite(const std::string_view a_voicePath, const bool a_isPlayer)
	{
		const std::scoped_lock lock{ g_lock };
		if (g_manifestPath.empty()) {
			return;
		}
		const auto kind = a_isPlayer ? Kind::kPlayer : Kind::kNpc;
		const auto [it, inserted] = g_entries.try_emplace(std::string{ a_voicePath }, kind);
		if (!inserted && it->second == kind) {
			return;
		}
		it->second = kind;
		Save();
	}

	bool IsTracked(const std::string_view a_voicePath)
	{
		const std::scoped_lock lock{ g_lock };
		// Engine paths vary in casing (some sites hand out ALL-CAPS), so the
		// lookup is case-insensitive; the map stays small enough to scan.
		for (const auto& [path, kind] : g_entries) {
			if (kind != Kind::kDoomed && path.size() == a_voicePath.size() &&
				_strnicmp(path.c_str(), a_voicePath.data(), a_voicePath.size()) == 0) {
				return true;
			}
		}
		return false;
	}

	void ApplyServerFingerprints(const std::string_view a_player, const std::string_view a_npc)
	{
		const std::scoped_lock lock{ g_lock };
		if (g_manifestPath.empty()) {
			return;
		}
		bool changed = ApplyFingerprint(g_playerFingerprint, a_player, Kind::kPlayer, "player");
		changed |= ApplyFingerprint(g_npcFingerprint, a_npc, Kind::kNpc, "NPC");
		const auto deleted = DeleteDoomed();
		if (changed || deleted > 0) {
			Save();
		}
	}
}
