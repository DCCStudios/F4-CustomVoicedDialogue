#include "PCH.h"

#include "Hooks/VoicePathHook.h"

#include "Compat/SilentProtagonist.h"
#include "Engine.h"
#include "GameContext.h"
#include "RE/Dialogue.h"
#include "Settings.h"
#include "ShadowPlayback.h"
#include "SilenceFallback.h"
#include "SubtitleHashStore.h"
#include "SynthQueue.h"
#include "VoicePath.h"

#include <charconv>

#include <xbyak/xbyak.h>

namespace CustomVoicedDialogue::Hooks::VoicePathHook
{
	namespace
	{
		std::atomic<bool> g_installed{ false };

		// OG only: the compiler there assigns responseText AFTER the
		// voice-path call this plugin hooks, so at hook time the text member
		// holds stale heap data (usually empty).  A miss is therefore given a
		// provisional silence and parked here; the post-ctor hook completes
		// it once the text is real.  The ctor and its return run
		// synchronously on one thread, so a thread_local slot pairs them.
		struct DeferredLine
		{
			REX_RE::DialogueResponse* response{ nullptr };
			std::string enginePath;
			std::string voiceType;
			bool isPlayer{ false };
		};
		thread_local std::optional<DeferredLine> tls_deferred;

		// Lines with no synthesizable content.  Bethesda and mod authors use
		// a single space (or similar) as the "no subtitle" convention, and
		// F4z Ro D'oh's SkipEmptyResponses treats those the same way.
		[[nodiscard]] bool HasMeaningfulText(std::string_view a_text) noexcept
		{
			return a_text.find_first_not_of(" \t\r\n") != std::string_view::npos;
		}

		void FinishLine(
			REX_RE::DialogueResponse* a_response,
			std::string_view a_enginePath,
			std::string_view a_text,
			std::string_view a_voiceType,
			bool a_isPlayer)
		{
			auto silenceSeconds = SilenceFallback::EstimateSeconds(
				a_text,
				Settings::WordsPerSecond(),
				Settings::MinimumSilenceSeconds(),
				Settings::WideCharactersPerWord());
			if (a_isPlayer) {
				// Bounded wait-for-voice: pad the line so the audio being
				// generated right now can arrive and still play within it,
				// instead of the scene advancing over a silent line.  Capped
				// by the longest shipped silence file.
				silenceSeconds = std::min<std::uint32_t>(silenceSeconds + Settings::PendingLineWaitSeconds(), 10);
			}
			const auto silencePath = std::format("Data\\Sound\\Voice\\CustomVoicedDialogue\\Silence_{}.wav", silenceSeconds);
			a_response->voiceFilePath.Set(silencePath.c_str());
			if (a_isPlayer) {
				// The native playback getter redirects this line to the same
				// carrier, so the vanilla recording can never play over it.
				Compat::SilentProtagonist::NotePlayerLineCarrier(a_enginePath, silencePath);
			}

			if (Settings::ForceSubtitles()) {
				SubtitleHashStore::Add(a_text);
			}

			SynthQueue::Enqueue({
				.voicePath = std::string{ VoicePath::StripDataPrefix(a_enginePath) },
				.text = std::string{ a_text },
				.voiceType = std::string{ a_voiceType },
				// Sampled here because this runs on the game thread; the
				// worker that submits the job cannot read actor state.
				.context = Settings::SendSceneContext() ? GameContext::Current() : std::string{},
				.isPlayer = a_isPlayer,
				// This line is playing its silence right now — the audio
				// may still catch it if it arrives early enough.
				.playOnArrival = a_isPlayer,
				.silenceSeconds = static_cast<float>(silenceSeconds),
			});

			if (Settings::VerboseLog()) {
				logger::info("Queued TTS for '{}' (text='{}') and substituted '{}'", a_enginePath, a_text, silencePath);
			}
		}

