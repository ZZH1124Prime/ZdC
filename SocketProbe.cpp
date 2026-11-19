// 测试程序：普通 getaddrinfo → connect → HTTP 请求，没有自行指定直连出口。
// 第一轮由 DLL 接管；控制器退出后，再用新 socket 请求并比较系统默认出口。
// 默认测试 IPv4 TCP；ZDC_PROBE_UDP=1 改为公开 UDP DNS，均不发送账号或聊天内容。
// 从控制器启动本程序，才能取得命名事件和控制器 PID；无需管理员权限。
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h> // Windows socket 类型；必须早于 windows.h。
#include <windows.h>  // 进程、命名事件、等待和环境变量。
#include <ws2tcpip.h> // 域名解析、IPv4 地址转换和接口选项。
#include <iphlpapi.h>
#include <windns.h>
#pragma comment(lib, "dnsapi.lib")
#pragma comment(lib, "iphlpapi.lib")
#include <mswsock.h> // ConnectEx 扩展函数。
#include <string>
#pragma comment(lib, "ws2_32.lib")
#include <cstdio>
#include <cstdlib>
#include <cstring>

// ==================== 2. 探针：同一进程的前后两次请求 ====================

// 普通网络程序：没有接口绑定或独立 DNS 代码，全部由注入的 DLL 接管。
// 各进程共享同一性能计数器时基，起止区间用于核对 DNS 调用是否重叠。
static long long TimestampTicks()
{
    LARGE_INTEGER timestamp{};
    QueryPerformanceCounter(&timestamp);
    return timestamp.QuadPart;
}

// 成功访问与出口匹配分别判断，避免无接管的HTTP200被标成直连通过。
static bool ExpectedSourceMatches(const char *label, const char *actual, const char *remote)
{
    char expected[64]{};
    IN_ADDR targetAddress{};
    const bool loopback = InetPtonA(AF_INET, remote, &targetAddress) == 1 &&
                          (ntohl(targetAddress.s_addr) >> 24) == 127;
    if (strstr(label, "HOOK_ACTIVE") && !loopback)
        GetEnvironmentVariableA("ZDC_SOURCE_IP", expected, sizeof(expected));
    else
    {
        // 恢复阶段按本次真实目标查系统路由，不用其他公网地址代替。
        SOCKADDR_INET target{}, source{};
        target.Ipv4.sin_family = AF_INET;
        MIB_IPFORWARD_ROW2 route{};
        if (InetPtonA(AF_INET, remote, &target.Ipv4.sin_addr) == 1 &&
            GetBestRoute2(nullptr, 0, nullptr, &target, 0, &route, &source) == NO_ERROR)
            InetNtopA(AF_INET, &source.Ipv4.sin_addr, expected, sizeof(expected));
    }
    const bool matches = expected[0] && !strcmp(expected, actual);
    printf("%s SOURCE_CHECK target=%s expected=%s actual=%s match=%d\n", label, remote, expected,
           actual, matches);
    return matches;
}

// 可选的 ConnectEx 路径：仍由普通 Winsock 查询函数指针，探针不设置物理出口。
// 使用事件等待真实异步完成，超时取消本次 I/O，并等取消完成后释放 OVERLAPPED。
static int ConnectUsingExtension(SOCKET socket, const sockaddr *address, int length)
{
    sockaddr_in wildcard{};
    wildcard.sin_family = AF_INET;
    if (bind(socket, reinterpret_cast<sockaddr *>(&wildcard), sizeof(wildcard)) != 0)
        return SOCKET_ERROR;
    GUID id = WSAID_CONNECTEX;
    LPFN_CONNECTEX connectEx = nullptr;
    DWORD bytes = 0;
    if (WSAIoctl(socket, SIO_GET_EXTENSION_FUNCTION_POINTER, &id, sizeof(id), &connectEx,
                 sizeof(connectEx), &bytes, nullptr, nullptr) != 0)
        return SOCKET_ERROR;
    OVERLAPPED pending{};
    pending.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!pending.hEvent)
        return SOCKET_ERROR;
    BOOL succeeded = connectEx(socket, address, length, nullptr, 0, nullptr, &pending);
    int error = succeeded ? 0 : WSAGetLastError();
    if (!succeeded && error == ERROR_IO_PENDING)
    {
        const DWORD wait = WaitForSingleObject(pending.hEvent, 6000);
        if (wait != WAIT_OBJECT_0)
            CancelIoEx(reinterpret_cast<HANDLE>(socket), &pending);
        DWORD flags = 0;
        succeeded = WSAGetOverlappedResult(socket, &pending, &bytes, TRUE, &flags);
        error = succeeded ? 0 : WSAGetLastError();
    }
    CloseHandle(pending.hEvent);
    if (succeeded)
    {
        // 告知 Winsock 连接已完成，后续 send/recv/getsockname 使用正常连接上下文。
        return setsockopt(socket, SOL_SOCKET, SO_UPDATE_CONNECT_CONTEXT, nullptr, 0);
    }
    WSASetLastError(error);
    return SOCKET_ERROR;
}

