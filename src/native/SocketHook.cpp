// 拦截 DLL：接管指定进程的同步解析，以及 connect / ConnectEx 出站连接。
// getaddrinfo 在物理网卡查询真实 DNS，支持该解析器的 HTTPS 端点和绑定接口的 UDP。
// connect 建立连接前绑定同一物理出口；开关关闭或控制器退出后调用原 API。
// 覆盖 IPv4 TCP、经 connect 建立的 IPv4 UDP 和同步解析；异步 DNS 仍透传。
// 不单独接管 sendto / WSASendTo，不修改系统代理或加载过滤驱动。
// 微软 Detours（MIT）：https://github.com/microsoft/Detours
// 固定版本：1dbd4ea281d73d478aa8d09a8b83fbd265f715f7
// 上游源码及许可证保留在 third_party/Detours。
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h> // Windows socket 类型；必须早于 windows.h。
#include <windows.h>  // 进程、命名事件、等待和环境变量。
#include <ws2tcpip.h> // 域名解析、IPv4 地址转换和接口选项。
#include <shellapi.h> // 解析创建进程时的第一个命令行参数。
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#include <mswsock.h>
#include <mstcpip.h>
#include <winhttp.h>
#include <windns.h>
#include <iphlpapi.h>
#pragma comment(lib, "iphlpapi.lib")
#include <intrin.h>
#include <wintrust.h>
#include <softpub.h>
#pragma comment(lib, "wintrust.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "dnsapi.lib")
#pragma comment(lib, "winhttp.lib")
#include <string>
#pragma comment(lib, "ws2_32.lib")
#include "detours.h"
#include <algorithm>
#include <array>
#include <bcrypt.h> // 随机 DNS 事务号。
#include <cctype>
#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <vector>
#include <memory>
#include <mutex>
#include <unordered_map>
#pragma comment(lib, "bcrypt.lib")

bool AttachWinInetRouting(bool (*enabled)());
// ==================== 3. DLL：解析与连接调用的实时接管 ====================
namespace
{
// Detours 会把这些函数指针更新为原实现的入口，钩子借此调用原 API，避免递归。
auto OriginalGetAddrInfo = &getaddrinfo;
auto OriginalConnect = &connect;
auto OriginalSend = &send;
auto OriginalSendTo = &sendto;
auto OriginalWSASendTo = &WSASendTo;
auto OriginalWSASendMsg = &WSASendMsg;
auto OriginalRecv = &recv;
auto OriginalWSASend = &WSASend;
auto OriginalWSARecv = &WSARecv;
auto OriginalCreateProcessW = &CreateProcessW;
auto OriginalCreateProcessA = &CreateProcessA;
auto OriginalCreateProcessAsUserW = &CreateProcessAsUserW;
auto OriginalCreateProcessAsUserA = &CreateProcessAsUserA;
auto OriginalWSAConnect = &WSAConnect;
auto OriginalWSAIoctl = &WSAIoctl;
auto OriginalGetAddrInfoExW = &GetAddrInfoExW;
auto OriginalFreeAddrInfoExW = &FreeAddrInfoExW;
auto OriginalWinHttpConnect = &WinHttpConnect;
auto OriginalGetIEProxyConfig = &WinHttpGetIEProxyConfigForCurrentUser;
auto OriginalDnsQueryW = &DnsQuery_W;
auto OriginalDnsQueryA = &DnsQuery_A;
auto OriginalDnsQueryEx = &DnsQueryEx;
auto OriginalGetAddrInfoW = &GetAddrInfoW;
#pragma warning(push)
#pragma warning(disable : 4996) // Observe existing applications' legacy resolver calls.
auto OriginalGetHostByName = &gethostbyname;
#pragma warning(pop)

std::string ReadEnvironmentValue(const char *name)
{
    char value[256]{};
    const DWORD n = GetEnvironmentVariableA(name, value, sizeof(value));
    return n > 0 && n < sizeof(value) ? std::string(value, n) : std::string();
}

// 真实应用验证的可选回执，只写 PID/API 名称，不记录页面、聊天或请求正文。
// 文件最多 64 KiB；未提供路径时完全关闭，普通探针无需日志文件。

struct LeaseMember { volatile LONG pid; DWORD reserved; ULONGLONG created; };
struct ControlLease
{
    DWORD version;
    volatile LONG sequence, ownerPid, enabled;
    ULONGLONG ownerCreated;
    BYTE reserved[40];
    LeaseMember members[256];
};
static_assert(sizeof(ControlLease) == 4160);
static_assert(offsetof(ControlLease, ownerCreated) == 16 && offsetof(ControlLease, members) == 64);
HANDLE controlMapping = nullptr, retainedTimingMapping = nullptr, runtimePairPin = nullptr;
ControlLease *controlLease = nullptr;
char currentHookPath[MAX_PATH]{};
bool RetainRuntimePair(HINSTANCE module)
{
    wchar_t configured[MAX_PATH]{};
    const bool required = GetEnvironmentVariableW(L"ZDC_RUNTIME_HOOK", configured, MAX_PATH) != 0;
    wchar_t path[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
    if (!length || length >= MAX_PATH) return !required;
    wchar_t *slash = wcsrchr(path, L'\\');
    if (!slash || static_cast<size_t>(slash - path) + 11 > MAX_PATH) return !required;
    wcscpy_s(slash + 1, MAX_PATH - static_cast<size_t>(slash + 1 - path), L".zdc-live");
    runtimePairPin = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (runtimePairPin == INVALID_HANDLE_VALUE) runtimePairPin = nullptr;
    return runtimePairPin || !required;
}
bool ProcessIdentityAlive(DWORD pid, ULONGLONG expected)
{
    HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return false;
    FILETIME created{}, exited{}, kernel{}, user{};
    const bool alive = GetProcessTimes(process, &created, &exited, &kernel, &user) &&
        ((static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime) == expected &&
        WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
    CloseHandle(process);
    return alive;
}
void RetainControlLease()
{
    char name[128]{};
    if (!GetEnvironmentVariableA("ZDC_CONTROL_MAP", name, sizeof(name))) return;
    controlMapping = OpenFileMappingA(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (!controlMapping) return;
    controlLease = static_cast<ControlLease *>(MapViewOfFile(controlMapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(ControlLease)));
    if (!controlLease || controlLease->version != 1)
    {
        if (controlLease) UnmapViewOfFile(controlLease);
        controlLease = nullptr;
        CloseHandle(controlMapping); controlMapping = nullptr;
        return;
    }
    name[0] = 0;
    if (GetEnvironmentVariableA("ZDC_TIMING_MAP", name, sizeof(name)))
        retainedTimingMapping = OpenFileMappingA(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
}

void RegisterInstalledMember()
{
    if (!controlLease) return;
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) return;
    const ULONGLONG stamp = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    for (auto &member : controlLease->members)
    {
        LONG old = InterlockedCompareExchange(&member.pid, 0, 0);
        // Preserve the root's first registration even after a short launcher
        // exits; later members can recycle the remaining identity slots.
        if (old > 0 && (&member == &controlLease->members[0] ||
            ProcessIdentityAlive(static_cast<DWORD>(old), member.created))) continue;
        if (old < 0 || InterlockedCompareExchange(&member.pid, -1, old) != old) continue;
        member.created = stamp;
        InterlockedExchange(&member.pid, static_cast<LONG>(GetCurrentProcessId()));
        break;
    }
}
struct LeaseOwnerHandle
{
    DWORD pid = 0;
    ULONGLONG created = 0;
    HANDLE process = nullptr;
    ~LeaseOwnerHandle() { if (process) CloseHandle(process); }
};
bool LeaseOwnerAlive(bool requireActive)
{
    if (!controlLease || controlLease->version != 1) return false;
    const LONG before = InterlockedCompareExchange(&controlLease->sequence, 0, 0);
    if (before & 1) return false;
    const LONG owner = controlLease->ownerPid, enabled = controlLease->enabled;
    const ULONGLONG created = controlLease->ownerCreated;
    MemoryBarrier();
    if (before != InterlockedCompareExchange(&controlLease->sequence, 0, 0) ||
        owner <= 0 || (requireActive && !enabled)) return false;
    // Retain an identity-checked owner handle per calling thread. Datagram sends do
    // not repeatedly open processes; a zero-time wait still detects owner exit.
    static thread_local LeaseOwnerHandle cached;
    if (cached.pid != static_cast<DWORD>(owner) || cached.created != created)
    {
        if (cached.process) CloseHandle(cached.process);
        cached = {};
        HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(owner));
        FILETIME stamp{}, exited{}, kernel{}, user{};
        if (!process) return false;
        if (!GetProcessTimes(process, &stamp, &exited, &kernel, &user) ||
            ((static_cast<ULONGLONG>(stamp.dwHighDateTime) << 32) | stamp.dwLowDateTime) != created)
        { CloseHandle(process); return false; }
        cached.pid = static_cast<DWORD>(owner); cached.created = created; cached.process = process;
    }
    if (cached.process && WaitForSingleObject(cached.process, 0) == WAIT_TIMEOUT) return true;
    if (cached.process) CloseHandle(cached.process);
    cached = {};
    return false;
}

// Fixed 64 x 88-byte per-session RTT table shared with ConnectionTimingChannel.cs.
// Concurrent writers skip a sample rather than block the application thread.
struct ConnectionTiming
{
    volatile LONG sequence;
    DWORD pid;
    double milliseconds;
    ULONGLONG started, finished;
    DWORD interfaceIndex, sourceAddress;
    DWORD localPort, remoteAddress, remotePort, reserved;
    ULONGLONG bytesIn, bytesOut;
    DWORD bytesRetrans, padding;
    ULONGLONG connectionTimeMs;
};
static_assert(sizeof(ConnectionTiming) == 88);
static_assert(offsetof(ConnectionTiming, bytesIn) == 56 && offsetof(ConnectionTiming, connectionTimeMs) == 80);
constexpr size_t TimingSlots = 64;
void PublishConnectionTiming(double milliseconds, ULONGLONG started, DWORD interfaceIndex,
                             const sockaddr_in &local, const sockaddr_in &peer, const TCP_INFO_v0 &info)
{
    char name[128]{};
    const DWORD length = GetEnvironmentVariableA("ZDC_TIMING_MAP", name, sizeof(name));
    if (!length || length >= sizeof(name)) return;
    HANDLE mapping = OpenFileMappingA(FILE_MAP_WRITE, FALSE, name);
    if (!mapping) return;
    auto table = static_cast<ConnectionTiming *>(MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0,
                                                               sizeof(ConnectionTiming) * TimingSlots));
    if (table)
    {
        const DWORD pid = GetCurrentProcessId();
        // Fixed hashing bounds cost and storage; collisions replace a sample, never mix fields.
        DWORD hash = 2166136261u;
        for (DWORD part : {pid, local.sin_addr.s_addr, peer.sin_addr.s_addr,
                           static_cast<DWORD>(ntohs(local.sin_port)), static_cast<DWORD>(ntohs(peer.sin_port))})
            hash = (hash ^ part) * 16777619u;
        const size_t slot = hash % TimingSlots;
        for (size_t attempt = 0; attempt < TimingSlots; ++attempt)
        {
            auto value = table + ((slot + attempt) % TimingSlots);
            const LONG previous = InterlockedCompareExchange(&value->sequence, 0, 0);
            // A crashed writer can leave a slot odd; use another slot without waiting.
            if ((previous & 1) || previous >= LONG_MAX - 2 ||
                InterlockedCompareExchange(&value->sequence, previous + 1, previous) != previous)
                continue;
            value->pid = GetCurrentProcessId();
            value->milliseconds = milliseconds;
            value->started = started;
            value->finished = GetTickCount64();
            value->interfaceIndex = interfaceIndex;
            value->sourceAddress = local.sin_addr.s_addr;
            value->localPort = ntohs(local.sin_port);
            value->remoteAddress = peer.sin_addr.s_addr;
            value->remotePort = ntohs(peer.sin_port);
            value->reserved = 0;
            value->bytesIn = info.BytesIn;
            value->bytesOut = info.BytesOut;
            value->bytesRetrans = info.BytesRetrans;
            value->padding = 0;
            value->connectionTimeMs = info.ConnectionTimeMs;
            InterlockedExchange(&value->sequence, previous + 2);
            break;
        }
        UnmapViewOfFile(table);
    }
    CloseHandle(mapping);
}

void WriteTestReceipt(const char *stage)
{
    char path[MAX_PATH]{};
    const DWORD count = GetEnvironmentVariableA("ZDC_TEST_RECEIPT", path, MAX_PATH);
    if (!count || count >= MAX_PATH)
    {
        return;
    }
    HANDLE file = CreateFileA(path, FILE_APPEND_DATA | FILE_READ_ATTRIBUTES,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                              OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }
    LARGE_INTEGER size{};
    if (GetFileSizeEx(file, &size) && size.QuadPart < 65536)
    {
        char line[192]{};
        const int length = _snprintf_s(line, sizeof(line), _TRUNCATE, "pid=%lu stage=%s\r\n",
                                       GetCurrentProcessId(), stage);
        DWORD written = 0;
        if (length > 0)
        {
            WriteFile(file, line, static_cast<DWORD>(length), &written, nullptr);
        }
    }
    CloseHandle(file);
}

// ConnectEx 由 Winsock 按 socket 的服务提供者返回函数指针，并非固定导出函数。
// 只替换成功的同步查询；调用时重新取得该 socket 的原实现，保留异步完成机制。
// Diagnostic-only, bounded across processes. Never include names, URLs, payloads
// or proxy credentials. A busy writer is skipped rather than blocking the app.
void WriteApiObservation(const char *event) noexcept
{
    const DWORD error = GetLastError();
    static volatile LONG attempts = 0;
    if (InterlockedIncrement(&attempts) <= 64)
    {
        wchar_t root[MAX_PATH]{}, path[MAX_PATH]{};
        const DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", root, MAX_PATH);
        if (count && count < MAX_PATH &&
            _snwprintf_s(path, MAX_PATH, _TRUNCATE, L"%s\\ZdC\\diagnostics\\api.log", root) > 0)
        {
            HANDLE mutex = CreateMutexW(nullptr, FALSE, L"Local\\ZdC.ApiObservationV1");
            const DWORD wait = mutex ? WaitForSingleObject(mutex, 0) : WAIT_FAILED;
            if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
            {
                HANDLE file = CreateFileW(path, FILE_APPEND_DATA | FILE_READ_ATTRIBUTES,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                    OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
                if (file != INVALID_HANDLE_VALUE)
                {
                    LARGE_INTEGER size{};
                    if (GetFileSizeEx(file, &size) && size.QuadPart < 262144 - 256)
                    {
                        char line[256]{};
                        const int n = _snprintf_s(line, sizeof(line), _TRUNCATE,
                            "tick=%llu pid=%lu %s\r\n", GetTickCount64(), GetCurrentProcessId(), event);
                        DWORD written = 0;
                        if (n > 0) WriteFile(file, line, static_cast<DWORD>(n), &written, nullptr);
                    }
                    CloseHandle(file);
                }
                ReleaseMutex(mutex);
            }
            if (mutex) CloseHandle(mutex);
        }
    }
    SetLastError(error);
}

void ObserveLoopbackCaller(const sockaddr *address, int length, void *caller) noexcept
{
    if (!address || length < static_cast<int>(sizeof(sockaddr_in)) || address->sa_family != AF_INET)
        return;
    const auto ip = reinterpret_cast<const sockaddr_in *>(address);
    if ((ntohl(ip->sin_addr.s_addr) >> 24) != 127) return;
    static volatile LONG observations = 0;
    if (InterlockedIncrement(&observations) > 16) return;
    const DWORD error = GetLastError();
    HMODULE module = nullptr;
    char path[MAX_PATH]{}, line[180]{};
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                          GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                          reinterpret_cast<LPCSTR>(caller), &module) &&
        GetModuleFileNameA(module, path, MAX_PATH))
    {
        const char *base = strrchr(path, '\\');
        _snprintf_s(line, sizeof(line), _TRUNCATE,
            "loopback_connect port=%u caller=%.80s offset=%llu", ntohs(ip->sin_port),
            base ? base + 1 : path,
            static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(caller) - reinterpret_cast<uintptr_t>(module)));
        WriteApiObservation(line);
    }
    SetLastError(error);
}

