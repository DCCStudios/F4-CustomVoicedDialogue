#pragma once

namespace CustomVoicedDialogue::SynthQueue
{
	struct Job
	{
		// Data-relative voice path ("Sound\Voice\...\file.wav") — the job
		// identity, the server-side cache key, and the write target.
		std::string voicePath;
		std::string text;
		std::string voiceType;
		bool isPlayer{ false };
		// Set for hook-time misses (the line is playing its silence right
		// now): if the wav arrives while most of the silence is still
		// ahead, it is played directly instead of wasting the encounter.
		// The silence duration bounds how late a start is still acceptable.
		bool playOnArrival{ false };
		float silenceSeconds{ 0.0f };
		// Stamped by Enqueue; used to judge how far into the silence the
		// audio arrived.
		std::chrono::steady_clock::time_point enqueuedAt{};
	};

	// Starts the background worker (call once, at kGameDataReady).
	void Start();

	// Queues a line for synthesis.  Cheap and non-blocking; safe to call
	// from the dialogue hook.  Duplicate voice paths are ignored while a job
	// for them is in flight.
	void Enqueue(Job a_job);

	// Speeds polling up while the dialogue menu is open so prefetched
	// options land before the player picks one.
	void SetDialogueMenuOpen(bool a_open);

	// Last observed server reachability (for diagnostics logging).
	[[nodiscard]] bool ServerReachable() noexcept;

	// Progress of the current conversation's generation batch (jobs queued
	// since the dialogue menu opened vs. jobs finished).  Used by the HUD
	// progress bar; the counters reset each time the menu opens.
	void GetBatchProgress(std::uint32_t& a_done, std::uint32_t& a_total) noexcept;
}