// UDP 最小实测：普通 connect/send/recv 查询公开 DNS；不自行设置出口。
// 可选 wildcard bind，用于确认 DLL 保留应用已占用的本地端口。
static bool RunUdpProbe(const char *label)
{

    SOCKET sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock == INVALID_SOCKET)
        return false;
    DWORD timeout = 2500;
    if (setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<char *>(&timeout),
                   sizeof(timeout)) != 0)
    {
        closesocket(sock);
        return false;
    }
    sockaddr_in local{};
    local.sin_family = AF_INET;
    if (GetEnvironmentVariableA("ZDC_UDP_BIND", nullptr, 0) &&
        bind(sock, reinterpret_cast<sockaddr *>(&local), sizeof(local)) != 0)
    {
        closesocket(sock);
        return false;
    }
    int localLength = sizeof(local);
    getsockname(sock, reinterpret_cast<sockaddr *>(&local), &localLength);
    const unsigned originalPort = ntohs(local.sin_port);
    sockaddr_in remote{};
    remote.sin_family = AF_INET;
    remote.sin_port = htons(53);
    inet_pton(AF_INET, "208.67.222.222", &remote.sin_addr);
    // myip.opendns.com 的 A 答案由公共解析器返回其观察到的请求源地址。
    unsigned char query[] = {0x5a, 0xdc, 1,   0,   0,   1, 0,   0,   0,   0,   0,   0,
                             4,    'm',  'y', 'i', 'p', 7, 'o', 'p', 'e', 'n', 'd', 'n',
                             's',  3,    'c', 'o', 'm', 0, 0,   1,   0,   1};
    query[0] = static_cast<unsigned char>(GetCurrentProcessId() >> 8);
    query[1] = static_cast<unsigned char>(GetCurrentProcessId());
    int result = connect(sock, reinterpret_cast<sockaddr *>(&remote), sizeof(remote));
    if (result == 0)
        result = send(sock, reinterpret_cast<char *>(query), sizeof(query), 0);
    unsigned char response[512]{};
    const int received =
        result > 0 ? recv(sock, reinterpret_cast<char *>(response), sizeof(response), 0) : -1;
    const int error = WSAGetLastError();
    localLength = sizeof(local);
    getsockname(sock, reinterpret_cast<sockaddr *>(&local), &localLength);
    char source[INET_ADDRSTRLEN]{};
    inet_ntop(AF_INET, &local.sin_addr, source, sizeof(source));
    closesocket(sock);
    bool valid = received >= static_cast<int>(sizeof(query)) && response[0] == query[0] &&
                 response[1] == query[1] && (response[2] & 0x80) && !(response[2] & 0x7a) &&
                 !(response[3] & 15) && response[4] == 0 && response[5] == 1 &&
                 memcmp(response + 12, query + 12, sizeof(query) - 12) == 0;
    bool foundAddress = false;
    printf("%s UDP source=%s port=%u original_port=%u received=%d error=%d valid_dns=%d\n", label,
           source, ntohs(local.sin_port), originalPort, received, received < 0 ? error : 0, valid);
    // 单问题固定请求；答案名称允许压缩指针或逐标签，所有边界均以实收长度为准。
    size_t pos = sizeof(query);
    const unsigned answers = (response[6] << 8) | response[7];
    for (unsigned i = 0; valid && i < answers; ++i)
    {
        while (pos < static_cast<size_t>(received) && response[pos] &&
               (response[pos] & 0xc0) != 0xc0)
            pos += 1 + response[pos];
        if (pos >= static_cast<size_t>(received))
        {
            valid = false;
            break;
        }
        pos += (response[pos] & 0xc0) == 0xc0 ? 2 : 1;
        if (pos + 10 > static_cast<size_t>(received))
        {
            valid = false;
            break;
        }
        const unsigned type = (response[pos] << 8) | response[pos + 1];
        const unsigned cls = (response[pos + 2] << 8) | response[pos + 3];
        const unsigned length = (response[pos + 8] << 8) | response[pos + 9];
        pos += 10;
        if (pos + length > static_cast<size_t>(received))
        {
            valid = false;
            break;
        }
        if (type == 1 && cls == 1 && length == 4)
        {
            foundAddress = true;
            printf("%s UDP_DNS_A=%u.%u.%u.%u\n", label, response[pos], response[pos + 1],
                   response[pos + 2], response[pos + 3]);
        }
        pos += length;
    }
    fflush(stdout);
    return valid && foundAddress && (!originalPort || originalPort == ntohs(local.sin_port)) &&
           ExpectedSourceMatches(label, source, "208.67.222.222");
}