DNS_STATUS WINAPI HookDnsQueryW(PCWSTR name, WORD type, DWORD options, PVOID extra,
                               PDNS_RECORD *records, PVOID *reserved)
{
    const DNS_STATUS status = OriginalDnsQueryW(name, type, options, extra, records, reserved);
    static volatile LONG observations = 0;
    if (InterlockedIncrement(&observations) > 8) return status;
    const DWORD error = GetLastError();
    unsigned count = 0, fake = 0;
    if (!status && records && !(options & DNS_QUERY_RETURN_MESSAGE))
        for (auto record = *records; record && count < 64; record = record->pNext, ++count)
            if (record->wType == DNS_TYPE_A &&
                (ntohl(record->Data.A.IpAddress) & 0xfffe0000u) == 0xc6120000u) ++fake;
    char line[128]{};
    _snprintf_s(line, sizeof(line), _TRUNCATE,
        "DnsQuery_W type=%u status=%ld records=%u fake_a=%u raw_message=%d",
        type, status, count, fake, (options & DNS_QUERY_RETURN_MESSAGE) != 0);
    WriteApiObservation(line);
    SetLastError(error);
    return status;
}

// Observe missing resolver entry points without wrapping async callbacks or
// recording host names. Results and allocation ownership remain with Windows.
DNS_STATUS WINAPI HookDnsQueryA(PCSTR name, WORD type, DWORD options, PVOID extra,
                               PDNS_RECORD *records, PVOID *reserved)
{
    const DNS_STATUS status = OriginalDnsQueryA(name, type, options, extra, records, reserved);
    static volatile LONG observations = 0;
    if (InterlockedIncrement(&observations) > 8) return status;
    const DWORD error = GetLastError();
    char line[100]{};
    _snprintf_s(line, sizeof(line), _TRUNCATE, "DnsQuery_A type=%u status=%ld raw_message=%d",
        type, status, (options & DNS_QUERY_RETURN_MESSAGE) != 0);
    WriteApiObservation(line);
    SetLastError(error);
    return status;
}

DNS_STATUS WINAPI HookDnsQueryEx(PDNS_QUERY_REQUEST request, PDNS_QUERY_RESULT result,
                                PDNS_QUERY_CANCEL cancel);
DNS_STATUS ObserveDnsQueryEx(DNS_STATUS status, WORD type, bool asynchronous)
{
    static volatile LONG observations = 0;
    if (InterlockedIncrement(&observations) > 8) return status;
    const DWORD error = GetLastError();
    char line[100]{};
    _snprintf_s(line, sizeof(line), _TRUNCATE, "DnsQueryEx type=%u status=%ld async=%d",
        type, status, asynchronous);
    WriteApiObservation(line);
    SetLastError(error);
    return status;
}

INT WSAAPI HookGetAddrInfoW(PCWSTR name, PCWSTR service, const ADDRINFOW *hints, PADDRINFOW *result);
INT ObserveWideResult(INT status, const ADDRINFOW *hints)
{
    static volatile LONG observations = 0;
    if (InterlockedIncrement(&observations) > 8) return status;
    const int error = WSAGetLastError();
    char line[100]{};
    _snprintf_s(line, sizeof(line), _TRUNCATE, "GetAddrInfoW status=%d family=%d type=%d flags=%d",
        status, hints ? hints->ai_family : 0, hints ? hints->ai_socktype : 0, hints ? hints->ai_flags : 0);
    WriteApiObservation(line);
    WSASetLastError(error);
    return status;
}

hostent *WSAAPI HookGetHostByName(const char *name);
hostent *ObserveHostResult(hostent *result)
{
    static volatile LONG observations = 0;
    if (InterlockedIncrement(&observations) > 8) return result;
    const int error = WSAGetLastError();
    unsigned fake = 0;
    if (result && result->h_addrtype == AF_INET && result->h_length == sizeof(IN_ADDR))
        for (unsigned i = 0; result->h_addr_list && result->h_addr_list[i] && i < 64; ++i)
        {
            IN_ADDR address{};
            memcpy(&address, result->h_addr_list[i], sizeof(address));
            if ((ntohl(address.s_addr) & 0xfffe0000u) == 0xc6120000u) ++fake;
        }
    char line[100]{};
    _snprintf_s(line, sizeof(line), _TRUNCATE, "gethostbyname success=%d fake_a=%u", result != nullptr, fake);
    WriteApiObservation(line);
    WSASetLastError(error);
    return result;
}

BOOL PASCAL HookConnectEx(SOCKET socket, const sockaddr *address, int length, PVOID sendBuffer,
                          DWORD sendLength, LPDWORD bytesSent, LPOVERLAPPED overlapped);
INT WSAAPI HookWSASendMsg(SOCKET socket, LPWSAMSG message, DWORD flags, LPDWORD bytes,
                         LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion);
INT WSAAPI HookExtensionWSASendMsg(SOCKET socket, LPWSAMSG message, DWORD flags, LPDWORD bytes,
                         LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion);
int WSAAPI HookWSAIoctl(SOCKET socket, DWORD code, LPVOID input, DWORD inputSize, LPVOID output,
                        DWORD outputSize, LPDWORD bytes, LPWSAOVERLAPPED overlapped,
                        LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    const int result = OriginalWSAIoctl(socket, code, input, inputSize, output, outputSize, bytes,
                                        overlapped, completion);
    const int error = WSAGetLastError();
    const GUID connectEx = WSAID_CONNECTEX;
    const GUID sendMsg = WSAID_WSASENDMSG;
    if (result == 0 && !overlapped && !completion && code == SIO_GET_EXTENSION_FUNCTION_POINTER &&
        input && inputSize == sizeof(GUID) && memcmp(input, &connectEx, sizeof(GUID)) == 0 &&
        output && outputSize >= sizeof(LPFN_CONNECTEX) && bytes && *bytes == sizeof(LPFN_CONNECTEX))
    {
        *static_cast<LPFN_CONNECTEX *>(output) = HookConnectEx;
        WriteTestReceipt("ConnectEx pointer_wrapped");
    }
    else if (result == 0 && !overlapped && !completion && code == SIO_GET_EXTENSION_FUNCTION_POINTER &&
        input && inputSize == sizeof(GUID) && memcmp(input, &sendMsg, sizeof(GUID)) == 0 &&
        output && outputSize >= sizeof(LPFN_WSASENDMSG) && bytes && *bytes == sizeof(LPFN_WSASENDMSG))
        *static_cast<LPFN_WSASENDMSG *>(output) = HookExtensionWSASendMsg;
    WSASetLastError(error);
    return result;
}
// WSAConnect / WinHttpConnect 只做调用归属诊断，原参数和返回值透传。
int WSAAPI HookWSAConnect(SOCKET socket, const sockaddr *address, int length, LPWSABUF caller,
                          LPWSABUF callee, LPQOS sendQos, LPQOS groupQos)
{
    ObserveLoopbackCaller(address, length, _ReturnAddress());
    WriteTestReceipt("observe_only WSAConnect");
    return OriginalWSAConnect(socket, address, length, caller, callee, sendQos, groupQos);
}
INT WSAAPI HookGetAddrInfoExW(PCWSTR name, PCWSTR service, DWORD nameSpace, LPGUID provider,
                              const ADDRINFOEXW *hints, PADDRINFOEXW *result, timeval *timeout,
                              LPOVERLAPPED overlapped,
                              LPLOOKUPSERVICE_COMPLETION_ROUTINE completion, LPHANDLE handle);
HINTERNET WINAPI HookWinHttpConnect(HINTERNET session, LPCWSTR server, INTERNET_PORT port,
                                    DWORD reserved)
{
    WriteTestReceipt("observe_only WinHttpConnect");
    return OriginalWinHttpConnect(session, server, port, reserved);
}

// 每次 API 调用重新读取手动复位事件；事件不存在或已关闭均使用原 API。
// 不创建工作线程，也不在开关切换时卸载 DLL。
// 托管宿主退出时立即停用，即使控制器正在等待管道EOF也不再接管新调用。
bool IsManagedOwnerAlive()
{
    const auto owner = ReadEnvironmentValue("ZDC_OWNER_PID");
    if (owner.empty())
        return true; // 独立命令行模式没有额外宿主。
    uint64_t pid = 0;
    for (char ch : owner)
    {
        if (ch < '0' || ch > '9')
            return false;
        pid = pid * 10 + static_cast<unsigned>(ch - '0');
        if (pid > MAXDWORD)
            return false;
    }
    const auto created = ReadEnvironmentValue("ZDC_OWNER_CREATED");
    uint64_t expectedCreation = 0;
    if (created.empty())
        return false;
    for (char ch : created)
    {
        if (ch < '0' || ch > '9' || expectedCreation > (UINT64_MAX - 9) / 10)
            return false;
        expectedCreation = expectedCreation * 10 + static_cast<unsigned>(ch - '0');
    }
    HANDLE process = pid ? OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE,
                                      static_cast<DWORD>(pid)) : nullptr;
    if (!process)
        return false;
    FILETIME creation{}, exit{}, kernel{}, user{};
    const bool hasTimes = GetProcessTimes(process, &creation, &exit, &kernel, &user) != FALSE;
    const uint64_t actualCreation = (static_cast<uint64_t>(creation.dwHighDateTime) << 32) |
                                    creation.dwLowDateTime;
    const bool alive = hasTimes && actualCreation == expectedCreation &&
                       WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
    CloseHandle(process);
    return alive;
}
bool IsControlSessionAvailable(bool requireActive)
{
    if (controlLease) return LeaseOwnerAlive(requireActive);
    if (!IsManagedOwnerAlive())
        return false;
    const auto name = ReadEnvironmentValue("ZDC_ACTIVE_EVENT");
    const auto pidText = ReadEnvironmentValue("ZDC_CONTROLLER_PID");
    if (name.empty() || pidText.empty())
    {
        return false;
    }
    uint64_t pid = 0;
    for (char ch : pidText)
    {
        if (ch < '0' || ch > '9')
        {
            return false;
        }
        pid = pid * 10 + static_cast<unsigned>(ch - '0');
        if (pid > MAXDWORD)
        {
            return false;
        }
    }
    if (pid == 0)
    {
        return false;
    }
    HANDLE controller = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(pid));
    if (!controller)
    {
        return false;
    }
    // 控制器崩溃或退出时，即使事件仍被其他句柄保留，也恢复原 API。
    const bool alive = WaitForSingleObject(controller, 0) == WAIT_TIMEOUT;
    if (!alive)
    {
        CloseHandle(controller);
        return false;
    }
    HANDLE event = OpenEventA(SYNCHRONIZE, FALSE, name.c_str());
    const bool enabled = event &&
                         (!requireActive || WaitForSingleObject(event, 0) == WAIT_OBJECT_0) &&
                         WaitForSingleObject(controller, 0) == WAIT_TIMEOUT;
    if (event)
    {
        CloseHandle(event);
    }
    CloseHandle(controller);
    return enabled;
}

// 网络调用遵循开关；子进程传播只要求控制器仍存活且事件存在。
// off 期间先让新子进程保留钩子，随后 on 才能接管它的新连接。
bool IsRoutingEnabled()
{
    return IsControlSessionAvailable(true);
}
bool IsControlSessionAlive()
{
    return IsControlSessionAvailable(false);
}

// IN_ADDR 保存二进制 IPv4 地址；DWORD 是 Windows 的 32 位无符号整数。
struct PhysicalNetworkConfig
{
    IN_ADDR source{};
    IN_ADDR dns{};
    DWORD index{};
};
bool ReadPhysicalNetworkConfig(PhysicalNetworkConfig &networkConfig)
{
    const auto source = ReadEnvironmentValue("ZDC_SOURCE_IP");
    const auto dns = ReadEnvironmentValue("ZDC_DNS");
    const auto index = ReadEnvironmentValue("ZDC_IFINDEX");
    if (source.empty() || dns.empty() || index.empty() ||
        inet_pton(AF_INET, source.c_str(), &networkConfig.source) != 1 ||
        inet_pton(AF_INET, dns.c_str(), &networkConfig.dns) != 1)
    {
        return false;
    }
    uint64_t number = 0;
    for (char ch : index)
    {
        if (ch < '0' || ch > '9')
        {
            return false;
        }
        number = number * 10 + static_cast<unsigned>(ch - '0');
        if (number > MAXDWORD)
        {
            return false;
        }
    }
    networkConfig.index = static_cast<DWORD>(number);
    return networkConfig.index != 0 && networkConfig.source.s_addr != INADDR_ANY &&
           networkConfig.dns.s_addr != INADDR_ANY;
}

