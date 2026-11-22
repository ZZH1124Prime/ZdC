# ZdC

ZdC 是面向 Windows 的轻量应用直连工具：登记指定应用后，这些应用的新连接绕过代理直接联网；
移除登记或退出 ZdC 后恢复原有的代理与网络行为。Linux 安装版和 Android App 的构建、安装及
规则联动说明见 [跨平台预览](ports/README.md)。

## 仓库结构

| 目录 | 内容 |
|---|---|
| `src/ZdC.App` | WPF 界面（卡片列表、托盘、启动与停止） |
| `src/ZdC.Core` | 应用登记、快捷方式、进程状态、连接观测、控制器与 Hook 的生命周期 |
| `src/native` | C++ 源码：注入用拦截 DLL、控制器、自建探针，以及 MSVC 构建脚本 |
| `installer/windows` | Windows 安装包：WiX 描述、单页安装器启动器、一键构建和校验脚本 |
| `ports` | Linux / Android 预览版（共享本地界面，导出 mihomo 进程规则） |
| `tests/ZdC.Core.Offline` | 离线检查：名单读写、快捷方式防护、卡片状态 |
| `third_party/Detours` | 微软 Detours 子模块（MIT），注入实现依赖 |
| `third_party/trafik-ruler` | 已移除的上游代码的许可证说明，仅保留必要声明 |
| `artifacts` | 构建输出目录（被 `.gitignore` 忽略，不随仓库分发） |

## Windows 构建与安装包

前置环境与完整说明见 [installer/windows/README.md](installer/windows/README.md)。简要在
仓库根目录执行：

~~~powershell
git submodule update --init third_party/Detours
dotnet tool install --global wix --version 5.0.2
python -B installer/windows/build.py
~~~

脚本会编译原生组件、发布 .NET 载荷、用 WiX 生成 MSI、逐文件回验哈希，并在
`artifacts/windows/` 生成一个内嵌 MSI 的单文件安装包和构建回执。构建要求工作区干净；
测试包可以使用 `--allow-dirty`。

## 发布

仓库根目录的 [release.json](release.json) 描述当前版本、标签、构建命令和发布附件；
[tools/release/release.py](tools/release/release.py) 是跨平台的通用发布脚本（Windows 和
Linux 共用）。发新版时修改描述文件里的版本号，然后：

~~~powershell
python tools/release/release.py plan
python tools/release/release.py doctor
python tools/release/release.py build
python tools/release/release.py tag --push --yes
python tools/release/release.py publish --yes
~~~

详细字段和命令见 [tools/release/README.md](tools/release/README.md)。

## 运行语义

设计目标是“先登记，再打开应用”：登记中的应用走直连，其他应用保持原有行为；工具退出后，
开启代理时按原有代理规则联网，未开启代理时按原有网络配置直连。规则变更针对新连接，界面
区分关闭窗口与彻底退出。

## 隐私与实现边界

应用名单与设置采用本地存储，功能范围集中于本机应用的直连管理。网络规则的作用范围限定为
已登记应用，并保持现有代理配置与远程连接可用。

## 游戏与反作弊

请勿将带反作弊的游戏及其启动器加入 ZdC。当前接管方式会向目标进程加载 DLL 并拦截网络 API，
可能被反作弊阻止，造成启动失败或被踢出；具体处理取决于游戏规则及检测结果，违规使用也存在
账号处罚风险。[BattlEye 官方 FAQ](https://www.battleye.com/support/faq/) 和
[Riot 官方说明](https://support.riotgames.com/en-us/riot/performance/game-memory-access-removed-for-third-party-apps)
均区分了工具拦截与封号，文件加载被阻止本身并不等于账号被封。

暂停直连或退出 ZdC 会停止接管新连接，但已经加载的 DLL 仍驻留到目标进程结束。若误将游戏加入，
请移除该项，完全退出游戏及其受控启动器，再从原入口启动；不要尝试绕过反作弊拦截。

## 许可证

本仓库根目录的 `COPYING` 是 GNU GPL v3 许可证正文；`third_party` 下保留了 Detours（MIT）和
已移除上游代码（GPL-3.0-or-later）的许可证与来源说明。分发二进制前请确认相应的许可证义务。
