#include "PCH.h"

#include "ShadowPlayback.h"

#include "Settings.h"

#include <mmsystem.h>

#pragma comment(lib, "winmm.lib")

namespace CustomVoicedDialogue::ShadowPlayback
{
	namespace
	{
		std::mutex g_lock;
		// Normalized data-relative voice path -> audio duration in seconds.
		std::unordered_map<std::string, float> g_sessionWrites;

		[[nodiscard]] const std::filesystem::path& GameRoot()
		{
			static const std::filesystem::path gameRoot = [] {
				std::array<wchar_t, 32768> buffer{};
				const auto length = ::GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
				return length > 0 && length < buffer.size()
					? std::filesystem::path{ std::wstring_view{ buffer.data(), length } }.parent_path()
					: std::filesystem::path{};
			}();
			return gameRoot;
		}

		// ---- direct Win32 playback (session-fresh files) -------------------
		// The engine's resource layer indexes loose files at launch, so a wav
		// written THIS session resolves to the archived asset at the same
		// path — the vanilla recording — or to nothing.  (This is exactly the
		// bug where picked lines spoke vanilla Nate while the log showed our
		// "direct" play succeeding: the engine played the BA2 fuz.)  Fresh
		// files therefore play through winmm/MCI with the absolute on-disk
		// path: the mod manager's VFS applies, and an archive fallback is
		// impossible.  Engine-channel playback stays for files the resource
		// layer indexed at launch (previous sessions' TTS).

		std::mutex g_mciLock;
		std::uint32_t g_mciCounter = 0;
		// Open MCI aliases and when they may be closed (end of audio + grace).
		std::vector<std::pair<std::wstring, std::chrono::steady_clock::time_point>> g_mciOpen;

		void CloseExpiredMci()  // g_mciLock held
		{
			const auto now = std::chrono::steady_clock::now();
			std::erase_if(g_mciOpen, [&](const auto& a_entry) {
				if (a_entry.second > now) {
					return false;
				}
				::mciSendStringW((L"close " + a_entry.first).c_str(), nullptr, 0, nullptr);
				return true;
			});
		}

		// ---- volume -------------------------------------------------------
		// Both playback routes are made to obey the game's own audio sliders.
		// The engine route does it properly, by putting the sound in the
		// dialogue output category (below); this factor exists for the Win32
		// route, which plays outside the game's mixer entirely and would
		// otherwise ignore the sliders completely.

		[[nodiscard]] float ReadIniVolume(const char* a_name, const float a_fallback)
		{
			const auto setting = RE::GetINISetting(a_name);
			if (!setting || setting->GetType() != RE::Setting::SETTING_TYPE::kFloat) {
				return a_fallback;
			}
			return std::clamp(setting->GetFloat(), 0.0f, 1.0f);
		}

		// Master x voice sliders x this plugin's own trim, read at play time
		// so slider changes take effect immediately.
		[[nodiscard]] float CurrentVoiceVolume()
		{
			const auto master = ReadIniVolume("fMasterVolume:Audio", 1.0f);
			const auto voice = ReadIniVolume("fVoiceVolume:Audio", 1.0f);
			const auto trim = static_cast<float>(Settings::TtsVolumePercent()) / 100.0f;
			return std::clamp(master * voice * trim, 0.0f, 1.0f);
		}

		// The output model vanilla uses for the player's own dialogue: 2D
		// (it is your voice, not a world sound), routed through the dialogue
		// category so the Voice slider and dialogue ducking apply.
		[[nodiscard]] RE::BGSSoundOutput* DialogueOutputModel()
		{
			static RE::BGSSoundOutput* model = [] {
				const auto dataHandler = RE::TESDataHandler::GetSingleton();
				return dataHandler
					? dataHandler->LookupForm<RE::BGSSoundOutput>(0x0B5183, "Fallout4.esm")  // SOMDialogue2D
					: nullptr;
			}();
			return model;
		}

		// ---- engine playback for fresh audio via a pre-indexed slot pool ---
		// The resource layer indexes loose files at launch, so a path created
		// mid-session is invisible — but a path that EXISTED at launch stays
		// resolvable, and the engine re-reads the file when it plays.  So a
		// pool of placeholder wavs ships with the mod; fresh TTS is copied
		// over the next free slot and that (indexed) path is played through
		// the game's own audio system, which restores 3D positioning, the
		// game's volume sliders, and normal mixing/ducking.
		//
		// Two things make this safe.  These paths have no archived asset
		// behind them, so a miss can never resolve to a vanilla recording;
		// and the engine's reported duration is compared against the file we
		// just wrote, so serving cached audio for a reused slot is detected
		// rather than heard.  Either way playback falls back to Win32.
		constexpr std::uint32_t kStreamSlots = 24;

