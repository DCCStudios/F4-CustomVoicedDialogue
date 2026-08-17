#include "PCH.h"

#include "SynthQueue.h"

#include "JsonWriter.h"
#include "Settings.h"
#include "ShadowPlayback.h"
#include "TtsClient.h"
#include "VoiceManifest.h"

namespace CustomVoicedDialogue::SynthQueue
{
	namespace
	{
		using Clock = std::chrono::steady_clock;

		struct PendingJob
		{
			Job job;
			Clock::time_point nextPoll{};
			std::uint32_t polls{ 0 };
			// True while the poll thread has an HTTP request in the air for
			// this job (never double-polled).
			bool inFlight{ false };
		};

		constexpr std::size_t kMaxTrackedJobs = 256;
		constexpr std::uint32_t kMaxPollsPerJob = 240;  // ~2 min at menu cadence

		std::mutex g_lock;
		std::condition_variable g_wake;
		std::condition_variable g_pollWake;
		std::deque<Job> g_incoming;
		std::unordered_set<std::string> g_tracked;  // voicePaths queued or pending
		std::vector<PendingJob> g_pending;          // guarded by g_lock
		std::atomic<bool> g_menuOpen{ false };
		std::atomic<bool> g_serverReachable{ false };
		std::atomic<bool> g_started{ false };
		std::atomic<std::uint32_t> g_batchTotal{ 0 };
		std::atomic<std::uint32_t> g_batchDone{ 0 };
		std::filesystem::path g_gameRoot;

		[[nodiscard]] std::filesystem::path ResolveGameRoot()
		{
			// The game root is where Fallout4.exe lives; file writes go to
			// <root>\Data\... from inside the game process so mod-manager
			// virtual file systems apply to them.
			std::array<wchar_t, 32768> buffer{};
			const auto length = ::GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
			if (length == 0 || length >= buffer.size()) {
				logger::error("Could not resolve the game executable path; generated audio cannot be written");
				return {};
			}
			return std::filesystem::path{ std::wstring_view{ buffer.data(), length } }.parent_path();
		}

		// URL-encodes the voice path for use in a query string.
		[[nodiscard]] std::string UrlEncode(const std::string_view a_value)
		{
			static constexpr char kHex[] = "0123456789ABCDEF";
			std::string result;
			result.reserve(a_value.size() * 3);
			for (const char character : a_value) {
				const auto byte = static_cast<unsigned char>(character);
				const bool unreserved =
					(byte >= 'A' && byte <= 'Z') || (byte >= 'a' && byte <= 'z') ||
					(byte >= '0' && byte <= '9') || byte == '-' || byte == '_' || byte == '.' || byte == '~';
				if (unreserved) {
					result.push_back(character);
				} else {
					result.push_back('%');
					result.push_back(kHex[byte >> 4]);
					result.push_back(kHex[byte & 0xF]);
				}
			}
			return result;
		}

		[[nodiscard]] std::wstring Widen(const std::string_view a_value)
		{
			// Query strings this plugin builds are ASCII (URL-encoded).
			return std::wstring{ a_value.begin(), a_value.end() };
		}