// IP_UNICAST_IF 的 IPv4 接口编号使用网络字节序。套接字已经绑定时保留原端口，
// 仅接受与配置一致的源地址；失败直接返回错误，不悄悄改走默认代理路径。
struct DatagramPacketInfo
{
    alignas(WSACMSGHDR) char control[WSA_CMSG_SPACE(sizeof(IN_PKTINFO))]{};
};

bool PrepareSynchronousDatagram(SOCKET socket, const sockaddr *destination, int length,
                               DWORD flags, WSAMSG &message, DatagramPacketInfo &packet) noexcept
{
    const int originalError = WSAGetLastError();
    bool prepared = false;
    try
    {
        int type = 0, typeLength = sizeof(type);
        // Stream sockets ignore WSASendTo's destination; inspect type first.
        if (IsRoutingEnabled() && getsockopt(socket, SOL_SOCKET, SO_TYPE,
            reinterpret_cast<char *>(&type), &typeLength) == 0 && type == SOCK_DGRAM &&
            destination && length >= static_cast<int>(sizeof(sockaddr_in)) &&
            destination->sa_family == AF_INET && !(flags & ~(MSG_DONTROUTE | MSG_PARTIAL | MSG_OOB)))
        {
            const DWORD host = ntohl(reinterpret_cast<const sockaddr_in *>(destination)->sin_addr.s_addr);
            DWORD chosenInterface = 0;
            int optionLength = sizeof(chosenInterface), broadcast = 0, broadcastLength = sizeof(broadcast);
            sockaddr_in local{};
            sockaddr_storage peer{};
            int peerLength = sizeof(peer);
            const bool unconnected = getpeername(socket, reinterpret_cast<sockaddr *>(&peer), &peerLength) == SOCKET_ERROR &&
                WSAGetLastError() == WSAENOTCONN;
            int localLength = sizeof(local);
            const int localStatus = getsockname(socket, reinterpret_cast<sockaddr *>(&local), &localLength);
            const bool wildcard = (localStatus == SOCKET_ERROR && WSAGetLastError() == WSAEINVAL) ||
                (localStatus == 0 && local.sin_family == AF_INET && local.sin_addr.s_addr == INADDR_ANY);
            PhysicalNetworkConfig config;
            if ((host >> 24) != 0 && (host >> 24) != 127 && (host >> 28) < 14 &&
                (host & 0xfffe0000u) != 0xc6120000u && wildcard && unconnected &&
                getsockopt(socket, IPPROTO_IP, IP_UNICAST_IF, reinterpret_cast<char *>(&chosenInterface), &optionLength) == 0 && !chosenInterface &&
                getsockopt(socket, SOL_SOCKET, SO_BROADCAST, reinterpret_cast<char *>(&broadcast), &broadcastLength) == 0 && !broadcast &&
                ReadPhysicalNetworkConfig(config))
            {
                message.Control.buf = packet.control;
                message.Control.len = sizeof(packet.control);
                auto *header = WSA_CMSG_FIRSTHDR(&message);
                header->cmsg_level = IPPROTO_IP;
                header->cmsg_type = IP_PKTINFO;
                header->cmsg_len = WSA_CMSG_LEN(sizeof(IN_PKTINFO));
                auto *info = reinterpret_cast<IN_PKTINFO *>(WSA_CMSG_DATA(header));
                info->ipi_addr = config.source;
                info->ipi_ifindex = config.index; // IN_PKTINFO takes a host-order index.
                prepared = true;
            }
        }
    }
    catch (...) {}
    WSASetLastError(originalError);
    return prepared;
}

LPFN_WSASENDMSG GetProviderSendMessage(SOCKET socket)
{
    // Obtain the extension for this socket's provider; never cache a global
    // provider pointer or change socket-wide routing/binding options.
    const GUID id = WSAID_WSASENDMSG;
    LPFN_WSASENDMSG sendMessage = nullptr;
    DWORD returned = 0;
    if (OriginalWSAIoctl(socket, SIO_GET_EXTENSION_FUNCTION_POINTER,
        const_cast<GUID *>(&id), sizeof(id), &sendMessage, sizeof(sendMessage),
        &returned, nullptr, nullptr) == SOCKET_ERROR) return nullptr;
    if (returned != sizeof(sendMessage) || !sendMessage)
    { WSASetLastError(WSAEOPNOTSUPP); return nullptr; }
    return sendMessage;
}

int SendSynchronousDatagram(SOCKET socket, WSAMSG &message, DWORD flags, LPDWORD bytes)
{
    const auto sendMessage = GetProviderSendMessage(socket);
    return sendMessage ? sendMessage(socket, &message, flags, bytes, nullptr, nullptr) : SOCKET_ERROR;
}

int WSAAPI HookSendTo(SOCKET socket, const char *buffer, int length, int flags,
                     const sockaddr *destination, int destinationLength)
{
    WSABUF data{length >= 0 ? static_cast<ULONG>(length) : 0, const_cast<char *>(buffer)};
    WSAMSG message{};
    message.name = const_cast<sockaddr *>(destination);
    message.namelen = destinationLength;
    message.lpBuffers = &data;
    message.dwBufferCount = 1;
    DatagramPacketInfo packet;
    if (length >= 0 && PrepareSynchronousDatagram(socket, destination, destinationLength, flags, message, packet))
    {
        DWORD bytes = 0;
        const int result = SendSynchronousDatagram(socket, message, flags, &bytes);
        return result == 0 ? static_cast<int>(bytes) : SOCKET_ERROR;
    }
    return OriginalSendTo(socket, buffer, length, flags, destination, destinationLength);
}

int WSAAPI HookWSASendTo(SOCKET socket, LPWSABUF buffers, DWORD count, LPDWORD bytes, DWORD flags,
                        const sockaddr *destination, int length, LPWSAOVERLAPPED overlapped,
                        LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    WSAMSG message{};
    message.name = const_cast<sockaddr *>(destination);
    message.namelen = length;
    message.lpBuffers = buffers;
    message.dwBufferCount = count;
    DatagramPacketInfo packet;
    if (!overlapped && !completion && PrepareSynchronousDatagram(socket, destination, length, flags, message, packet))
        return SendSynchronousDatagram(socket, message, flags, bytes);
    return OriginalWSASendTo(socket, buffers, count, bytes, flags, destination, length, overlapped, completion);
}

INT DispatchSendMessage(LPFN_WSASENDMSG original, SOCKET socket, LPWSAMSG message, DWORD flags, LPDWORD bytes,
                       LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    // Pending I/O keeps the application's original buffers/callback/IOCP path.
    // Only synchronous calls may borrow our stack-based ancillary storage.
    if (message && !overlapped && !completion && !message->Control.buf && !message->Control.len)
    {
        auto copy = *message;
        DatagramPacketInfo packet;
        if (PrepareSynchronousDatagram(socket, copy.name, copy.namelen, flags, copy, packet))
            return original(socket, &copy, flags, bytes, nullptr, nullptr);
    }
    return original(socket, message, flags, bytes, overlapped, completion);
}

INT WSAAPI HookWSASendMsg(SOCKET socket, LPWSAMSG message, DWORD flags, LPDWORD bytes,
                         LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    return DispatchSendMessage(OriginalWSASendMsg, socket, message, flags, bytes, overlapped, completion);
}

INT WSAAPI HookExtensionWSASendMsg(SOCKET socket, LPWSAMSG message, DWORD flags, LPDWORD bytes,
                         LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    const int error = WSAGetLastError();
    const auto original = GetProviderSendMessage(socket);
    if (!original) return SOCKET_ERROR;
    WSASetLastError(error);
    return DispatchSendMessage(original, socket, message, flags, bytes, overlapped, completion);
}

bool BindSocketToPhysicalAdapter(SOCKET socket, const PhysicalNetworkConfig &networkConfig)
{
    sockaddr_in current{};
    int length = sizeof(current);
    const int status = getsockname(socket, reinterpret_cast<sockaddr *>(&current), &length);
    if (status != 0 && WSAGetLastError() != WSAEINVAL)
        return false;
    if (status == 0 &&
        (current.sin_family != AF_INET || (current.sin_addr.s_addr != INADDR_ANY &&
                                           current.sin_addr.s_addr != networkConfig.source.s_addr)))
    {
        WSASetLastError(WSAEADDRNOTAVAIL);
        return false;
    }
    DWORD previous = 0;
    int optionLength = sizeof(previous);
    if (getsockopt(socket, IPPROTO_IP, IP_UNICAST_IF, reinterpret_cast<char *>(&previous),
                   &optionLength) != 0)
        return false;
    const DWORD index = htonl(networkConfig.index);
    if (setsockopt(socket, IPPROTO_IP, IP_UNICAST_IF, reinterpret_cast<const char *>(&index),
                   sizeof(index)) != 0)
        return false;
    // wildcard 已占用端口时，只选接口；原 connect 再选择该接口的源地址。
    if (status == 0 && (current.sin_port != 0 || current.sin_addr.s_addr != INADDR_ANY))
        return true;
    sockaddr_in local{};
    local.sin_family = AF_INET;
    local.sin_addr = networkConfig.source;
    if (bind(socket, reinterpret_cast<const sockaddr *>(&local), sizeof(local)) == 0)
        return true;
    const int error = WSAGetLastError();
    const DWORD restore = htonl(previous);
    setsockopt(socket, IPPROTO_IP, IP_UNICAST_IF, reinterpret_cast<const char *>(&restore),
               sizeof(restore));
    WSASetLastError(error);
    return false;
}


// DNS 报文使用网络字节序（高字节在前），与 x64 主机常用的小端顺序不同。
// 以下辅助函数只负责报文读写；真正发出 DNS 查询的是 ResolveViaPhysicalDns。
uint16_t ReadNetworkUInt16(const unsigned char *p)
{
    return static_cast<uint16_t>((static_cast<uint16_t>(p[0]) << 8) | p[1]);
}
void AppendNetworkUInt16(std::vector<unsigned char> &bytes, uint16_t value)
{
    bytes.push_back(static_cast<unsigned char>(value >> 8));
    bytes.push_back(static_cast<unsigned char>(value));
}
std::string ToLowerAscii(std::string value)
{
    for (auto &ch : value)
    {
        ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    }
    return value;
}

// DNS 名称解析同时检查压缩指针、长度与循环次数，避免读取到数据包边界外。
bool ReadDnsName(const unsigned char *bytes, size_t size, size_t &position, std::string &name)
{
    size_t cursor = position;
    bool jumped = false;
    name.clear();
    for (unsigned steps = 0; steps < 128; ++steps)
    {
        if (cursor >= size)
        {
            return false;
        }
        const unsigned n = bytes[cursor++];
        if (n == 0)
        {
            if (!jumped)
            {
                position = cursor;
            }
            return name.size() <= 253;
        }
        if ((n & 0xc0) == 0xc0)
        {
            if (cursor >= size)
            {
                return false;
            }
            const size_t target = ((n & 0x3f) << 8) | bytes[cursor++];
            if (target >= size)
            {
                return false;
            }
            if (!jumped)
            {
                position = cursor;
            }
            jumped = true;
            cursor = target;
            continue;
        }
        if ((n & 0xc0) != 0 || n > 63 || cursor + n > size)
        {
            return false;
        }
        if (!name.empty())
        {
            name.push_back('.');
        }
        for (unsigned i = 0; i < n; ++i)
        {
            const auto ch = bytes[cursor++];
            if (ch <= 32 || ch >= 127 || ch == '.')
            {
                return false;
            }
            name.push_back(static_cast<char>(std::tolower(ch)));
        }
        if (name.size() > 253)
        {
            return false;
        }
    }
    return false;
}

struct DnsAnswer
{
    std::string owner;
    std::string alias;
    IN_ADDR address{};
    bool isAddress{};
};
int SelectDnsAddress(const std::vector<DnsAnswer> &answers, const std::string &question,
                     IN_ADDR &result, std::string *canonical)
{
    auto owner = question;
    for (unsigned hops = 0; hops < 16; ++hops)
    {
        for (const auto &a : answers)
        {
            if (a.owner == owner && a.isAddress)
            {
                // 保留域名查询的真实 IPv4；fake-IP 与空地址作为解析失败处理。
                const auto ip = ntohl(a.address.s_addr);
                if (ip == 0 || (ip & 0xfffe0000u) == 0xc6120000u)
                {
                    return EAI_FAIL;
                }
                result = a.address;
                if (canonical)
                    *canonical = owner;
                return 0;
            }
        }
        auto next = std::find_if(answers.begin(), answers.end(), [&](const auto &a) {
            return a.owner == owner && !a.isAddress && !a.alias.empty();
        });
        if (next == answers.end())
        {
            return EAI_NONAME;
        }
        owner = next->alias;
    }
    return EAI_FAIL;
}

