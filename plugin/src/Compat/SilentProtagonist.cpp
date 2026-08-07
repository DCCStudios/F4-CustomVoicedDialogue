#include "PCH.h"

#include "Compat/SilentProtagonist.h"

#include "Settings.h"

#include <xbyak/xbyak.h>

// Silent Protagonist (SilentProtagonistF4SE, OG 1.10.163 only — it refuses
// to load on any other runtime) mutes the voiced player by patching the
// player voice playback code: a getter hook that swaps the voice file for a
// nonexistent placeholder, plus two timer hooks that skip the speak wait.
//
// Strategy (after three in-game crashes proved that letting the engine
// natively play a real file for player lines through a replacement getter
// hook is not safe in this code path): DO NOT touch the getter.  The
// "player voice file never exists -> playback bails" behaviour is the one
// configuration proven stable for months, and it already guarantees the
// vanilla recording can never be heard.  Only the two *timer* patches are
// replaced — same site, same proven patch shape, pure assembly — writing
// this plugin's known line duration instead of Silent Protagonist's
// "skip the wait" constants.  TTS plays through the generic audio channel;
// the timers make the dialogue hold for its real length.
//
// When Silent Protagonist is not installed, an exact clone of its getter
// patch is applied (static nonexistent path, byte-identical layout) so the
// player is muted the same way, and the same timer thunks provide timing.
//
// All of its patches are fully known from source: each site is
// "mov rax, imm64; call rax" with the imm64 pointing at a thunk inside its
// DLL, followed by a fixed tail.  Because F4SE loads plugins alphabetically
// (CustomVoicedDialogue before SilentProtagonistF4SE), this plugin can
// snapshot the original bytes before they are patched.

namespace CustomVoicedDialogue::Compat::SilentProtagonist
{
	namespace
	{
		constexpr const wchar_t* kOGModuleName = L"SilentProtagonistF4SE.dll";
		constexpr const wchar_t* kNGModuleName = L"SilentProtagonistNG.dll";

		// Engine globals used by Silent Protagonist's own thunks (OG
		// 1.10.163 fixed RVAs, straight from its source).
		constexpr std::uintptr_t kPlayerSingletonRva = 0x59D6FD0;
		constexpr std::uintptr_t kDurationScaleRva = 0x371B1E0;

		struct Site
		{
			std::uintptr_t rva;
			std::size_t size;
			const char* name;
		};

		// From SilentProtagonistF4SE's source (fixed RVAs; the plugin is
		// hard-locked to runtime 1.10.163 so these can never drift).
		constexpr std::array<Site, 3> kSites{ {
			{ 0xD92082, 0x13, "player voice-file getter" },
			{ 0xD9396B, 0x10, "speak-duration timer" },
			{ 0xDA9BDD, 0x0D, "scene-advance timer" },
		} };

		std::array<std::array<std::uint8_t, 0x13>, kSites.size()> g_original{};
		bool g_snapshotTaken = false;

		// The engine may read the swapped path indefinitely from job
		// threads, so it must be an immortal static — and it must never
		// exist on disk or in an archive (that is the whole mute).
		constexpr char kMutePath[] = "Data\\Sound\\Voice\\CustomVoicedDialogue\\xxx_cvd_mute_placeholder.wav";

		// Values the timer thunks feed the engine for the player's current
		// line, as float bits.  While a line is held they carry its
		// duration; once the hold deadline passes, a watcher thread resets
		// them to Silent Protagonist's proven "advance now" constants —
		// the engine re-evaluates these sites, so a constant positive
		// duration would hold the dialogue forever.
		constexpr std::uint32_t kSpeakExpiredBits = 0x38D1B717u;    // 1e-4f, SP's speak-duration value
		constexpr std::uint32_t kAdvanceExpiredBits = 0xBF800000u;  // -1.0f, SP's scene-advance value
		std::atomic<std::uint32_t> g_speakBits{ kSpeakExpiredBits };
		std::atomic<std::uint32_t> g_advanceBits{ kAdvanceExpiredBits };

		std::mutex g_holdLock;
		std::condition_variable g_holdWake;
		std::chrono::steady_clock::time_point g_holdDeadline{};
		bool g_holdActive = false;
		bool g_watcherStarted = false;