		// Applies the configured TTS volume to 16-bit PCM wav bytes in place
		// (saturating).  Non-PCM or malformed data is left untouched.
		void ApplyTtsVolume(std::vector<std::uint8_t>& a_bytes)
		{
			const auto percent = Settings::TtsVolumePercent();
			if (percent == 100 || a_bytes.size() < 44 ||
				std::memcmp(a_bytes.data(), "RIFF", 4) != 0 || std::memcmp(a_bytes.data() + 8, "WAVE", 4) != 0) {
				return;
			}

			// Walk the RIFF chunks; require plain 16-bit PCM before scaling.
			bool pcm16 = false;
			std::size_t offset = 12;
			while (offset + 8 <= a_bytes.size()) {
				const auto chunkSize = *reinterpret_cast<const std::uint32_t*>(a_bytes.data() + offset + 4);
				const auto dataStart = offset + 8;
				if (std::memcmp(a_bytes.data() + offset, "fmt ", 4) == 0 && dataStart + 16 <= a_bytes.size()) {
					const auto format = *reinterpret_cast<const std::uint16_t*>(a_bytes.data() + dataStart);
					const auto bits = *reinterpret_cast<const std::uint16_t*>(a_bytes.data() + dataStart + 14);
					pcm16 = format == 1 && bits == 16;
				} else if (std::memcmp(a_bytes.data() + offset, "data", 4) == 0 && pcm16) {
					const auto sampleCount = std::min<std::size_t>(chunkSize, a_bytes.size() - dataStart) / 2;
					auto* samples = reinterpret_cast<std::int16_t*>(a_bytes.data() + dataStart);
					const auto gain = static_cast<std::int32_t>(percent);
					for (std::size_t i = 0; i < sampleCount; ++i) {
						const auto scaled = static_cast<std::int32_t>(samples[i]) * gain / 100;
						samples[i] = static_cast<std::int16_t>(std::clamp<std::int32_t>(scaled, -32768, 32767));
					}
					return;
				}
				offset = dataStart + chunkSize + (chunkSize & 1);
			}
		}

		// Reads the audio duration from 16-bit PCM wav bytes (0 on failure).
		[[nodiscard]] float WavDurationSeconds(const std::vector<std::uint8_t>& a_bytes)
		{
			if (a_bytes.size() < 44 ||
				std::memcmp(a_bytes.data(), "RIFF", 4) != 0 || std::memcmp(a_bytes.data() + 8, "WAVE", 4) != 0) {
				return 0.0f;
			}
			std::uint32_t byteRate = 0;
			std::size_t offset = 12;
			while (offset + 8 <= a_bytes.size()) {
				const auto chunkSize = *reinterpret_cast<const std::uint32_t*>(a_bytes.data() + offset + 4);
				const auto dataStart = offset + 8;
				if (std::memcmp(a_bytes.data() + offset, "fmt ", 4) == 0 && dataStart + 16 <= a_bytes.size()) {
					byteRate = *reinterpret_cast<const std::uint32_t*>(a_bytes.data() + dataStart + 8);
				} else if (std::memcmp(a_bytes.data() + offset, "data", 4) == 0 && byteRate != 0) {
					const auto dataSize = std::min<std::size_t>(chunkSize, a_bytes.size() - dataStart);
					return static_cast<float>(dataSize) / static_cast<float>(byteRate);
				}
				offset = dataStart + chunkSize + (chunkSize & 1);
			}
			return 0.0f;
		}