int ParseDnsAnswer(const unsigned char *bytes, size_t size, uint16_t id,
                   const std::string &question, IN_ADDR &result, std::string *canonical = nullptr)
{
    if (size < 12 || ReadNetworkUInt16(bytes) != id)
    {
        return EAI_AGAIN;
    }
    const uint16_t flags = ReadNetworkUInt16(bytes + 2);
    if ((flags & 0x8000) == 0 || (flags & 0x7800) != 0 || (flags & 0x0200) != 0)
    {
        return EAI_AGAIN;
    }
    const unsigned rcode = flags & 15;
    if (rcode == 3)
    {
        return EAI_NONAME;
    }
    if (rcode != 0)
    {
        return EAI_FAIL;
    }
    if (ReadNetworkUInt16(bytes + 4) != 1)
    {
        return EAI_FAIL;
    }
    size_t position = 12;
    std::string echoed;
    if (!ReadDnsName(bytes, size, position, echoed) || echoed != question || position + 4 > size ||
        ReadNetworkUInt16(bytes + position) != 1 || ReadNetworkUInt16(bytes + position + 2) != 1)
    {
        return EAI_FAIL;
    }
    position += 4;
    const unsigned count = ReadNetworkUInt16(bytes + 6);
    if (count > 128)
    {
        return EAI_FAIL;
    }
    std::vector<DnsAnswer> answers;
    for (unsigned i = 0; i < count; ++i)
    {
        DnsAnswer answer;
        if (!ReadDnsName(bytes, size, position, answer.owner) || position + 10 > size)
        {
            return EAI_FAIL;
        }
        const unsigned type = ReadNetworkUInt16(bytes + position);
        const unsigned klass = ReadNetworkUInt16(bytes + position + 2);
        const size_t dataLength = ReadNetworkUInt16(bytes + position + 8);
        position += 10;
        if (position + dataLength > size)
        {
            return EAI_FAIL;
        }
        if (klass == 1 && type == 1 && dataLength == 4)
        {
            memcpy(&answer.address, bytes + position, 4);
            answer.isAddress = true;
            answers.push_back(answer);
        }
        else if (klass == 1 && type == 5)
        {
            size_t aliasPosition = position;
            if (!ReadDnsName(bytes, size, aliasPosition, answer.alias) ||
                aliasPosition != position + dataLength)
            {
                return EAI_FAIL;
            }
            answers.push_back(answer);
        }
        position += dataLength;
    }
    return SelectDnsAddress(answers, question, result, canonical);
}

// Keep DNS on the selected interface when the default route to its resolver
// goes elsewhere. A known HTTPS endpoint belongs to that same configured IP.
bool ConfigurePhysicalDoh(const PhysicalNetworkConfig &config, DNS_CUSTOM_SERVER &server)
{
    static const bool supportsCustomServers =
        GetProcAddress(GetModuleHandleW(L"dnsapi.dll"), "DnsGetApplicationSettings") != nullptr;
    if (!supportsCustomServers) return false;
    wchar_t ip[INET_ADDRSTRLEN]{};
    if (!InetNtopW(AF_INET, &config.dns, ip, INET_ADDRSTRLEN)) return false;
    static wchar_t primary[] = L"https://223.5.5.5/dns-query";
    static wchar_t secondary[] = L"https://223.6.6.6/dns-query";
    if (wcscmp(ip, L"223.5.5.5") == 0) server.pwszTemplate = primary;
    else if (wcscmp(ip, L"223.6.6.6") == 0) server.pwszTemplate = secondary;
    else return false;

    DWORD best = 0;
    if (GetBestInterface(config.dns.s_addr, &best) != NO_ERROR || !best || best == config.index)
        return false;
    server.dwServerType = DNS_CUSTOM_SERVER_TYPE_DOH;
    server.ServerAddr.Ipv4.sin_family = AF_INET;
    server.ServerAddr.Ipv4.sin_addr = config.dns;
    return true;
}

int ResolveOverPhysicalHttps(const std::string &name, const PhysicalNetworkConfig &config,
                             DNS_CUSTOM_SERVER &server, IN_ADDR &address, std::string *canonical)
{
    const std::wstring wideName(name.begin(), name.end());
    DNS_QUERY_REQUEST3 request{};
    request.Version = DNS_QUERY_REQUEST_VERSION3;
    request.QueryName = wideName.c_str();
    request.QueryType = DNS_TYPE_A;
    request.QueryOptions = DNS_QUERY_WIRE_ONLY | DNS_QUERY_TREAT_AS_FQDN;
    request.InterfaceIndex = config.index;
    request.cCustomServers = 1;
    request.pCustomServers = &server;
    DNS_QUERY_RESULT reply{};
    reply.Version = DNS_QUERY_RESULTS_VERSION1;
    const auto status = OriginalDnsQueryEx(reinterpret_cast<PDNS_QUERY_REQUEST>(&request), &reply, nullptr);
    const auto freeRecords = [](DNS_RECORD *records) { DnsRecordListFree(records, DnsFreeRecordList); };
    std::unique_ptr<DNS_RECORD, decltype(freeRecords)> records(reply.pQueryRecords, freeRecords);
    char receipt[96]{};
    _snprintf_s(receipt, sizeof(receipt), _TRUNCATE, "dns https if=%lu status=%ld", config.index, status);
    WriteTestReceipt(receipt);
    if (status == DNS_ERROR_RCODE_NAME_ERROR || status == DNS_INFO_NO_RECORDS) return EAI_NONAME;
    if (status != ERROR_SUCCESS) return EAI_AGAIN;
    auto asciiName = [](PCWSTR value) {
        std::string text;
        for (; value && *value; ++value)
        {
            if (*value > 127 || text.size() == 254) return std::string{};
            text.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(*value))));
        }
        if (!text.empty() && text.back() == '.') text.pop_back();
        return text;
    };
    std::vector<DnsAnswer> answers;
    for (auto record = reinterpret_cast<DNS_RECORDW *>(records.get()); record; record = record->pNext)
    {
        if (record->Flags.S.Section != DnsSectionAnswer) continue;
        if (answers.size() >= 128) return EAI_FAIL;
        if (record->wType == DNS_TYPE_A)
        {
            DnsAnswer a{asciiName(record->pName), {}, {}, true};
            a.address.s_addr = record->Data.A.IpAddress;
            answers.push_back(a);
        }
        else if (record->wType == DNS_TYPE_CNAME)
            answers.push_back({asciiName(record->pName), asciiName(record->Data.CNAME.pNameHost), {}, false});
    }
    return SelectDnsAddress(answers, name, address, canonical);
}

// 两条传输路径都查询选定网卡上的 DNS，并沿 CNAME 链取第一个有效 A 记录。
// UDP 路径使用独立套接字，截断响应返回重试错误。
int ResolveViaPhysicalDns(const std::string &input, const PhysicalNetworkConfig &networkConfig,
                          IN_ADDR &result, std::string *canonical = nullptr)
{
    std::string name = ToLowerAscii(input);
    if (!name.empty() && name.back() == '.')
    {
        name.pop_back();
    }
    if (name.empty() || name.size() > 253)
    {
        return EAI_NONAME;
    }
    USHORT id = 0;
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&id), sizeof(id),
                        BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0)
    {
        return EAI_FAIL;
    }
    std::vector<unsigned char> query;
    AppendNetworkUInt16(query, id);
    AppendNetworkUInt16(query, 0x0100);
    AppendNetworkUInt16(query, 1);
    AppendNetworkUInt16(query, 0);
    AppendNetworkUInt16(query, 0);
    AppendNetworkUInt16(query, 0);
    size_t start = 0;
    while (start < name.size())
    {
        const size_t end = name.find('.', start);
        const size_t stop = end == std::string::npos ? name.size() : end;
        const size_t length = stop - start;
        if (length == 0 || length > 63)
        {
            return EAI_NONAME;
        }
        query.push_back(static_cast<unsigned char>(length));
        for (size_t i = start; i < stop; ++i)
        {
            unsigned char ch = static_cast<unsigned char>(name[i]);
            if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_'))
            {
                return EAI_NONAME;
            }
            query.push_back(ch);
        }
        start = stop + 1;
    }
    query.push_back(0);
    AppendNetworkUInt16(query, 1);
    AppendNetworkUInt16(query, 1);
    DNS_CUSTOM_SERVER secureServer{};
    if (ConfigurePhysicalDoh(networkConfig, secureServer))
    {
        const int status = ResolveOverPhysicalHttps(name, networkConfig, secureServer, result, canonical);
        if (status != EAI_AGAIN) return status;
        // If HTTPS is unavailable, retry the original interface-bound UDP transport.
    }
    SOCKET socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket == INVALID_SOCKET)
    {
        return EAI_FAIL;
    }
    struct CloseSocket
    {
        SOCKET s;
        ~CloseSocket()
        {
            closesocket(s);
        }
    } closer{socket};
    if (!BindSocketToPhysicalAdapter(socket, networkConfig))
    {
        return EAI_FAIL;
    }
    DWORD timeout = 2000;
    if (setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char *>(&timeout),
                   sizeof(timeout)) != 0)
    {
        return EAI_FAIL;
    }
    if (setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char *>(&timeout),
                   sizeof(timeout)) != 0)
    {
        return EAI_FAIL;
    }
    sockaddr_in server{};
    server.sin_family = AF_INET;
    server.sin_port = htons(53);
    server.sin_addr = networkConfig.dns;
    if (sendto(socket, reinterpret_cast<const char *>(query.data()), static_cast<int>(query.size()),
               0, reinterpret_cast<const sockaddr *>(&server),
               sizeof(server)) != static_cast<int>(query.size()))
    {
        return EAI_AGAIN;
    }
    // 每个进程、每次查询使用各自的 UDP socket，日志显示实际源端口和事务号。
    sockaddr_in queryLocal{};
    int queryLocalLength = sizeof(queryLocal);
    getsockname(socket, reinterpret_cast<sockaddr *>(&queryLocal), &queryLocalLength);
    LARGE_INTEGER sentAt{};
    QueryPerformanceCounter(&sentAt);
    printf("[pid=%lu] DNS_SENT id=%u port=%u tick=%lld name=%s\n", GetCurrentProcessId(),
           static_cast<unsigned>(id), static_cast<unsigned>(ntohs(queryLocal.sin_port)),
           sentAt.QuadPart, name.c_str());
    fflush(stdout);
    std::array<unsigned char, 4096> response{};
    // 最多读取四次，防止错误来源或无关响应造成无限等待。
    for (unsigned attempt = 0; attempt < 4; ++attempt)
    {
        sockaddr_in sender{};
        int senderLength = sizeof(sender);
        const int n = recvfrom(socket, reinterpret_cast<char *>(response.data()),
                               static_cast<int>(response.size()), 0,
                               reinterpret_cast<sockaddr *>(&sender), &senderLength);
        if (n == SOCKET_ERROR)
        {
            return EAI_AGAIN;
        }
        if (sender.sin_family != AF_INET || sender.sin_port != server.sin_port ||
            sender.sin_addr.s_addr != server.sin_addr.s_addr || n < 12 ||
            ReadNetworkUInt16(response.data()) != id)
        {
            continue;
        }
        const int parseResult =
            ParseDnsAnswer(response.data(), static_cast<size_t>(n), id, name, result, canonical);
        LARGE_INTEGER receivedAt{};
        QueryPerformanceCounter(&receivedAt);
        printf("[pid=%lu] DNS_RECEIVED id=%u port=%u tick=%lld result=%d\n", GetCurrentProcessId(),
               static_cast<unsigned>(id), static_cast<unsigned>(ntohs(queryLocal.sin_port)),
               receivedAt.QuadPart, parseResult);
        fflush(stdout);
        return parseResult;
    }
    return EAI_AGAIN;
}

// 同步 GetAddrInfoExW 可要求两种名称：CNAME 最终名称与查询使用的完整域名。
// 使用自有结果对象并配套释放入口，避免把自分配字符串交给 Windows 释放器。
volatile LONG pendingDnsQueries = 0;
struct AsyncDnsQuery
{
    volatile LONG references = 2; // submitting thread and possible completion
    DNS_QUERY_REQUEST3 request{};
    DNS_CUSTOM_SERVER server{};
    PDNS_QUERY_COMPLETION_ROUTINE completion = nullptr;
    PVOID context = nullptr;
    void Release() noexcept
    {
        if (InterlockedDecrement(&references) == 0)
        {
            InterlockedDecrement(&pendingDnsQueries);
            delete this;
        }
    }
};

VOID WINAPI CompletePhysicalDns(PVOID context, PDNS_QUERY_RESULT result)
{
    auto *query = static_cast<AsyncDnsQuery *>(context);
    // Keep the caller's result, cancellation object and callback context intact.
    // The second reference also covers completion before DnsQueryEx returns.
    struct CompletionRelease { AsyncDnsQuery *query; ~CompletionRelease() { query->Release(); } } release{query};
    query->completion(query->context, result);
}

DNS_STATUS WINAPI HookDnsQueryEx(PDNS_QUERY_REQUEST request, PDNS_QUERY_RESULT result,
                                PDNS_QUERY_CANCEL cancel)
{
    const DWORD incomingError = GetLastError();
    const WORD type = request ? request->QueryType : 0;
    const bool asynchronous = request && request->pQueryCompletionCallback;
    DNS_QUERY_REQUEST3 copy{};
    DNS_CUSTOM_SERVER server{};
    AsyncDnsQuery *pending = nullptr;
    bool synchronousCopy = false;
    try
    {
        bool supported = request && result &&
            (request->Version == DNS_QUERY_REQUEST_VERSION1 || request->Version == DNS_QUERY_REQUEST_VERSION3) &&
            request->InterfaceIndex == 0 && !request->pDnsServerList;
        if (supported && request->Version == DNS_QUERY_REQUEST_VERSION3)
        {
            const auto *extended = reinterpret_cast<const DNS_QUERY_REQUEST3 *>(request);
            supported = !extended->cCustomServers && !extended->pCustomServers &&
                !extended->IsNetworkQueryRequired && !extended->RequiredNetworkIndex;
        }
        PhysicalNetworkConfig config;
        if (supported && IsRoutingEnabled() && ReadPhysicalNetworkConfig(config))
        {
            memcpy(&copy, request, request->Version == DNS_QUERY_REQUEST_VERSION3 ?
                sizeof(DNS_QUERY_REQUEST3) : sizeof(DNS_QUERY_REQUEST));
            copy.InterfaceIndex = config.index;
            const auto specialTransport = DNS_QUERY_USE_TCP_ONLY | DNS_QUERY_NO_WIRE_QUERY | DNS_QUERY_MULTICAST_ONLY;
            if (copy.QueryName && wcschr(copy.QueryName, L'.') &&
                !(copy.QueryOptions & specialTransport) && ConfigurePhysicalDoh(config, server))
            {
                server.ullFlags = DNS_CUSTOM_SERVER_UDP_FALLBACK;
                copy.Version = DNS_QUERY_REQUEST_VERSION3;
                copy.cCustomServers = 1;
                copy.pCustomServers = &server;
            }
            if (!asynchronous)
                synchronousCopy = true;
            else
            {
                if (InterlockedIncrement(&pendingDnsQueries) <= 256)
                {
                    pending = new (std::nothrow) AsyncDnsQuery;
                    if (pending)
                    {
                        pending->request = copy;
                        if (copy.pCustomServers)
                        {
                            pending->server = server;
                            pending->request.pCustomServers = &pending->server;
                        }
                        pending->completion = copy.pQueryCompletionCallback;
                        pending->context = copy.pQueryContext;
                        pending->request.pQueryCompletionCallback = CompletePhysicalDns;
                        pending->request.pQueryContext = pending;
                    }
                }
                if (!pending) InterlockedDecrement(&pendingDnsQueries);
            }
        }
    }
    catch (...) {} // Preserve the application's original call if setup fails.
    // Invoke Windows outside setup's exception boundary: never replay a query
    // because an application completion callback raises an exception.
    SetLastError(incomingError);
    if (pending)
    {
        const DNS_STATUS status = OriginalDnsQueryEx(
            reinterpret_cast<PDNS_QUERY_REQUEST>(&pending->request), result, cancel);
        const DWORD error = GetLastError();
        if (status != DNS_REQUEST_PENDING) pending->Release(); // Windows will not call completion.
        pending->Release();
        SetLastError(error);
        return ObserveDnsQueryEx(status, type, true);
    }
    if (synchronousCopy)
        return ObserveDnsQueryEx(OriginalDnsQueryEx(reinterpret_cast<PDNS_QUERY_REQUEST>(&copy), result, cancel), type, false);
    return ObserveDnsQueryEx(OriginalDnsQueryEx(request, result, cancel), type, asynchronous);
}

