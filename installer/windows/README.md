# Windows x64 安装包

从仓库根目录运行。需要 Windows x64、带 msilib 的 Python 3.10–3.12、支持 C# 12 的 .NET SDK、.NET Framework 4.8 引用程序集和 7-Zip。

~~~powershell
python -B installer/windows/build.py
~~~

正式交付目录固定为 `artifacts/windows`。一次构建生成一个单页 WPF Setup EXE，内含真实 MSI。已存在的同名文件受覆盖保护。只验证重建时运行：

~~~powershell
python -B installer/windows/build.py --check-build
~~~

构建按 `release.json` 的完整 Git 提交读取应用版本、石板和字体样式等视觉定义，从 `artifacts/ZdC` 读取已发布载荷。逐文件核对清单与实际 CAB。构建临时目录位于仓库的 `artifacts` 内，成功或失败后都会清理。正式包只包含运行所需的安装资源与清单列出的载荷。

下一版先产出并核验新的 Windows 应用载荷，再在 `release.json` 中更新 Commit 和各文件的 Name、After（SHA-256），或者用 `--manifest` 和 `--payload` 指向本仓内的新清单和载荷目录。产品版本取自该提交的 `src/ZdC.App/ZdC.App.csproj`。较高版本延续现有 MSI UpgradeCode，ProductCode 按版本和架构确定，PackageCode 每次新建。因此重建核对载荷和安装数据库配置；已验收的正式包另以发布时 SHA-256 识别。

发布使用 `artifacts/windows/ZdC-Setup-<版本>-Windows-x64-<提交前七位>.exe`。根目录的 Git 忽略规则覆盖 artifacts；构建源码、模板和发布输入清单在 installer/windows 中随源码管理。