		[[nodiscard]] std::string SlotPath(const std::uint32_t a_slot)
		{
			return std::format("Sound\\Voice\\CustomVoicedDialogue\\Stream_{:02}.wav", a_slot);
		}

		std::mutex g_slotLock;
		std::uint32_t g_nextSlot = 0;
		std::array<RE::BSSoundHandle, kStreamSlots> g_slotHandles{};

		[[nodiscard]] bool PlayThroughMci(const std::filesystem::path& a_file, const float a_durationSeconds)
		{
			const std::scoped_lock lock{ g_mciLock };
			CloseExpiredMci();
			const auto alias = L"cvdtts" + std::to_wstring(g_mciCounter++);
			const auto open = L"open \"" + a_file.wstring() + L"\" type waveaudio alias " + alias;
			if (::mciSendStringW(open.c_str(), nullptr, 0, nullptr) != 0) {
				return false;
			}
			// This route bypasses the game's mixer, so the game's own sliders
			// are applied by hand (MCI takes 0-1000).  Best effort: a device
			// that refuses the command still plays.
			const auto level = static_cast<int>(CurrentVoiceVolume() * 1000.0f);
			::mciSendStringW(
				(L"setaudio " + alias + L" volume to " + std::to_wstring(level)).c_str(), nullptr, 0, nullptr);
			if (::mciSendStringW((L"play " + alias).c_str(), nullptr, 0, nullptr) != 0) {
				::mciSendStringW((L"close " + alias).c_str(), nullptr, 0, nullptr);
				return false;
			}
			// Keep the device open until the audio has finished (plus grace so
			// a slightly long file is never cut), then release it lazily on a
			// later play.
			const auto closeAfter = std::chrono::milliseconds(
				static_cast<std::int64_t>(a_durationSeconds * 1000.0f) + 2000);
			g_mciOpen.emplace_back(alias, std::chrono::steady_clock::now() + closeAfter);
			return true;
		}

		[[nodiscard]] std::string Normalize(const std::string_view a_path)
		{
			std::string normalized{ a_path };
			for (auto& character : normalized) {
				if (character == '/') {
					character = '\\';
				} else {
					character = static_cast<char>(std::tolower(static_cast<unsigned char>(character)));
				}
			}
			return normalized;
		}

		// Builds a sound handle for a data-relative path (the recipe proven
		// in-game by FPGunplayOverhaul and AudioUtil).
		[[nodiscard]] RE::BSSoundHandle BuildHandle(const std::string& a_dataRelativePath)
		{
			RE::BSSoundHandle handle;
			auto* audioManager = RE::BSAudioManager::GetSingleton();
			if (!audioManager) {
				return handle;
			}
			RE::BSResource::ID soundID;
			soundID.GenerateFromPath(a_dataRelativePath.c_str());

			// The real signature takes the file path as a fifth argument (CK
			// 2.21 PDB; the ID alone is a hash and the loose-file open needs
			// the actual path).  The vendored header still declares the
			// four-argument variant, so the call goes through the relocation
			// directly.  Usage flags 0x1A first, then 0x00 — AudioUtil's
			// proven defaults.
			using func_t = void (*)(RE::BSAudioManager*, RE::BSSoundHandle&, const RE::BSResource::ID&, std::uint32_t, std::uint8_t, const char*);
			static REL::Relocation<func_t> getSoundHandleByFile{ RE::ID::BSAudioManager::GetSoundHandleByFile };

			getSoundHandleByFile(audioManager, handle, soundID, 0x1A, 128, a_dataRelativePath.c_str());
			if (!handle.IsValid()) {
				getSoundHandleByFile(audioManager, handle, soundID, 0x00, 128, a_dataRelativePath.c_str());
			}
			return handle;
		}