INT WSAAPI HookGetAddrInfoW(PCWSTR name, PCWSTR service, const ADDRINFOW *hints, PADDRINFOW *result)
{
    try
    {
        const ADDRINFOW defaults{};
        const auto *effective = hints ? hints : &defaults;
        const int supportedFlags = AI_ADDRCONFIG | AI_V4MAPPED | AI_NUMERICSERV;
        if (!name || !result || !IsRoutingEnabled() ||
            (effective->ai_family != AF_INET && effective->ai_family != AF_UNSPEC) ||
            (effective->ai_socktype != 0 && effective->ai_socktype != SOCK_STREAM) ||
            (effective->ai_protocol != 0 && effective->ai_protocol != IPPROTO_TCP) ||
            (effective->ai_flags & ~supportedFlags) || effective->ai_addrlen ||
            effective->ai_canonname || effective->ai_addr || effective->ai_next)
            return ObserveWideResult(OriginalGetAddrInfoW(name, service, hints, result), hints);
        bool domain = wcschr(name, L'.') != nullptr && _wcsicmp(name, L"localhost.") != 0;
        std::string ascii;
        for (const wchar_t *ch = name; *ch; ++ch)
        {
            if (*ch > 127) { domain = false; break; }
            ascii.push_back(static_cast<char>(*ch));
        }
        IN_ADDR address{};
        if (!domain || InetPtonW(AF_INET, name, &address) == 1)
            return ObserveWideResult(OriginalGetAddrInfoW(name, service, hints, result), hints);
        *result = nullptr;
        PhysicalNetworkConfig config;
        if (!ReadPhysicalNetworkConfig(config)) return EAI_FAIL;
        const int error = ResolveViaPhysicalDns(ascii, config, address);
        if (error) return error;
        wchar_t numeric[INET_ADDRSTRLEN]{};
        if (!InetNtopW(AF_INET, &address, numeric, INET_ADDRSTRLEN)) return EAI_FAIL;
        auto numericHints = *effective;
        numericHints.ai_family = AF_INET;
        numericHints.ai_flags |= AI_NUMERICHOST;
        return ObserveWideResult(OriginalGetAddrInfoW(numeric, service, &numericHints, result), hints);
    }
    catch (...) { if (result) *result = nullptr; return EAI_MEMORY; }
}

hostent *WSAAPI HookGetHostByName(const char *name)
{
    try
    {
        if (!name || !IsRoutingEnabled()) return ObserveHostResult(OriginalGetHostByName(name));
        bool domain = strchr(name, '.') != nullptr && _stricmp(name, "localhost.") != 0;
        for (const unsigned char *ch = reinterpret_cast<const unsigned char *>(name); *ch; ++ch)
            if (*ch > 127) { domain = false; break; }
        IN_ADDR numeric{};
        if (!domain || InetPtonA(AF_INET, name, &numeric) == 1)
            return ObserveHostResult(OriginalGetHostByName(name));
        PhysicalNetworkConfig config;
        if (!ReadPhysicalNetworkConfig(config)) { WSASetLastError(WSANO_RECOVERY); return nullptr; }
        IN_ADDR address{};
        std::string canonical;
        const int error = ResolveViaPhysicalDns(name, config, address, &canonical);
        if (error) { WSASetLastError(error); return nullptr; }
        // gethostbyname exposes thread-local storage, never caller-owned memory.
        // Each later call on this thread may replace it; no free API is needed.
        struct HostResult
        {
            hostent entry{};
            std::string canonical;
            IN_ADDR address{};
            char *aliases[1]{};
            char *addresses[2]{};
        };
        thread_local HostResult result;
        result.canonical = canonical.empty() ? name : canonical;
        result.address = address;
        result.addresses[0] = reinterpret_cast<char *>(&result.address);
        result.entry.h_name = result.canonical.data();
        result.entry.h_aliases = result.aliases;
        result.entry.h_addrtype = AF_INET;
        result.entry.h_length = sizeof(IN_ADDR);
        result.entry.h_addr_list = result.addresses;
        return ObserveHostResult(&result.entry);
    }
    catch (...) { WSASetLastError(WSA_NOT_ENOUGH_MEMORY); return nullptr; }
}

struct OwnedExtendedAddress
{
    ADDRINFOEX2W info{};
    sockaddr_in address{};
    std::wstring canonical;
    std::wstring fullyQualified;
};
std::mutex extendedResultsMutex;
std::unordered_map<PADDRINFOEXW, std::unique_ptr<OwnedExtendedAddress>> extendedResults;

VOID WSAAPI HookFreeAddrInfoExW(PADDRINFOEXW result)
{
    std::unique_ptr<OwnedExtendedAddress> owned;
    {
        std::lock_guard<std::mutex> lock(extendedResultsMutex);
        const auto found = extendedResults.find(result);
        if (found != extendedResults.end())
        {
            owned = std::move(found->second);
            extendedResults.erase(found);
        }
    }
    if (owned)
        WriteTestReceipt("GetAddrInfoExW owned_result_freed");
    else
        OriginalFreeAddrInfoExW(result);
}

INT WSAAPI HookGetAddrInfoExW(PCWSTR name, PCWSTR service, DWORD nameSpace, LPGUID provider,
                              const ADDRINFOEXW *hints, PADDRINFOEXW *result, timeval *timeout,
                              LPOVERLAPPED overlapped,
                              LPLOOKUPSERVICE_COMPLETION_ROUTINE completion, LPHANDLE handle)
{
    // 解析分支和开关读取都会分配字符串，异常边界覆盖整个 C API 入口。
    std::unique_ptr<ADDRINFOEXW, decltype(OriginalFreeAddrInfoExW)> temporary(
        nullptr, OriginalFreeAddrInfoExW);
    try
    {
        char receipt[120]{};
        _snprintf_s(receipt, sizeof(receipt), _TRUNCATE,
                    "GetAddrInfoExW ns=%lu family=%d type=%d flags=%d ov=%d cb=%d", nameSpace,
                    hints ? hints->ai_family : -1, hints ? hints->ai_socktype : -1,
                    hints ? hints->ai_flags : -1, overlapped != nullptr, completion != nullptr);
        WriteTestReceipt(receipt);
        const int supportedFlags = AI_CANONNAME | AI_FQDN | AI_ADDRCONFIG | AI_NUMERICHOST;
        static volatile LONG observations = 0;
        if (InterlockedIncrement(&observations) <= 8) WriteApiObservation(receipt);
        if (!IsRoutingEnabled() || !name || !hints || !result || provider || timeout ||
            overlapped || completion || (nameSpace != NS_DNS && nameSpace != NS_ALL) ||
            (hints->ai_family != AF_INET && hints->ai_family != AF_UNSPEC) ||
            (hints->ai_socktype != 0 && hints->ai_socktype != SOCK_STREAM) ||
            (hints->ai_protocol != 0 && hints->ai_protocol != IPPROTO_TCP) ||
            (hints->ai_flags & ~supportedFlags) || hints->ai_addrlen || hints->ai_canonname ||
            hints->ai_addr || hints->ai_next || hints->ai_blob || hints->ai_bloblen ||
            hints->ai_provider)
        {
            return OriginalGetAddrInfoExW(name, service, nameSpace, provider, hints, result,
                                          timeout, overlapped, completion, handle);
        }
        IN_ADDR numeric{};
        if (InetPtonW(AF_INET, name, &numeric) == 1 || (hints->ai_flags & AI_NUMERICHOST))
            return OriginalGetAddrInfoExW(name, service, nameSpace, provider, hints, result,
                                          timeout, overlapped, completion, handle);
        *result = nullptr;
        PADDRINFOEXW allocated = nullptr;
        // 本轮只接管完整 ASCII 域名，单标签搜索后缀和 IDN 保持系统原行为。
        std::string domain;
        for (const wchar_t *ch = name; *ch; ++ch)
        {
            if (*ch > 127)
                return OriginalGetAddrInfoExW(name, service, nameSpace, provider, hints, result,
                                              timeout, overlapped, completion, handle);
            domain.push_back(static_cast<char>(*ch));
        }
        if (domain.find('.') == std::string::npos)
            return OriginalGetAddrInfoExW(name, service, nameSpace, provider, hints, result,
                                          timeout, overlapped, completion, handle);
        *result = nullptr;
        if (handle)
            *handle = nullptr;
        PhysicalNetworkConfig config;
        if (!ReadPhysicalNetworkConfig(config))
            return EAI_FAIL;
        IN_ADDR real{};
        std::string canonical;
        const int error = ResolveViaPhysicalDns(domain, config, real, &canonical);
        if (error)
            return error;
        wchar_t addressText[INET_ADDRSTRLEN]{};
        if (!InetNtopW(AF_INET, &real, addressText, INET_ADDRSTRLEN))
            return EAI_FAIL;
        ADDRINFOEXW numericHints{};
        numericHints.ai_family = AF_INET;
        numericHints.ai_socktype = SOCK_STREAM;
        numericHints.ai_protocol = hints->ai_protocol;
        numericHints.ai_flags = AI_NUMERICHOST | (hints->ai_flags & AI_ADDRCONFIG);
        // 端口名称/数字端口的检查仍交给系统，随后深拷贝结果并立即释放临时链。
        const int allocatedError =
            OriginalGetAddrInfoExW(addressText, service, NS_DNS, nullptr, &numericHints, &allocated,
                                   nullptr, nullptr, nullptr, nullptr);
        temporary.reset(allocated); // 成功、错误返回和分配异常均使用同一释放路径。
        if (allocatedError)
            return allocatedError;
        if (!allocated || allocated->ai_family != AF_INET ||
            allocated->ai_addrlen != sizeof(sockaddr_in))
        {
            return EAI_FAIL;
        }
        auto owned = std::make_unique<OwnedExtendedAddress>();
        owned->address = *reinterpret_cast<sockaddr_in *>(allocated->ai_addr);
        owned->info.ai_flags = hints->ai_flags;
        owned->info.ai_family = AF_INET;
        owned->info.ai_socktype = allocated->ai_socktype;
        owned->info.ai_protocol = allocated->ai_protocol;
        owned->info.ai_addrlen = sizeof(sockaddr_in);
        owned->info.ai_addr = reinterpret_cast<sockaddr *>(&owned->address);
        owned->info.ai_version = ADDRINFOEX_VERSION_2;
        owned->canonical.assign(canonical.begin(), canonical.end());
        auto fqdn = ToLowerAscii(domain);
        if (!fqdn.empty() && fqdn.back() == '.')
            fqdn.pop_back();
        owned->fullyQualified.assign(fqdn.begin(), fqdn.end());
        if (hints->ai_flags & AI_CANONNAME)
            owned->info.ai_canonname = owned->canonical.data();
        else if (hints->ai_flags & AI_FQDN)
            owned->info.ai_canonname = owned->fullyQualified.data();
        if ((hints->ai_flags & (AI_CANONNAME | AI_FQDN)) == (AI_CANONNAME | AI_FQDN))
            owned->info.ai_fqdn = owned->fullyQualified.data();
        temporary.reset();
        auto key = reinterpret_cast<PADDRINFOEXW>(&owned->info);
        {
            std::lock_guard<std::mutex> lock(extendedResultsMutex);
            extendedResults.emplace(key, std::move(owned));
        }
        *result = key;
        char ip[INET_ADDRSTRLEN]{};
        inet_ntop(AF_INET, &real, ip, sizeof(ip));
        _snprintf_s(receipt, sizeof(receipt), _TRUNCATE,
                    "GetAddrInfoExW physical if=%lu real=%s owned=1", config.index, ip);
        WriteTestReceipt(receipt);
        return 0;
    }
    catch (...)
    {
        if (result)
            *result = nullptr;
        return EAI_MEMORY;
    }
}

