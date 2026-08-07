#pragma once

namespace CustomVoicedDialogue::Json
{
	// Minimal JSON string escaping for the request bodies this plugin sends.
	// Responses are consumed as raw bytes / HTTP status codes, so no JSON
	// parser is needed on this side.
	[[nodiscard]] inline std::string Escape(std::string_view a_value)
	{
		std::string result;
		result.reserve(a_value.size() + 8);
		for (const char character : a_value) {
			switch (character) {
			case '"':
				result += "\\\"";
				break;
			case '\\':
				result += "\\\\";
				break;
			case '\b':
				result += "\\b";
				break;
			case '\f':
				result += "\\f";
				break;
			case '\n':
				result += "\\n";
				break;
			case '\r':
				result += "\\r";
				break;
			case '\t':
				result += "\\t";
				break;
			default:
				if (static_cast<unsigned char>(character) < 0x20) {
					result += std::format("\\u{:04x}", static_cast<unsigned char>(character));
				} else {
					result += character;
				}
				break;
			}
		}
		return result;
	}
}
