#include "PCH.h"

#include "Prefetch.h"

#include "Engine.h"
#include "Settings.h"
#include "SynthQueue.h"
#include "VoicePath.h"

namespace CustomVoicedDialogue::Prefetch
{
	namespace
	{
		// The engine routine that builds a line's voice file path.  Using
		// the engine's own builder guarantees menu-time paths match the
		// paths the dialogue hook sees at selection time.
		// Resolved lazily; prefetch degrades to a no-op when unavailable.
		using BuildVoicePath_t = void (*)(RE::TESResponse*, char*, RE::TESForm*, RE::TESTopic*, RE::TESTopicInfo*);

		[[nodiscard]] BuildVoicePath_t ResolveBuildVoicePath()
		{
			return reinterpret_cast<BuildVoicePath_t>(Engine::Get().buildVoicePath);
		}

		// OG's BuildVoicePath takes its arguments differently (calling it
		// with the NG signature yields a correct directory but a garbage
		// basename), so on OG the path is assembled by hand instead.  The
		// format is verified against this plugin's own hook corpus:
		// Data\Sound\Voice\<owning plugin>\<voicetype EDID>\<%08X local id>_<response #>.wav
		[[nodiscard]] std::string BuildPathManually(
			RE::BGSVoiceType* a_voiceType,
			RE::TESTopicInfo* a_topicInfo,
			std::uint32_t a_responseIndex)
		{
			const auto file = a_topicInfo->GetFile(0);
			const char* editorID = a_voiceType->GetFormEditorID();
			if (!file || !editorID || !*editorID) {
				return {};
			}
			return std::format(
				"Data\\Sound\\Voice\\{}\\{}\\{:08X}_{}.wav",
				file->filename,
				editorID,
				a_topicInfo->GetLocalFormID(),
				a_responseIndex);
		}

		[[nodiscard]] RE::BGSVoiceType* GetPlayerVoiceType()
		{
			const auto player = RE::PlayerCharacter::GetSingleton();
			if (!player) {
				return nullptr;
			}
			const auto npc = player->GetNPC();
			return npc ? npc->voiceType : nullptr;
		}

		// Per-scan tallies for the verbose diagnostic summary.
		struct ScanStats
		{
			std::uint32_t infosSeen{ 0 };
			std::uint32_t enqueued{ 0 };
			std::uint32_t hadAudio{ 0 };
			std::uint32_t noResponses{ 0 };
			std::uint32_t notPlayerTopic{ 0 };
		};

		// Queues synthesis for every response of one player dialogue option.
		void PrefetchTopicInfo(
			const BuildVoicePath_t a_buildVoicePath,
			RE::BGSVoiceType* a_playerVoiceType,
			RE::TESTopic* a_topic,
			RE::TESTopicInfo* a_topicInfo,
			ScanStats& a_stats)
		{
			if (!a_topicInfo) {
				return;
			}
			++a_stats.infosSeen;

			// Only true player options.  On OG, GetCurrentTopicInfo can hand
			// back the scene's currently active INFO — including NPC response
			// lines ("Right away, boss.") — which must never be synthesized
			// in the player's voice.  A real player option always belongs to
			// the player-dialogue action's own topic.
			if (!a_topic || a_topicInfo->parentTopic != a_topic) {
				++a_stats.notPlayerTopic;
				return;
			}

			// Shared response data: the generic short replies ("Maybe
			// later.", "'Scuse me.") keep their response chain on a donor
			// INFO reached through dataInfo (XDI's "sharedInfo").  The
			// engine speaks the DONOR — its DialogueResponse path uses the
			// donor's plugin and form id (verified in the hook log: picking
			// DLCNukaWorld 0001F97E played Fallout4.esm 0001F5FF's path) —
			// so both the response chain and the voice-path identity must
			// come from the walked donor, or prefetched audio lands on
			// paths the engine never asks for.
			auto* source = a_topicInfo;
			for (int depth = 0; source->dataInfo && depth < 4; ++depth) {
				source = source->dataInfo;
			}
			if (!source->responses.head) {
				++a_stats.noResponses;
				return;
			}

			// Walk the response chain (usually a single line).
			std::uint32_t responseIndex = 0;
			for (auto response = source->responses.head; response; response = response->pNext) {
				++responseIndex;
				const auto text = response->GetResponseText();
				if (!text || !*text) {
					continue;
				}

				// MAX_PATH-sized buffer, mirroring the engine's own ctor.
				// Identity comes from the donor (see the walk above).
				std::array<char, 0x104> buffer{};
				std::string manualPath;
				if (a_buildVoicePath) {
					a_buildVoicePath(response, buffer.data(), a_playerVoiceType, source->parentTopic, source);
				} else {
					manualPath = BuildPathManually(a_playerVoiceType, source, responseIndex);
					if (manualPath.size() < buffer.size()) {
						std::memcpy(buffer.data(), manualPath.c_str(), manualPath.size() + 1);
					}
				}
				if (buffer[0] == '\0') {
					continue;
				}

				const std::string_view enginePath{ buffer.data() };
				const auto voiceType = VoicePath::ExtractVoiceType(enginePath);

				// Skip options that already have audio.  In replace mode
				// only a .wav counts, mirroring the hook's decision.
				const bool hasAudio = Settings::ReplaceVoicedPlayerLines()
					? VoicePath::GeneratedWavExists(buffer.data())
					: VoicePath::VoiceAssetExists(buffer.data());
				if (hasAudio) {
					++a_stats.hadAudio;
					continue;
				}

				if (Settings::VerboseLog()) {
					logger::info("Prefetching dialogue option: path='{}', text='{}'", enginePath, text);
				}

				++a_stats.enqueued;
				SynthQueue::Enqueue({
					.voicePath = std::string{ VoicePath::StripDataPrefix(enginePath) },
					.text = std::string{ text },
					.voiceType = std::string{ voiceType },
					.isPlayer = true,
				});
			}
		}