		// Copies the fresh wav over a free slot and plays that indexed path.
		// Returns false whenever the engine cannot be shown to be playing
		// exactly what was written, leaving the caller to use Win32 audio.
		[[nodiscard]] bool PlayThroughEngineSlot(const std::filesystem::path& a_file, const float a_durationSeconds)
		{
			if (GameRoot().empty() || a_durationSeconds <= 0.0f) {
				return false;
			}

			std::uint32_t slot = 0;
			{
				const std::scoped_lock lock{ g_slotLock };
				// Never reuse a slot that is still sounding — overwriting it
				// would cut that line off (and the file may still be open).
				std::uint32_t probe = 0;
				for (; probe < kStreamSlots; ++probe) {
					const auto candidate = (g_nextSlot + probe) % kStreamSlots;
					if (!g_slotHandles[candidate].IsValid() || !g_slotHandles[candidate].IsPlaying()) {
						slot = candidate;
						break;
					}
				}
				if (probe == kStreamSlots) {
					return false;  // every slot busy: let Win32 audio take it
				}
				g_nextSlot = (slot + 1) % kStreamSlots;
			}

			const auto relative = SlotPath(slot);
			const auto target = GameRoot() / "Data" / relative;
			// The copy goes through Win32 so the mod manager's virtual file
			// system applies exactly as it does for the engine's own open.
			if (!::CopyFileW(a_file.c_str(), target.c_str(), FALSE)) {
				return false;
			}

			auto handle = BuildHandle(relative);
			if (!handle.IsValid()) {
				return false;
			}

			// Verify the engine is about to play THIS audio: a reused slot
			// whose data the engine still has cached would report the old
			// clip's length (in milliseconds).  A duration of zero means the
			// engine simply has not measured it yet — unverifiable rather
			// than wrong, and the slot was just overwritten with this line,
			// so play it.  Only a definite mismatch hands off to Win32.
			const auto expectedMs = static_cast<std::int64_t>(a_durationSeconds * 1000.0f);
			const auto reportedMs = static_cast<std::int64_t>(handle.GetDuration());
			if (reportedMs > 0 && std::llabs(reportedMs - expectedMs) > 400) {
				if (Settings::VerboseLog()) {
					logger::info(
						"Engine slot {} reported {} ms for a {} ms line; using direct Win32 audio instead",
						slot,
						reportedMs,
						expectedMs);
				}
				handle.Stop();
				return false;
			}

			// Route through the dialogue output category, exactly where the
			// vanilla player voice goes: the Voice slider applies and other
			// audio ducks under it as it would for real dialogue.
			if (const auto outputModel = DialogueOutputModel()) {
				handle.SetOutputModel(outputModel);
			}
			if (const auto player = RE::PlayerCharacter::GetSingleton()) {
				if (auto* object3D = player->Get3D()) {
					handle.SetObjectToFollow(object3D);
				}
			}
			if (!handle.Play()) {
				return false;
			}

			const std::scoped_lock lock{ g_slotLock };
			g_slotHandles[slot] = handle;
			return true;
		}

		// Writes a short silent wav, so the slot paths exist (and are thus
		// indexed) from the next launch on even when the mod's own copies
		// are missing.
		void WriteSilentPlaceholder(const std::filesystem::path& a_target)
		{
			constexpr std::uint32_t sampleRate = 48000;
			constexpr std::uint32_t samples = sampleRate / 4;  // 0.25 s
			const std::uint32_t dataBytes = samples * 2;
			const std::uint32_t riffSize = 36 + dataBytes;

			std::error_code ec;
			std::filesystem::create_directories(a_target.parent_path(), ec);
			std::ofstream stream{ a_target, std::ios::binary | std::ios::trunc };
			if (!stream) {
				return;
			}
			const auto put32 = [&](std::uint32_t a_value) { stream.write(reinterpret_cast<const char*>(&a_value), 4); };
			const auto put16 = [&](std::uint16_t a_value) { stream.write(reinterpret_cast<const char*>(&a_value), 2); };
			stream.write("RIFF", 4);
			put32(riffSize);
			stream.write("WAVEfmt ", 8);
			put32(16);
			put16(1);                    // PCM
			put16(1);                    // mono
			put32(sampleRate);
			put32(sampleRate * 2);       // byte rate
			put16(2);                    // block align
			put16(16);                   // bits
			stream.write("data", 4);
			put32(dataBytes);
			const std::vector<char> silence(dataBytes, 0);
			stream.write(silence.data(), static_cast<std::streamsize>(silence.size()));
		}
	}

	void EnsureStreamSlots()
	{
		if (GameRoot().empty()) {
			return;
		}
		std::uint32_t created = 0;
		for (std::uint32_t slot = 0; slot < kStreamSlots; ++slot) {
			const auto target = GameRoot() / "Data" / SlotPath(slot);
			if (::GetFileAttributesW(target.c_str()) == INVALID_FILE_ATTRIBUTES) {
				WriteSilentPlaceholder(target);
				++created;
			}
		}
		if (created > 0) {
			logger::info(
				"Created {} playback slot file(s); the game indexes them at startup, so engine audio for "
				"freshly generated lines becomes available from the next launch",
				created);
		}
	}

