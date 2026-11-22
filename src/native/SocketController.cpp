// 控制器：选择物理网卡，打开直连开关，启动自建测试程序并加载拦截 DLL。
// 三个探针同步开始，第一轮全部完成后关闭开关并退出，再验证系统默认出口。
// 阅读顺序：本文件 → SocketProbe.cpp → SocketHook.cpp。
// 无参数运行自建探针；--weixin 在用户自行退出微信后启动微信验证。
// 配置通过子进程继承的环境变量传递；命名事件用于开关和完成通知。
// 微软 Detours（MIT）：https://github.com/microsoft/Detours
// 固定版本：1dbd4ea281d73d478aa8d09a8b83fbd265f715f7
// 上游源码及许可证保留在 third_party/Detours。
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h> // Windows socket 类型；必须早于 windows.h。
#include <ws2tcpip.h> // 域名解析、IPv4 地址转换和接口选项。
#include <windows.h>  // 进程、命名事件、等待和环境变量。
#include <string>
#include <tlhelp32.h> // 只读枚举进程，避免重复启动或接入现有微信。
#include <cstring>
#pragma comment(lib, "ws2_32.lib")
#include <iphlpapi.h> // 枚举现有网卡、地址、DNS 和网关。
#include <netioapi.h> // 读取网卡是否为物理接口。
#include <cstdio>
#include <vector>
#include <array>
#include "detours.h"
#pragma comment(lib, "iphlpapi.lib")

