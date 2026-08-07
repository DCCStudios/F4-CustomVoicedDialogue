#include "PCH.h"

#include "Engine.h"

namespace CustomVoicedDialogue::Engine
{
	namespace
	{
		Addresses g_addresses{};
		bool g_resolved{ false };

		// Address Library IDs shared by the NG and AE databases.  The OG
		// database uses a disjoint ID space and its executable is encrypted
		// on disk, so OG never resolves through these (see Resolve()).
		constexpr std::uint64_t kIDDialogueResponseCtor = 2227131;
		constexpr std::uint64_t kIDFixedStringSet = 2268671;
		constexpr std::uint64_t kIDBSIStreamCtor = 2275793;
		constexpr std::uint64_t kIDBSIStreamDtor = 2275803;
		constexpr std::uint64_t kIDBuildVoicePath = 2208307;
		constexpr std::uint64_t kIDShowSubtitle = 2249542;
		constexpr std::uint64_t kIDDisplayNextSubtitle = 2249551;

		// Instruction delta of the "call BSFixedString::Set" inside
		// DialogueResponse::ctor; byte-verified on 1.10.984 and 1.11.221.
		constexpr std::ptrdiff_t kSetPathCallDelta = 0x102;

		[[nodiscard]] bool IsOG() noexcept
		{
			return REX::FModule::GetRuntimeIndex() == REX::FModule::Runtime::kOG;
		}

		// Known DLL names across F4z Ro D'oh releases (v1.1 ships
		// "F4z Ro D'oh.dll", the v3 source builds "F4z-Ro-D'oh.dll").
		constexpr std::array<const wchar_t*, 3> kF4zModuleNames{
			L"F4z Ro D'oh.dll",
			L"F4z-Ro-D'oh.dll",
			L"F4zRoDoh.dll",
		};

		[[nodiscard]] std::uintptr_t ResolveID(std::uint64_t a_id)
		{
			// The same ID occupies the NG and AE slots; the OG slot is
			// deliberately 0 and never queried (Resolve() branches first).
			const REL::ID id{ 0, a_id, a_id };
			return REL::Relocation<std::uintptr_t>{ id }.address();
		}

		// ---- in-memory signature scanning (OG path) ---------------------

		struct CodeRange
		{
			const std::uint8_t* begin{ nullptr };
			std::size_t size{ 0 };
			std::uintptr_t base{ 0 };
		};

		// Executable sections of the running game module, read from the
		// in-memory PE headers (already decrypted at this point).
		[[nodiscard]] std::vector<CodeRange> GetExecutableRanges()
		{
			std::vector<CodeRange> ranges;
			const auto module = reinterpret_cast<const std::uint8_t*>(::GetModuleHandleW(nullptr));
			if (!module) {
				return ranges;
			}

			const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(module);
			const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(module + dos->e_lfanew);
			const auto sections = IMAGE_FIRST_SECTION(nt);
			for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
				const auto& section = sections[i];
				if ((section.Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0) {
					continue;
				}
				ranges.push_back({
					module + section.VirtualAddress,
					section.Misc.VirtualSize,
					reinterpret_cast<std::uintptr_t>(module) + section.VirtualAddress,
				});
			}
			return ranges;
		}

		struct Pattern
		{
			std::vector<std::int16_t> bytes;  // -1 = wildcard

			static Pattern Parse(std::string_view a_signature)
			{
				Pattern pattern;
				std::size_t i = 0;
				while (i < a_signature.size()) {
					if (a_signature[i] == ' ') {
						++i;
						continue;
					}
					if (a_signature[i] == '?') {
						pattern.bytes.push_back(-1);
						while (i < a_signature.size() && a_signature[i] == '?') {
							++i;
						}
						continue;
					}
					const auto hex = a_signature.substr(i, 2);
					pattern.bytes.push_back(static_cast<std::int16_t>(
						std::stoi(std::string{ hex }, nullptr, 16)));
					i += 2;
				}
				return pattern;
			}
		};

		[[nodiscard]] std::vector<std::uintptr_t> FindAll(const Pattern& a_pattern, std::size_t a_limit)
		{
			std::vector<std::uintptr_t> matches;
			for (const auto& range : GetExecutableRanges()) {
				if (range.size < a_pattern.bytes.size()) {
					continue;
				}
				const auto limit = range.size - a_pattern.bytes.size();
				for (std::size_t offset = 0; offset <= limit; ++offset) {
					bool hit = true;
					for (std::size_t i = 0; i < a_pattern.bytes.size(); ++i) {
						const auto expected = a_pattern.bytes[i];
						if (expected >= 0 && range.begin[offset + i] != static_cast<std::uint8_t>(expected)) {
							hit = false;
							break;
						}
					}
					if (hit) {
						matches.push_back(range.base + offset);
						if (matches.size() >= a_limit) {
							return matches;
						}
					}
				}
			}
			return matches;
		}

