# ZdC 的 Linux 和 Android 预览版

两端使用同一份本地界面、应用名单格式、规则生成器和现有 ZdC 标志。Windows 的入口在 `src/ZdC.App`。Linux 和 Android 的平台代码负责系统选择器、启动、持久化和文件导出。

## 构建

- **Linux 安装包**　在仓库根目录执行 `python3 ports/linux/build_deb.py`。产物为 `artifacts/zdc_0.1.0~preview1_all.deb`。Ubuntu 20.04 桌面可用 `sudo apt install ./artifacts/zdc_0.1.0~preview1_all.deb` 安装，从应用菜单打开 ZdC。卸载命令为 `sudo apt remove zdc`，名单保存在当前用户的 XDG 配置目录下。
- **Android App**　用 Android Studio 打开 `ports/android`，或在该目录使用 JDK 17、Android SDK 35 和 Build Tools 35.0.0 执行 `./gradlew :app:assembleDebug`。Windows 可使用 `gradlew.bat`。最低系统版本为 Android 6.0，界面需要支持 ES2018 的系统 WebView。调试 APK 在 `app/build/outputs/apk/debug/`。
- **回归测试**　在仓库根目录用 Node 18+ 执行 `node --test ports/tests/model.test.js`，用 `python3 ports/tests/test_deb.py` 检查 deb 安装目录及入口权限。在 `ports/android` 执行 `./gradlew :app:testDebugUnitTest` 可用 Robolectric 模拟 API 23 和 35，检查选择、启动、名单恢复以及系统文件选择器的保存和取消。

## 在现有客户端中使用

1. 在 Linux 选择应用真正发起连接的 ELF 可执行文件。脚本、启动器、子进程和解释器可能拥有不同的进程路径。Android 从当前用户可见、有桌面入口的包中选择。登记与移除均保存在本机。
2. 选择“导出规则集”。Linux 生成精确的 `PROCESS-PATH` 规则，Android 生成精确的 `PROCESS-NAME` 包名规则。导出的 YAML 是 mihomo 的 classical 规则集。仅适用于能识别进程并能导入本地规则集的客户端。
3. 在客户端的配置目录导入规则文件。例如文件位于 `rules/zdc-apps.yaml` 时，向现有 `rule-providers` 映射添加以下条目，再在 `rules` 中按所需优先级添加 `RULE-SET,zdc-apps,DIRECT`，置于兜底规则之前。已有的规则和代理组继续保留。移动端导入入口和可用文件路径由客户端决定。

```yaml
rule-providers:
  zdc-apps:
    type: file
    behavior: classical
    format: yaml
    path: ./rules/zdc-apps.yaml
```

4. 在客户端重载后启动已登记的应用，并从连接日志核对新连接命中的规则与出站。应用未走该核心、核心未识别进程或规则被更高优先级覆盖时，名单无法决定实际路径。未登记应用继续匹配原有规则。升级后路径或包名有变化时，移除旧入口并登记新入口。
5. 重新编辑名单后要重新导出并重载。关闭 ZdC 后，已导入的规则继续由客户端执行。撤销时，移除客户端内对应的 `RULE-SET` 和 provider 条目并重载。当前预览提供可审查的规则交接，实际连接状态以客户端日志为准。

Android 预览使用现有代理核心合作模式。每个用户档案只有一个活动 VPN，启动另一项 VPN 服务会替换它。本版只使用包启动入口及系统文件选择器，无需 VPN 授权。[Android VPN](https://developer.android.com/develop/connectivity/vpn) · [mihomo 进程规则](https://wiki.metacubex.one/en/config/rules/) · [规则集](https://wiki.metacubex.one/en/config/rule-providers/)
