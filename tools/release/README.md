# 一键发布

每个项目只需要在仓库根目录维护一个 `release.json`，然后运行一次脚本即可完成发布：

```powershell
python tools/release/release.py
```

Windows 也可以直接运行 `tools\release\release.cmd`，Linux/macOS 可以运行
`tools/release/release.sh`（或先 `chmod +x` 再直接执行 `release.py`）。

脚本不需要任何命令行参数。发新版时只改 `release.json`（以及按版本命名的发布说明文件），
保存后运行上面的一行命令即可。

## 脚本做了什么

1. **检查仓库**：工作区必须干净、当前分支符合配置、`tag_commit` 是当前分支的祖先；
   如果安装包自带构建回执，会核对回执里的源码提交与 `tag_commit` 一致；
2. **构建（按需）**：如果已经存在匹配 `tag_commit` 的安装包和回执，跳过构建；否则按
   `platforms.<平台>.build` 依次执行构建命令，并重新校验产物；
3. **打标签（按需）**：标签不存在时创建注解标签并推送；已存在且指向正确提交则跳过；
4. **发布（按需）**：生成 `SHA256SUMS-<版本>.txt`，调用 `gh release create`（或 GitHub
   REST API）创建 Release 并上传安装包、回执、校验文件和许可证附件；Release 已存在则跳过。

因此这个脚本可以重复运行：已经完成的步骤不会重复执行。

## 前置条件

- Python 3.9+、git；
- 上传 Release 需要二选一：
  - [GitHub CLI](https://cli.github.com/)：`gh auth login`；
  - 或环境变量 `GH_TOKEN` / `GITHUB_TOKEN`（`repo` 或 `public_repo` 权限）。

只预览不发布：把 `release.json` 里的 `"dry_run"` 改成 `true`，或临时设置环境变量
`RELEASE_DRY_RUN=1`。

## release.json

```json
{
  "project": "ZdC",
  "repo": "owner/name",
  "remote": "origin",
  "branch": "master",
  "version": "0.1.2",
  "tag": "v{version}",
  "tag_commit": "a4c160e1e258cac9b675826fe6abed9ddc54c670",
  "title": "ZdC {version} — Windows x64",
  "notes": "artifacts/windows/RELEASE_NOTES-{version}.md",
  "platform": "auto",
  "dry_run": false,
  "force_build": false,
  "platforms": {
    "windows": {
      "build": ["python -B installer/windows/build.py"],
      "assets": ["artifacts/windows/*-{version}-*.exe", "artifacts/windows/*.receipt.txt"],
      "extra_assets": ["third_party/Detours/LICENSE"],
      "sha256sums": "artifacts/windows/SHA256SUMS-{version}.txt",
      "receipt": "artifacts/windows/*.receipt.txt",
      "receipt_commit_pattern": "源码提交：([0-9a-f]{40})"
    },
    "linux": {
      "build": ["python3 build.py"],
      "assets": ["dist/*-{version}*.deb"],
      "extra_assets": []
    }
  },
  "release": { "draft": false, "prerelease": false, "latest": true }
}
```

| 字段 | 说明 |
|---|---|
| `version` | 版本号；模板里用 `{version}`，也是唯一的“每次发版要改”的字段 |
| `tag` | 标签名模板，默认 `v{version}` |
| `tag_commit` | 标签要指向的源码提交。**发布包含构建回执时，它必须等于回执里的源码提交**；如果安装包就是当前 HEAD 构建、之后没有追加提交，可以写 `HEAD` |
| `notes` | 发布说明文件，支持 `{version}`；内容会作为 Release 描述 |
| `dry_run` | `true` 时只打印计划 |
| `force_build` | `true` 时即使已有产物也重新构建 |
| `platform` | `auto`（按当前系统选择）、`windows`、`linux` 或自定义键 |
| `platforms.<平台>.build` | 构建命令列表，按顺序执行 |
| `platforms.<平台>.assets` | 要上传的附件 glob，支持 `{version}` |
| `platforms.<平台>.extra_assets` | 许可证、声明等额外附件 |
| `platforms.<平台>.sha256sums` | 生成校验文件的位置（会一起上传） |
| `platforms.<平台>.receipt` / `receipt_commit_pattern` | 可选，用于核对构建回执里的源码提交与 `tag_commit` |
| `release` | `draft` / `prerelease` / `latest` 三项发布选项 |

## 在其它项目或 Linux 上使用

脚本本身与项目无关。把 `tools/release/release.py`（和可选的 `release.sh`）复制到目标仓库，
在目标仓库根目录写自己的 `release.json` 即可；Linux 上会自动使用 `linux` 平台配置。

```bash
chmod +x tools/release/release.sh
./tools/release/release.sh
```

Windows/Linux 共用同一份脚本；如果目标机没有 `gh`，设置 `GH_TOKEN` 后脚本会走 GitHub
REST API 上传。