		// Returns the unique match address, or 0 when there is no match or
		// more than one (ambiguity is treated as failure).
		[[nodiscard]] std::uintptr_t FindUnique(const Pattern& a_pattern, std::string_view a_name)
		{
			const auto matches = FindAll(a_pattern, 2);
			if (matches.empty()) {
				logger::error("Signature '{}' was not found in this executable", a_name);
				return 0;
			}
			if (matches.size() > 1) {
				logger::error("Signature '{}' is ambiguous in this executable; refusing to use it", a_name);
				return 0;
			}
			return matches[0];
		}

		// Counts rel32 call references to each candidate in one pass over
		// the executable code (used to tell twin functions apart).
		[[nodiscard]] std::vector<std::size_t> CountCallReferences(const std::vector<std::uintptr_t>& a_candidates)
		{
			std::vector<std::size_t> counts(a_candidates.size(), 0);
			for (const auto& range : GetExecutableRanges()) {
				if (range.size < 5) {
					continue;
				}
				for (std::size_t offset = 0; offset + 5 <= range.size; ++offset) {
					if (range.begin[offset] != 0xE8) {
						continue;
					}
					const auto displacement = *reinterpret_cast<const std::int32_t*>(range.begin + offset + 1);
					const auto destination = range.base + offset + 5 + displacement;
					for (std::size_t i = 0; i < a_candidates.size(); ++i) {
						if (destination == a_candidates[i]) {
							++counts[i];
						}
					}
				}
			}
			return counts;
		}

		// Recovers BSFixedString::Set without the ctor's original call (the
		// byte F4z Ro D'oh's patch destroys).  The Set wrapper body is
		// byte-identical on 1.10.984 and 1.11.221, so the same shape is
		// expected on OG; the scan finds it and its rarely-used pool
		// sibling, and a caller-count vote separates them (measured 529:1
		// on NG and 527:1 on AE).  Fails closed without a landslide.
		[[nodiscard]] std::uintptr_t RecoverFixedStringSetBySignature()
		{
			const auto candidates = FindAll(
				Pattern::Parse(
					"40 53 48 83 EC 20 48 8B 01 48 8B D9 48 89 44 24 30 48 85 D2 74 1B 45 33 C0"
					" E8 ? ? ? ? 48 8D 4C 24 30 E8 ? ? ? ? 48 8B C3 48 83 C4 20 5B C3"),
				8);
			if (candidates.empty()) {
				logger::error("BSFixedString::Set wrapper signature not found; cannot recover from F4z Ro D'oh's patch");
				return 0;
			}
			if (candidates.size() == 1) {
				return candidates[0];
			}

			const auto counts = CountCallReferences(candidates);
			std::size_t winner = 0;
			std::size_t runnerUpCount = 0;
			for (std::size_t i = 1; i < candidates.size(); ++i) {
				if (counts[i] > counts[winner]) {
					runnerUpCount = counts[winner];
					winner = i;
				} else if (counts[i] > runnerUpCount) {
					runnerUpCount = counts[i];
				}
			}

			constexpr std::size_t kMinimumCallers = 50;
			constexpr std::size_t kMinimumMargin = 5;
			if (counts[winner] < kMinimumCallers || counts[winner] < runnerUpCount * kMinimumMargin) {
				logger::error(
					"BSFixedString::Set caller-count vote was not decisive ({} vs {}); refusing to guess",
					counts[winner],
					runnerUpCount);
				return 0;
			}
			logger::info(
				"Recovered BSFixedString::Set at 0x{:X} by signature ({} callers vs {} for the runner-up)",
				candidates[winner],
				counts[winner],
				runnerUpCount);
			return candidates[winner];
		}

		[[nodiscard]] std::uintptr_t Rel32Target(std::uintptr_t a_callSite)
		{
			const auto rel = *reinterpret_cast<const std::int32_t*>(a_callSite + 1);
			return a_callSite + 5 + rel;
		}