		// Writes wav bytes atomically at Data\<voicePath>: temp file first,
		// then rename, so the engine can never observe a half-written file.
		[[nodiscard]] bool WriteVoiceFile(const Job& a_job, std::vector<std::uint8_t> a_bytes)
		{
			const auto& a_voicePath = a_job.voicePath;
			if (g_gameRoot.empty() || a_bytes.empty()) {
				return false;
			}

			ApplyTtsVolume(a_bytes);

			const auto target = g_gameRoot / "Data" / a_voicePath;
			std::error_code ec;
			std::filesystem::create_directories(target.parent_path(), ec);
			if (ec) {
				logger::error("Could not create directories for '{}': {}", target.string(), ec.message());
				return false;
			}

			const auto temp = target.wstring() + L".cvdtmp";
			{
				HANDLE file = ::CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
				if (file == INVALID_HANDLE_VALUE) {
					logger::error("Could not create temp voice file '{}' (error {})", target.string(), ::GetLastError());
					return false;
				}
				DWORD written = 0;
				const auto ok = ::WriteFile(file, a_bytes.data(), static_cast<DWORD>(a_bytes.size()), &written, nullptr);
				::CloseHandle(file);
				if (!ok || written != a_bytes.size()) {
					::DeleteFileW(temp.c_str());
					logger::error("Short write for voice file '{}'", target.string());
					return false;
				}
			}

			if (!::MoveFileExW(temp.c_str(), target.wstring().c_str(), MOVEFILE_REPLACE_EXISTING)) {
				logger::error("Could not move voice file into place at '{}' (error {})", target.string(), ::GetLastError());
				::DeleteFileW(temp.c_str());
				return false;
			}

			logger::info("Wrote TTS audio ({} bytes) to '{}'", a_bytes.size(), target.string());

			const auto duration = WavDurationSeconds(a_bytes);

			// The engine's voice layer may not serve files created after the
			// session started; the hook plays these directly until the next
			// launch makes them native.
			ShadowPlayback::NoteSessionWrite(a_voicePath, duration);

			// Remember the file so a later voice change can invalidate it.
			VoiceManifest::RecordWrite(a_voicePath, a_job.isPlayer);

			// The line is still playing its silence right now: the hook
			// padded it with the wait-for-voice budget, so play the audio as
			// long as it still fits inside the line (small overhang allowed)
			// rather than wasting the encounter.  Scheduled on the game
			// thread — the audio manager is not called from this worker.
			if (a_job.playOnArrival) {
				const auto elapsed = std::chrono::duration<float>(std::chrono::steady_clock::now() - a_job.enqueuedAt).count();
				if (elapsed + duration <= a_job.silenceSeconds + 0.75f) {
					const std::string voicePath = a_voicePath;
					if (const auto tasks = F4SE::GetTaskInterface()) {
						tasks->AddTask([voicePath]() { ShadowPlayback::Play(voicePath); });
					}
				}
			}
			return true;
		}

		[[nodiscard]] std::string BuildSynthBody(const Job& a_job)
		{
			// Prefetch jobs submit without the server's inline-completion
			// wait, so a whole dialogue wheel is queued in milliseconds; a
			// picked line keeps it for a chance at an instant 200.
			return std::format(
				R"({{"text":"{}","voicePath":"{}","voiceType":"{}","context":"{}","isPlayer":{},"wait":{}}})",
				Json::Escape(a_job.text),
				Json::Escape(a_job.voicePath),
				Json::Escape(a_job.voiceType),
				Json::Escape(a_job.context),
				a_job.isPlayer ? "true" : "false",
				a_job.playOnArrival ? "true" : "false");
		}

		void Untrack(const std::string& a_voicePath)
		{
			{
				const std::scoped_lock guard{ g_lock };
				g_tracked.erase(a_voicePath);
			}
			// A tracked job reached a terminal state (written, rejected, or
			// given up) — that is one unit of batch progress either way.
			if (g_batchDone.load(std::memory_order_relaxed) < g_batchTotal.load(std::memory_order_relaxed)) {
				g_batchDone.fetch_add(1, std::memory_order_relaxed);
			}
		}

		// Sends the initial synth request.  Returns true when the job is
		// finished (successfully or permanently) and false when it should be
		// polled later.
		[[nodiscard]] bool RequestSynth(const Job& a_job)
		{
			const auto response = TtsClient::PostJson(L"/api/synth", BuildSynthBody(a_job));
			if (response.status == 0) {
				g_serverReachable.store(false, std::memory_order_release);
				return false;  // retried after the server retry window
			}

			g_serverReachable.store(true, std::memory_order_release);
			switch (response.status) {
			case 200:
				if (!WriteVoiceFile(a_job, response.body)) {
					logger::warn("Synthesized audio for '{}' could not be written; the line keeps its silence fallback", a_job.voicePath);
				}
				Untrack(a_job.voicePath);
				return true;
			case 202:
				return false;  // queued server-side; poll /api/result
			default:
				logger::warn("Server rejected synthesis for '{}' (HTTP {})", a_job.voicePath, response.status);
				Untrack(a_job.voicePath);
				return true;
			}
		}