		void HoldWatcher()
		{
			std::unique_lock lock{ g_holdLock };
			for (;;) {
				g_holdWake.wait(lock, [] { return g_holdActive; });
				const auto deadline = g_holdDeadline;
				if (g_holdWake.wait_until(lock, deadline, [&] { return g_holdDeadline != deadline; })) {
					continue;  // a newer line extended the hold
				}
				g_speakBits.store(kSpeakExpiredBits, std::memory_order_relaxed);
				g_advanceBits.store(kAdvanceExpiredBits, std::memory_order_relaxed);
				g_holdActive = false;
			}
		}

		[[nodiscard]] bool IsOG() noexcept
		{
			return REX::FModule::GetRuntimeIndex() == REX::FModule::Runtime::kOG;
		}

		[[nodiscard]] std::pair<std::uintptr_t, std::size_t> ModuleRange(const wchar_t* a_name)
		{
			const auto module = reinterpret_cast<const std::uint8_t*>(::GetModuleHandleW(a_name));
			if (!module) {
				return { 0, 0 };
			}
			const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(module);
			const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(module + dos->e_lfanew);
			return { reinterpret_cast<std::uintptr_t>(module), nt->OptionalHeader.SizeOfImage };
		}

		// Every site shares the same 12-byte head: mov rax, imm64 (48 B8 +
		// 8-byte thunk address inside Silent Protagonist's DLL); call rax
		// (FF D0).  This cannot match original engine code.
		[[nodiscard]] bool IsItsPatch(const std::uint8_t* a_bytes, std::uintptr_t a_spBase, std::size_t a_spSize)
		{
			if (a_bytes[0] != 0x48 || a_bytes[1] != 0xB8 || a_bytes[10] != 0xFF || a_bytes[11] != 0xD0) {
				return false;
			}
			std::uint64_t thunk = 0;
			std::memcpy(&thunk, a_bytes + 2, sizeof(thunk));
			return thunk >= a_spBase && thunk < a_spBase + a_spSize;
		}

		void WriteBytes(std::uintptr_t a_target, const std::uint8_t* a_bytes, std::size_t a_size)
		{
			DWORD oldProtect = 0;
			::VirtualProtect(reinterpret_cast<void*>(a_target), a_size, PAGE_EXECUTE_READWRITE, &oldProtect);
			std::memcpy(reinterpret_cast<void*>(a_target), a_bytes, a_size);
			::VirtualProtect(reinterpret_cast<void*>(a_target), a_size, oldProtect, &oldProtect);
			::FlushInstructionCache(::GetCurrentProcess(), reinterpret_cast<void*>(a_target), a_size);
		}

		// Writes the shared patch layout: mov rax, imm64(thunk); call rax;
		// then the given tail (padded with NOPs).  Byte-identical to Silent
		// Protagonist's own patches at each site.
		void WritePatch(std::uintptr_t a_target, const void* a_thunk, std::size_t a_size,
			std::initializer_list<std::uint8_t> a_tail)
		{
			std::array<std::uint8_t, 0x13> patch{};
			patch.fill(0x90);
			patch[0] = 0x48;
			patch[1] = 0xB8;
			const auto thunkAddress = reinterpret_cast<std::uintptr_t>(a_thunk);
			std::memcpy(patch.data() + 2, &thunkAddress, sizeof(thunkAddress));
			patch[10] = 0xFF;
			patch[11] = 0xD0;
			std::copy(a_tail.begin(), a_tail.end(), patch.begin() + 12);
			WriteBytes(a_target, patch.data(), a_size);
		}

		// ---- thunks (pure assembly, SP-shaped: no calls, no locks) --------

		// Getter site (only installed when Silent Protagonist is absent):
		// exact clone of its HookSilentGetter — replicate the displaced r9
		// spill, swap rdx to the static nonexistent path when the speaker is
		// the player.  rax and flags are dead at every site (the patch head
		// itself clobbers them).
		struct MuteGetterThunk : Xbyak::CodeGenerator
		{
			explicit MuteGetterThunk(const std::uintptr_t a_base)
			{
				Xbyak::Label done;
				mov(ptr[rsp + 0x28], r9);  // displaced spill (+8 for our return address)
				mov(rax, a_base + kPlayerSingletonRva);
				mov(rax, ptr[rax]);
				cmp(rcx, rax);
				jnz(done);
				mov(rdx, reinterpret_cast<std::uintptr_t>(kMutePath));
				L(done);
				ret();
			}
		};