		// True when the pattern matches at exactly this address (which must
		// lie fully inside an executable section).
		[[nodiscard]] bool MatchesAt(std::uintptr_t a_address, const Pattern& a_pattern)
		{
			for (const auto& range : GetExecutableRanges()) {
				if (a_address >= range.base && a_address + a_pattern.bytes.size() <= range.base + range.size) {
					const auto* bytes = reinterpret_cast<const std::uint8_t*>(a_address);
					for (std::size_t i = 0; i < a_pattern.bytes.size(); ++i) {
						if (a_pattern.bytes[i] >= 0 && bytes[i] != static_cast<std::uint8_t>(a_pattern.bytes[i])) {
							return false;
						}
					}
					return true;
				}
			}
			return false;
		}

		// ---- per-runtime resolution -------------------------------------

		bool ResolveViaAddressLibrary()
		{
			const auto ctor = ResolveID(kIDDialogueResponseCtor);
			g_addresses.fixedStringSet = ResolveID(kIDFixedStringSet);
			g_addresses.bsiStreamCtor = ResolveID(kIDBSIStreamCtor);
			g_addresses.bsiStreamDtor = ResolveID(kIDBSIStreamDtor);
			g_addresses.buildVoicePath = ResolveID(kIDBuildVoicePath);
			g_addresses.showSubtitle = ResolveID(kIDShowSubtitle);
			g_addresses.displayNextSubtitle = ResolveID(kIDDisplayNextSubtitle);

			// Opcode guard: the site must still be a rel32 call landing on
			// BSFixedString::Set.  One known modification is tolerated:
			// F4z Ro D'oh's write_jmp5 leaves a rel32 jmp (0xE9) here, and
			// its behaviour is a strict subset of ours, so with its DLL
			// positively identified we supersede its patch (this plugin
			// installs at kPostLoad, after every plugin's load-time hooks).
			// Anything else fails closed.
			const auto site = ctor + kSetPathCallDelta;
			const auto firstByte = *reinterpret_cast<const std::uint8_t*>(site);
			if (firstByte == 0xE8 && Rel32Target(site) == g_addresses.fixedStringSet) {
				g_addresses.setPathCallSite = site;
				return true;
			}
			if (firstByte == 0xE9 && IsF4zRoDohLoaded()) {
				logger::info(
					"F4z Ro D'oh's dialogue hook found at 0x{:X}; superseding it (this plugin includes its silent-voice behaviour, no need to uninstall it)",
					site);
				g_addresses.setPathCallSite = site;
				return true;
			}
			logger::error(
				"The dialogue hook site failed its opcode guard (0x{:X}, byte 0x{:02X}); another mod patched it or this game version is not supported yet",
				site,
				firstByte);
			return false;
		}

