#pragma once

// Runtime resolution of every engine address this plugin touches.
//
// NG (1.10.984) and AE (1.11.x) resolve through the Address Library with
// fixed, opcode-guarded instruction deltas — all values below were verified
// offline against the executables with tools/CvdTools (see
// tools/guardcheck.manifest.json).
//
// OG (1.10.163) ships with an encrypted code section on disk, so there is
// no offline database mapping for these functions.  The image is decrypted
// in memory by the time F4SE plugins load, so OG resolves by scanning the
// running executable for two byte signatures that were verified to match
// both NG and AE builds unchanged (they originate from F4z Ro D'oh's
// sig-scanning era):
//   DialogueResponse::ctor caller:  E8 ? ? ? ? 48 8B F0 EB 03 49 8B F5 44 8B 43 08
//   BSIStream::ctor caller:         E8 ? ? ? ? 33 DB 38 5C 24 30
// The voice-path call site inside the ctor is then located by the pattern
// "lea rcx,[rsi+18h]; call" (48 8D 4E 18 E8), which simultaneously proves
// the register choreography the hook thunk depends on.  Anything that does
// not resolve stays disabled (fail closed).

namespace CustomVoicedDialogue::Engine
{
	struct Addresses
	{
		// The 5-byte "call BSFixedString::Set" replaced by the voice hook.
		std::uintptr_t setPathCallSite{ 0 };
		// BSFixedString append/Set (also the call's verified destination).
		std::uintptr_t fixedStringSet{ 0 };
		// BSIStream ctor; dtor is 0 on OG (virtual dtor slot used instead).
		std::uintptr_t bsiStreamCtor{ 0 };
		std::uintptr_t bsiStreamDtor{ 0 };
		// Engine voice-path builder (prefetch); 0 = prefetch unavailable.
		std::uintptr_t buildVoicePath{ 0 };
		// SubtitleManager function bases; 0 = subtitle forcing unavailable.
		std::uintptr_t showSubtitle{ 0 };
		std::uintptr_t displayNextSubtitle{ 0 };
		// OG only: the unique "call DialogueResponse::ctor" site and the ctor
		// itself.  The OG compiler assigns responseText AFTER the voice-path
		// call, so the text must be read after the ctor returns; hooking this
		// call site provides that moment.  0 on NG/AE (text is already valid
		// at the in-ctor hook there, byte-verified offline).
		std::uintptr_t ctorCallSite{ 0 };
		std::uintptr_t dialogueResponseCtor{ 0 };
	};

	// Resolves everything for the running executable.  Returns false when
	// the core dialogue hook cannot be safely installed (optional features
	// resolve independently and may be 0 in the result).
	[[nodiscard]] bool Resolve();

	// Only valid after Resolve() returned true.
	[[nodiscard]] const Addresses& Get() noexcept;

	// True when a F4z Ro D'oh DLL (any known name/version) is loaded.
	// Its hooks are a strict subset of this plugin's behaviour, so when it
	// is positively identified its patches are superseded instead of
	// treated as a conflict — users do not have to uninstall it.
	[[nodiscard]] bool IsF4zRoDohLoaded() noexcept;
}