		// Queues every player option a dialogue action can offer.
		void PrefetchAction(
			const BuildVoicePath_t a_buildVoicePath,
			RE::BGSVoiceType* a_playerVoiceType,
			RE::BGSSceneActionPlayerDialogue* a_action,
			ScanStats& a_stats)
		{
			// The four wheel topics (Positive/Negative/Neutral/Question) live
			// on the conversation base as responseTopics.  NOT pTopic: the
			// engine only fills that in with the SELECTED topic after a
			// choice is made, so reading it meant options never prefetched
			// until they were picked.  (Enumeration per XDI's
			// BuildDialogueMap, which drives the visible wheel.)
			for (const auto topic : a_action->responseTopics) {
				if (!topic || !topic->topicInfos) {
					continue;
				}
				const auto count = std::min<std::uint32_t>(topic->numTopicInfos, 128);
				for (std::uint32_t index = 0; index < count; ++index) {
					if (topic->topicInfos[index]) {
						PrefetchTopicInfo(a_buildVoicePath, a_playerVoiceType, topic, topic->topicInfos[index], a_stats);
					}
				}
			}
		}

		// Queues the dialogue an actor can offer, before any conversation
		// starts.  The actor's own alias-instance list says which quests it
		// is cast in and as which alias, so only that actor's scenes are
		// walked — a bounded, targeted scan instead of sweeping every quest
		// in the load order.
		void PrefetchForActor(RE::Actor* a_actor)
		{
			if (!a_actor || !Settings::EnablePrefetch() || !Settings::EnablePlayerLines()) {
				return;
			}
			const auto playerVoiceType = GetPlayerVoiceType();
			if (!playerVoiceType) {
				return;
			}
			auto buildVoicePath = ResolveBuildVoicePath();
			if (Engine::Get().ctorCallSite != 0) {
				buildVoicePath = nullptr;  // OG: paths are assembled by hand
			} else if (!buildVoicePath) {
				return;
			}

			if (!a_actor->extraList) {
				return;
			}
			const auto aliasInstances = a_actor->extraList->GetByType<RE::ExtraAliasInstanceArray>();
			if (!aliasInstances) {
				return;
			}

			// Snapshot (quest, aliasID) under the array's own lock; the scene
			// walk below must not hold it.
			std::vector<std::pair<RE::TESQuest*, std::uint32_t>> roles;
			{
				const RE::BSAutoReadLock lock{ aliasInstances->aliasArrayLock };
				for (const auto& instance : aliasInstances->aliasArray) {
					if (instance.quest && instance.alias) {
						roles.emplace_back(instance.quest, instance.alias->aliasID);
					}
				}
			}
			if (roles.empty()) {
				return;
			}

			ScanStats stats;
			for (const auto& [quest, aliasID] : roles) {
				for (const auto scene : quest->scenes) {
					if (!scene) {
						continue;
					}
					for (const auto action : scene->actions) {
						if (!action || action->GetActionType() != RE::SCENE_ACTION_TYPE::kPlayerDialogue) {
							continue;
						}
						const auto playerDialogue = static_cast<RE::BGSSceneActionPlayerDialogue*>(action);
						// The action speaks to one alias; only prefetch the
						// scenes this actor is actually cast in.
						if (static_cast<std::uint32_t>(playerDialogue->dialogueTarget) != aliasID) {
							continue;
						}
						PrefetchAction(buildVoicePath, playerVoiceType, playerDialogue, stats);
					}
				}
			}

			if (stats.enqueued > 0) {
				logger::info(
					"Look-ahead prefetch for '{}': {} line(s) queued from {} quest role(s)",
					a_actor->GetDisplayFullName(),
					stats.enqueued,
					roles.size());
			}
		}

