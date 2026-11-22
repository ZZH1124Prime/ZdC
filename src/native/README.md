# 原生 Windows 组件

本目录是 Windows 应用直连功能用到的全部 C++ 源码，使用微软 Detours（MIT，见
`third_party/Detours`）在目标进程中接管网络 API。

| 文件 | 产物 | 用途 |
|---|---|---|
| `SocketHook.cpp` + `WinInetRouting.cpp` | `ZdC.SocketHook64.dll` / `ZdC.SocketHook32.dll` | 注入目标进程的拦截 DLL，接管 socket / WinInet 调用 |
| `SocketController.cpp` | `ZdC.SocketController.exe` | 由 ZdC 启动的控制器，选择物理出口并加载拦截 DLL |
| `SocketProbe.cpp` | `ZdC.SocketProbe.exe` | 自建探针，验证直连和退出后的恢复行为（不随安装包发布） |

## 构建

前置：Windows x64、Visual Studio 2022（或 Build Tools）勾选“使用 C++ 的桌面开发”和
Windows SDK、已检出的 Detours 子模块。

~~~powershell
git submodule update --init third_party/Detours
python -B src/native/build.py
~~~

输出写入 `artifacts/native/`（已被 `.gitignore` 忽略）。安装包构建脚本会自动调用本脚本，
一般不需要单独运行。

`python -B src/native/build.py --output <目录>` 可以把输出放到其他目录。
