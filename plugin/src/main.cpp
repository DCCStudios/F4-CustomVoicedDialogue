#include "PCH.h"

#include "Compat/SilentProtagonist.h"
#include "Engine.h"
#include "Hooks/SubtitleHooks.h"
#include "Menu/Menu.h"
#include "Hooks/VoicePathHook.h"
#include "Prefetch.h"
#include "Settings.h"
#include "SubtitleHashStore.h"
#include "SynthQueue.h"

namespace CustomVoicedDialogue
{
	namespace
	{
		void NoteCoexistingPlugins()
		{
			// F4z Ro D'oh patches the same code sites this plugin uses.
			// Its behaviour is a strict subset of ours, so its patches are
			// deliberately superseded during Install() on every runtime and
			// it can stay installed; if that ever cannot be done safely, the
			// resolver fails closed and logs the specific reason.
			if (Engine::IsF4zRoDohLoaded()) {
				logger::info(
					"F4z Ro D'oh is installed. CustomVoicedDialogue supersedes its hooks and includes its "
					"silent-voice behaviour; it is redundant but does not need to be uninstalled.");
			}
		}

		void OnMessage(F4SE::MessagingInterface::Message* a_message)
		{
			if (!a_message) {
				return;
			}

			switch (a_message->type) {
			case F4SE::MessagingInterface::kPostLoad:
				// Patches must go in before other plugins start reading the
				// patched code, and the purge thread is safe to start now.
				NoteCoexistingPlugins();
				Compat::SilentProtagonist::Supersede();
				Hooks::VoicePathHook::Install();
				Hooks::SubtitleHooks::Install();
				SubtitleHashStore::StartPurgeThread();
				// After every plugin has loaded (the framework DLL loads
				// after this one alphabetically).
				Menu::Register();
				break;
			case F4SE::MessagingInterface::kGameDataReady:
				// Menu sinks and the network worker need game systems up.
				if (Hooks::VoicePathHook::Installed()) {
					SynthQueue::Start();
					Prefetch::Register();
				}
				break;
			default:
				break;
			}
		}
	}
}

F4SEPluginLoad(const F4SE::LoadInterface* a_f4se)
{
	F4SE::Init(a_f4se, {
		.log = true,
		.logName = "CustomVoicedDialogue",
		.trampoline = true,
		.trampolineSize = 0x800,
	});

	CustomVoicedDialogue::Settings::Load();

	// Must run before Silent Protagonist's load-time patches (alphabetical
	// plugin load order guarantees this DLL loads first).
	CustomVoicedDialogue::Compat::SilentProtagonist::Snapshot();

	const auto* messaging = F4SE::GetMessagingInterface();
	if (!messaging || !messaging->RegisterListener(CustomVoicedDialogue::OnMessage)) {
		logger::error("Unable to register the F4SE messaging listener");
		return false;
	}

	logger::info("CustomVoicedDialogue loaded");
	return true;
}

extern "C"
{
	F4SE_EXPORT bool F4SEPlugin_Query(const F4SE::QueryInterface*, F4SE::PluginInfo* a_info)
	{
		if (!a_info) {
			return false;
		}

		a_info->infoVersion = F4SE::PluginInfo::kVersion;
		a_info->name = "CustomVoicedDialogue";
		a_info->version = 1;
		return true;
	}
}