// 输入是应用刚提交的域名；输出是原 getaddrinfo 分配的真实地址链。
// 这里只改变解析来源，因此应用仍可用原 freeaddrinfo 正常释放结果。
int WSAAPI HookGetAddrInfo(PCSTR node, PCSTR service, const ADDRINFOA *hints, PADDRINFOA *output)
{
    // A null hints pointer means an unspecified family/socket/protocol, not a
    // request to bypass routing. Windows still allocates the final numeric list.
    const ADDRINFOA defaultHints{};
    const ADDRINFOA *effective = hints ? hints : &defaultHints;
    static volatile LONG observedCalls = 0;
    if (InterlockedIncrement(&observedCalls) <= 8)
    {
        char event[120]{};
        _snprintf_s(event, sizeof(event), _TRUNCATE, "getaddrinfo hints=%d family=%d type=%d flags=%d",
            hints != nullptr, effective->ai_family, effective->ai_socktype, effective->ai_flags);
        WriteApiObservation(event);
    }
    char receipt[120]{};
    _snprintf_s(receipt, sizeof(receipt), _TRUNCATE,
                "getaddrinfo active=%d family=%d type=%d flags=%d public_test=%d",
                IsRoutingEnabled(), hints ? hints->ai_family : -1, hints ? hints->ai_socktype : -1,
                hints ? hints->ai_flags : -1, node && strcmp(node, "www.cloudflare.com") == 0);
    WriteTestReceipt(receipt);
    if (!IsRoutingEnabled() || !node ||
        (effective->ai_family != AF_INET && effective->ai_family != AF_UNSPEC) ||
        (effective->ai_socktype != 0 && effective->ai_socktype != SOCK_STREAM))
    {
        return OriginalGetAddrInfo(node, service, hints, output);
    }
    // 与扩展解析入口一致：单标签/本机名及非ASCII输入交回系统处理。
    bool fullAscii = strchr(node, '.') != nullptr && _stricmp(node, "localhost.") != 0;
    for (const unsigned char *ch = reinterpret_cast<const unsigned char *>(node); *ch; ++ch)
        if (*ch > 127)
        {
            fullAscii = false;
            break;
        }
    if (!fullAscii)
        return OriginalGetAddrInfo(node, service, hints, output);
    IN_ADDR numeric{};
    if (inet_pton(AF_INET, node, &numeric) == 1 || (effective->ai_flags & AI_NUMERICHOST))
    {
        return OriginalGetAddrInfo(node, service, hints, output);
    }
    if (!output)
    {
        return EAI_FAIL;
    }
    *output = nullptr;
    // 测试约定不请求 canonical name；数字地址分配可以安全交给原 freeaddrinfo
    // 释放。
    if (effective->ai_flags & AI_CANONNAME)
    {
        return EAI_BADFLAGS;
    }
    PhysicalNetworkConfig networkConfig;
    if (!ReadPhysicalNetworkConfig(networkConfig))
    {
        return EAI_FAIL;
    }
    try
    {
        IN_ADDR real{};
        const int error = ResolveViaPhysicalDns(node, networkConfig, real);
        if (error)
        {
            return error;
        }
        char address[INET_ADDRSTRLEN]{};
        if (!inet_ntop(AF_INET, &real, address, sizeof(address)))
        {
            return EAI_FAIL;
        }
        // 浏览器使用 AF_UNSPEC 请求双栈，本轮明确返回已验证的 IPv4 地址链。
        char dnsAddress[INET_ADDRSTRLEN]{};
        inet_ntop(AF_INET, &networkConfig.dns, dnsAddress, sizeof(dnsAddress));
        _snprintf_s(receipt, sizeof(receipt), _TRUNCATE,
                    "dns physical if=%lu server=%s real=%s public_test=%d", networkConfig.index,
                    dnsAddress, address, strcmp(node, "www.cloudflare.com") == 0);
        WriteTestReceipt(receipt);
        auto numericHints = *effective;
        numericHints.ai_family = AF_INET;
        numericHints.ai_flags |= AI_NUMERICHOST;
        return OriginalGetAddrInfo(address, service, &numericHints, output);
    }
    catch (...)
    {
        return EAI_MEMORY;
    }
}

// 此时应用已经取得真实地址。在原 connect 前绑定物理出口，其余连接语义不变。
// Chromium reads this configuration on startup and during its lazy proxy polls.
// Return an application-local direct view while enabled; never write OS settings.
// Returning to the original function lets later polls restore the system proxy.
BOOL WINAPI HookGetIEProxyConfig(WINHTTP_CURRENT_USER_IE_PROXY_CONFIG *config)
{
    const int savedError = GetLastError();
    try
    {
        if (config && IsRoutingEnabled())
        {
            *config = {};
            static volatile LONG observations = 0;
            if (InterlockedIncrement(&observations) <= 4) WriteApiObservation("WinHttpGetIEProxyConfig direct_view");
            WriteTestReceipt("proxy_config direct_application_view");
            SetLastError(savedError);
            return TRUE;
        }
        WriteTestReceipt("proxy_config original_system_view");
        static volatile LONG observations = 0;
        if (InterlockedIncrement(&observations) <= 4) WriteApiObservation("WinHttpGetIEProxyConfig system_view");
    }
    catch (...) {} // A failed observation returns to Windows' original configuration.
    SetLastError(savedError);
    return OriginalGetIEProxyConfig(config);
}

// Read kernel TCP RTT after establishment, including asynchronous ConnectEx sockets.
// Sampling is bounded per process and never waits for or changes an I/O completion.
void ObserveTcpRtt(SOCKET socket) noexcept
{
    const int savedError = WSAGetLastError();
    try
    {
        static volatile LONG64 lastSample = 0, lastAttempt = 0;
        const LONG64 now = static_cast<LONG64>(GetTickCount64());
        const LONG64 previous = InterlockedCompareExchange64(&lastSample, 0, 0);
        const LONG64 attempted = InterlockedCompareExchange64(&lastAttempt, 0, 0);
        // Rejected loopback/UDP/pending sockets do not consume the 3-second sample budget.
        // Bound unsuccessful eligibility probes too, avoiding syscalls on every I/O.
        if (now - previous >= 3000 && now - attempted >= 250 &&
            InterlockedCompareExchange64(&lastAttempt, now, attempted) == attempted && IsControlSessionAlive())
        {
            sockaddr_in peer{}, local{};
            int peerLength = sizeof(peer), localLength = sizeof(local);
            if (getpeername(socket, reinterpret_cast<sockaddr *>(&peer), &peerLength) == 0 &&
                peer.sin_family == AF_INET && getsockname(socket, reinterpret_cast<sockaddr *>(&local), &localLength) == 0 &&
                local.sin_family == AF_INET)
            {
                const DWORD host = ntohl(peer.sin_addr.s_addr);
                if ((host >> 24) != 0 && (host >> 24) != 127 && (host >> 28) < 14)
                {
                    TCP_INFO_v0 info{};
                    DWORD version = 0, bytes = 0;
                    if (OriginalWSAIoctl(socket, SIO_TCP_INFO, &version, sizeof(version), &info, sizeof(info),
                                         &bytes, nullptr, nullptr) == 0 && bytes >= sizeof(info) &&
                        info.State == TCPSTATE_ESTABLISHED && info.RttUs > 0 && info.RttUs <= 120000000)
                    {
                        const bool enabled = IsRoutingEnabled();
                        PhysicalNetworkConfig physical;
                        if (!enabled || (ReadPhysicalNetworkConfig(physical) &&
                            physical.source.s_addr == local.sin_addr.s_addr))
                        {
                            if (InterlockedCompareExchange64(&lastSample, now, previous) != previous)
                            { WSASetLastError(savedError); return; }
                            PublishConnectionTiming(info.RttUs / 1000.0, static_cast<ULONGLONG>(now),
                                                    enabled ? physical.index : 0, local, peer, info);
                            WriteTestReceipt("tcp_rtt_sampled");
                        }
                    }
                }
            }
        }
    }
    catch (...) {} // Observation failure must leave the application's socket untouched.
    WSASetLastError(savedError);
}
int WSAAPI HookSend(SOCKET socket, const char *buffer, int length, int flags)
{
    ObserveTcpRtt(socket);
    return OriginalSend(socket, buffer, length, flags);
}
int WSAAPI HookRecv(SOCKET socket, char *buffer, int length, int flags)
{
    ObserveTcpRtt(socket);
    return OriginalRecv(socket, buffer, length, flags);
}
int WSAAPI HookWSASend(SOCKET socket, LPWSABUF buffers, DWORD count, LPDWORD sent, DWORD flags,
                       LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    ObserveTcpRtt(socket);
    return OriginalWSASend(socket, buffers, count, sent, flags, overlapped, completion);
}
int WSAAPI HookWSARecv(SOCKET socket, LPWSABUF buffers, DWORD count, LPDWORD received, LPDWORD flags,
                       LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    ObserveTcpRtt(socket);
    return OriginalWSARecv(socket, buffers, count, received, flags, overlapped, completion);
}

int WSAAPI HookConnect(SOCKET socket, const sockaddr *address, int length)
{
    ObserveLoopbackCaller(address, length, _ReturnAddress());
    char receipt[160]{};
    // 仅诊断模式记录 socket 类型与目标，分清 IPv6/UDP 路由探测和实际 TCP 请求。
    // 保存错误码，观察本身不改变传给应用的 Winsock 错误状态。
    const int priorError = WSAGetLastError();
    if (GetEnvironmentVariableA("ZDC_TEST_RECEIPT", nullptr, 0) > 0)
    {
        int observedType = -1, typeLength = sizeof(observedType);
        getsockopt(socket, SOL_SOCKET, SO_TYPE, reinterpret_cast<char *>(&observedType),
                   &typeLength);
        char target[INET6_ADDRSTRLEN]{};
        unsigned port = 0;
        if (address && address->sa_family == AF_INET &&
            length >= static_cast<int>(sizeof(sockaddr_in)))
        {
            const auto ipv4 = reinterpret_cast<const sockaddr_in *>(address);
            inet_ntop(AF_INET, &ipv4->sin_addr, target, sizeof(target));
            port = ntohs(ipv4->sin_port);
        }
        else if (address && address->sa_family == AF_INET6 &&
                 length >= static_cast<int>(sizeof(sockaddr_in6)))
        {
            const auto ipv6 = reinterpret_cast<const sockaddr_in6 *>(address);
            inet_ntop(AF_INET6, &ipv6->sin6_addr, target, sizeof(target));
            port = ntohs(ipv6->sin6_port);
        }
        _snprintf_s(receipt, sizeof(receipt), _TRUNCATE,
                    "connect active=%d family=%d type=%d target=%s port=%u socket=%llx",
                    IsRoutingEnabled(), address ? address->sa_family : -1, observedType, target,
                    port, static_cast<unsigned long long>(socket));
        WriteTestReceipt(receipt);
    }
    WSASetLastError(priorError);
    auto passthrough = [&]() {
        const int result = OriginalConnect(socket, address, length);
        const int error = WSAGetLastError();
        if (result == 0) ObserveTcpRtt(socket);
        _snprintf_s(receipt, sizeof(receipt), _TRUNCATE,
                    "connect passthrough socket=%llx result=%d error=%d",
                    static_cast<unsigned long long>(socket), result,
                    result == SOCKET_ERROR ? error : 0);
        WriteTestReceipt(receipt);
        WSASetLastError(error);
        return result;
    };
    if (!IsRoutingEnabled() || !address || length < static_cast<int>(sizeof(sockaddr_in)) ||
        address->sa_family != AF_INET)
    {
        return passthrough();
    }
    // 本机回环通信不应被绑定到物理网卡。
    if ((ntohl(reinterpret_cast<const sockaddr_in *>(address)->sin_addr.s_addr) >> 24) == 127)
        return passthrough();
    int type = 0;
    int typeLength = sizeof(type);
    if (getsockopt(socket, SOL_SOCKET, SO_TYPE, reinterpret_cast<char *>(&type), &typeLength) != 0)
    {
        return SOCKET_ERROR;
    }
    if (type == SOCK_DGRAM)
    {
        // UDP connect 只设置默认对端，不发送数据。保留已有端口和应用显式绑定；
        // 仅接管尚未 connected 的单播 socket，已有会话及断开/重连语义保持原样。
        const auto destination = reinterpret_cast<const sockaddr_in *>(address);
        const DWORD host = ntohl(destination->sin_addr.s_addr);
        if ((host & 0xfffe0000u) == 0xc6120000u)
        {
            // 当前 TUN 的 fake-IP 不是物理网可达地址；此处不尝试猜测真实目标。
            WriteTestReceipt("udp fake_ip_rejected");
            WSASetLastError(WSAEADDRNOTAVAIL);
            return SOCKET_ERROR;
        }
        sockaddr_in peer{};
        int peerLength = sizeof(peer);
        if ((host >> 24) == 0 || (host >> 24) == 127 || (host >> 28) >= 14)
            return passthrough();
        const int peerStatus =
            getpeername(socket, reinterpret_cast<sockaddr *>(&peer), &peerLength);
        if (peerStatus == 0 || WSAGetLastError() != WSAENOTCONN)
            return passthrough();
        BOOL broadcast = FALSE;
        int broadcastLength = sizeof(broadcast);
        if (getsockopt(socket, SOL_SOCKET, SO_BROADCAST, reinterpret_cast<char *>(&broadcast),
                       &broadcastLength) != 0 ||
            broadcast)
            return passthrough(); // 广播用途的 socket 保留应用自己的接口选择。
        PhysicalNetworkConfig config;
        if (!ReadPhysicalNetworkConfig(config))
        {
            WSASetLastError(WSAEINVAL);
            return SOCKET_ERROR;
        }
        sockaddr_in local{};
        int localLength = sizeof(local);
        const int localStatus =
            getsockname(socket, reinterpret_cast<sockaddr *>(&local), &localLength);
        if (localStatus != 0 && WSAGetLastError() != WSAEINVAL)
            return SOCKET_ERROR;
        if (localStatus == 0 && local.sin_addr.s_addr != INADDR_ANY &&
            local.sin_addr.s_addr != config.source.s_addr)
        {
            WriteTestReceipt("udp explicit_other_source_passthrough");
            return passthrough();
        }
        DWORD previous = 0;
        int optionLength = sizeof(previous);
        if (getsockopt(socket, IPPROTO_IP, IP_UNICAST_IF, reinterpret_cast<char *>(&previous),
                       &optionLength) != 0)
            return SOCKET_ERROR;
        const DWORD selected = htonl(config.index);
        if (setsockopt(socket, IPPROTO_IP, IP_UNICAST_IF, reinterpret_cast<const char *>(&selected),
                       sizeof(selected)) != 0)
            return SOCKET_ERROR;
        const int result = OriginalConnect(socket, address, length);
        const int error = WSAGetLastError();
        if (result == SOCKET_ERROR && error != WSAEWOULDBLOCK)
        {
            const DWORD restore = htonl(previous);
            setsockopt(socket, IPPROTO_IP, IP_UNICAST_IF, reinterpret_cast<const char *>(&restore),
                       sizeof(restore));
        }
        localLength = sizeof(local);
        char source[INET_ADDRSTRLEN]{};
        if (getsockname(socket, reinterpret_cast<sockaddr *>(&local), &localLength) == 0)
            inet_ntop(AF_INET, &local.sin_addr, source, sizeof(source));
        _snprintf_s(receipt, sizeof(receipt), _TRUNCATE,
                    "udp if=%lu local=%s port=%u result=%d error=%d", config.index, source,
                    ntohs(local.sin_port), result, result == SOCKET_ERROR ? error : 0);
        WriteTestReceipt(receipt);
        WSASetLastError(error);
        return result;
    }
    if (type != SOCK_STREAM)
    {
        return passthrough();
    }
    PhysicalNetworkConfig networkConfig;
    if (!ReadPhysicalNetworkConfig(networkConfig))
    {
        WSASetLastError(WSAEINVAL);
        return SOCKET_ERROR;
    }
    if (!BindSocketToPhysicalAdapter(socket, networkConfig))
    {
        WriteTestReceipt("connect bind_failed");
        return SOCKET_ERROR;
    }
    char target[INET_ADDRSTRLEN]{}, local[INET_ADDRSTRLEN]{};
    inet_ntop(AF_INET, &reinterpret_cast<const sockaddr_in *>(address)->sin_addr, target,
              sizeof(target));
    sockaddr_in bound{};
    int boundLength = sizeof(bound);
    if (getsockname(socket, reinterpret_cast<sockaddr *>(&bound), &boundLength) == 0)
    {
        inet_ntop(AF_INET, &bound.sin_addr, local, sizeof(local));
    }
    _snprintf_s(receipt, sizeof(receipt), _TRUNCATE, "connect bound if=%lu local=%s target=%s",
                networkConfig.index, local, target);
    WriteTestReceipt(receipt);
    const int result = OriginalConnect(socket, address, length);
    const int error = WSAGetLastError();
    if (result == 0) ObserveTcpRtt(socket);
    _snprintf_s(receipt, sizeof(receipt), _TRUNCATE, "connect result=%d error=%d", result, result == SOCKET_ERROR ? error : 0);
    WriteTestReceipt(receipt);
    WSASetLastError(error);
    return result;
}