		// Called in place of the engine's own voiceFilePath assignment.
		// a_path is the stack buffer the engine just built
		// ("Data\Sound\Voice\<plugin>\<voicetype>\<name>.wav").
		void SwapVoicePath(REX_RE::DialogueResponse* a_response, char* a_path)
		{
			const std::string_view enginePath{ a_path ? a_path : "" };
			const auto voiceType = VoicePath::ExtractVoiceType(enginePath);
			const auto responseText = a_response->responseText.Get();
			const bool deferText = Engine::Get().ctorCallSite != 0;

			if (Settings::VerboseLog()) {
				logger::info("Dialogue line: voiceType='{}', path='{}', text='{}'", voiceType, enginePath, responseText);
			}

			// Decide whether this line is in scope before paying for the
			// resource-existence probe.  The empty-text bail-out only applies
			// where the text is trustworthy at this point.
			const bool isPlayer = VoicePath::IsPlayerVoiceType(voiceType);
			const bool inScope = isPlayer ? Settings::EnablePlayerLines() : Settings::EnableNPCLines();
			if (!inScope || voiceType.empty() || (!deferText && !HasMeaningfulText(responseText))) {
				a_response->voiceFilePath.Set(a_path);
				return;
			}

			// Normally, a recorded voice asset (or previously generated TTS
			// wav at this same path) wins and the engine plays it exactly as
			// vanilla would.  In replace mode, shipped .fuz/.xwm acting is
			// deliberately ignored and only a .wav (our generated audio, or
			// a mod's loose wav) counts as "this line has audio".
			const bool replaceRecorded = isPlayer ? Settings::ReplaceVoicedPlayerLines() : Settings::ReplaceVoicedNPCLines();
			const bool hasAudio = replaceRecorded
				? VoicePath::GeneratedWavExists(a_path)
				: VoicePath::VoiceAssetExists(a_path);
			if (hasAudio) {
				// Wavs written during this session pass the existence check
				// but the voice layer may refuse to serve them until the
				// next launch (observed on OG + MO2).  HerikaServer's
				// ecosystem avoids the voice file system entirely for fresh
				// audio; do the same here: play the wav directly through the
				// generic audio channel, with duration-matched silence as
				// the line's timing carrier.  With bDirectAudioPlayback=1
				// every generated player wav takes this path, not just the
				// session-fresh ones.  Player lines only — their
				// construction coincides with playback (NPC/radio lines are
				// built in advance, so they stay native and simply voice
				// from the next session on).
				const auto strippedPath = VoicePath::StripDataPrefix(enginePath);
				float directDuration = 0.0f;
				bool playDirect = isPlayer && ShadowPlayback::IsSessionFresh(strippedPath, directDuration);
				// Replace mode never trusts the native voice channel: on OG
				// it resolves the vanilla recording (BA2 fuz) over the
				// swapped path, and with Silent Protagonist muting it
				// (wanted — that is what silences the vanilla voice),
				// generated audio must always take the generic channel.
				if (!playDirect && isPlayer &&
					(Settings::DirectAudioPlayback() || Settings::ReplaceVoicedPlayerLines()) &&
					VoicePath::GeneratedWavExists(a_path)) {
					directDuration = ShadowPlayback::WavDurationOnDisk(strippedPath);
					playDirect = directDuration > 0.0f;
				}
				if (playDirect) {
					const auto silencePath = SilenceFallback::PickForSeconds(directDuration);
					a_response->voiceFilePath.Set(silencePath.c_str());
					Compat::SilentProtagonist::NotePlayerLineCarrier(enginePath, silencePath);
					ShadowPlayback::Play(strippedPath);
					return;
				}
				a_response->voiceFilePath.Set(a_path);
				return;
			}

			if (deferText) {
				// Park the miss with a provisional minimum silence; the
				// post-ctor hook re-sizes it and queues once the real text
				// exists.
				const auto silencePath = SilenceFallback::Pick({});
				a_response->voiceFilePath.Set(silencePath.c_str());
				tls_deferred = DeferredLine{
					a_response,
					std::string{ enginePath },
					std::string{ voiceType },
					isPlayer,
				};
				return;
			}

			// No audio exists: substitute silence sized to the text so the
			// subtitle stays up, and queue TTS generation.  Once the worker
			// writes the wav at the engine path, the next play of this line
			// takes the branch above and speaks.
			FinishLine(a_response, enginePath, responseText, voiceType, isPlayer);
		}