		// Queues the current options and the whole conversation tree of every
		// player-dialogue action in the active scene, so lines are generated
		// as soon as the conversation starts — usually well before the
		// exchange that offers them.  SynthQueue dedupes, so re-running this
		// while the menu stays open is cheap.
		void PrefetchCurrentOptions()
		{
			if (!Settings::EnablePrefetch() || !Settings::EnablePlayerLines()) {
				return;
			}

			// On OG the engine builder is not used (wrong argument mapping);
			// the manual builder inside PrefetchTopicInfo takes over there.
			auto buildVoicePath = ResolveBuildVoicePath();
			const bool manualPaths = Engine::Get().ctorCallSite != 0;
			if (manualPaths) {
				buildVoicePath = nullptr;
			} else if (!buildVoicePath) {
				return;
			}

			const auto player = RE::PlayerCharacter::GetSingleton();
			if (!player) {
				return;
			}

			const auto scene = player->GetCurrentScene();
			if (!scene) {
				return;
			}

			const auto playerVoiceType = GetPlayerVoiceType();
			if (!playerVoiceType) {
				return;
			}

			const auto target = scene->targetRef.get();

			ScanStats stats;
			for (const auto action : scene->actions) {
				if (!action || action->GetActionType() != RE::SCENE_ACTION_TYPE::kPlayerDialogue) {
					continue;
				}

				PrefetchAction(buildVoicePath, playerVoiceType, static_cast<RE::BGSSceneActionPlayerDialogue*>(action), stats);
			}

			// One summary per change, not per 1.5 s rescan tick.
			static ScanStats lastLogged;
			if (Settings::VerboseLog() &&
				(stats.enqueued > 0 || stats.infosSeen != lastLogged.infosSeen ||
					stats.noResponses != lastLogged.noResponses || stats.notPlayerTopic != lastLogged.notPlayerTopic)) {
				lastLogged = stats;
				logger::info(
					"Prefetch scan: {} info(s) seen, {} enqueued, {} already had audio, {} without responses, {} skipped as non-player",
					stats.infosSeen,
					stats.enqueued,
					stats.hadAudio,
					stats.noResponses,
					stats.notPlayerTopic);
			}
		}

		std::atomic<bool> g_menuOpen{ false };

		// ---- look-ahead prefetch -------------------------------------------
		// Talking to someone is nearly always preceded by looking at them, so
		// the conversation's lines can start generating before the wheel even
		// opens.  The crosshair target is derived here rather than read from
		// the HUD: the nearest living, non-hostile actor within talking range
		// that the player is facing and has line of sight to.

		constexpr float kLookRangeUnits = 600.0f;      // a little past talking range
		constexpr float kLookConeCosine = 0.94f;       // ~20 degrees off-centre

