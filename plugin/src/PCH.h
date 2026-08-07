#pragma once

#define _AMD64_

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <deque>
#include <filesystem>
#include <format>
#include <limits>
#include <mutex>
#include <optional>
#include <source_location>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#pragma warning(push)
#include <F4SE/F4SE.h>
#include <RE/Fallout.h>
#include <REX/REX.h>
#include <windows.h>
#pragma warning(pop)

namespace logger
{

	inline void info(std::string_view a_message)
	{
		REX::Impl::Log(std::source_location::current(), REX::ELogLevel::Info, a_message);
	}

	inline void warn(std::string_view a_message)
	{
		REX::Impl::Log(std::source_location::current(), REX::ELogLevel::Warning, a_message);
	}

	inline void error(std::string_view a_message)
	{
		REX::Impl::Log(std::source_location::current(), REX::ELogLevel::Error, a_message);
	}

	template <class... Args>
	void info(std::format_string<Args...> a_format, Args&&... a_args)
	{
		REX::Impl::Log(std::source_location::current(), REX::ELogLevel::Info, a_format, std::forward<Args>(a_args)...);
	}

	template <class... Args>
	void warn(std::format_string<Args...> a_format, Args&&... a_args)
	{
		REX::Impl::Log(std::source_location::current(), REX::ELogLevel::Warning, a_format, std::forward<Args>(a_args)...);
	}

	template <class... Args>
	void error(std::format_string<Args...> a_format, Args&&... a_args)
	{
		REX::Impl::Log(std::source_location::current(), REX::ELogLevel::Error, a_format, std::forward<Args>(a_args)...);
	}
}