		// Last-resort text recovery for deferred lines whose text is STILL
		// not assigned when the post-ctor hook runs (seen on OG for the
		// generic shared-response player lines, whose text arrives through a
		// later code path).  The engine path itself carries everything
		// needed to read the text from form data instead: the owning
		// plugin, the INFO's local form id, and the response number.  The
		// donor walk mirrors Prefetch: shared-response INFOs keep their
		// response chain on dataInfo.
		[[nodiscard]] std::string LookupResponseTextFromForm(const std::string_view a_enginePath)
		{
			// ...\<plugin>\<voicetype>\<XXXXXXXX>_<n>.wav
			const auto lastSep = a_enginePath.find_last_of("\\/");
			if (lastSep == std::string_view::npos || lastSep < 2) {
				return {};
			}
			const auto typeSep = a_enginePath.find_last_of("\\/", lastSep - 1);
			if (typeSep == std::string_view::npos || typeSep < 2) {
				return {};
			}
			const auto pluginSep = a_enginePath.find_last_of("\\/", typeSep - 1);
			if (pluginSep == std::string_view::npos) {
				return {};
			}
			const auto plugin = a_enginePath.substr(pluginSep + 1, typeSep - pluginSep - 1);
			const auto basename = a_enginePath.substr(lastSep + 1);
			if (basename.size() < 10 || basename[8] != '_') {
				return {};
			}

			std::uint32_t localID = 0;
			if (std::from_chars(basename.data(), basename.data() + 8, localID, 16).ec != std::errc{}) {
				return {};
			}
			std::uint32_t responseNumber = 0;
			const auto dot = basename.find('.', 9);
			const auto digitsEnd = basename.data() + (dot == std::string_view::npos ? basename.size() : dot);
			if (std::from_chars(basename.data() + 9, digitsEnd, responseNumber).ec != std::errc{} || responseNumber == 0) {
				return {};
			}

			const auto handler = RE::TESDataHandler::GetSingleton();
			if (!handler) {
				return {};
			}
			const auto info = handler->LookupForm<RE::TESTopicInfo>(localID, plugin);
			if (!info) {
				return {};
			}

			auto* source = info;
			for (int depth = 0; source && !source->responses.head && source->dataInfo && depth < 4; ++depth) {
				source = source->dataInfo;
			}
			if (!source) {
				return {};
			}
			std::uint32_t index = 0;
			for (auto response = source->responses.head; response; response = response->pNext) {
				if (++index == responseNumber) {
					const auto text = response->GetResponseText();
					return text ? std::string{ text } : std::string{};
				}
			}
			return {};
		}

		// OG only: runs immediately after DialogueResponse::ctor returns,
		// when responseText is finally populated.
		void CompleteDeferredLine(REX_RE::DialogueResponse* a_response)
		{
			if (!tls_deferred || tls_deferred->response != a_response) {
				tls_deferred.reset();
				return;
			}
			DeferredLine line = std::move(*tls_deferred);
			tls_deferred.reset();

			std::string text{ a_response->responseText.Get() };
			if (!HasMeaningfulText(text)) {
				text = LookupResponseTextFromForm(line.enginePath);
				if (Settings::VerboseLog() && HasMeaningfulText(text)) {
					logger::info("Recovered text for '{}' from form data: '{}'", line.enginePath, text);
				}
			}
			if (!HasMeaningfulText(text)) {
				// Nothing to synthesize (grunts, wordless radio filler):
				// put the engine's own path back so any vanilla audio still
				// plays instead of the provisional silence.
				a_response->voiceFilePath.Set(line.enginePath.c_str());
				if (Settings::VerboseLog()) {
					logger::info("No text for '{}'; restored the engine path", line.enginePath);
				}
				return;
			}

			FinishLine(a_response, line.enginePath, text, line.voiceType, line.isPlayer);
		}

