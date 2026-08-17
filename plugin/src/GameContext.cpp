#include "PCH.h"

#include "GameContext.h"

namespace CustomVoicedDialogue::GameContext
{
	namespace
	{
		// A whole dialogue wheel is queued in one go; the scene cannot
		// meaningfully change between those lines, so one sample serves them
		// all.  Game-thread-only use, so no lock is needed.
		constexpr auto kMemoTtl = std::chrono::milliseconds{ 250 };
		std::string g_memo;
		std::chrono::steady_clock::time_point g_memoTaken{};
		bool g_memoValid{ false };

		[[nodiscard]] RE::Actor* ResolveListener()
		{
			const auto topics = RE::MenuTopicManager::GetSingleton();
			if (!topics) {
				return nullptr;
			}
			const auto reference = topics->speaker.get();
			return reference ? reference.get()->As<RE::Actor>() : nullptr;
		}
	}

	Snapshot Capture(RE::Actor* a_listener)
	{
		Snapshot snapshot{};

		const auto player = RE::PlayerCharacter::GetSingleton();
		if (!player) {
			return snapshot;
		}

		snapshot.inCombat = player->IsInCombat();
		snapshot.sneaking = player->IsSneaking();

		auto* listener = a_listener ? a_listener : ResolveListener();
		if (listener && listener != player) {
			snapshot.listenerHostile = listener->GetHostileToActor(player);
		}
		return snapshot;
	}

	std::string Describe(const Snapshot& a_snapshot)
	{
		std::vector<std::string_view> parts;
		parts.reserve(3);

		if (a_snapshot.inCombat) {
			parts.emplace_back("in combat");
		}
		if (a_snapshot.sneaking) {
			parts.emplace_back("sneaking, staying quiet");
		}
		if (a_snapshot.listenerHostile) {
			parts.emplace_back("the listener is hostile to them");
		}

		if (parts.empty()) {
			return {};
		}

		std::string description;
		for (std::size_t i = 0; i < parts.size(); ++i) {
			if (i != 0) {
				description += "; ";
			}
			description += parts[i];
		}
		return description;
	}

	std::string Current(RE::Actor* a_listener)
	{
		const auto now = std::chrono::steady_clock::now();
		if (g_memoValid && now - g_memoTaken < kMemoTtl) {
			return g_memo;
		}
		g_memo = Describe(Capture(a_listener));
		g_memoTaken = now;
		g_memoValid = true;
		return g_memo;
	}
}
