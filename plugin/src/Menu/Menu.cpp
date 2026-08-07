#include "PCH.h"

#include "Menu/Menu.h"

#include "Settings.h"
#include "SynthQueue.h"

#include <filesystem>

#include "Menu/F4SEMenuFramework.h"

namespace CustomVoicedDialogue::Menu
{
	namespace
	{
		// Widget mirrors of the live settings; synced from Settings at
		// registration and written back (memory + INI) on every change.
		bool s_playerLines{ true };
		bool s_npcLines{ false };
		bool s_replacePlayer{ false };
		bool s_replaceNPC{ false };
		bool s_forceSubtitles{ true };
		bool s_verboseLog{ false };
		bool s_prefetch{ true };
		bool s_directPlayback{ false };
		bool s_showProgress{ true };
		int s_volume{ 100 };
		int s_pendingWait{ 3 };

		// Small always-on-top progress readout while a conversation's lines
		// generate; drawn through the framework's HUD layer.
		void __stdcall RenderProgressOverlay()
		{
			if (!Settings::ShowGenerationProgress()) {
				return;
			}
			std::uint32_t done = 0;
			std::uint32_t total = 0;
			SynthQueue::GetBatchProgress(done, total);
			if (total == 0 || done >= total) {
				return;
			}

			const auto viewport = ImGuiMCP::GetMainViewport();
			if (!viewport) {
				return;
			}
			ImGuiMCP::SetNextWindowPos(
				ImGuiMCP::ImVec2{ viewport->Pos.x + viewport->Size.x - 20.0f, viewport->Pos.y + 20.0f },
				ImGuiMCP::ImGuiCond_Always,
				ImGuiMCP::ImVec2{ 1.0f, 0.0f });
			ImGuiMCP::SetNextWindowBgAlpha(0.55f);
			ImGuiMCP::Begin(
				"##CvdTtsProgress",
				nullptr,
				ImGuiMCP::ImGuiWindowFlags_NoTitleBar | ImGuiMCP::ImGuiWindowFlags_NoResize |
					ImGuiMCP::ImGuiWindowFlags_NoMove | ImGuiMCP::ImGuiWindowFlags_AlwaysAutoResize |
					ImGuiMCP::ImGuiWindowFlags_NoSavedSettings | ImGuiMCP::ImGuiWindowFlags_NoFocusOnAppearing |
					ImGuiMCP::ImGuiWindowFlags_NoNav);
			ImGuiMCP::Text("Generating dialogue voice  %u / %u", done, total);
			ImGuiMCP::ProgressBar(
				static_cast<float>(done) / static_cast<float>(total),
				ImGuiMCP::ImVec2{ 230.0f, 0.0f },
				nullptr);
			ImGuiMCP::End();
		}