		// jmp5 thunk: at the patched site rsi holds the DialogueResponse and
		// rdx still holds the path buffer (it was arg2 of the replaced call).
		// The register facts hold on every supported runtime: NG/AE were
		// byte-verified offline, and the OG resolver only accepts the site
		// when it matches "lea rcx,[rsi+18h]; call".
		struct Thunk : Xbyak::CodeGenerator
		{
			explicit Thunk(std::uintptr_t a_returnAddress)
			{
				// Volatile registers are already clobbered here from the
				// engine's perspective (the original instruction was itself
				// a call), so only the argument registers are preserved.
				push(rcx);
				push(rdx);
				push(r8);
				push(r9);
				sub(rsp, 0x20);

				mov(rcx, rsi);
				mov(rax, reinterpret_cast<std::uintptr_t>(&SwapVoicePath));
				call(rax);

				add(rsp, 0x20);
				pop(r9);
				pop(r8);
				pop(rdx);
				pop(rcx);

				jmp(ptr[rip]);
				dq(a_returnAddress);
			}
		};

		// OG only: replaces the unique "call DialogueResponse::ctor" so the
		// deferred line can be completed the moment the ctor returns (rax is
		// the finished DialogueResponse; the caller only relies on rax, and
		// volatile registers are dead across the original call anyway).
		// Entered by jmp5, so rsp and the ctor's stack argument are exactly
		// where the original call expected them.
		struct PostCtorThunk : Xbyak::CodeGenerator
		{
			PostCtorThunk(std::uintptr_t a_ctor, std::uintptr_t a_returnAddress)
			{
				mov(rax, a_ctor);
				call(rax);

				push(rax);
				push(rax);  // second push keeps 16-byte alignment
				sub(rsp, 0x20);

				mov(rcx, rax);
				mov(rax, reinterpret_cast<std::uintptr_t>(&CompleteDeferredLine));
				call(rax);

				add(rsp, 0x20);
				pop(rax);
				pop(rax);

				jmp(ptr[rip]);
				dq(a_returnAddress);
			}
		};
	}

	void Install()
	{
		if (!Engine::Resolve()) {
			logger::error("CustomVoicedDialogue is inactive: engine addresses could not be resolved for this game version");
			return;
		}

		const auto site = Engine::Get().setPathCallSite;
		Thunk code{ site + 0x5 };
		code.ready();

		auto& trampoline = REL::GetTrampoline();
		trampoline.write_jmp5(site, reinterpret_cast<std::uintptr_t>(trampoline.allocate(code)));

		// OG: complete deferred lines when the ctor returns (the resolver
		// stored the unique, still-unpatched E8 call site).
		const auto ctorCallSite = Engine::Get().ctorCallSite;
		if (ctorCallSite != 0) {
			PostCtorThunk postCode{ Engine::Get().dialogueResponseCtor, ctorCallSite + 0x5 };
			postCode.ready();
			trampoline.write_jmp5(ctorCallSite, reinterpret_cast<std::uintptr_t>(trampoline.allocate(postCode)));
			logger::info("Installed the post-ctor text hook at 0x{:X}", ctorCallSite);
		}

		g_installed.store(true, std::memory_order_release);
		logger::info("Installed the dialogue voice path hook at 0x{:X}", site);
	}

	bool Installed() noexcept
	{
		return g_installed.load(std::memory_order_acquire);
	}
}