		[[nodiscard]] RE::Actor* FindLookedAtActor()
		{
			const auto player = RE::PlayerCharacter::GetSingleton();
			const auto processLists = RE::ProcessLists::GetSingleton();
			if (!player || !processLists || player->GetCurrentScene()) {
				return nullptr;  // already conversing: the scene scan owns it
			}

			const auto origin = player->GetPosition();
			// Z angle is the compass heading; 0 faces +Y and turns clockwise.
			const auto heading = player->data.angle.z;
			const RE::NiPoint3 facing{ std::sin(heading), std::cos(heading), 0.0f };

			RE::Actor* best = nullptr;
			float bestDistance = kLookRangeUnits;
			for (const auto& handle : processLists->highActorHandles) {
				const auto actor = handle.get();
				if (!actor || actor.get() == player) {
					continue;
				}
				auto* candidate = actor.get();
				if (candidate->IsDead(true) || candidate->GetHostileToActor(player)) {
					continue;
				}

				auto delta = candidate->GetPosition() - origin;
				delta.z = 0.0f;
				const auto distance = delta.Length();
				if (distance < 1.0f || distance > bestDistance) {
					continue;
				}
				if ((delta.x * facing.x + delta.y * facing.y) / distance < kLookConeCosine) {
					continue;
				}
				bool pickPerformed = false;
				if (!player->HasLOSToTarget(candidate, &pickPerformed)) {
					continue;
				}
				best = candidate;
				bestDistance = distance;
			}
			return best;
		}

		// Runs on the game thread: tracks how long the same actor has been
		// looked at and prefetches once the dwell threshold is met.  Each
		// actor is prefetched once per session — the work is idempotent, but
		// re-walking their scenes every few seconds would be waste.
		void TickLookPrefetch()
		{
			static std::uint32_t current = 0;
			static std::chrono::steady_clock::time_point since;
			static std::unordered_set<std::uint32_t> alreadyPrefetched;

			const auto dwellMs = Settings::LookPrefetchMs();
			if (dwellMs == 0) {
				return;
			}

			auto* actor = FindLookedAtActor();
			if (!actor) {
				current = 0;
				return;
			}

			const auto formID = actor->GetFormID();
			const auto now = std::chrono::steady_clock::now();
			if (formID != current) {
				current = formID;
				since = now;
				return;
			}
			if (now - since < std::chrono::milliseconds(dwellMs)) {
				return;
			}
			if (!alreadyPrefetched.insert(actor->GetFormID()).second) {
				return;
			}
			PrefetchForActor(actor);
		}

		// New options appear as a conversation advances, but the menu-open
		// event fires only once — rescan on the game thread while the menu
		// stays open so later exchanges prefetch too.  The same tick drives
		// look-ahead prefetch while no conversation is running.
		void StartRescanThread()
		{
			static std::atomic<bool> started{ false };
			if (started.exchange(true)) {
				return;
			}
			std::thread([]() {
				for (;;) {
					std::this_thread::sleep_for(std::chrono::milliseconds(500));
					const auto tasks = F4SE::GetTaskInterface();
					if (!tasks) {
						continue;
					}
					if (g_menuOpen.load(std::memory_order_relaxed)) {
						tasks->AddTask([]() { PrefetchCurrentOptions(); });
					} else {
						tasks->AddTask([]() { TickLookPrefetch(); });
					}
				}
			}).detach();
		}

		class MenuWatcher final : public RE::BSTEventSink<RE::MenuOpenCloseEvent>
		{
		public:
			static MenuWatcher* GetSingleton()
			{
				static MenuWatcher instance;
				return std::addressof(instance);
			}

			RE::BSEventNotifyControl ProcessEvent(const RE::MenuOpenCloseEvent& a_event, RE::BSTEventSource<RE::MenuOpenCloseEvent>*) override
			{
				if (a_event.menuName != "DialogueMenu") {
					return RE::BSEventNotifyControl::kContinue;
				}

				SynthQueue::SetDialogueMenuOpen(a_event.opening);
				g_menuOpen.store(a_event.opening, std::memory_order_relaxed);

				if (a_event.opening) {
					// The scene's option set is stable by the time the menu
					// opens; enumerate on this thread and hand the network
					// work to the synthesis worker.  The rescan thread keeps
					// this fresh while the conversation advances.
					PrefetchCurrentOptions();
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};
	}

	void Register()
	{
		if (const auto ui = RE::UI::GetSingleton()) {
			ui->RegisterSink(MenuWatcher::GetSingleton());
			StartRescanThread();
			logger::info("Registered the dialogue menu prefetch watcher");
		} else {
			logger::warn("UI singleton unavailable; dialogue prefetch is disabled");
		}
	}
}