		// Polls one pending job.  Returns true when it is finished.
		[[nodiscard]] bool PollResult(const Job& a_job)
		{
			// A picked line long-polls: the server holds the request until
			// the audio is ready (or the window closes), so it arrives with
			// near-zero extra latency.  Prefetch jobs poll normally — a held
			// request per queued line would serialize the worker.
			const auto path = std::format(
				"/api/result?voicePath={}{}",
				UrlEncode(a_job.voicePath),
				a_job.playOnArrival ? "&waitMs=1500" : "");
			const auto response = TtsClient::Get(Widen(path));
			if (response.status == 0) {
				g_serverReachable.store(false, std::memory_order_release);
				return false;
			}

			g_serverReachable.store(true, std::memory_order_release);
			switch (response.status) {
			case 200:
				if (!WriteVoiceFile(a_job, response.body)) {
					logger::warn("Synthesized audio for '{}' could not be written; the line keeps its silence fallback", a_job.voicePath);
				}
				Untrack(a_job.voicePath);
				return true;
			case 202:
				return false;
			default:
				logger::warn("Synthesis failed for '{}' (HTTP {}); the line keeps its silence fallback", a_job.voicePath, response.status);
				Untrack(a_job.voicePath);
				return true;
			}
		}

		// Pulls a plain string value out of a flat JSON body ("" when the
		// key is absent).  The status fingerprints are hex, so no unescaping
		// is ever needed.
		[[nodiscard]] std::string ExtractJsonString(const std::vector<std::uint8_t>& a_body, const std::string_view a_key)
		{
			const std::string_view body{ reinterpret_cast<const char*>(a_body.data()), a_body.size() };
			const auto marker = std::format("\"{}\":\"", a_key);
			const auto start = body.find(marker);
			if (start == std::string_view::npos) {
				return {};
			}
			const auto valueStart = start + marker.size();
			const auto valueEnd = body.find('"', valueStart);
			if (valueEnd == std::string_view::npos) {
				return {};
			}
			return std::string{ body.substr(valueStart, valueEnd - valueStart) };
		}

		// Pulls a flat JSON array of strings ("invalidated":["a","b"]).  The
		// values are engine voice paths — backslashes arrive escaped as \\,
		// which is the only unescaping these ever need.
		[[nodiscard]] std::vector<std::string> ExtractJsonStringArray(
			const std::vector<std::uint8_t>& a_body,
			const std::string_view a_key)
		{
			const std::string_view body{ reinterpret_cast<const char*>(a_body.data()), a_body.size() };
			const auto marker = std::format("\"{}\":[", a_key);
			const auto start = body.find(marker);
			if (start == std::string_view::npos) {
				return {};
			}
			const auto arrayEnd = body.find(']', start);
			if (arrayEnd == std::string_view::npos) {
				return {};
			}

			std::vector<std::string> values;
			auto cursor = start + marker.size();
			while (cursor < arrayEnd) {
				const auto valueStart = body.find('"', cursor);
				if (valueStart == std::string_view::npos || valueStart > arrayEnd) {
					break;
				}
				std::string value;
				auto index = valueStart + 1;
				for (; index < arrayEnd && body[index] != '"'; ++index) {
					if (body[index] == '\\' && index + 1 < arrayEnd) {
						++index;
					}
					value.push_back(body[index]);
				}
				if (!value.empty()) {
					values.push_back(std::move(value));
				}
				cursor = index + 1;
			}
			return values;
		}

		// How often a reachable server is re-checked, so changing the voice
		// in the app takes effect within seconds instead of at next launch.
		constexpr int kStatusPollSeconds = 5;

		// Fetches /api/status: refreshes reachability, hands the server's
		// voice fingerprints to the manifest (which deletes this plugin's
		// stale generated files when the app's voice configuration changed),
		// and drops any line the app has generated a new take of.  The game
		// root rides along so the app can tell whether generated audio still
		// exists on disk — only this side knows the real path, because a mod
		// manager's virtual file system resolves it.
		void CheckServerStatus()
		{
			const auto path = std::format("/api/status?gameRoot={}", UrlEncode(g_gameRoot.string()));
			const auto response = TtsClient::Get(Widen(path));
			g_serverReachable.store(response.status != 0, std::memory_order_release);
			if (response.status != 200) {
				return;
			}
			VoiceManifest::ApplyServerFingerprints(
				ExtractJsonString(response.body, "voiceFingerprint"),
				ExtractJsonString(response.body, "npcVoiceFingerprint"));
			for (const auto& voicePath : ExtractJsonStringArray(response.body, "invalidated")) {
				VoiceManifest::Invalidate(voicePath);
			}
		}