// ConnectEx 要求调用者预先 bind。通配地址已经占用端口时不重复 bind，
// 只指定 IP_UNICAST_IF，由原 ConnectEx 在该接口上选择最终源地址。
// 此处不等待、不触碰 OVERLAPPED、IOCP 或发送缓冲区，完成通知仍由 Windows 发出。
BOOL PASCAL HookConnectEx(SOCKET socket, const sockaddr *address, int length, PVOID sendBuffer,
                          DWORD sendLength, LPDWORD bytesSent, LPOVERLAPPED overlapped)
{
    ObserveLoopbackCaller(address, length, _ReturnAddress());
    try
    {
        LPFN_CONNECTEX original = nullptr;
        GUID id = WSAID_CONNECTEX;
        DWORD bytes = 0;
        if (OriginalWSAIoctl(socket, SIO_GET_EXTENSION_FUNCTION_POINTER, &id, sizeof(id), &original,
                             sizeof(original), &bytes, nullptr, nullptr) != 0 ||
            !original)
        {
            return FALSE;
        }
        const bool active = IsRoutingEnabled();
        DWORD previousInterface = 0, selectedInterface = 0;
        bool changedInterface = false;
        if (active && address && length >= static_cast<int>(sizeof(sockaddr_in)) &&
            address->sa_family == AF_INET &&
            (ntohl(reinterpret_cast<const sockaddr_in *>(address)->sin_addr.s_addr) >> 24) != 127)
        {
            PhysicalNetworkConfig config;
            sockaddr_in local{};
            int localLength = sizeof(local);
            if (!ReadPhysicalNetworkConfig(config))
            {
                WSASetLastError(WSAEINVAL);
                return FALSE;
            }
            if (getsockname(socket, reinterpret_cast<sockaddr *>(&local), &localLength) != 0)
            {
                return FALSE; // 未 bind 的 socket 保持 ConnectEx 的错误约定。
            }
            if (local.sin_family != AF_INET || (local.sin_addr.s_addr != INADDR_ANY &&
                                                local.sin_addr.s_addr != config.source.s_addr))
            {
                WSASetLastError(WSAEADDRNOTAVAIL);
                return FALSE;
            }
            int optionLength = sizeof(previousInterface);
            // getsockopt 返回主机字节序，setsockopt 接收网络字节序。
            if (getsockopt(socket, IPPROTO_IP, IP_UNICAST_IF,
                           reinterpret_cast<char *>(&previousInterface), &optionLength) != 0)
                return FALSE;
            const DWORD index = htonl(config.index);
            if (setsockopt(socket, IPPROTO_IP, IP_UNICAST_IF,
                           reinterpret_cast<const char *>(&index), sizeof(index)) != 0)
            {
                return FALSE;
            }
            selectedInterface = config.index;
            changedInterface = previousInterface != selectedInterface;
            WriteTestReceipt("ConnectEx physical_interface_selected");
        }
        const BOOL result =
            original(socket, address, length, sendBuffer, sendLength, bytesSent, overlapped);
        const int error = WSAGetLastError();
        // 同步失败表示本次未提交异步 I/O，可以撤回本次选项变更并保留原 bind/端口。
        // pending 或成功时不回滚，避免改变正在完成的连接；异步完成后的重试另行处理。
        if (!result && error != ERROR_IO_PENDING && changedInterface)
        {
            DWORD currentInterface = 0;
            int optionLength = sizeof(currentInterface);
            if (getsockopt(socket, IPPROTO_IP, IP_UNICAST_IF,
                           reinterpret_cast<char *>(&currentInterface), &optionLength) == 0 &&
                currentInterface == selectedInterface)
            {
                const DWORD previousNetworkOrder = htonl(previousInterface);
                const int restored =
                    setsockopt(socket, IPPROTO_IP, IP_UNICAST_IF,
                               reinterpret_cast<const char *>(&previousNetworkOrder),
                               sizeof(previousNetworkOrder));
                WriteTestReceipt(restored == 0 ? "ConnectEx immediate_failure_interface_restored"
                                               : "ConnectEx immediate_failure_restore_failed");
            }
        }
        char receipt[120]{};
        _snprintf_s(receipt, sizeof(receipt), _TRUNCATE, "ConnectEx active=%d result=%d error=%d",
                    active, result, result ? 0 : error);
        WriteTestReceipt(receipt);
        WSASetLastError(error);
        return result;
    }
    catch (...)
    {
        // 开关/配置读取失败时返回 Winsock 错误，不让 C++ 异常穿过应用的 API 调用。
        WSASetLastError(WSAENOBUFS);
        return FALSE;
    }
}

// 可选的真实应用启动传播。仅在明确提供测试路径时启用，普通探针不走这里。
// 保留原有参数，仅暂缓所选新子进程的首次执行，不接入任何已运行的进程。
// 受保护的子进程和其他应用保持原样，不改变任何缓解策略。
// Shared propagation for ANSI/Unicode ordinary child creation. Keep the application's
// original argument encoding, environment, startup structure and security attributes.
struct ImageIdentity
{
    std::wstring path;
    std::array<BYTE, 32> signer{};
    std::array<BYTE, 32> issuer{};
    std::wstring publisherName, publisherOrganization, publisherCountry;
    bool publisherVerified = false;
    BY_HANDLE_FILE_INFORMATION file{};
    bool trusted = false;
};

std::wstring ReadCertificateAttribute(PCCERT_CONTEXT certificate, const char *oid)
{
    const DWORD size = CertGetNameStringW(certificate, CERT_NAME_ATTR_TYPE, 0,
        const_cast<char *>(oid), nullptr, 0);
    if (size <= 1 || size > 1024) return {};
    std::wstring value(size, L'\0');
    if (CertGetNameStringW(certificate, CERT_NAME_ATTR_TYPE, 0,
        const_cast<char *>(oid), value.data(), size) != size) return {};
    value.resize(size - 1);
    return value;
}

bool ReadImageIdentity(const wchar_t *path, ImageIdentity &result)
{
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    struct CloseFile { HANDLE value; ~CloseFile() { CloseHandle(value); } } close{file};
    wchar_t canonical[32768]{};
    DWORD count = GetFinalPathNameByHandleW(file, canonical, 32768, FILE_NAME_NORMALIZED);
    if (!count || count >= 32768 || !GetFileInformationByHandle(file, &result.file)) return false;
    result.path = canonical;
    static std::mutex gate;
    static std::array<ImageIdentity, 64> cache;
    static size_t cursor = 0;
    {
        std::lock_guard<std::mutex> lock(gate);
        for (const auto &entry : cache)
            if (_wcsicmp(entry.path.c_str(), result.path.c_str()) == 0 &&
                entry.file.dwVolumeSerialNumber == result.file.dwVolumeSerialNumber &&
                entry.file.nFileIndexHigh == result.file.nFileIndexHigh &&
                entry.file.nFileIndexLow == result.file.nFileIndexLow &&
                entry.file.nFileSizeHigh == result.file.nFileSizeHigh &&
                entry.file.nFileSizeLow == result.file.nFileSizeLow &&
                CompareFileTime(&entry.file.ftLastWriteTime, &result.file.ftLastWriteTime) == 0)
            { result = entry; return result.trusted; }
    }
    WINTRUST_FILE_INFO info{};
    info.cbStruct = sizeof(info); info.pcwszFilePath = path; info.hFile = file;
    WINTRUST_DATA trust{};
    trust.cbStruct = sizeof(trust); trust.dwUIChoice = WTD_UI_NONE;
    trust.fdwRevocationChecks = WTD_REVOKE_NONE; trust.dwUnionChoice = WTD_CHOICE_FILE;
    trust.pFile = &info; trust.dwStateAction = WTD_STATEACTION_VERIFY;
    trust.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    struct CloseTrustState
    {
        GUID *action;
        WINTRUST_DATA *trust;
        ~CloseTrustState()
        {
            trust->dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(nullptr, action, trust);
        }
    } closeTrust{&action, &trust};
    const LONG status = WinVerifyTrust(nullptr, &action, &trust);
    if (status == ERROR_SUCCESS)
    {
        auto provider = WTHelperProvDataFromStateData(trust.hWVTStateData);
        auto signer = provider ? WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0) : nullptr;
        auto cert = signer && signer->csCertChain ? signer->pasCertChain[0].pCert : nullptr;
        DWORD bytes = static_cast<DWORD>(result.signer.size());
        result.trusted = cert && CryptHashCertificate2(BCRYPT_SHA256_ALGORITHM, 0, nullptr,
            cert->pbCertEncoded, cert->cbCertEncoded, result.signer.data(), &bytes) && bytes == result.signer.size();
        if (result.trusted && signer->csCertChain > 1)
        {
            // A verified publisher can renew its leaf certificate. Require the same
            // actual issuing CA and exact signed CN/O/C, not a file's company text.
            auto issuer = signer->pasCertChain[1].pCert;
            bytes = static_cast<DWORD>(result.issuer.size());
            if (issuer && CryptHashCertificate2(BCRYPT_SHA256_ALGORITHM, 0, nullptr,
                issuer->pbCertEncoded, issuer->cbCertEncoded, result.issuer.data(), &bytes) &&
                bytes == result.issuer.size())
            {
                result.publisherName = ReadCertificateAttribute(cert, szOID_COMMON_NAME);
                result.publisherOrganization = ReadCertificateAttribute(cert, szOID_ORGANIZATION_NAME);
                result.publisherCountry = ReadCertificateAttribute(cert, szOID_COUNTRY_NAME);
                result.publisherVerified = !result.publisherName.empty() &&
                    !result.publisherOrganization.empty() && !result.publisherCountry.empty();
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(gate);
        cache[cursor++ % cache.size()] = result;
    }
    return result.trusted;
}

bool SameUserAndSession(HANDLE target) noexcept
{
    HANDLE own = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &own)) return false;
    alignas(TOKEN_USER) BYTE a[256]{}, b[256]{};
    DWORD bytes = 0, ownSession = 0, childSession = 0;
    const bool same = GetTokenInformation(own, TokenUser, a, sizeof(a), &bytes) &&
        GetTokenInformation(target, TokenUser, b, sizeof(b), &bytes) &&
        EqualSid(reinterpret_cast<TOKEN_USER *>(a)->User.Sid, reinterpret_cast<TOKEN_USER *>(b)->User.Sid) &&
        GetTokenInformation(own, TokenSessionId, &ownSession, sizeof(ownSession), &bytes) &&
        GetTokenInformation(target, TokenSessionId, &childSession, sizeof(childSession), &bytes) &&
        ownSession == childSession;
    CloseHandle(own);
    return same;
}

bool IsSignedInstallationChild(const wchar_t *mainImage, const wchar_t *childImage) noexcept
{
    try
    {
        ImageIdentity main, child;
        if (!ReadImageIdentity(mainImage, main) || !ReadImageIdentity(childImage, child)) return false;
        const auto slash = main.path.find_last_of(L'\\');
        if (slash == std::wstring::npos) return false;
        const auto directory = main.path.substr(0, slash + 1);
        // Never turn a registered Windows utility into authority over the OS tree.
        wchar_t windows[MAX_PATH]{};
        if (!GetWindowsDirectoryW(windows, MAX_PATH)) return false;
        const std::wstring windowsRoot = std::wstring(L"\\\\?\\") + windows + L"\\";
        if (_wcsnicmp(directory.c_str(), windowsRoot.c_str(), windowsRoot.size()) == 0) return false;
        return child.path.size() > directory.size() &&
            _wcsnicmp(child.path.c_str(), directory.c_str(), directory.size()) == 0 &&
            (main.signer == child.signer ||
             (main.publisherVerified && child.publisherVerified && main.issuer == child.issuer &&
              main.publisherName == child.publisherName &&
              main.publisherOrganization == child.publisherOrganization &&
              main.publisherCountry == child.publisherCountry));
    }
    catch (...) { return false; }
}