		bool ResolveViaSignatures()
		{
			// 1. A unique caller of DialogueResponse::ctor.
			const auto ctorXref = FindUnique(
				Pattern::Parse("E8 ? ? ? ? 48 8B F0 EB 03 49 8B F5 44 8B 43 08"),
				"DialogueResponse::ctor caller");
			if (!ctorXref) {
				return false;
			}
			const auto ctor = Rel32Target(ctorXref);
			g_addresses.ctorCallSite = ctorXref;
			g_addresses.dialogueResponseCtor = ctor;

			// 2. The voice-path call site inside the ctor.  The pattern
			//    "lea rcx,[rsi+18h]; call rel32" both locates the site and
			//    proves rsi holds the DialogueResponse (the thunk relies on
			//    that) and that +0x18 is still the voiceFilePath member.
			//    A trailing 0xE9 instead of 0xE8 means another mod already
			//    replaced the call.  The pattern can legitimately match more
			//    than once here (e.g. a member-init call on the same field,
			//    or a coincidental byte run), so ambiguity is resolved rather
			//    than refused: F4z Ro D'oh's patch replaces exactly this call,
			//    making its lone 0xE9 a beacon for the genuine site; failing
			//    that, the call target must equal the independently recovered
			//    BSFixedString::Set.  Anything still ambiguous fails closed.
			struct CtorSite
			{
				std::uintptr_t site;   // address of the branch byte
				std::uint8_t branch;   // 0xE8 call, 0xE9 jmp5, 0xFF = FF 25 jmp6
			};
			const std::uint8_t leaPrefix[]{ 0x48, 0x8D, 0x4E, 0x18 };
			std::vector<CtorSite> found;
			for (std::uintptr_t at = ctor; at < ctor + 0x200; ++at) {
				if (std::memcmp(reinterpret_cast<const void*>(at), leaPrefix, sizeof(leaPrefix)) != 0) {
					continue;
				}
				const auto branchByte = *reinterpret_cast<const std::uint8_t*>(at + 4);
				if (branchByte == 0xE8 || branchByte == 0xE9) {
					found.push_back({ at + 4, branchByte });
				} else if (branchByte == 0xFF && *reinterpret_cast<const std::uint8_t*>(at + 5) == 0x25) {
					found.push_back({ at + 4, branchByte });
				}
			}
			if (found.empty()) {
				logger::error("Could not locate the voice-path call inside DialogueResponse::ctor");
				return false;
			}

			const bool f4zLoaded = IsF4zRoDohLoaded();
			std::size_t e9Count = 0;
			std::size_t jmp6Count = 0;
			for (const auto& candidate : found) {
				if (candidate.branch == 0xE9) {
					++e9Count;
				} else if (candidate.branch == 0xFF) {
					++jmp6Count;
				}
				// Full candidate detail: the OG executable cannot be examined
				// offline, so this log line is the only window into it.
				if (candidate.branch == 0xFF) {
					logger::info("Voice-path candidate at ctor+0x{:X}: FF 25 (6-byte jmp patch)", candidate.site - ctor);
				} else {
					logger::info(
						"Voice-path candidate at ctor+0x{:X}: 0x{:02X} -> 0x{:X}",
						candidate.site - ctor,
						candidate.branch,
						Rel32Target(candidate.site));
				}
			}

			std::uintptr_t site = 0;
			bool sitePatched = false;
			std::uintptr_t recoveredSet = 0;
			if (found.size() == 1 && found[0].branch != 0xFF) {
				site = found[0].site;
				sitePatched = found[0].branch == 0xE9;
			} else if (f4zLoaded && e9Count == 1) {
				// F4z Ro D'oh replaces exactly this call, so its own patch
				// marks the genuine site among the pattern matches.
				for (const auto& candidate : found) {
					if (candidate.branch == 0xE9) {
						site = candidate.site;
					}
				}
				sitePatched = true;
				logger::info("F4z Ro D'oh's patch identifies the voice-path call at ctor+0x{:X}", site - ctor);
			} else if (f4zLoaded && e9Count == 0 && jmp6Count > 0) {
				logger::error(
					"F4z Ro D'oh appears to have patched the voice-path call with a 6-byte jmp, which cannot be "
					"safely superseded on this game version; remove F4z Ro D'oh to use CustomVoicedDialogue");
				return false;
			} else if (e9Count == 0) {
				// No patches in play: the genuine call's target must be the
				// independently recovered BSFixedString::Set.
				recoveredSet = RecoverFixedStringSetBySignature();
				if (recoveredSet != 0) {
					std::size_t matches = 0;
					for (const auto& candidate : found) {
						if (candidate.branch == 0xE8 && Rel32Target(candidate.site) == recoveredSet) {
							site = candidate.site;
							++matches;
						}
					}
					if (matches != 1) {
						site = 0;
					} else {
						logger::info("BSFixedString::Set's address identifies the voice-path call at ctor+0x{:X}", site - ctor);
					}
				}
			}
			if (!site) {
				logger::error(
					"The voice-path call inside DialogueResponse::ctor could not be identified unambiguously "
					"({} candidates, listed above); CustomVoicedDialogue is inactive on this game version",
					found.size());
				return false;
			}

			if (!sitePatched) {
				// The call's destination *is* BSFixedString::Set.
				g_addresses.fixedStringSet = Rel32Target(site);
			} else if (IsF4zRoDohLoaded()) {
				// F4z Ro D'oh replaced the call, taking our Set source with
				// it.  Recover Set independently, then supersede its patch
				// exactly like the Address Library runtimes do.
				g_addresses.fixedStringSet = RecoverFixedStringSetBySignature();
				if (g_addresses.fixedStringSet == 0) {
					logger::error(
						"F4z Ro D'oh has patched the voice-path call and BSFixedString::Set could not be recovered; remove F4z Ro D'oh to use CustomVoicedDialogue on this game version");
					return false;
				}
				logger::info("F4z Ro D'oh's dialogue hook found; superseding it (no need to uninstall it)");
			} else {
				logger::error("Another mod has already patched the voice-path call; CustomVoicedDialogue is inactive");
				return false;
			}

			g_addresses.setPathCallSite = site;

			// 3. BSIStream ctor, for archive-aware existence checks.  The
			//    dtor stays 0: the object's virtual deleting destructor
			//    (vtable slot 0, called with flags=0) is used instead.
			const auto streamXref = FindUnique(
				Pattern::Parse("E8 ? ? ? ? 33 DB 38 5C 24 30"),
				"BSIStream::ctor caller");
			if (!streamXref) {
				return false;
			}
			g_addresses.bsiStreamCtor = Rel32Target(streamXref);

			// 4. BuildVoicePath, for prefetch: it is the call immediately
			//    before the voice-path call inside the ctor (NG: ctor+0xED
			//    versus the Set at +0x102).  Candidate calls are verified by
			//    the callee's prologue, which is byte-identical on NG and AE
			//    for these 0x2F bytes with no relocations (and unique in both
			//    executables), so a stray 0xE8 byte inside another
			//    instruction's operand can never pass.
			const auto buildVoicePathPrologue = Pattern::Parse(
				"48 89 5C 24 10 48 89 7C 24 18 41 56 48 81 EC 40 01 00 00"
				" 48 8B 9C 24 70 01 00 00 49 8B C1 49 8B F8 C6 44 24 20 00"
				" 4C 8B F2 4C 8B CB 4C 8B C0");
			// Same shape with the stack-frame size and the stack-argument
			// offset wildcarded — the older OG compiler may size the frame
			// differently while keeping the structure.
			const auto relaxedPrologue = Pattern::Parse(
				"48 89 5C 24 10 48 89 7C 24 18 41 56 48 81 EC ? ? 00 00"
				" 48 8B 9C 24 ? ? 00 00 49 8B C1 49 8B F8 C6 44 24 20 00"
				" 4C 8B F2 4C 8B CB 4C 8B C0");
			// The OG compiler's rendition, captured live from the candidate
			// dump on 1.10.163: same big-frame path builder (rbx/rbp/rsi
			// saves + push rdi + sub rsp,150h), called at site-0x14 — one
			// byte closer than NG's site-0x15.
			const auto ogPrologue = Pattern::Parse(
				"48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 81 EC ? ? 00 00");
			for (std::ptrdiff_t back = 5; back <= 0x80; ++back) {
				const auto at = site - back;
				if (*reinterpret_cast<const std::uint8_t*>(at) != 0xE8) {
					continue;
				}
				const auto target = Rel32Target(at);
				if (MatchesAt(target, buildVoicePathPrologue) || MatchesAt(target, relaxedPrologue) || MatchesAt(target, ogPrologue)) {
					g_addresses.buildVoicePath = target;
					break;
				}
			}
			if (g_addresses.buildVoicePath != 0) {
				logger::info(
					"OG runtime: prefetch enabled (BuildVoicePath at 0x{:X}); forced subtitles remain unavailable",
					g_addresses.buildVoicePath);
			} else {
				logger::info("OG runtime: BuildVoicePath not found; prefetch is unavailable and lines voice on replay instead");
				// The OG executable cannot be examined offline, so dump what
				// the scan saw: every candidate call and its target's first
				// bytes.  This is the data a future signature comes from.
				for (std::ptrdiff_t back = 5; back <= 0x80; ++back) {
					const auto at = site - back;
					if (*reinterpret_cast<const std::uint8_t*>(at) != 0xE8) {
						continue;
					}
					const auto target = Rel32Target(at);
					bool inCode = false;
					for (const auto& range : GetExecutableRanges()) {
						if (target >= range.base && target + 24 <= range.base + range.size) {
							inCode = true;
							break;
						}
					}
					if (!inCode) {
						continue;
					}
					std::string head;
					for (std::size_t i = 0; i < 24; ++i) {
						head += std::format("{:02X} ", reinterpret_cast<const std::uint8_t*>(target)[i]);
					}
					logger::info("  candidate call at site-0x{:X} -> 0x{:X}: {}", back, target, head);
				}
			}
			return true;
		}
	}

	bool Resolve()
	{
		if (g_resolved) {
			return true;
		}

		const bool ok = IsOG() ? ResolveViaSignatures() : ResolveViaAddressLibrary();
		if (!ok) {
			return false;
		}

		logger::info(
			"Engine addresses resolved ({}): hookSite=0x{:X}, fixedStringSet=0x{:X}, bsiStream=0x{:X}, buildVoicePath=0x{:X}",
			IsOG() ? "OG signature scan" : "address library",
			g_addresses.setPathCallSite,
			g_addresses.fixedStringSet,
			g_addresses.bsiStreamCtor,
			g_addresses.buildVoicePath);
		g_resolved = true;
		return true;
	}

	const Addresses& Get() noexcept
	{
		return g_addresses;
	}

	bool IsF4zRoDohLoaded() noexcept
	{
		for (const auto name : kF4zModuleNames) {
			if (::GetModuleHandleW(name)) {
				return true;
			}
		}
		return false;
	}
}