		// Speak-duration site: replicate the displaced vanilla code
		// (mulss xmm0, [scale]; movss [actor+38Ch], xmm0), then for the
		// player overwrite the stored duration with this plugin's line
		// duration.  (Silent Protagonist's version stores 1e-4 instead —
		// that is what rushed dialogue.)
		struct SpeakDurationThunk : Xbyak::CodeGenerator
		{
			explicit SpeakDurationThunk(const std::uintptr_t a_base)
			{
				Xbyak::Label done;
				mov(rax, a_base + kDurationScaleRva);
				mulss(xmm0, ptr[rax]);
				movss(ptr[rsi + 0x38C], xmm0);
				mov(rax, a_base + kPlayerSingletonRva);
				mov(rax, ptr[rax]);
				cmp(rsi, rax);
				jnz(done);
				mov(rax, reinterpret_cast<std::uintptr_t>(&g_speakBits));
				mov(eax, ptr[rax]);
				mov(ptr[rsi + 0x38C], eax);
				L(done);
				ret();
			}
		};

		// Scene-advance site: replicate the displaced spills, then for the
		// player load this plugin's line duration into xmm0 — the value the
		// scene uses to decide how long the line holds.  (Silent
		// Protagonist forces -1.0 here, skipping the wait entirely; with the
		// player's voice file nonexistent, vanilla would compute ~0.)
		struct SceneAdvanceThunk : Xbyak::CodeGenerator
		{
			explicit SceneAdvanceThunk(const std::uintptr_t a_base)
			{
				Xbyak::Label done;
				mov(ptr[rsp + 0x88], rbp);  // displaced spills (+8 for our return address)
				mov(ptr[rsp + 0x80], rdi);
				mov(rax, a_base + kPlayerSingletonRva);
				mov(rax, ptr[rax]);
				cmp(rsi, rax);
				jnz(done);
				mov(rax, reinterpret_cast<std::uintptr_t>(&g_advanceBits));
				movss(xmm0, ptr[rax]);
				L(done);
				ret();
			}
		};
	}

	void Snapshot()
	{
		if (!IsOG()) {
			return;
		}
		const auto base = reinterpret_cast<std::uintptr_t>(::GetModuleHandleW(nullptr));
		for (std::size_t i = 0; i < kSites.size(); ++i) {
			std::memcpy(g_original[i].data(), reinterpret_cast<const void*>(base + kSites[i].rva), kSites[i].size);
		}
		g_snapshotTaken = true;
	}

