// WinINet keeps proxy settings on each InternetOpen handle. Keep the application's
// original settings and synchronize only that handle before a new HTTP operation.
// This translation unit is separate because winhttp.h and wininet.h share types.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <wininet.h>
#include "detours.h"
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
#pragma comment(lib, "wininet.lib")

namespace
{
auto OpenA = &InternetOpenA;
auto OpenW = &InternetOpenW;
auto ConnectA = &InternetConnectA;
auto ConnectW = &InternetConnectW;
auto OpenUrlA = &InternetOpenUrlA;
auto OpenUrlW = &InternetOpenUrlW;
auto SendA = &HttpSendRequestA;
auto SendW = &HttpSendRequestW;
auto SendExA = &HttpSendRequestExA;
auto SendExW = &HttpSendRequestExW;
auto EndA = &HttpEndRequestA;
auto EndW = &HttpEndRequestW;
auto SetA = &InternetSetOptionA;
auto SetW = &InternetSetOptionW;
auto Close = &InternetCloseHandle;
bool (*RoutingEnabled)() = nullptr;
thread_local bool internalCall = false;

struct Proxy
{
    DWORD type = INTERNET_OPEN_TYPE_PRECONFIG;
    std::string server, bypass;
};
struct Root
{
    std::recursive_mutex mutex;
    Proxy original;
    bool direct = false;
    bool dirty = false;
    bool active = true;
};
std::mutex rootsMutex;
std::unordered_map<HINTERNET, std::shared_ptr<Root>> roots;

struct InternalCall
{
    bool previous = internalCall;
    InternalCall() { internalCall = true; }
    ~InternalCall() { internalCall = previous; }
};

bool ReadProxy(HINTERNET handle, Proxy &result)
{
    DWORD bytes = 0;
    InternetQueryOptionA(handle, INTERNET_OPTION_PROXY, nullptr, &bytes);
    if (bytes < sizeof(INTERNET_PROXY_INFO) || bytes > 65536) return false;
    std::vector<BYTE> buffer(bytes);
    if (!InternetQueryOptionA(handle, INTERNET_OPTION_PROXY, buffer.data(), &bytes)) return false;
    const auto *info = reinterpret_cast<const INTERNET_PROXY_INFO *>(buffer.data());
    result.type = info->dwAccessType;
    result.server = info->lpszProxy ? info->lpszProxy : "";
    result.bypass = info->lpszProxyBypass ? info->lpszProxyBypass : "";
    return true;
}

std::shared_ptr<Root> FindRoot(HINTERNET &handle)
{
    for (unsigned depth = 0; handle && depth != 8; ++depth)
    {
        {
            std::lock_guard<std::mutex> lock(rootsMutex);
            const auto found = roots.find(handle);
            if (found != roots.end()) return found->second;
        }
        HINTERNET parent = nullptr;
        DWORD bytes = sizeof(parent);
        if (!InternetQueryOptionA(handle, INTERNET_OPTION_PARENT_HANDLE, &parent, &bytes) || parent == handle)
            break;
        handle = parent;
    }
    return {};
}

bool Apply(HINTERNET handle, Root &root, bool enabled)
{
    if (!root.dirty && root.direct == enabled) return true;
    INTERNET_PROXY_INFO info{};
    info.dwAccessType = enabled ? INTERNET_OPEN_TYPE_DIRECT : root.original.type;
    if (!enabled)
    {
        info.lpszProxy = root.original.server.empty() ? nullptr : root.original.server.data();
        info.lpszProxyBypass = root.original.bypass.empty() ? nullptr : root.original.bypass.data();
    }
    InternalCall guard;
    if (!SetA(handle, INTERNET_OPTION_PROXY, &info, sizeof(info))) return false;
    root.dirty = true;
    if (!enabled && (root.original.type == INTERNET_OPEN_TYPE_PRECONFIG ||
                     root.original.type == INTERNET_OPEN_TYPE_PRECONFIG_WITH_NO_AUTOPROXY))
        if (!SetA(handle, INTERNET_OPTION_REFRESH, nullptr, 0)) return false;
    root.direct = enabled;
    root.dirty = false;
    return true;
}

void Synchronize(HINTERNET handle) noexcept
{
    if (internalCall || !handle) return;
    const DWORD error = GetLastError();
    try
    {
        const auto root = FindRoot(handle);
        if (root)
        {
            std::lock_guard<std::recursive_mutex> lock(root->mutex);
            if (root->active) Apply(handle, *root, RoutingEnabled && RoutingEnabled());
        }
    }
    catch (...) {}
    SetLastError(error);
}

void Register(HINTERNET handle, DWORD mode) noexcept
{
    if (!handle || internalCall) return;
    const DWORD error = GetLastError();
    try
    {
        auto root = std::make_shared<Root>();
        if (!ReadProxy(handle, root->original)) { SetLastError(error); return; }
        // PRECONFIG may initially query as DIRECT before WinINet loads settings.
        root->original.type = mode;
        {
            std::lock_guard<std::mutex> lock(rootsMutex);
            if (roots.size() >= 256) { SetLastError(error); return; }
            roots[handle] = root;
        }
        Synchronize(handle);
    }
    catch (...) {}
    SetLastError(error);
}

template <typename Function>
BOOL SetOption(Function original, HINTERNET handle, DWORD option, LPVOID buffer, DWORD length)
{
    if (internalCall || !handle || (option != INTERNET_OPTION_PROXY && option != INTERNET_OPTION_REFRESH))
        return original(handle, option, buffer, length);
    bool invoked = false;
    BOOL result = FALSE;
    DWORD error = GetLastError();
    try
    {
        HINTERNET rootHandle = handle;
        const auto root = FindRoot(rootHandle);
        if (!root || rootHandle != handle) return original(handle, option, buffer, length);
        std::lock_guard<std::recursive_mutex> lock(root->mutex);
        if (!root->active) return original(handle, option, buffer, length);
        // Let Windows validate and normalize the application's A/W input first.
        {
            InternalCall guard;
            result = original(handle, option, buffer, length);
            error = GetLastError();
            invoked = true;
        }
        if (result)
        {
            Proxy updated;
            if (ReadProxy(handle, updated))
            {
                if (option == INTERNET_OPTION_PROXY && buffer && length >= sizeof(INTERNET_PROXY_INFO))
                    updated.type = static_cast<INTERNET_PROXY_INFO *>(buffer)->dwAccessType;
                else if (option == INTERNET_OPTION_REFRESH)
                    updated.type = INTERNET_OPEN_TYPE_PRECONFIG;
                root->original = std::move(updated);
                root->direct = false;
                root->dirty = false;
                Apply(handle, *root, RoutingEnabled && RoutingEnabled());
            }
            else
            {
                // The application has already applied its own settings. If they
                // cannot be captured, leave them intact rather than restoring a
                // stale snapshot later or recording the previous DIRECT state.
                root->direct = false;
                root->active = false;
                std::lock_guard<std::mutex> mapLock(rootsMutex);
                const auto found = roots.find(handle);
                if (found != roots.end() && found->second == root) roots.erase(found);
            }
        }
        SetLastError(error);
        return result;
    }
    catch (...)
    {
        if (!invoked) return original(handle, option, buffer, length);
        SetLastError(error);
        return result;
    }
}

HINTERNET WINAPI HookOpenA(LPCSTR agent, DWORD type, LPCSTR proxy, LPCSTR bypass, DWORD flags)
{ HINTERNET h; { InternalCall guard; h = OpenA(agent, type, proxy, bypass, flags); } Register(h, type); return h; }
HINTERNET WINAPI HookOpenW(LPCWSTR agent, DWORD type, LPCWSTR proxy, LPCWSTR bypass, DWORD flags)
{ HINTERNET h; { InternalCall guard; h = OpenW(agent, type, proxy, bypass, flags); } Register(h, type); return h; }
HINTERNET WINAPI HookConnectA(HINTERNET h, LPCSTR server, INTERNET_PORT port, LPCSTR user, LPCSTR password, DWORD service, DWORD flags, DWORD_PTR context)
{ Synchronize(h); return ConnectA(h, server, port, user, password, service, flags, context); }
HINTERNET WINAPI HookConnectW(HINTERNET h, LPCWSTR server, INTERNET_PORT port, LPCWSTR user, LPCWSTR password, DWORD service, DWORD flags, DWORD_PTR context)
{ Synchronize(h); return ConnectW(h, server, port, user, password, service, flags, context); }
HINTERNET WINAPI HookOpenUrlA(HINTERNET h, LPCSTR url, LPCSTR headers, DWORD length, DWORD flags, DWORD_PTR context)
{ Synchronize(h); return OpenUrlA(h, url, headers, length, flags, context); }
HINTERNET WINAPI HookOpenUrlW(HINTERNET h, LPCWSTR url, LPCWSTR headers, DWORD length, DWORD flags, DWORD_PTR context)
{ Synchronize(h); return OpenUrlW(h, url, headers, length, flags, context); }
BOOL WINAPI HookSendA(HINTERNET h, LPCSTR headers, DWORD length, LPVOID data, DWORD bytes)
{ Synchronize(h); return SendA(h, headers, length, data, bytes); }
BOOL WINAPI HookSendW(HINTERNET h, LPCWSTR headers, DWORD length, LPVOID data, DWORD bytes)
{ Synchronize(h); return SendW(h, headers, length, data, bytes); }
BOOL WINAPI HookSendExA(HINTERNET h, LPINTERNET_BUFFERSA input, LPINTERNET_BUFFERSA output, DWORD flags, DWORD_PTR context)
{ Synchronize(h); return SendExA(h, input, output, flags, context); }
BOOL WINAPI HookSendExW(HINTERNET h, LPINTERNET_BUFFERSW input, LPINTERNET_BUFFERSW output, DWORD flags, DWORD_PTR context)
{ Synchronize(h); return SendExW(h, input, output, flags, context); }
BOOL WINAPI HookEndA(HINTERNET h, LPINTERNET_BUFFERSA output, DWORD flags, DWORD_PTR context)
{ Synchronize(h); return EndA(h, output, flags, context); }
BOOL WINAPI HookEndW(HINTERNET h, LPINTERNET_BUFFERSW output, DWORD flags, DWORD_PTR context)
{ Synchronize(h); return EndW(h, output, flags, context); }
BOOL WINAPI HookSetA(HINTERNET h, DWORD option, LPVOID buffer, DWORD length)
{ return SetOption(SetA, h, option, buffer, length); }
BOOL WINAPI HookSetW(HINTERNET h, DWORD option, LPVOID buffer, DWORD length)
{ return SetOption(SetW, h, option, buffer, length); }
BOOL WINAPI HookClose(HINTERNET handle)
{
    std::shared_ptr<Root> before;
    { std::lock_guard<std::mutex> lock(rootsMutex); const auto found = roots.find(handle); if (found != roots.end()) before = found->second; }
    if (before) { std::lock_guard<std::recursive_mutex> lock(before->mutex); before->active = false; }
    // Never hold our locks across close: asynchronous callbacks may reenter us.
    const BOOL result = Close(handle);
    const DWORD error = GetLastError();
    if (result)
    {
        std::lock_guard<std::mutex> lock(rootsMutex);
        const auto found = roots.find(handle);
        if (found != roots.end() && found->second == before) roots.erase(found);
    }
    else if (before) { std::lock_guard<std::recursive_mutex> lock(before->mutex); before->active = true; }
    SetLastError(error);
    return result;
}
} // namespace

bool AttachWinInetRouting(bool (*enabled)())
{
    RoutingEnabled = enabled;
#define ATTACH(name) if (DetourAttach(reinterpret_cast<PVOID *>(&name), Hook##name) != NO_ERROR) return false
    ATTACH(OpenA); ATTACH(OpenW); ATTACH(ConnectA); ATTACH(ConnectW);
    ATTACH(OpenUrlA); ATTACH(OpenUrlW); ATTACH(SendA); ATTACH(SendW);
    ATTACH(SendExA); ATTACH(SendExW); ATTACH(EndA); ATTACH(EndW);
    ATTACH(SetA); ATTACH(SetW); ATTACH(Close);
#undef ATTACH
    return true;
}