// 从现有适配器读取配置，只接受一个已连接、带网关的物理 IPv4 接口。
static bool FindPhysicalAdapter(std::string &sourceIp, ULONG &interfaceIndex,
                                std::string &dnsServer)
{
    ULONG bufferSize = 16384;
    std::vector<unsigned char> adapterBuffer(bufferSize);
    ULONG queryStatus;
    for (int attempt = 0; attempt < 3; ++attempt)
    {
        queryStatus =
            GetAdaptersAddresses(AF_INET, GAA_FLAG_INCLUDE_GATEWAYS, nullptr,
                                 (IP_ADAPTER_ADDRESSES *)adapterBuffer.data(), &bufferSize);
        if (queryStatus != ERROR_BUFFER_OVERFLOW)
        {
            break;
        }
        adapterBuffer.resize(bufferSize);
    }
    if (queryStatus != NO_ERROR)
    {
        return false;
    }
    int matchingAdapters = 0;
    for (auto adapter = (IP_ADAPTER_ADDRESSES *)adapterBuffer.data(); adapter;
         adapter = adapter->Next)
    {
        MIB_IF_ROW2 interfaceInfo{};
        interfaceInfo.InterfaceIndex = adapter->IfIndex;
        if (GetIfEntry2(&interfaceInfo) != NO_ERROR ||
            !interfaceInfo.InterfaceAndOperStatusFlags.HardwareInterface ||
            adapter->OperStatus != IfOperStatusUp || !adapter->FirstGatewayAddress)
        {
            continue;
        }
        for (auto unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next)
        {
            if (unicast->Address.lpSockaddr->sa_family != AF_INET ||
                unicast->DadState != IpDadStatePreferred)
            {
                continue;
            }
            auto dnsAddress = adapter->FirstDnsServerAddress;
            while (dnsAddress && dnsAddress->Address.lpSockaddr->sa_family != AF_INET)
            {
                dnsAddress = dnsAddress->Next;
            }
            if (!dnsAddress)
            {
                continue;
            }
            char sourceText[64]{}, dnsText[64]{};
            InetNtopA(AF_INET, &((sockaddr_in *)unicast->Address.lpSockaddr)->sin_addr, sourceText,
                      sizeof(sourceText));
            InetNtopA(AF_INET, &((sockaddr_in *)dnsAddress->Address.lpSockaddr)->sin_addr, dnsText,
                      sizeof(dnsText));
            sourceIp = sourceText;
            dnsServer = dnsText;
            interfaceIndex = adapter->IfIndex;
            ++matchingAdapters;
            break;
        }
    }
    return matchingAdapters == 1;
}
// 微信验证与探针共用物理网卡和开关；路径只从本机标准安装位置读取。
struct WeixinPaths
{
    std::wstring executable;
    std::wstring appEx;
};
static bool ReadWeixinPaths(WeixinPaths &paths)
{
    wchar_t programFiles[MAX_PATH]{}, appData[MAX_PATH]{};
    DWORD n = GetEnvironmentVariableW(L"ProgramW6432", programFiles, MAX_PATH);
    if (!n)
    {
        n = GetEnvironmentVariableW(L"ProgramFiles", programFiles, MAX_PATH);
    }
    const DWORD m = GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH);
    if (!n || n >= MAX_PATH || !m || m >= MAX_PATH)
    {
        return false;
    }
    paths.executable = std::wstring(programFiles) + L"\\Tencent\\Weixin\\Weixin.exe";
    paths.appEx = std::wstring(appData) +
                  L"\\Tencent\\xwechat\\xplugin\\plugins\\RadiumWMPF\\WeChatAppEx.exe";
    for (const auto &path : {paths.executable, paths.appEx})
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY))
        {
            wprintf(L"STOP: executable not found: %ls\n", path.c_str());
            return false;
        }
    }
    return true;
}
// 返回 false 表示检查失败或检测到现有会话；两种情况都保持现状。
static bool ConfirmWeixinNotRunning()
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return false;
    }
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    bool clear = true;
    if (!Process32FirstW(snapshot, &entry))
    {
        clear = false;
    }
    else
    {
        do
        {
            if (_wcsicmp(entry.szExeFile, L"Weixin.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"WeChat.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"WeChatAppEx.exe") == 0)
            {
                printf("EXISTING_WEIXIN pid=%lu\n", entry.th32ProcessID);
                clear = false;
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    if (!clear)
    {
        puts("STOP: please exit Weixin yourself, then retry. No process was changed.");
    }
    return clear;
}
// 此函数不终止微信。控制器退出后，DLL依据开关和控制器存活状态回到原API。
static int RunWeixin(const WeixinPaths &paths, const std::string &hookDllPath)
{
    if (!ConfirmWeixinNotRunning())
    {
        return 6; // 启动前再次检查，缩小检查与启动之间的窗口。
    }
    if (!SetEnvironmentVariableA("ZDC_CHILD_DLL", hookDllPath.c_str()) ||
        !SetEnvironmentVariableW(L"ZDC_CHILD_EXE", paths.executable.c_str()) ||
        !SetEnvironmentVariableW(L"ZDC_CHILD_AUX_EXE", paths.appEx.c_str()) ||
        !SetEnvironmentVariableA("ZDC_CHILD_MODE", "weixin"))
    {
        printf("WEIXIN_ENV_ERROR %lu\n", GetLastError());
        return 6;
    }
    // 日志仅记录PID/API/IP；固定在已有构建输出目录，最多64KiB。
    const auto logPath =
        hookDllPath.substr(0, hookDllPath.find_last_of("\\/") + 1) + "weixin-receipt.log";
    if (!SetEnvironmentVariableA("ZDC_TEST_RECEIPT", logPath.c_str()))
    {
        return 6;
    }
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    std::wstring command = L"\"" + paths.executable + L"\"";
    const BOOL started =
        DetourCreateProcessWithDllExW(paths.executable.c_str(), command.data(), nullptr, nullptr,
                                      FALSE, DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, nullptr,
                                      nullptr, &startup, &child, hookDllPath.c_str(), nullptr);
    if (!started)
    {
        printf("WEIXIN_START_ERROR %lu\n", GetLastError());
        if (child.hThread)
        {
            CloseHandle(child.hThread);
        }
        if (child.hProcess)
        {
            CloseHandle(child.hProcess);
        }
        return 7;
    }
    CloseHandle(child.hThread);
    printf("WEIXIN_STARTED pid=%lu\n", child.dwProcessId);
    printf("RECEIPT %s\n", logPath.c_str());
    puts("IPv4 TCP and connected-UDP proof. Async DNS, IPv6 and unconnected sendto stay original.");
    puts("Keep this console open while testing. Press ENTER to disable and exit; Weixin stays "
         "open.");
    fflush(stdout);
    (void)getchar();
    CloseHandle(child.hProcess);
    return 0;
}
// 通用单应用入口：只启动新的指定 EXE，不接入现有会话。
// Windows 命令行的双引号/反斜杠按 argv 规则转义，保留用户传给应用的参数。
static std::wstring QuoteArgument(const std::wstring &argument)
{
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t ch : argument)
    {
        if (ch == L'\\')
        {
            ++slashes;
            continue;
        }
        if (ch == L'"')
            result.append(slashes * 2 + 1, L'\\');
        else
            result.append(slashes, L'\\');
        slashes = 0;
        result += ch;
    }
    result.append(slashes * 2, L'\\');
    result += L'"';
    return result;
}
static bool CheckApplicationPath(const wchar_t *requested, std::wstring &full)
{
    wchar_t path[MAX_PATH]{};
    const DWORD n = GetFullPathNameW(requested, MAX_PATH, path, nullptr);
    if (!n || n >= MAX_PATH)
        return false;
    full = path;
    if (full.size() < 4 || _wcsicmp(full.c_str() + full.size() - 4, L".exe") != 0)
        return false;
    const DWORD attributes = GetFileAttributesW(full.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY))
        return false;
    const auto name = full.substr(full.find_last_of(L"\\/") + 1);
    WeixinPaths knownWeixin;
    const bool weixin = _wcsicmp(name.c_str(), L"Weixin.exe") == 0 &&
        ReadWeixinPaths(knownWeixin) && _wcsicmp(full.c_str(), knownWeixin.executable.c_str()) == 0;
    DWORD ownSession = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &ownSession)) return false;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
        return false;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    bool clear = Process32FirstW(snapshot, &entry) != FALSE;
    if (clear)
    {
        do
        {
            // 同名检查刻意保守，避免单实例程序把新请求转交给用户已开的会话。
            if (_wcsicmp(entry.szExeFile, name.c_str()) == 0 ||
                (weixin && _wcsicmp(entry.szExeFile, L"WeChatAppEx.exe") == 0))
            {
                DWORD session = 0;
                if (!ProcessIdToSessionId(entry.th32ProcessID, &session) || session != ownSession) continue;
                HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
                if (!process)
                {
                    if (GetLastError() == ERROR_INVALID_PARAMETER) continue;
                    clear = false; break;
                }
                wchar_t image[32768]{};
                DWORD length = 32768;
                const bool queried = QueryFullProcessImageNameW(process, 0, image, &length) != FALSE;
                CloseHandle(process);
                if (!queried) { clear = false; break; }
                if (_wcsicmp(image, full.c_str()) == 0 ||
                    (weixin && _wcsicmp(image, knownWeixin.appEx.c_str()) == 0))
                {
                    printf("EXISTING_APPLICATION pid=%lu\n", entry.th32ProcessID);
                    clear = false;
                    break;
                }
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return clear;
}
static int RunApplication(const std::wstring &path, int argc, wchar_t **argv,
                          const std::string &dll, HANDLE active)
{
    WeixinPaths knownWeixin;
    const auto name = path.substr(path.find_last_of(L"\\/") + 1);
    const bool weixin = _wcsicmp(name.c_str(), L"Weixin.exe") == 0 &&
                        ReadWeixinPaths(knownWeixin) &&
                        _wcsicmp(path.c_str(), knownWeixin.executable.c_str()) == 0;
    if (!SetEnvironmentVariableA("ZDC_CHILD_DLL", dll.c_str()) ||
        !SetEnvironmentVariableW(L"ZDC_CHILD_EXE", path.c_str()) ||
        !SetEnvironmentVariableW(L"ZDC_CHILD_AUX_EXE", weixin ? knownWeixin.appEx.c_str() : L"") ||
        !SetEnvironmentVariableA("ZDC_CHILD_MODE", weixin ? "weixin" : "browser"))
        return 6;
    // 子进程沿用已经验证的精确路径+网络服务角色筛选，不扩大到任意后代。
    std::wstring command = QuoteArgument(path);
    for (int i = 3; i < argc; ++i)
        command += L" " + QuoteArgument(argv[i]);
    wchar_t workingDirectory[32768]{};
    const DWORD workingLength = GetEnvironmentVariableW(L"ZDC_WORKING_DIRECTORY", workingDirectory, 32768);
    if (workingLength >= 32768) return 6;
    if (workingLength && (GetFileAttributesW(workingDirectory) == INVALID_FILE_ATTRIBUTES ||
        !(GetFileAttributesW(workingDirectory) & FILE_ATTRIBUTE_DIRECTORY))) return 6;
    wchar_t showText[8]{};
    const DWORD showLength = GetEnvironmentVariableW(L"ZDC_WINDOW_STYLE", showText, 8);
    const int show = showLength == 1 && (showText[0] == L'0' || showText[0] == L'1' ||
        showText[0] == L'3' || showText[0] == L'7') ? showText[0] - L'0' : 1;
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = static_cast<WORD>(show == 0 || show == 3 || show == 7 ? show : 1);
    PROCESS_INFORMATION child{};
    if (!DetourCreateProcessWithDllExW(path.c_str(), command.data(), nullptr, nullptr, FALSE,
                                       (show == 0 ? CREATE_NO_WINDOW : DETACHED_PROCESS) | CREATE_NEW_PROCESS_GROUP, nullptr,
                                       workingLength ? workingDirectory : nullptr, &startup, &child, dll.c_str(), nullptr))
    {
        printf("APPLICATION_START_ERROR %lu\n", GetLastError());
        return 7;
    }
    CloseHandle(child.hThread);
    printf("APPLICATION_STARTED pid=%lu\n", child.dwProcessId);
    puts("Commands: off = default for new sockets; on = direct; ENTER/q = exit. Application stays "
         "open.");
    fflush(stdout);
    char input[32]{};
    while (fgets(input, sizeof(input), stdin))
    {
        input[strcspn(input, "\r\n")] = 0;
        if (!strcmp(input, "off"))
        {
            ResetEvent(active);
            puts("ROUTING_OFF");
        }
        else if (!strcmp(input, "on"))
        {
            SetEvent(active);
            puts("ROUTING_ON");
        }
        else if (!input[0] || !strcmp(input, "q"))
            break;
        else
            puts("Use on, off, or ENTER/q.");
        fflush(stdout);
    }
    ResetEvent(active);
    CloseHandle(child.hProcess);
    return 0;
}
int wmain(int argc, wchar_t **argv)
{
    const bool appMode = argc >= 3 && wcscmp(argv[1], L"--app") == 0;
    std::wstring appPath;
    if (appMode && !CheckApplicationPath(argv[2], appPath))
    {
        puts("STOP: application path invalid or same-name process already running; nothing "
             "changed.");
        return 6;
    }
    const bool weixinMode = argc >= 2 && wcscmp(argv[1], L"--weixin") == 0;
    const bool checkOnly = weixinMode && argc == 3 && wcscmp(argv[2], L"--check") == 0;
    if (argc != 1 && !appMode && !(weixinMode && (argc == 2 || checkOnly)))
    {
        puts("Usage: ZdC.SocketController.exe [--weixin [--check] | --app EXE [arguments...]]");
        return 2;
    }
    WeixinPaths weixinPaths;
    if (weixinMode)
    {
        if (!ReadWeixinPaths(weixinPaths))
        {
            return 6;
        }
        if (!ConfirmWeixinNotRunning())
        {
            return 6;
        }
        if (checkOnly)
        {
            puts("WEIXIN_CHECK_OK: files found, no existing Weixin session. Nothing launched.");
            return 0;
        }
    }

    WSADATA winsockData{};
    if (WSAStartup(MAKEWORD(2, 2), &winsockData))
    {
        return 1;
    }
    std::string sourceIp, dnsServer;
    ULONG interfaceIndex = 0;
    if (!FindPhysicalAdapter(sourceIp, interfaceIndex, dnsServer))
    {
        puts("STOP: expected exactly one active physical IPv4 adapter with gateway and DNS");
        WSACleanup();
        return 2;
    }
    char executablePath[MAX_PATH]{};
    const DWORD pathLength = GetModuleFileNameA(nullptr, executablePath, MAX_PATH);
    if (pathLength == 0 || pathLength >= MAX_PATH)
    {
        puts("STOP: controller executable path is unavailable or too long");
        WSACleanup();
        return 2;
    }
    std::string executableDirectory = executablePath;
    executableDirectory =
        executableDirectory.substr(0, executableDirectory.find_last_of("\\/") + 1);
    const std::string probePath = executableDirectory + "ZdC.SocketProbe.exe";
    std::string hookDllPath = executableDirectory + "ZdC.SocketHook64.dll";
    if (appMode)
    {
        char runtime[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableA("ZDC_RUNTIME_HOOK", runtime, MAX_PATH);
        if (length >= MAX_PATH) { WSACleanup(); return 6; }
        if (length)
        {
            const DWORD attributes = GetFileAttributesA(runtime);
            if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY))
            { WSACleanup(); return 6; }
            hookDllPath = runtime;
        }
    }
    const std::string controllerPid = std::to_string(GetCurrentProcessId());
    const std::string eventSuffix = controllerPid + "." + std::to_string(GetTickCount64());
    const std::string activeEventName = "Local\\ZdC.SocketProof.Active." + eventSuffix,
                      startEventName = "Local\\ZdC.SocketProof.Start." + eventSuffix;
    constexpr DWORD probeCount = 3;
    std::array<HANDLE, probeCount> readyEvents{}, doneEvents{}, processHandles{};
    std::array<DWORD, probeCount> processIds{};
    // 三个探针各有 ready/done 通知，只有 start 事件由所有探针共享。
    // 手动复位事件不会因某个等待者返回而清除信号，因此三个探针都能同时放行。
    char initialEnabled[8]{};
    GetEnvironmentVariableA("ZDC_START_ENABLED", initialEnabled, sizeof(initialEnabled));
    HANDLE activeEvent = CreateEventA(nullptr, TRUE, strcmp(initialEnabled, "0") != 0, activeEventName.c_str());
    HANDLE startEvent = CreateEventA(nullptr, TRUE, FALSE, startEventName.c_str());
    auto finish = [&](int exitCode) {
        // 任一路径都先停用直连，再释放可能仍在等开始信号的探针；不强杀子进程。
        if (activeEvent)
        {
            ResetEvent(activeEvent);
        }
        if (startEvent)
        {
            SetEvent(startEvent);
        }
        for (DWORD i = 0; i < probeCount; ++i)
        {
            if (processHandles[i])
            {
                CloseHandle(processHandles[i]);
            }
            if (readyEvents[i])
            {
                CloseHandle(readyEvents[i]);
            }
            if (doneEvents[i])
            {
                CloseHandle(doneEvents[i]);
            }
        }
        if (startEvent)
        {
            CloseHandle(startEvent);
        }
        if (activeEvent)
        {
            CloseHandle(activeEvent);
        }
        fflush(stdout);
        WSACleanup();
        return exitCode;
    };
    if (!activeEvent || !startEvent)
    {
        printf("EVENT_ERROR %lu\n", GetLastError());
        return finish(3);
    }
    const std::string interfaceText = std::to_string(interfaceIndex);
    if (!SetEnvironmentVariableA("ZDC_SOURCE_IP", sourceIp.c_str()) ||
        !SetEnvironmentVariableA("ZDC_IFINDEX", interfaceText.c_str()) ||
        !SetEnvironmentVariableA("ZDC_DNS", dnsServer.c_str()) ||
        !SetEnvironmentVariableA("ZDC_ACTIVE_EVENT", activeEventName.c_str()) ||
        !SetEnvironmentVariableA("ZDC_START_EVENT", startEventName.c_str()) ||
        !SetEnvironmentVariableA("ZDC_CONTROLLER_PID", controllerPid.c_str()))
    {
        printf("ENVIRONMENT_ERROR %lu\n", GetLastError());
        return finish(3);
    }
    if (appMode)
    {
        printf("APPLICATION_CONTROLLER source=%s interface=%lu DNS=%s\n", sourceIp.c_str(),
               interfaceIndex, dnsServer.c_str());
        return finish(RunApplication(appPath, argc, argv, hookDllPath, activeEvent));
    }
    if (weixinMode)
    {
        printf("WEIXIN_CONTROLLER source=%s interface=%lu DNS=%s\n", sourceIp.c_str(),
               interfaceIndex, dnsServer.c_str());
        return finish(RunWeixin(weixinPaths, hookDllPath));
    }
    printf("CONTROLLER source=%s interface=%lu DNS=%s probes=%lu\n", sourceIp.c_str(),
           interfaceIndex, dnsServer.c_str(), probeCount);
    fflush(stdout);
    // 45 秒是启动、ready 同步和第一轮完成的总期限，不因多个阶段而累加。
    const ULONGLONG deadline = GetTickCount64() + 45000;
    auto remainingTime = [&]() -> DWORD {
        const ULONGLONG now = GetTickCount64();
        return now < deadline ? static_cast<DWORD>(deadline - now) : 0;
    };
    for (DWORD i = 0; i < probeCount; ++i)
    {
        const std::string childSuffix = eventSuffix + "." + std::to_string(i + 1);
        const std::string readyName = "Local\\ZdC.SocketProof.Ready." + childSuffix,
                          doneName = "Local\\ZdC.SocketProof.Done." + childSuffix;
        readyEvents[i] = CreateEventA(nullptr, TRUE, FALSE, readyName.c_str());
        doneEvents[i] = CreateEventA(nullptr, TRUE, FALSE, doneName.c_str());
        if (!readyEvents[i] || !doneEvents[i] ||
            !SetEnvironmentVariableA("ZDC_READY_EVENT", readyName.c_str()) ||
            !SetEnvironmentVariableA("ZDC_DONE_EVENT", doneName.c_str()))
        {
            printf("CHILD_EVENT_ERROR probe=%lu error=%lu\n", i + 1, GetLastError());
            return finish(3);
        }
        STARTUPINFOA startupInfo{};
        startupInfo.cb = sizeof(startupInfo);
        PROCESS_INFORMATION child{};
        // 顺序创建时每个子进程取得自己那份环境快照；真正请求在 start 信号后并发。
        // Detours 只加载到同目录的新建自控探针，所有现有进程保持原状。
        const BOOL started = DetourCreateProcessWithDllExA(
            probePath.c_str(), nullptr, nullptr, nullptr, FALSE, 0, nullptr,
            executableDirectory.c_str(), &startupInfo, &child, hookDllPath.c_str(), nullptr);
        if (!started)
        {
            const DWORD error = GetLastError();
            if (child.hThread)
            {
                CloseHandle(child.hThread);
            }
            if (child.hProcess)
            {
                CloseHandle(child.hProcess);
            }
            printf("START_ERROR probe=%lu error=%lu\n", i + 1, error);
            return finish(4);
        }
        CloseHandle(child.hThread);
        processHandles[i] = child.hProcess;
        processIds[i] = child.dwProcessId;
    }
    // TRUE 表示必须三个 ready 都有信号，而不是任意一个就开始请求。
    DWORD result = WaitForMultipleObjects(probeCount, readyEvents.data(), TRUE, remainingTime());
    if (result != WAIT_OBJECT_0)
    {
        printf("READY_WAIT_ERROR result=%lu error=%lu\n", result,
               result == WAIT_FAILED ? GetLastError() : 0);
        return finish(5);
    }
    printf("ALL_READY PIDs=%lu,%lu,%lu; releasing shared start event\n", processIds[0],
           processIds[1], processIds[2]);
    fflush(stdout);
    if (!SetEvent(startEvent))
    {
        printf("START_EVENT_ERROR %lu\n", GetLastError());
        return finish(5);
    }
    result = WaitForMultipleObjects(probeCount, doneEvents.data(), TRUE, remainingTime());
    if (result != WAIT_OBJECT_0)
    {
        printf("DONE_WAIT_ERROR result=%lu error=%lu\n", result,
               result == WAIT_FAILED ? GetLastError() : 0);
        return finish(5);
    }
    puts("ALL_DONE; CONTROLLER_EXIT: disabled; three probes retry after this process exits");
    // finish 复位 active 后真正返回退出；探针等待控制器进程结束，再做第二轮。
    return finish(0);
}

// 构建：从仓库根目录执行 python -B src/native/build.py（产物在 artifacts/native）。

// 微信预检（只读）：.\ZdC.SocketController.exe --weixin --check
// 用户自行退出微信后启动验证：.\ZdC.SocketController.exe --weixin
// 保持控制台打开；按回车关闭接管并退出控制器，微信保留运行，已有连接需重连。
