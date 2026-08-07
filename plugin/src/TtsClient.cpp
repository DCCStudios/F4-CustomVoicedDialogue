#include "PCH.h"

#include "TtsClient.h"

#include "Settings.h"

#include <winhttp.h>

namespace CustomVoicedDialogue::TtsClient
{
	namespace
	{
		struct HInternet
		{
			HINTERNET handle{ nullptr };

			~HInternet()
			{
				if (handle) {
					::WinHttpCloseHandle(handle);
				}
			}

			explicit operator bool() const noexcept { return handle != nullptr; }
		};

		[[nodiscard]] Response Send(const wchar_t* a_verb, std::wstring_view a_path, const std::string* a_body)
		{
			Response response{};

			HInternet session{ ::WinHttpOpen(
				L"CustomVoicedDialogue/1.0",
				WINHTTP_ACCESS_TYPE_NO_PROXY,
				WINHTTP_NO_PROXY_NAME,
				WINHTTP_NO_PROXY_BYPASS,
				0) };
			if (!session) {
				return response;
			}

			const auto timeout = static_cast<int>(Settings::RequestTimeoutMs());
			::WinHttpSetTimeouts(session.handle, timeout, timeout, timeout, timeout);

			const auto& host = Settings::ServerHost();
			std::wstring wideHost{ host.begin(), host.end() };
			HInternet connection{ ::WinHttpConnect(session.handle, wideHost.c_str(), Settings::ServerPort(), 0) };
			if (!connection) {
				return response;
			}

			const std::wstring path{ a_path };
			HInternet request{ ::WinHttpOpenRequest(
				connection.handle,
				a_verb,
				path.c_str(),
				nullptr,
				WINHTTP_NO_REFERER,
				WINHTTP_DEFAULT_ACCEPT_TYPES,
				0) };
			if (!request) {
				return response;
			}

			const wchar_t* headers = a_body ? L"Content-Type: application/json\r\n" : WINHTTP_NO_ADDITIONAL_HEADERS;
			const auto headersLength = a_body ? static_cast<DWORD>(-1) : 0;
			const auto* bodyData = a_body ? a_body->data() : WINHTTP_NO_REQUEST_DATA;
			const auto bodyLength = a_body ? static_cast<DWORD>(a_body->size()) : 0;

			if (!::WinHttpSendRequest(
					request.handle,
					headers,
					headersLength,
					const_cast<char*>(static_cast<const char*>(bodyData)),
					bodyLength,
					bodyLength,
					0)) {
				return response;
			}
			if (!::WinHttpReceiveResponse(request.handle, nullptr)) {
				return response;
			}

			DWORD status = 0;
			DWORD statusSize = sizeof(status);
			if (!::WinHttpQueryHeaders(
					request.handle,
					WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
					WINHTTP_HEADER_NAME_BY_INDEX,
					&status,
					&statusSize,
					WINHTTP_NO_HEADER_INDEX)) {
				return response;
			}
			response.status = status;

			while (true) {
				DWORD available = 0;
				if (!::WinHttpQueryDataAvailable(request.handle, &available) || available == 0) {
					break;
				}
				const auto offset = response.body.size();
				response.body.resize(offset + available);
				DWORD read = 0;
				if (!::WinHttpReadData(request.handle, response.body.data() + offset, available, &read)) {
					response.body.resize(offset);
					break;
				}
				response.body.resize(offset + read);
			}

			return response;
		}
	}

	Response PostJson(const std::wstring_view a_path, const std::string& a_body)
	{
		return Send(L"POST", a_path, &a_body);
	}

	Response Get(const std::wstring_view a_path)
	{
		return Send(L"GET", a_path, nullptr);
	}
}
