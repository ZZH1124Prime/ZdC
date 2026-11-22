# Windows x64 安装包

安装包由 WiX 生成 MSI，再由 `launcher/` 里的单页 WPF 启动器内嵌 MSI 生成一个 Setup EXE。
一次构建同时产出：原生二进制、.NET 载荷、MSI、Setup EXE 和构建回执。

## 前置环境

- Windows x64
- .NET SDK 9（构建应用和启动器）
- .NET Framework 4.8 引用程序集（`.NET Framework 4.8 Developer Pack`，编译启动器）
- Visual Studio 2022（或 Build Tools）勾选“使用 C++ 的桌面开发”和 Windows SDK（编译原生组件）
- Python 3.10+
- 7-Zip（解包回验 MSI 内的文件）
- WiX 5（固定版本，见下方说明）

~~~powershell
git submodule update --init third_party/Detours
dotnet tool install --global wix --version 5.0.2
~~~

> OSMF 费用自 WiX v6 引入（年收入超过阈值的组织需要赞助）；WiX v7 起强制接受
> EULA（`wix eula accept wix7` 或每条命令加 `-acceptEula wix7`）。因此这里固定在免费
> 且稳定的 WiX 5.0.2。
> 如果机器上装过其他版本的 wix 全局工具，先 `dotnet tool uninstall --global wix` 再按上面的版本安装。

## 构建

在仓库根目录执行：

~~~powershell
python -B installer/windows/build.py
~~~

构建要求工作区干净（产物绑定当前提交，避免“包里的代码和提交对不上”）：

- 有未提交改动时会直接失败；
- 只想出测试包时可以加 `--allow-dirty`，产物名会带 `-dirty` 后缀；
- 排查失败时加 `--keep-work`，临时目录会保留并在最后打印路径。

产物固定写入 `artifacts/windows/`：

~~~
ZdC-Setup-<版本>-Windows-x64-<提交前七位>[-dirty].exe
ZdC-Setup-<版本>-Windows-x64-<提交前七位>[-dirty].receipt.txt
~~~

同名文件已存在时构建会拒绝覆盖。`artifacts/` 已被 `.gitignore` 忽略。

## 构建做了什么

1. **原生二进制**：调用 `python -B src/native/build.py`，用 MSVC 编译
   `ZdC.SocketController.exe`、`ZdC.SocketHook32/64.dll`（和测试用探针）到 `artifacts/native/`。
2. **.NET 载荷**：`dotnet publish` 发布 `src/ZdC.App`（框架依赖、x64）到临时目录，
   并把 `SourceRevisionId` 固定为当前提交。
3. **载荷校验**：文件集合必须与预期一致（10 个文件）；`ZdC.exe`/`ZdC.dll` 的产品版本必须是
   `<版本>+<当前提交>`；原生文件必须和本次编译结果哈希相同；deps.json 必须声明对应版本。
4. **WiX**：`installer/windows/Package.wxs` 描述固定结构（目录、Feature、桌面快捷方式、
   升级规则、Secure 属性），构建时在临时目录生成 `Files.wxs` 为每个载荷文件创建 Component，
   然后 `wix build -arch x64` 生成 `ZdC.msi`。
5. **MSI 校验**：读取 MSI 的 ProductCode / ProductVersion / UpgradeCode / PackageCode /
   摘要信息（必须是 `x64;1033`），再用 7-Zip 把 CAB 里的每个文件解出来和载荷逐项比对哈希。
6. **启动器**：从应用源码取视觉资源（`Assets/ZdC.png`、`ZdCMark.png`、`App.xaml` 的主题、
   `MainWindow.xaml` 的石板路径和尺寸代码），生成 `Identity.cs`（内嵌 MSI 的 SHA-256、包码、
   版本、载荷总大小），用 Roslyn 编译成 .NET Framework 4.8 的单文件 Setup EXE，MSI 作为资源
   嵌入。安装时启动器先校验内嵌 MSI 的 SHA-256，再调用 `msi.dll` 安装。
7. 删除临时目录，只留下 `artifacts/windows/` 的成品和回执。

## 安装与升级

- 安装界面提供“关闭窗口后最小化到托盘”（默认勾选；更新安装时沿用当前偏好）。选择结果写入
  `%LOCALAPPDATA%\ZdC\close-to-tray`，应用启动时按它恢复；之后可在应用内齿轮菜单随时修改。
- 安装器启动后会检查目标机 .NET 9 桌面运行时、ZdC 是否在运行、安装目录是否合法；
- MSI 采用 per-machine 安装（`Program Files\ZdC`）、固定 UpgradeCode，ProductCode 由版本推导，
  PackageCode 每次构建由 WiX 新建；
- **同版本内容更新**：安装器检测到“同一 ProductCode、不同 PackageCode”时使用
  `REINSTALL=Main REINSTALLMODE=vamus` 覆盖文件；
- **版本升级（推荐做法）**：每次发版把 `src/ZdC.App/ZdC.App.csproj` 的 `Version` +1，
  例如 `0.1.1`。这样走标准 MajorUpgrade（`RemoveExistingProducts`），同版本覆盖的特殊处理
  也就不再被依赖；
- MSI 详细日志：`%LOCALAPPDATA%\ZdC\diagnostics\installer\<时间>-<guid>.log`；
- 安装包未签名，SmartScreen 可能提示未知发布者。

## 排障

| 现象 | 处理 |
|---|---|
| 构建提示工作区有改动 | 提交改动，或用 `--allow-dirty` |
| 找不到 wix | 按上面的命令安装 WiX 5.0.2 |
| 找不到 vswhere/cl | 安装 Visual Studio C++ 桌面开发组件 |
| 安装失败 1603 | 先完全退出 ZdC，查看安装日志；确认安装目录为空或与已装版本一致 |
| 安装界面提示需要 .NET 9 桌面运行时 | 安装 .NET 9 Desktop Runtime（x64） |
| 提示已安装更高版本 | 先卸载新版本，或提升本次构建的版本号 |

## 与旧版（msilib + release.json）的区别

旧的构建方式需要手工维护 `release.json`（提交 + 每个文件哈希），还要手工准备载荷目录；
默认清单会一直指向旧提交，容易打出旧载荷。现在：

- 载荷直接从当前工作区发布，版本和提交戳在构建时校验；
- WiX 负责 MSI 数据库，手写 MSI 表（约 250 行）已删除；
- 不再需要 Python 的 `msilib`（Python 3.13 起已移除）；
- 每次构建自动生成回执，记录全部哈希和 MSI 身份。