template <typename Create>
BOOL CreateManagedChild(LPCWSTR application, LPCWSTR commandLine, DWORD creationFlags,
                        LPPROCESS_INFORMATION processInformation, Create create)
{
    const std::string dll = currentHookPath[0] ? currentHookPath : ReadEnvironmentValue("ZDC_CHILD_DLL");
    wchar_t allowed[MAX_PATH]{}, auxiliary[MAX_PATH]{};
    const DWORD allowedLength = GetEnvironmentVariableW(L"ZDC_CHILD_EXE", allowed, MAX_PATH);
    const DWORD auxiliaryLength =
        GetEnvironmentVariableW(L"ZDC_CHILD_AUX_EXE", auxiliary, MAX_PATH);
    const bool weixinMode = ReadEnvironmentValue("ZDC_CHILD_MODE") == "weixin";
    // 先准确识别将创建的可执行文件，避免为了检查而暂停不相关应用。
    std::wstring requested = application ? application : L"";
    if (requested.empty() && commandLine)
    {
        int count = 0;
        LPWSTR *arguments = CommandLineToArgvW(commandLine, &count);
        if (arguments && count > 0)
        {
            requested = arguments[0];
        }
        if (arguments)
        {
            LocalFree(arguments);
        }
    }
    wchar_t fullPath[MAX_PATH]{};
    const DWORD fullLength =
        requested.empty() ? 0 : GetFullPathNameW(requested.c_str(), MAX_PATH, fullPath, nullptr);
    const bool exactMain = allowedLength > 0 && allowedLength < MAX_PATH && fullLength > 0 &&
                           fullLength < MAX_PATH && _wcsicmp(fullPath, allowed) == 0;
    const bool exactAux = weixinMode && auxiliaryLength > 0 && auxiliaryLength < MAX_PATH &&
                          fullLength > 0 && fullLength < MAX_PATH &&
                          _wcsicmp(fullPath, auxiliary) == 0;
    const bool signedChild = !exactMain && !exactAux && allowedLength > 0 &&
        allowedLength < MAX_PATH && fullLength > 0 && fullLength < MAX_PATH &&
        IsSignedInstallationChild(allowed, fullPath);
    // Parse complete argv tokens: a filename or quoted payload containing a flag
    // must not opt a process into propagation. Never add or change sandbox flags.
    bool mainProcess = true, explicitlyUnsandboxed = false;
    int argumentCount = 0;
    LPWSTR *parsed = commandLine ? CommandLineToArgvW(commandLine, &argumentCount) : nullptr;
    if (parsed)
    {
        for (int i = 1; i < argumentCount; ++i)
        {
            if (wcsncmp(parsed[i], L"--type=", 7) == 0) mainProcess = false;
            if (!wcscmp(parsed[i], L"--no-sandbox") ||
                !wcscmp(parsed[i], L"--service-sandbox-type=none"))
                explicitlyUnsandboxed = true;
        }
        LocalFree(parsed);
    }
    else if (commandLine && *commandLine) mainProcess = false;
    // Exact configured images only; actual child image and mitigation policies
    // are verified below before modifying imports in the new suspended process.
    const bool allowedRole = (exactMain || exactAux || signedChild) &&
        (explicitlyUnsandboxed || mainProcess);
    const bool candidate =
        (controlLease || IsControlSessionAlive()) && !dll.empty() && processInformation && allowedRole;
    if (!candidate)
    {
        return create(creationFlags);
    }
    const BOOL started = create(creationFlags | CREATE_SUSPENDED);
    if (!started)
    {
        return FALSE;
    }
    const DWORD originalError = GetLastError();
    wchar_t image[MAX_PATH]{};
    DWORD imageLength = MAX_PATH;
    PROCESS_MITIGATION_DYNAMIC_CODE_POLICY dynamicPolicy{};
    PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY signaturePolicy{};
    HANDLE token = nullptr;
    DWORD appContainer = 1, tokenBytes = 0;
    const bool ordinaryToken = OpenProcessToken(processInformation->hProcess, TOKEN_QUERY, &token) &&
        GetTokenInformation(token, TokenIsAppContainer, &appContainer, sizeof(appContainer), &tokenBytes) &&
        !appContainer && !IsTokenRestricted(token) && SameUserAndSession(token);
    if (token) CloseHandle(token);
    USHORT processMachine = 0, nativeMachine = 0;
    const bool knownArchitecture = IsWow64Process2(processInformation->hProcess, &processMachine, &nativeMachine) != FALSE;
    const DWORD targetBits = !knownArchitecture ? 0 : processMachine == IMAGE_FILE_MACHINE_I386 ? 32 :
        processMachine == IMAGE_FILE_MACHINE_UNKNOWN && nativeMachine == IMAGE_FILE_MACHINE_AMD64 ? 64 : 0;
#ifdef _WIN64
    constexpr DWORD ownBits = 64;
#else
    constexpr DWORD ownBits = 32;
#endif
    const bool eligible = ordinaryToken && targetBits != 0 &&
        QueryFullProcessImageNameW(processInformation->hProcess, 0, image, &imageLength) &&
        (_wcsicmp(image, allowed) == 0 || (exactAux && _wcsicmp(image, auxiliary) == 0) ||
            (signedChild && _wcsicmp(image, fullPath) == 0 && IsSignedInstallationChild(allowed, image))) &&
        GetProcessMitigationPolicy(processInformation->hProcess, ProcessDynamicCodePolicy,
                                   &dynamicPolicy, sizeof(dynamicPolicy)) &&
        GetProcessMitigationPolicy(processInformation->hProcess, ProcessSignaturePolicy,
                                   &signaturePolicy, sizeof(signaturePolicy)) &&
        !dynamicPolicy.ProhibitDynamicCode && !signaturePolicy.MicrosoftSignedOnly &&
        !signaturePolicy.StoreSignedOnly;
    BOOL loaded = FALSE;
    DWORD error = ERROR_ACCESS_DENIED;
    if (eligible)
    {
        LPCSTR dlls[] = {dll.c_str()};
        loaded = targetBits == ownBits
            ? DetourUpdateProcessWithDll(processInformation->hProcess, dlls, 1)
            : DetourProcessViaHelperW(processInformation->dwProcessId, dll.c_str(), OriginalCreateProcessW);
        error = loaded ? ERROR_SUCCESS : GetLastError();
    }
    fprintf(stderr, "ZDC_CHILD pid=%lu eligible=%d updated=%d error=%lu\n",
            processInformation->dwProcessId, eligible, loaded, error);
    fflush(stderr);
    char childReceipt[120]{};
    _snprintf_s(childReceipt, sizeof(childReceipt), _TRUNCATE,
                "child pid=%lu eligible=%d updated=%d error=%lu", processInformation->dwProcessId,
                eligible, loaded, error);
    WriteTestReceipt(childReceipt);
    // 原调用要求挂起时仍保持挂起，让浏览器完成其原定初始化。
    if (!(creationFlags & CREATE_SUSPENDED))
    {
        ResumeThread(processInformation->hThread);
    }
    SetLastError(originalError);
    return started;
}

thread_local bool creatingManagedChild = false;
struct ChildCreationScope
{
    ChildCreationScope() { creatingManagedChild = true; }
    ~ChildCreationScope() { creatingManagedChild = false; }
};
BOOL WINAPI HookCreateProcessW(LPCWSTR application, LPWSTR commandLine,
    LPSECURITY_ATTRIBUTES processAttributes, LPSECURITY_ATTRIBUTES threadAttributes,
    BOOL inheritHandles, DWORD creationFlags, LPVOID environment, LPCWSTR directory,
    LPSTARTUPINFOW startupInfo, LPPROCESS_INFORMATION processInformation)
{
    auto create = [&](DWORD flags) {
        return OriginalCreateProcessW(application, commandLine, processAttributes, threadAttributes,
            inheritHandles, flags, environment, directory, startupInfo, processInformation);
    };
    if (creatingManagedChild) return create(creationFlags);
    ChildCreationScope scope;
    return CreateManagedChild(application, commandLine, creationFlags, processInformation, create);
}

std::wstring AnsiProcessText(LPCSTR text)
{
    if (!text) return {};
    const int size = MultiByteToWideChar(CP_ACP, 0, text, -1, nullptr, 0);
    if (size <= 0) return {};
    std::wstring result(static_cast<size_t>(size), L'\0');
    if (!MultiByteToWideChar(CP_ACP, 0, text, -1, result.data(), size)) return {};
    result.resize(static_cast<size_t>(size - 1));
    return result;
}
BOOL WINAPI HookCreateProcessA(LPCSTR application, LPSTR commandLine,
    LPSECURITY_ATTRIBUTES processAttributes, LPSECURITY_ATTRIBUTES threadAttributes,
    BOOL inheritHandles, DWORD creationFlags, LPVOID environment, LPCSTR directory,
    LPSTARTUPINFOA startupInfo, LPPROCESS_INFORMATION processInformation)
{
    auto create = [&](DWORD flags) {
        return OriginalCreateProcessA(application, commandLine, processAttributes, threadAttributes,
            inheritHandles, flags, environment, directory, startupInfo, processInformation);
    };
    if (creatingManagedChild) return create(creationFlags);
    ChildCreationScope scope;
    const auto wideApplication = AnsiProcessText(application);
    const auto wideCommand = AnsiProcessText(commandLine);
    return CreateManagedChild(application ? wideApplication.c_str() : nullptr,
        commandLine ? wideCommand.c_str() : nullptr, creationFlags, processInformation, create);
}

BOOL WINAPI HookCreateProcessAsUserW(HANDLE token, LPCWSTR application, LPWSTR commandLine,
    LPSECURITY_ATTRIBUTES processAttributes, LPSECURITY_ATTRIBUTES threadAttributes,
    BOOL inheritHandles, DWORD creationFlags, LPVOID environment, LPCWSTR directory,
    LPSTARTUPINFOW startupInfo, LPPROCESS_INFORMATION processInformation)
{
    auto create = [&](DWORD flags) {
        return OriginalCreateProcessAsUserW(token, application, commandLine, processAttributes,
            threadAttributes, inheritHandles, flags, environment, directory, startupInfo, processInformation);
    };
    if (creatingManagedChild) return create(creationFlags);
    ChildCreationScope scope;
    WriteApiObservation("CreateProcessAsUserW");
    return CreateManagedChild(application, commandLine, creationFlags, processInformation, create);
}

BOOL WINAPI HookCreateProcessAsUserA(HANDLE token, LPCSTR application, LPSTR commandLine,
    LPSECURITY_ATTRIBUTES processAttributes, LPSECURITY_ATTRIBUTES threadAttributes,
    BOOL inheritHandles, DWORD creationFlags, LPVOID environment, LPCSTR directory,
    LPSTARTUPINFOA startupInfo, LPPROCESS_INFORMATION processInformation)
{
    auto create = [&](DWORD flags) {
        return OriginalCreateProcessAsUserA(token, application, commandLine, processAttributes,
            threadAttributes, inheritHandles, flags, environment, directory, startupInfo, processInformation);
    };
    if (creatingManagedChild) return create(creationFlags);
    ChildCreationScope scope;
    WriteApiObservation("CreateProcessAsUserA");
    const auto wideApplication = AnsiProcessText(application);
    const auto wideCommand = AnsiProcessText(commandLine);
    return CreateManagedChild(application ? wideApplication.c_str() : nullptr,
        commandLine ? wideCommand.c_str() : nullptr, creationFlags, processInformation, create);
}

} // namespace

// x64 DLL 导出：直接指定 ordinal 1，供 DetourCreateProcessWithDllEx 使用。
#ifdef _WIN64
#pragma comment(linker, "/EXPORT:DetourFinishHelperProcess,@1,NONAME")
#else
#pragma comment(linker, "/EXPORT:DetourFinishHelperProcess=_DetourFinishHelperProcess@16,@1,NONAME")
#endif

// Windows 加载 DLL 时进入这里。事务保证两个钩子一起安装或一起撤销。
// WINAPI/WSAAPI 是 Windows 接口的调用约定；并非额外线程或后台服务。
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID)
{
    if (DetourIsHelperProcess())
    {
        return TRUE;
    }
    if (reason == DLL_PROCESS_ATTACH)
    {
        DetourRestoreAfterWith();
        const DWORD imageLength = GetModuleFileNameA(module, currentHookPath, MAX_PATH);
        if (!imageLength || imageLength >= MAX_PATH) currentHookPath[0] = 0;
        if (!RetainRuntimePair(module)) return TRUE; // Leave the application unmodified if the pair cannot be retained.
        RetainControlLease();
        if (DetourTransactionBegin() != NO_ERROR)
        {
            return FALSE;
        }
        if (DetourUpdateThread(GetCurrentThread()) != NO_ERROR ||
            !AttachWinInetRouting(IsRoutingEnabled) ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalGetAddrInfo), HookGetAddrInfo) !=
                NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalConnect), HookConnect) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalSend), HookSend) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalSendTo), HookSendTo) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalWSASendTo), HookWSASendTo) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalWSASendMsg), HookWSASendMsg) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalRecv), HookRecv) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalWSASend), HookWSASend) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalWSARecv), HookWSARecv) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalCreateProcessW), HookCreateProcessW) !=
                NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalCreateProcessA), HookCreateProcessA) !=
                NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalCreateProcessAsUserW), HookCreateProcessAsUserW) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalCreateProcessAsUserA), HookCreateProcessAsUserA) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalWSAConnect), HookWSAConnect) !=
                NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalWSAIoctl), HookWSAIoctl) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalFreeAddrInfoExW),
                         HookFreeAddrInfoExW) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalGetAddrInfoExW), HookGetAddrInfoExW) !=
                NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalGetIEProxyConfig), HookGetIEProxyConfig) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalDnsQueryW), HookDnsQueryW) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalDnsQueryA), HookDnsQueryA) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalDnsQueryEx), HookDnsQueryEx) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalGetAddrInfoW), HookGetAddrInfoW) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalGetHostByName), HookGetHostByName) != NO_ERROR ||
            DetourAttach(reinterpret_cast<PVOID *>(&OriginalWinHttpConnect), HookWinHttpConnect) !=
                NO_ERROR)
        {
            DetourTransactionAbort();
            return FALSE;
        }
        const bool installed = DetourTransactionCommit() == NO_ERROR;
        if (installed)
        {
            RegisterInstalledMember();
            WriteTestReceipt("dll_hooks_installed");
        }
        return installed;
    }
    // 本验证模块仅随测试进程结束而卸载；禁止在仍有 API 调用时 FreeLibrary。
    return TRUE;
}

// 构建：从仓库根目录执行 python -B src/native/build.py（产物在 artifacts/native）。