static void WINAPI CompleteProbeDns(void *context, DNS_QUERY_RESULT *)
{
    SetEvent(static_cast<HANDLE>(context));
}

static bool RunHttpProbe(const char *label, PADDRINFOEXW *deferredDnsResult = nullptr)
{
    addrinfo hints{};
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo *resolved = nullptr;
    const long long dnsBegin = TimestampTicks();
    char dnsApi[32]{};
    GetEnvironmentVariableA("ZDC_PROBE_DNS", dnsApi, sizeof(dnsApi));
    int resolveError = 0;
    if (std::string(dnsApi) == "extended")
    {
        ADDRINFOEXW extendedHints{};
        extendedHints.ai_family = AF_UNSPEC;
        extendedHints.ai_flags = AI_CANONNAME | AI_FQDN;
        PADDRINFOEXW extended = nullptr;
        resolveError = GetAddrInfoExW(L"www.cloudflare.com", L"80", NS_DNS, nullptr, &extendedHints,
                                      &extended, nullptr, nullptr, nullptr, nullptr);
        if (!resolveError && extended)
        {
            const auto versioned = reinterpret_cast<PADDRINFOEX2W>(extended);
            printf("%s EXTENDED_DNS version=%d family=%d canonical=%ls fqdn=%ls\n", label,
                   versioned->ai_version, extended->ai_family,
                   extended->ai_canonname ? extended->ai_canonname : L"(null)",
                   versioned->ai_fqdn ? versioned->ai_fqdn : L"(null)");
            char ip[INET_ADDRSTRLEN]{};
            if (extended->ai_family == AF_INET &&
                inet_ntop(AF_INET, &reinterpret_cast<sockaddr_in *>(extended->ai_addr)->sin_addr,
                          ip, sizeof(ip)))
            {
                // 仅转换已有数字地址，便于复用后续普通连接代码；不会再次查询域名。
                hints.ai_flags = AI_NUMERICHOST;
                resolveError = getaddrinfo(ip, "80", &hints, &resolved);
            }
            else
                resolveError = EAI_FAMILY;
        }
        if (deferredDnsResult && extended)
            *deferredDnsResult = extended; // 首轮保留到控制器退出，验证关闭后仍能正确释放。
        else
            FreeAddrInfoExW(extended);
    }
    else if (std::string(dnsApi) == "windows" || std::string(dnsApi) == "windows-async")
    {
        const bool asynchronous = std::string(dnsApi) == "windows-async";
        HANDLE completed = asynchronous ? CreateEventW(nullptr, TRUE, FALSE, nullptr) : nullptr;
        if (asynchronous && !completed) return false;
        DNS_QUERY_REQUEST3 request{};
        request.Version = asynchronous ? DNS_QUERY_REQUEST_VERSION3 : DNS_QUERY_REQUEST_VERSION1;
        request.QueryName = L"www.cloudflare.com";
        request.QueryType = DNS_TYPE_A;
        request.QueryOptions = DNS_QUERY_TREAT_AS_FQDN | (asynchronous ? 0 : DNS_QUERY_WIRE_ONLY);
        request.pQueryCompletionCallback = asynchronous ? CompleteProbeDns : nullptr;
        request.pQueryContext = completed;
        DNS_QUERY_RESULT reply{};
        reply.Version = DNS_QUERY_RESULTS_VERSION1;
        DNS_QUERY_CANCEL cancel{};
        auto status = DnsQueryEx(reinterpret_cast<DNS_QUERY_REQUEST *>(&request), &reply,
                                 asynchronous ? &cancel : nullptr);
        if (status == DNS_REQUEST_PENDING && completed)
        {
            if (WaitForSingleObject(completed, 15000) != WAIT_OBJECT_0)
            {
                DnsCancelQuery(&cancel);
                WaitForSingleObject(completed, INFINITE);
            }
            status = reply.QueryStatus;
            printf("%s WINDOWS_DNS_CALLBACK status=%ld\n", label, status);
        }
        if (completed) CloseHandle(completed);
        resolveError = status ? status : EAI_NONAME;
        for (auto record = reply.pQueryRecords; !status && record; record = record->pNext)
        {
            if (record->Flags.S.Section != DnsSectionAnswer || record->wType != DNS_TYPE_A)
                continue;
            char ip[INET_ADDRSTRLEN]{};
            if (inet_ntop(AF_INET, &record->Data.A.IpAddress, ip, sizeof(ip)))
            {
                printf("%s WINDOWS_DNS status=%ld target=%s\n", label, status, ip);
                hints.ai_flags = AI_NUMERICHOST;
                resolveError = getaddrinfo(ip, "80", &hints, &resolved);
                break;
            }
        }
        if (reply.pQueryRecords) DnsRecordListFree(reply.pQueryRecords, DnsFreeRecordList);
    }
    else
        resolveError = getaddrinfo("www.cloudflare.com", "80", &hints, &resolved);
    const long long dnsEnd = TimestampTicks();
    printf("%s DNS_WINDOW begin=%lld end=%lld result=%d\n", label, dnsBegin, dnsEnd, resolveError);
    fflush(stdout);
    if (resolveError || !resolved)
    {
        printf("%s DNS_ERROR %d\n", label, resolveError);
        return false;
    }
    char remoteIp[64]{};
    InetNtopA(AF_INET, &((sockaddr_in *)resolved->ai_addr)->sin_addr, remoteIp, sizeof(remoteIp));
    SOCKET connectionSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (connectionSocket == INVALID_SOCKET)
    {
        freeaddrinfo(resolved);
        return false;
    }
    DWORD timeoutMilliseconds = 6000;
    setsockopt(connectionSocket, SOL_SOCKET, SO_RCVTIMEO, (char *)&timeoutMilliseconds,
               sizeof(timeoutMilliseconds));
    setsockopt(connectionSocket, SOL_SOCKET, SO_SNDTIMEO, (char *)&timeoutMilliseconds,
               sizeof(timeoutMilliseconds));
    // 非阻塞 connect 配合 select，将建立连接的等待限制为 6 秒。
    u_long nonblocking = 1;
    ioctlsocket(connectionSocket, FIONBIO, &nonblocking);
    char api[32]{};
    GetEnvironmentVariableA("ZDC_PROBE_API", api, sizeof(api));
    const bool useConnectEx = std::string(api) == "connectex";
    printf("%s CONNECT_API %s\n", label, useConnectEx ? "ConnectEx" : "connect");
    if (!useConnectEx && GetEnvironmentVariableA("ZDC_TCP_BIND", nullptr, 0))
    {
        sockaddr_in wildcard{};
        wildcard.sin_family = AF_INET;
        if (bind(connectionSocket, reinterpret_cast<sockaddr *>(&wildcard), sizeof(wildcard)) != 0)
        {
            freeaddrinfo(resolved);
            closesocket(connectionSocket);
            return false;
        }
    }
    int connectResult =
        useConnectEx
            ? ConnectUsingExtension(connectionSocket, resolved->ai_addr, (int)resolved->ai_addrlen)
            : connect(connectionSocket, resolved->ai_addr, (int)resolved->ai_addrlen);
    freeaddrinfo(resolved);
    if (connectResult == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK)
    {
        fd_set writeSet, errorSet;
        FD_ZERO(&writeSet);
        FD_ZERO(&errorSet);
        FD_SET(connectionSocket, &writeSet);
        FD_SET(connectionSocket, &errorSet);
        timeval connectTimeout{6, 0};
        connectResult = select(0, nullptr, &writeSet, &errorSet, &connectTimeout);
        int socketError = 0, errorLength = sizeof(socketError);
        getsockopt(connectionSocket, SOL_SOCKET, SO_ERROR, (char *)&socketError, &errorLength);
        connectResult =
            (connectResult > 0 && socketError == 0 && FD_ISSET(connectionSocket, &writeSet))
                ? 0
                : SOCKET_ERROR;
    }
    if (connectResult == SOCKET_ERROR)
    {
        printf("%s CONNECT_ERROR %d target=%s\n", label, WSAGetLastError(), remoteIp);
        closesocket(connectionSocket);
        return false;
    }
    nonblocking = 0;
    ioctlsocket(connectionSocket, FIONBIO, &nonblocking);
    sockaddr_in localAddress{};
    int addressLength = sizeof(localAddress);
    getsockname(connectionSocket, (sockaddr *)&localAddress, &addressLength);
    char sourceIp[64]{};
    InetNtopA(AF_INET, &localAddress.sin_addr, sourceIp, sizeof(sourceIp));
    // 公开诊断端点，不发送账号、Cookie 或聊天内容；这一步验证 HTTP 而非 TLS。
    const char request[] = "GET /cdn-cgi/trace HTTP/1.1\r\nHost: "
                           "www.cloudflare.com\r\nConnection: close\r\n\r\n";
    int bytesSent = 0;
    while (bytesSent < (int)sizeof(request) - 1)
    {
        int bytesTransferred =
            send(connectionSocket, request + bytesSent, (int)sizeof(request) - 1 - bytesSent, 0);
        if (bytesTransferred <= 0)
        {
            break;
        }
        bytesSent += bytesTransferred;
    }
    std::string response;
    char buffer[4096];
    int bytesTransferred = 0;
    while (response.size() < 16384 &&
           (bytesTransferred = recv(connectionSocket, buffer, sizeof(buffer), 0)) > 0)
    {
        response.append(buffer, bytesTransferred);
    }
    closesocket(connectionSocket);
    printf("%s local=%s target=%s %s\n", label, sourceIp, remoteIp,
           response.substr(0, response.find("\r\n")).c_str());
    for (const char *key : {"ip=", "loc=", "colo="})
    {
        auto fieldStart = response.find(std::string("\n") + key);
        if (fieldStart != std::string::npos)
        {
            ++fieldStart;
            printf(
                "%s %s\n", label,
                response.substr(fieldStart, response.find('\n', fieldStart) - fieldStart).c_str());
        }
    }
    fflush(stdout);
    return response.find("HTTP/1.1 200") == 0 && ExpectedSourceMatches(label, sourceIp, remoteIp);
}
int main()
{
    WSADATA winsockData{};
    if (WSAStartup(MAKEWORD(2, 2), &winsockData))
    {
        return 1;
    }

    char firstProbeDoneEventName[128]{}, controllerPidText[32]{};
    GetEnvironmentVariableA("ZDC_DONE_EVENT", firstProbeDoneEventName,
                            sizeof(firstProbeDoneEventName));
    GetEnvironmentVariableA("ZDC_CONTROLLER_PID", controllerPidText, sizeof(controllerPidText));
    HANDLE firstProbeDoneEvent = OpenEventA(EVENT_MODIFY_STATE, FALSE, firstProbeDoneEventName);
    HANDLE controllerProcess =
        OpenProcess(SYNCHRONIZE, FALSE, strtoul(controllerPidText, nullptr, 10));
    if (!firstProbeDoneEvent || !controllerProcess)
    {
        puts("START VIA CONTROLLER ONLY");
        return 2;
    }
    char readyName[128]{}, startName[128]{};
    GetEnvironmentVariableA("ZDC_READY_EVENT", readyName, sizeof(readyName));
    GetEnvironmentVariableA("ZDC_START_EVENT", startName, sizeof(startName));
    HANDLE readyEvent = OpenEventA(EVENT_MODIFY_STATE, FALSE, readyName);
    HANDLE startEvent = OpenEventA(SYNCHRONIZE, FALSE, startName);
    if (!readyEvent || !startEvent)
    {
        if (readyEvent)
        {
            CloseHandle(readyEvent);
        }
        if (startEvent)
        {
            CloseHandle(startEvent);
        }
        CloseHandle(firstProbeDoneEvent);
        CloseHandle(controllerProcess);
        WSACleanup();
        return 2;
    }
    const std::string prefix = "[pid=" + std::to_string(GetCurrentProcessId()) + "] ";
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    printf("%sREADY ticks_per_second=%lld\n", prefix.c_str(), frequency.QuadPart);
    fflush(stdout);
    SetEvent(readyEvent);
    CloseHandle(readyEvent);
    // 三个独立进程都准备好后，控制器一次性放开共享事件。
    const DWORD startResult = WaitForSingleObject(startEvent, 45000);
    CloseHandle(startEvent);
    if (startResult != WAIT_OBJECT_0)
    {
        CloseHandle(firstProbeDoneEvent);
        CloseHandle(controllerProcess);
        WSACleanup();
        return 3;
    }
    const bool udp = GetEnvironmentVariableA("ZDC_PROBE_UDP", nullptr, 0) > 0;
    PADDRINFOEXW deferredDnsResult = nullptr;
    bool directSucceeded = udp ? RunUdpProbe((prefix + "HOOK_ACTIVE").c_str())
                               : RunHttpProbe((prefix + "HOOK_ACTIVE").c_str(), &deferredDnsResult);
    SetEvent(firstProbeDoneEvent);
    CloseHandle(firstProbeDoneEvent);
    if (WaitForSingleObject(controllerProcess, 10000) != WAIT_OBJECT_0)
    {
        FreeAddrInfoExW(deferredDnsResult);
        puts("CONTROLLER_EXIT_TIMEOUT");
        CloseHandle(controllerProcess);
        return 3;
    }
    CloseHandle(controllerProcess);
    FreeAddrInfoExW(deferredDnsResult);
    if (deferredDnsResult)
        printf("%sEXTENDED_RESULT_RELEASED_AFTER_EXIT\n", prefix.c_str());
    bool defaultSucceeded = udp ? RunUdpProbe((prefix + "CONTROLLER_GONE").c_str())
                                : RunHttpProbe((prefix + "CONTROLLER_GONE").c_str());
    printf("%sSUMMARY direct=%s restored=%s\n", prefix.c_str(), directSucceeded ? "PASS" : "FAIL",
           defaultSucceeded ? "PASS" : "FAIL");
    fflush(stdout);
    WSACleanup();
    return directSucceeded && defaultSucceeded ? 0 : 4;
}

// 编译：在 x64 Native Tools Command Prompt 中进入
// D:\ZdC\artifacts\socket-proof。
// 本命令编译当前文件；另外两个源文件的末尾也有各自的编译命令。
// clang-format off
// cl /nologo /std:c++17 /utf-8 /EHsc /W4 /MT ..\..\SocketProbe.cpp /Fe:ZdC.SocketProbe.exe /Fo:Probe.obj
// clang-format on
// 三个文件编译完成后运行 .\ZdC.SocketController.exe，自动完成两轮并退出。