	void NoteSessionWrite(const std::string_view a_voicePath, const float a_durationSeconds)
	{
		const std::scoped_lock lock{ g_lock };
		g_sessionWrites[Normalize(a_voicePath)] = a_durationSeconds;
	}

	bool IsSessionFresh(const std::string_view a_voicePath, float& a_durationSeconds)
	{
		const std::scoped_lock lock{ g_lock };
		const auto it = g_sessionWrites.find(Normalize(a_voicePath));
		if (it == g_sessionWrites.end()) {
			return false;
		}
		a_durationSeconds = it->second;
		return true;
	}

	void Forget(const std::string_view a_voicePath)
	{
		const std::scoped_lock lock{ g_lock };
		g_sessionWrites.erase(Normalize(a_voicePath));
	}

	float WavDurationOnDisk(const std::string_view a_voicePath)
	{
		const auto& gameRoot = GameRoot();
		if (gameRoot.empty()) {
			return 0.0f;
		}

		const auto target = gameRoot / "Data" / a_voicePath;
		HANDLE file = ::CreateFileW(
			target.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
		if (file == INVALID_HANDLE_VALUE) {
			return 0.0f;
		}
		std::array<std::uint8_t, 512> header{};
		DWORD read = 0;
		const auto ok = ::ReadFile(file, header.data(), static_cast<DWORD>(header.size()), &read, nullptr);
		const auto fileSize = ::GetFileSize(file, nullptr);
		::CloseHandle(file);
		if (!ok || read < 44 ||
			std::memcmp(header.data(), "RIFF", 4) != 0 || std::memcmp(header.data() + 8, "WAVE", 4) != 0) {
			return 0.0f;
		}

		std::uint32_t byteRate = 0;
		std::size_t offset = 12;
		while (offset + 8 <= read) {
			const auto chunkSize = *reinterpret_cast<const std::uint32_t*>(header.data() + offset + 4);
			const auto dataStart = offset + 8;
			if (std::memcmp(header.data() + offset, "fmt ", 4) == 0 && dataStart + 16 <= read) {
				byteRate = *reinterpret_cast<const std::uint32_t*>(header.data() + dataStart + 8);
			} else if (std::memcmp(header.data() + offset, "data", 4) == 0 && byteRate != 0) {
				const auto dataSize = std::min<std::uint64_t>(chunkSize, fileSize > dataStart ? fileSize - dataStart : 0);
				return static_cast<float>(dataSize) / static_cast<float>(byteRate);
			}
			offset = dataStart + chunkSize + (chunkSize & 1);
		}
		return 0.0f;
	}

	void Play(const std::string_view a_voicePath)
	{
		// Files written this session are invisible to the engine's resource
		// layer (indexed at launch): asking the engine to play them serves
		// the ARCHIVED asset at the same path — the vanilla recording — or
		// nothing.  Play them through Win32 audio with the absolute path
		// instead; only launch-indexed files may use the engine channel.
		float freshDuration = 0.0f;
		if (IsSessionFresh(a_voicePath, freshDuration)) {
			if (freshDuration <= 0.0f) {
				freshDuration = WavDurationOnDisk(a_voicePath);
			}
			const auto file = GameRoot() / "Data" / a_voicePath;

			// Preferred: the game's own audio system, through a slot path it
			// indexed at startup (3D position, volume sliders, mixing).  It
			// self-checks and reports failure rather than playing the wrong
			// audio, so Win32 audio remains the guaranteed-correct fallback.
			if (Settings::EngineAudioForFreshLines() && PlayThroughEngineSlot(file, freshDuration)) {
				if (Settings::VerboseLog()) {
					logger::info("Playing session-fresh TTS through the game audio system: '{}'", a_voicePath);
				}
				return;
			}

			const auto played = PlayThroughMci(file, freshDuration);
			if (Settings::VerboseLog()) {
				logger::info("Playing session-fresh TTS via direct Win32 audio: '{}' (ok={})", a_voicePath, played);
			}
			if (played) {
				return;
			}
			logger::warn("Direct Win32 playback failed for '{}'; falling back to the engine channel", a_voicePath);
		}

		const std::string path{ a_voicePath };
		auto handle = BuildHandle(path);
		if (!handle.IsValid()) {
			logger::warn("Could not build a sound handle for TTS '{}'", path);
			return;
		}

		if (const auto player = RE::PlayerCharacter::GetSingleton()) {
			if (auto* object3D = player->Get3D()) {
				handle.SetObjectToFollow(object3D);
			}
		}

		const bool played = handle.Play();
		if (Settings::VerboseLog()) {
			logger::info("Playing session-fresh TTS directly: '{}' (Play()={})", path, played);
		}
	}
}