		// Submits new jobs and keeps server status fresh.  Never blocks on
		// result polling — that runs on its own thread so a held long-poll
		// can never delay prefetch submissions (the server generates every
		// submitted line concurrently).
		void SubmitLoop()
		{
			using namespace std::chrono_literals;

			logger::info("Synthesis worker started (server {}:{})", Settings::ServerHost(), Settings::ServerPort());

			// Initial reachability probe so the first dialogue line does not
			// pay the connection timeout; also applies the voice fingerprints.
			CheckServerStatus();
			if (g_serverReachable.load(std::memory_order_acquire)) {
				logger::info("CustomVoicedDialogue server is reachable");
			} else {
				logger::warn(
					"CustomVoicedDialogue server is not reachable at {}:{}; unvoiced lines fall back to silence until it starts",
					Settings::ServerHost(),
					Settings::ServerPort());
			}

			auto lastServerAttempt = Clock::now();
			auto lastStatusCheck = Clock::now();
			// Work held back while the server is unreachable; kept local to
			// the worker so the wake condition stays "new work arrived".
			std::deque<Job> deferred;
			while (true) {
				std::deque<Job> incoming;
				{
					std::unique_lock lock{ g_lock };
					const auto pollMs = g_menuOpen.load(std::memory_order_acquire) ? Settings::MenuPollMs() : Settings::IdlePollMs();
					g_wake.wait_for(lock, std::chrono::milliseconds(pollMs), [] { return !g_incoming.empty(); });
					incoming.swap(g_incoming);
				}

				const auto now = Clock::now();

				// Periodic status ping: keeps reachability honest even with
				// no queued work and notices mid-session voice changes in the
				// app (which invalidate this plugin's stale files).  While the
				// server is up this is a sub-millisecond loopback call, so it
				// runs on its own short interval — the retry window exists to
				// space out timing-out requests to a server that is DOWN, and
				// using it here made a voice change take up to that long to
				// take effect.
				const auto statusInterval = g_serverReachable.load(std::memory_order_acquire)
					? std::chrono::seconds(kStatusPollSeconds)
					: std::chrono::seconds(Settings::ServerRetrySeconds());
				if (now - lastStatusCheck >= statusInterval) {
					lastStatusCheck = now;
					CheckServerStatus();
				}

				// While the server is down, retry no more often than the
				// configured window so a missing companion app costs one
				// timed-out request per window instead of one per line.
				if (!g_serverReachable.load(std::memory_order_acquire)) {
					if (now - lastServerAttempt < std::chrono::seconds(Settings::ServerRetrySeconds())) {
						for (auto& job : incoming) {
							deferred.push_back(std::move(job));
						}
						continue;
					}
					lastServerAttempt = now;
				} else {
					lastServerAttempt = now;
				}

				// The retry window elapsed (or the server is up): fold any
				// deferred work back in, oldest first.
				while (!deferred.empty()) {
					incoming.push_front(std::move(deferred.back()));
					deferred.pop_back();
				}

				for (auto& job : incoming) {
					if (!RequestSynth(job)) {
						PendingJob pending{ .job = std::move(job) };
						// A picked line starts its (long-)poll almost
						// immediately; prefetch lines wait the normal cadence.
						pending.nextPoll = now + std::chrono::milliseconds(
							pending.job.playOnArrival ? 50 : Settings::MenuPollMs());
						bool overflow = false;
						std::string overflowPath;
						{
							const std::scoped_lock guard{ g_lock };
							if (g_pending.size() < kMaxTrackedJobs) {
								g_pending.push_back(std::move(pending));
							} else {
								overflow = true;
								overflowPath = pending.job.voicePath;
							}
						}
						if (overflow) {
							logger::warn("Pending synthesis list is full; dropping '{}'", overflowPath);
							Untrack(overflowPath);
						} else {
							g_pollWake.notify_one();
						}
					}
				}
			}
		}

