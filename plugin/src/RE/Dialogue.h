#pragma once

// Engine types this plugin needs that the vendored CommonLibF4 does not
// define (it only forward-declares DialogueResponse).  Layouts follow the
// public reverse engineering in F4z Ro D'oh (GPL-3, by shadeMe), verified
// against the NG/AE executables.  All function addresses come from
// Engine::Resolve() so one binary serves OG/NG/AE.

#include "Engine.h"

namespace CustomVoicedDialogue::REX_RE
{
	// BSFixedString whose Set() goes through the engine's string-pool append
	// routine.  The vendored CommonLibF4 BSFixedString does not expose a
	// runtime Set for the CS variant used by DialogueResponse.
	class EngineFixedString
	{
	public:
		/*00*/ RE::BSStringPool::Entry* data{ nullptr };

		[[nodiscard]] std::string_view Get() const
		{
			if (!data) {
				return {};
			}
			// u8() walks the kShallow (0x4000) alias chain to the leaf entry
			// before reading the characters at leaf+0x18.  Localized strings
			// (all Fallout4.esm dialogue text) are shallow entries, so a
			// naive read of data+1 returns empty for them — verified against
			// F4z Ro D'oh v1.1's identical chain-walk on OG.
			const auto* characters = data->u8();
			return characters ? std::string_view{ characters } : std::string_view{};
		}

		void Set(const char* a_string)
		{
			using func_t = void (*)(EngineFixedString*, const char*);
			reinterpret_cast<func_t>(Engine::Get().fixedStringSet)(this, a_string);
		}
	};
	static_assert(sizeof(EngineFixedString) == 0x8);

	// 40 — one spoken line, created right before the engine loads its audio.
	class DialogueResponse
	{
	public:
		/*00*/ EngineFixedString responseText;
		/*08*/ RE::BGSKeyword* animFace;
		/*10*/ std::uint16_t uiPercent;
		/*12*/ std::uint8_t pad12[6];
		/*18*/ EngineFixedString voiceFilePath;
		/*20*/ RE::TESIdleForm* speakerIdle;
		/*28*/ RE::TESIdleForm* listenerIdle;
		/*30*/ RE::BGSSoundDescriptorForm* voiceSound;
		/*38*/ std::uint8_t useEmotionAnim;
		/*39*/ std::uint8_t hasLipFile;
		/*3A*/ std::uint8_t endOnSceneEnd;
		/*3B*/ std::uint8_t pad3B[5];
	};
	static_assert(sizeof(DialogueResponse) == 0x40);

	// 20 — resource-layer input stream.  Constructing one is the reliable
	// way to ask "does this Data-relative asset exist" across loose files
	// and BA2 archives.
	class BSIStream
	{
	public:
		/*00*/ void** vtbl;
		/*08*/ std::uint64_t stream;   // smart pointer to the actual file stream
		/*10*/ std::uint8_t valid;     // 1 when the resource resolved
		/*11*/ std::uint8_t pad11[7];
		/*18*/ EngineFixedString filePath;

		// The engine allocates these itself; this plugin runs the real
		// ctor/dtor over a raw buffer, exactly like F4z Ro D'oh.
		[[nodiscard]] static bool ResourceExists(const char* a_dataRelativePath)
		{
			using ctor_t = void (*)(void*, const char*, void*, bool);
			const auto& addresses = Engine::Get();

			alignas(16) std::uint8_t buffer[sizeof(BSIStream)]{};
			reinterpret_cast<ctor_t>(addresses.bsiStreamCtor)(buffer, a_dataRelativePath, nullptr, false);
			const auto instance = reinterpret_cast<BSIStream*>(buffer);
			const bool valid = instance->valid != 0;

			if (addresses.bsiStreamDtor != 0) {
				// Non-virtual base destructor (Address Library runtimes).
				using dtor_t = void* (*)(void*, bool);
				reinterpret_cast<dtor_t>(addresses.bsiStreamDtor)(buffer, false);
			} else {
				// OG: the virtual deleting destructor is vtable slot 0;
				// flags=0 destructs without freeing our stack buffer.
				using vdtor_t = void* (*)(void*, unsigned int);
				reinterpret_cast<vdtor_t>(instance->vtbl[0])(buffer, 0);
			}
			return valid;
		}
	};
	static_assert(sizeof(BSIStream) == 0x20);
}