	void Supersede()
	{
		if (!IsOG()) {
			// The NG/AE variant is a different mod with different patches;
			// superseding it is not implemented, so just say what it means.
			if (::GetModuleHandleW(kNGModuleName) && Settings::EnablePlayerLines()) {
				logger::warn(
					"Silent Protagonist (NG) is installed; it mutes player voice playback, so player TTS will "
					"not be heard. Remove it — bReplaceVoicedPlayerLines=1 makes it unnecessary.");
			}
			return;
		}

		if (!Settings::EnablePlayerLines() && !Settings::ReplaceVoicedPlayerLines()) {
			return;
		}
		if (!g_snapshotTaken) {
			logger::error("No pre-patch snapshot exists; the player line timing hooks cannot install");
			return;
		}

		const auto base = reinterpret_cast<std::uintptr_t>(::GetModuleHandleW(nullptr));
		const auto [spBase, spSize] = ModuleRange(kOGModuleName);

		if (spBase != 0) {
			// Silent Protagonist is installed.  Every site must be positively
			// its patch (and the snapshot must predate it) before touching
			// anything.  Its getter patch is deliberately left in place — the
			// player mute it provides is the proven-stable configuration.
			for (std::size_t i = 0; i < kSites.size(); ++i) {
				const auto current = reinterpret_cast<const std::uint8_t*>(base + kSites[i].rva);
				if (!IsItsPatch(current, spBase, spSize)) {
					logger::error(
						"Silent Protagonist is installed but its {} patch is not in the expected shape; "
						"leaving the player voice playback alone",
						kSites[i].name);
					return;
				}
				if (IsItsPatch(g_original[i].data(), spBase, spSize)) {
					logger::error(
						"The pre-patch snapshot already contains Silent Protagonist's {} patch (unexpected "
						"load order); leaving the player voice playback alone",
						kSites[i].name);
					return;
				}
			}
		} else {
			// No Silent Protagonist: every site must hold the exact vanilla
			// code its patches (and ours) displace.
			static constexpr std::array<std::uint8_t, 5> kGetterSpill{ 0x4C, 0x89, 0x4C, 0x24, 0x20 };   // mov [rsp+20h], r9
			static constexpr std::array<std::uint8_t, 4> kDurationHead{ 0xF3, 0x0F, 0x59, 0x05 };        // mulss xmm0, [rip+..]
			static constexpr std::array<std::uint8_t, 4> kAdvanceHead{ 0x48, 0x89, 0xAC, 0x24 };         // mov [rsp+..], rbp
			const auto vanillaShape =
				std::memcmp(g_original[0].data(), kGetterSpill.data(), kGetterSpill.size()) == 0 &&
				std::memcmp(g_original[1].data(), kDurationHead.data(), kDurationHead.size()) == 0 &&
				std::memcmp(g_original[2].data(), kAdvanceHead.data(), kAdvanceHead.size()) == 0;
			auto unmodified = true;
			for (std::size_t i = 0; i < kSites.size(); ++i) {
				unmodified = unmodified &&
				             std::memcmp(reinterpret_cast<const void*>(base + kSites[i].rva),
								 g_original[i].data(), kSites[i].size) == 0;
			}
			if (!vanillaShape || !unmodified) {
				logger::warn(
					"The player voice playback code does not look as expected; vanilla player recordings may "
					"play over TTS for voiced lines");
				return;
			}

			// Apply the mute Silent Protagonist would have provided: its
			// exact patch layout, including the jmp at site+0xE that routes
			// the second entry point (reached by a branch from elsewhere in
			// SpeakSoundFunction) back through the hook.
			static MuteGetterThunk muteThunk{ base };
			WritePatch(base + kSites[0].rva, muteThunk.getCode(), kSites[0].size,
				{ 0xEB, 0x05, 0xEB, 0xF0, 0x90, 0x90, 0x90 });
		}

		// Replace the speak-skip timers (Silent Protagonist's, or vanilla's
		// missing-file ~0 timing) with this plugin's real line durations.
		// Tail bytes match Silent Protagonist's own patches at these sites.
		static SpeakDurationThunk durationThunk{ base };
		static SceneAdvanceThunk advanceThunk{ base };
		WritePatch(base + kSites[1].rva, durationThunk.getCode(), kSites[1].size, { 0x90, 0x90, 0x90, 0x90 });
		WritePatch(base + kSites[2].rva, advanceThunk.getCode(), kSites[2].size, { 0x90 });

		logger::info(
			"Player line timing hooked (durations from this plugin{}); the native player voice stays muted",
			spBase != 0 ? "; Silent Protagonist's mute kept, its speak-skip timers replaced" : "; Silent Protagonist absent, equivalent mute applied");
	}

	void NotePlayerLineCarrier(const std::string_view a_enginePath, const std::string_view a_carrierPath)
	{
		(void)a_enginePath;
		// The carrier name encodes the intended hold time ("..._<N>.wav");
		// the timer thunks read it as the player line duration.
		const auto underscore = a_carrierPath.find_last_of('_');
		if (underscore == std::string_view::npos) {
			return;
		}
		std::uint32_t seconds = 0;
		for (auto i = underscore + 1; i < a_carrierPath.size() && a_carrierPath[i] >= '0' && a_carrierPath[i] <= '9'; ++i) {
			seconds = seconds * 10 + static_cast<std::uint32_t>(a_carrierPath[i] - '0');
		}
		if (seconds < 1 || seconds > 10) {
			return;
		}
		const auto bits = std::bit_cast<std::uint32_t>(static_cast<float>(seconds));
		g_speakBits.store(bits, std::memory_order_relaxed);
		g_advanceBits.store(bits, std::memory_order_relaxed);
		{
			const std::scoped_lock lock{ g_holdLock };
			// Small grace on top of the hold so the TTS can finish speaking.
			g_holdDeadline = std::chrono::steady_clock::now() +
			                 std::chrono::milliseconds(seconds * 1000 + 350);
			g_holdActive = true;
			if (!g_watcherStarted) {
				std::thread(HoldWatcher).detach();
				g_watcherStarted = true;
			}
		}
		g_holdWake.notify_all();
	}
}