		// Collects finished audio for pending jobs.  Network happens outside
		// the lock; picked lines are polled first (they long-poll server-side
		// for near-zero arrival latency).
		void PollLoop()
		{
			using namespace std::chrono_literals;

			while (true) {
				{
					std::unique_lock lock{ g_lock };
					g_pollWake.wait_for(lock, 100ms);
				}

				std::vector<Job> due;
				{
					const std::scoped_lock guard{ g_lock };
					const auto now = Clock::now();
					for (auto& pending : g_pending) {
						if (!pending.inFlight && now >= pending.nextPoll) {
							pending.inFlight = true;
							++pending.polls;
							due.push_back(pending.job);
						}
					}
				}
				if (due.empty()) {
					continue;
				}
				std::stable_sort(due.begin(), due.end(), [](const Job& a_lhs, const Job& a_rhs) {
					return a_lhs.playOnArrival > a_rhs.playOnArrival;
				});

				const auto pollInterval = std::chrono::milliseconds(
					g_menuOpen.load(std::memory_order_acquire) ? Settings::MenuPollMs() : Settings::IdlePollMs());
				for (const auto& job : due) {
					const auto finished = PollResult(job);
					bool gaveUp = false;
					{
						const std::scoped_lock guard{ g_lock };
						const auto it = std::find_if(g_pending.begin(), g_pending.end(), [&](const PendingJob& a_pending) {
							return a_pending.job.voicePath == job.voicePath;
						});
						if (it != g_pending.end()) {
							if (finished) {
								g_pending.erase(it);
							} else if (it->polls > kMaxPollsPerJob) {
								gaveUp = true;
								g_pending.erase(it);
							} else {
								it->inFlight = false;
								it->nextPoll = Clock::now() + pollInterval;
							}
						}
					}
					if (gaveUp) {
						logger::warn("Gave up waiting for synthesis of '{}'", job.voicePath);
						Untrack(job.voicePath);
					}
				}
			}
		}
	}

	void Start()
	{
		bool expected = false;
		if (!g_started.compare_exchange_strong(expected, true)) {
			return;
		}
		g_gameRoot = ResolveGameRoot();
		if (!g_gameRoot.empty()) {
			VoiceManifest::Init(g_gameRoot);
		}
		ShadowPlayback::EnsureStreamSlots();
		std::thread{ SubmitLoop }.detach();
		std::thread{ PollLoop }.detach();
	}

	void Enqueue(Job a_job)
	{
		if (a_job.voicePath.empty() || a_job.text.empty()) {
			return;
		}
		a_job.enqueuedAt = std::chrono::steady_clock::now();

		{
			const std::scoped_lock guard{ g_lock };
			if (g_tracked.contains(a_job.voicePath)) {
				return;
			}
			if (g_tracked.size() >= kMaxTrackedJobs) {
				return;
			}
			g_tracked.insert(a_job.voicePath);
			g_incoming.push_back(std::move(a_job));
			g_batchTotal.fetch_add(1, std::memory_order_relaxed);
		}
		g_wake.notify_one();
	}

	void SetDialogueMenuOpen(const bool a_open)
	{
		g_menuOpen.store(a_open, std::memory_order_release);
		if (a_open) {
			// A fresh conversation: restart the progress batch.
			g_batchTotal.store(0, std::memory_order_relaxed);
			g_batchDone.store(0, std::memory_order_relaxed);
			g_wake.notify_one();
		}
	}

	bool ServerReachable() noexcept
	{
		return g_serverReachable.load(std::memory_order_acquire);
	}

	void GetBatchProgress(std::uint32_t& a_done, std::uint32_t& a_total) noexcept
	{
		a_done = g_batchDone.load(std::memory_order_relaxed);
		a_total = g_batchTotal.load(std::memory_order_relaxed);
	}
}
