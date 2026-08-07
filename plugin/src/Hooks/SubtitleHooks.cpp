#include "PCH.h"

#include "Hooks/SubtitleHooks.h"

#include "Engine.h"
#include "RE/Dialogue.h"
#include "Settings.h"
#include "SubtitleHashStore.h"

#include <xbyak/xbyak.h>

// Hook choreography (register usage, stack offsets, and the show/skip
// continuation points) follows F4z Ro D'oh v3 (GPL-3, by shadeMe).  Every
// site's instruction bytes were verified offline on 1.10.984 and 1.11.221
// with tools/CvdTools; a mismatched executable leaves the vanilla code
// untouched (the feature is cosmetic, so partial installation is fine).

namespace CustomVoicedDialogue::Hooks::SubtitleHooks
{
	namespace
	{
		// Each patched instruction is a 7-byte rip-relative read of the
		// cached subtitle INI bool, replaced with a 6-byte jmp + 1 NOP.
		struct Site
		{
			const char* name;
			std::ptrdiff_t delta;
			std::array<std::uint8_t, 3> expectedBytes;
			std::size_t expectedCount;
		};

		[[nodiscard]] bool ShouldForceDialogueSubs(const REX_RE::EngineFixedString* a_subtitle)
		{
			if (a_subtitle && SubtitleHashStore::Contains(a_subtitle->Get())) {
				return true;
			}
			const auto ini = RE::INIPrefSettingCollection::GetSingleton();
			const auto setting = ini ? ini->GetSetting("bDialogueSubtitles:Interface") : nullptr;
			return setting && setting->GetBinary();
		}

		[[nodiscard]] bool ShouldForceGeneralSubs(const REX_RE::EngineFixedString* a_subtitle)
		{
			if (a_subtitle && SubtitleHashStore::Contains(a_subtitle->Get())) {
				return true;
			}
			const auto ini = RE::INIPrefSettingCollection::GetSingleton();
			const auto setting = ini ? ini->GetSetting("bGeneralSubtitles:Interface") : nullptr;
			return setting && setting->GetBinary();
		}

		// SubtitleManager::ShowSubtitle sites: the replaced read feeds a
		// branch, so the thunk jumps directly to the show/skip targets.
		// r13 holds the subtitle string; rsi and the spoken-to flag at
		// [rsp+0xC0] must survive the call.
		struct ShowSubtitleThunk : Xbyak::CodeGenerator
		{
			ShowSubtitleThunk(bool (*a_predicate)(const REX_RE::EngineFixedString*), std::uintptr_t a_returnShow, std::uintptr_t a_returnSkip)
			{
				Xbyak::Label show;

				movzx(rax, byte[rsp + 0xC0]);
				push(rsi);
				push(rax);

				push(rcx);
				push(rdx);
				push(r8);
				push(r9);
				sub(rsp, 0x20);

				mov(rcx, r13);
				mov(rax, reinterpret_cast<std::uintptr_t>(a_predicate));
				call(rax);

				add(rsp, 0x20);
				pop(r9);
				pop(r8);
				pop(rdx);
				pop(rcx);

				test(al, al);
				pop(rax);
				pop(rsi);
				jnz(show);

				jmp(ptr[rip]);
				dq(a_returnSkip);

				L(show);
				jmp(ptr[rip]);
				dq(a_returnShow);
			}
		};

		// SubtitleManager::DisplayNextSubtitle sites: the replaced read's
		// flags are consumed by the following original branch, so the thunk
		// re-materializes them with "cmp al, 0" before jumping back.
		// The SubtitleInfo (text is its first member) lives at rsi+r14.
		struct DisplayNextThunk : Xbyak::CodeGenerator
		{
			DisplayNextThunk(bool (*a_predicate)(const REX_RE::EngineFixedString*), std::uintptr_t a_return)
			{
				push(rcx);
				push(rdx);
				push(r8);
				push(r9);
				sub(rsp, 0x20);

				lea(rcx, ptr[rsi + r14]);
				mov(rax, reinterpret_cast<std::uintptr_t>(a_predicate));
				call(rax);

				add(rsp, 0x20);
				pop(r9);
				pop(r8);
				pop(rdx);
				pop(rcx);

				cmp(al, 0);
				jmp(ptr[rip]);
				dq(a_return);
			}
		};

