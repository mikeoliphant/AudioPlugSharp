// Hand-written support types used by the generated C++ layer (aps/generated/abi.hpp).
#pragma once

#include "generated/abi.h"

#include <algorithm>
#include <cstring>
#include <exception>
#include <string>
#include <string_view>

namespace aps {

// Result of a call into the managed side: an aps_result code plus the value (valid only when ok()).
template <typename T>
struct Result {
    aps_result code = APS_ERROR;
    T value{};

    bool ok() const noexcept { return code == APS_OK; }
    explicit operator bool() const noexcept { return ok(); }
    const T& operator*() const& noexcept { return value; }
    T& operator*() & noexcept { return value; }
    const T* operator->() const noexcept { return &value; }
    T valueOr(T fallback) const { return ok() ? value : fallback; }
};

template <>
struct Result<void> {
    aps_result code = APS_ERROR;

    bool ok() const noexcept { return code == APS_OK; }
    explicit operator bool() const noexcept { return ok(); }
};

using Status = Result<void>;

// Throw from a NativeApi implementation to report APS_NOT_SUPPORTED.
struct NotSupported : std::exception {
    const char* what() const noexcept override { return "not supported"; }
};

namespace detail {

inline std::string readFixed(const char* s, size_t capacity) {
    size_t n = 0;
    while (n < capacity && s[n] != '\0') ++n;
    return std::string(s, n);
}

// Copies a UTF-8 string into a fixed buffer, NUL-terminated, truncating on a code point boundary.
inline void writeFixed(std::string_view s, char* dst, size_t capacity) {
    size_t n = std::min(s.size(), capacity - 1);
    while (n > 0 && n < s.size() && (static_cast<unsigned char>(s[n]) & 0xC0) == 0x80) --n;
    std::memcpy(dst, s.data(), n);
    std::memset(dst + n, 0, capacity - n);
}

} // namespace detail
} // namespace aps