		void __stdcall RenderSettings()
		{
			ImGuiMCP::TextColored(ImGuiMCP::ImVec4(0.55f, 0.85f, 1.0f, 1.0f), "Text-to-speech dialogue");
			ImGuiMCP::Separator();
			ImGuiMCP::Spacing();

			if (ImGuiMCP::SliderInt("TTS volume", &s_volume, 0, 150, "%d%%")) {
				Settings::SetTtsVolumePercent(static_cast<std::uint32_t>(s_volume));
			}
			ImGuiMCP::TextWrapped(
				"Applied to TTS audio as it is generated. Lines that already have "
				"generated audio keep their loudness; above 100%% loud voices may clip.");
			ImGuiMCP::Spacing();
			ImGuiMCP::Separator();
			ImGuiMCP::Spacing();

			if (ImGuiMCP::Checkbox("Voice unvoiced player lines", &s_playerLines)) {
				Settings::SetEnablePlayerLines(s_playerLines);
			}
			if (ImGuiMCP::Checkbox("Voice unvoiced NPC lines", &s_npcLines)) {
				Settings::SetEnableNPCLines(s_npcLines);
			}
			if (ImGuiMCP::Checkbox("Custom player voice (replace the vanilla voice acting)", &s_replacePlayer)) {
				Settings::SetReplaceVoicedPlayerLines(s_replacePlayer);
			}
			if (ImGuiMCP::Checkbox("Re-voice NPCs (replace vanilla NPC voice acting)", &s_replaceNPC)) {
				Settings::SetReplaceVoicedNPCLines(s_replaceNPC);
			}
			ImGuiMCP::Spacing();
			ImGuiMCP::Separator();
			ImGuiMCP::Spacing();

			if (ImGuiMCP::SliderInt("Wait for voice", &s_pendingWait, 0, 8, "%d s")) {
				Settings::SetPendingLineWaitSeconds(static_cast<std::uint32_t>(s_pendingWait));
			}
			ImGuiMCP::TextWrapped(
				"When a picked line's voice is still generating, the line is "
				"held open this many extra seconds so the audio can arrive "
				"and play, instead of the conversation advancing over a "
				"silent line. 0 = never wait.");
			ImGuiMCP::Spacing();

			if (ImGuiMCP::Checkbox("Direct audio playback (HerikaServer-style)", &s_directPlayback)) {
				Settings::SetDirectAudioPlayback(s_directPlayback);
			}
			ImGuiMCP::TextWrapped(
				"Always plays generated player lines through the generic audio "
				"channel instead of the engine's voice file system (silence "
				"carries the dialogue timing). Try this if generated lines "
				"stay silent on your setup.");
			if (ImGuiMCP::Checkbox("Show TTS generation progress bar", &s_showProgress)) {
				Settings::SetShowGenerationProgress(s_showProgress);
			}
			ImGuiMCP::Spacing();

			if (ImGuiMCP::Checkbox("Force subtitles for TTS/silenced lines", &s_forceSubtitles)) {
				Settings::SetForceSubtitles(s_forceSubtitles);
			}
			ImGuiMCP::TextDisabled("(turning this ON takes effect on the next game launch)");
			if (ImGuiMCP::Checkbox("Prefetch dialogue options when the menu opens", &s_prefetch)) {
				Settings::SetEnablePrefetch(s_prefetch);
			}
			if (ImGuiMCP::Checkbox("Verbose log", &s_verboseLog)) {
				Settings::SetVerboseLog(s_verboseLog);
			}

			ImGuiMCP::Spacing();
			ImGuiMCP::Separator();
			ImGuiMCP::TextWrapped(
				"All changes are saved to CustomVoicedDialogue.ini immediately. "
				"Voices, TTS services, and API keys are configured in the "
				"CustomVoicedDialogue desktop app.");
		}
	}

	void Register()
	{
		if (!F4SEMenuFramework::IsInstalled()) {
			logger::info("F4SE Menu Framework is not installed; the in-game settings page is unavailable");
			return;
		}

		s_playerLines = Settings::EnablePlayerLines();
		s_npcLines = Settings::EnableNPCLines();
		s_replacePlayer = Settings::ReplaceVoicedPlayerLines();
		s_replaceNPC = Settings::ReplaceVoicedNPCLines();
		s_forceSubtitles = Settings::ForceSubtitles();
		s_verboseLog = Settings::VerboseLog();
		s_prefetch = Settings::EnablePrefetch();
		s_directPlayback = Settings::DirectAudioPlayback();
		s_showProgress = Settings::ShowGenerationProgress();
		s_volume = static_cast<int>(Settings::TtsVolumePercent());
		s_pendingWait = static_cast<int>(Settings::PendingLineWaitSeconds());

		F4SEMenuFramework::SetSection("CustomVoicedDialogue");
		F4SEMenuFramework::AddSectionItem("Settings", RenderSettings);
		F4SEMenuFramework::AddHudElement(RenderProgressOverlay);
		logger::info("Registered the in-game settings page (F4SE Menu Framework)");
	}
}