		[[nodiscard]] bool GuardMatches(std::uintptr_t a_base, const Site& a_site)
		{
			if (a_base == 0) {
				logger::warn("Subtitle hook '{}' is unavailable on this runtime", a_site.name);
				return false;
			}
			const auto* actual = reinterpret_cast<const std::uint8_t*>(a_base + a_site.delta);

			// F4z Ro D'oh patches these exact sites with a write_jmp6
			// (FF 25 rel32).  With its hook on the dialogue path already
			// superseded, its subtitle store never fills, so re-patching
			// over its jmp restores the feature instead of losing it.
			if (actual[0] == 0xFF && actual[1] == 0x25 && Engine::IsF4zRoDohLoaded()) {
				logger::info("Subtitle hook '{}': superseding F4z Ro D'oh's patch", a_site.name);
				return true;
			}

			for (std::size_t index = 0; index < a_site.expectedCount; ++index) {
				if (actual[index] != a_site.expectedBytes[index]) {
					logger::error(
						"Subtitle hook '{}' opcode guard mismatch at 0x{:X}+{}: expected 0x{:02X}, found 0x{:02X}; leaving it uninstalled",
						a_site.name,
						a_base + a_site.delta,
						index,
						a_site.expectedBytes[index],
						actual[index]);
					return false;
				}
			}
			return true;
		}

		void InstallSite(std::uintptr_t a_target, Xbyak::CodeGenerator& a_code, const char* a_name)
		{
			a_code.ready();
			auto& trampoline = REL::GetTrampoline();
			trampoline.write_jmp6(a_target, reinterpret_cast<std::uintptr_t>(trampoline.allocate(a_code)));
			// The replaced instruction is 7 bytes; pad the leftover byte.
			REL::WriteSafeFill(a_target + 6, 0x90, 1);
			logger::info("Installed subtitle hook '{}' at 0x{:X}", a_name, a_target);
		}
	}

	void Install()
	{
		if (!Settings::ForceSubtitles()) {
			logger::info("Forced subtitles are disabled in the INI; subtitle hooks skipped");
			return;
		}

		const auto& addresses = Engine::Get();

		// cmp byte ptr [rip+disp], bpl — the cached bDialogueSubtitles read.
		constexpr Site showDialog{ "ShowSubtitle dialogue", 0xB6, { 0x40, 0x38, 0x2D }, 3 };
		if (GuardMatches(addresses.showSubtitle, showDialog)) {
			const auto target = addresses.showSubtitle + showDialog.delta;
			ShowSubtitleThunk code{ &ShouldForceDialogueSubs, target + 0x0D, target + 0xD2 };
			InstallSite(target, code, showDialog.name);
		}

		// movzx eax, byte ptr [rip+disp] — the cached bGeneralSubtitles read.
		constexpr Site showGeneral{ "ShowSubtitle general", 0x17D, { 0x0F, 0xB6, 0x05 }, 3 };
		if (GuardMatches(addresses.showSubtitle, showGeneral)) {
			const auto target = addresses.showSubtitle + showGeneral.delta;
			ShowSubtitleThunk code{ &ShouldForceGeneralSubs, target + 0x21, target + 0x0B };
			InstallSite(target, code, showGeneral.name);
		}

		// cmp byte ptr [rip+disp], 0 — same bools re-read while advancing.
		constexpr Site nextDialog{ "DisplayNextSubtitle dialogue", 0x12B, { 0x80, 0x3D, 0x00 }, 2 };
		if (GuardMatches(addresses.displayNextSubtitle, nextDialog)) {
			const auto target = addresses.displayNextSubtitle + nextDialog.delta;
			DisplayNextThunk code{ &ShouldForceDialogueSubs, target + 0x7 };
			InstallSite(target, code, nextDialog.name);
		}

		constexpr Site nextGeneral{ "DisplayNextSubtitle general", 0x141, { 0x80, 0x3D, 0x00 }, 2 };
		if (GuardMatches(addresses.displayNextSubtitle, nextGeneral)) {
			const auto target = addresses.displayNextSubtitle + nextGeneral.delta;
			DisplayNextThunk code{ &ShouldForceGeneralSubs, target + 0x7 };
			InstallSite(target, code, nextGeneral.name);
		}
	}
}
