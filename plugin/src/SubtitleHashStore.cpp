#include "PCH.h"

#include "SubtitleHashStore.h"

namespace CustomVoicedDialogue::SubtitleHashStore
{
	namespace
	{
		std::mutex g_lock;
		std::unordered_set<std::uint64_t> g_store;

		[[nodiscard]] std::uint64_t Hash(const std::string_view a_string) noexcept
		{
			// djb2; collisions only cost an unnecessarily forced subtitle.
			std::uint64_t hash = 5381;
			for (const auto character : a_string) {
				hash = ((hash << 5) + hash) + static_cast<unsigned char>(character);
			}
			return hash;
		}
	}

	void Add(const std::string_view a_responseText)
	{
		if (a_responseText.empty()) {
			return;
		}
		const std::scoped_lock guard{ g_lock };
		g_store.insert(Hash(a_responseText));
	}

	bool Contains(const std::string_view a_responseText)
	{
		if (a_responseText.empty()) {
			return false;
		}
		const std::scoped_lock guard{ g_lock };
		return g_store.contains(Hash(a_responseText));
	}

	void StartPurgeThread()
	{
		std::thread([] {
			using namespace std::chrono_literals;
			while (true) {
				std::this_thread::sleep_for(60s);
				std::size_t purged = 0;
				{
					const std::scoped_lock guard{ g_lock };
					purged = g_store.size();
					g_store.clear();
				}
				if (purged > 0) {
					logger::info("Purged {} entries from the forced-subtitle store", purged);
				}
			}
		}).detach();
	}
}
