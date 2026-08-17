#pragma once

namespace RE
{
	class Actor;
}

namespace CustomVoicedDialogue::GameContext
{
	// The world state that shapes how a line is delivered, kept deliberately
	// narrow: only signals that were measured to actually change the
	// performance, and only ones that cost a pointer hop or a virtual call
	// (no new engine addresses on any runtime).
	//
	// Combat raises urgency and volume; sneaking drops a line to a murmur;
	// whether the listener is hostile flips a line's whole meaning — "Thanks
	// for the help" is sincere to an ally and sarcastic to an enemy.
	//
	// Every signal here is deliberately a coarse boolean.  A line's audio is
	// cached and reused whenever that line comes up again, so context that
	// varies continuously (how far away the listener happens to be standing)
	// would fragment the cache and make one line sound different for no
	// reason the player could follow.
	struct Snapshot
	{
		bool inCombat{ false };
		bool sneaking{ false };
		bool listenerHostile{ false };
	};

	// Samples the current scene.  Game thread only — it reads live actor
	// state.  a_listener names the conversation partner when the caller
	// already knows it (prefetch does); otherwise the actor the dialogue
	// menu is pointed at is used.
	[[nodiscard]] Snapshot Capture(RE::Actor* a_listener = nullptr);

	// Renders a snapshot as the short "context" clause sent with a line.
	// Empty for an ordinary conversation, so a calm exchange in a settlement
	// costs no extra tokens and steers nothing.
	[[nodiscard]] std::string Describe(const Snapshot& a_snapshot);

	// Capture + Describe for the current scene, memoized briefly so queueing
	// a nine-line dialogue wheel samples once instead of nine times.  Game
	// thread only.
	[[nodiscard]] std::string Current(RE::Actor* a_listener = nullptr);
}
