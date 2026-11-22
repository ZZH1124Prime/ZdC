# 发布自动化

`tools/release/release.py` 是一个跨平台的通用发布脚本（Windows / Linux 都能用，只依赖
Python 3 标准库、git 和 GitHub CLI 或 token）。每个项目在仓库根目录放一个 `release.json`
描述自己：版本号、标签、构建命令、发布附件和说明文件。发新版时只需要改描述文件里的
`version`（和需要时的 `tag_commit`），其余由脚本完成。

## 前置条件

- Python 3.9+
- git
- 上传 Release 需要二选一：
  - [GitHub CLI](https://cli.github.com/)：`gh auth login`；
  - 或环境变量 `GH_TOKEN` / `GITHUB_TOKEN`（有 `repo` 或 `public_repo` 权限）。

## 快速开始（本仓库）

在仓库根目录执行：

```powershell
python tools/release/release.py plan       # 看解析后的版本、标签、提交、附件
python tools/release/release.py doctor     # 检查工作区、回执、标签、附件、上传工具
python tools/release/release.py build      # 执行 release.json 里的构建命令
python tools/release/release.py tag --push --yes
python tools/release/release.py publish --yes
```

发布前先把发行说明写到 `release.json` 的 `notes` 指向的文件（默认按版本命名，例如
`artifacts/windows/RELEASE_NOTES-0.1.2.md`），并确认安装包和回执已经生成。

Linux 上用法相同，脚本会自动选择 `linux` 平台；也可以显式指定：

```bash
python3 tools/release/release.py --platform linux plan
chmod +x tools/release/release.py   # 可选：直接 ./tools/release/release.py plan
```

## 命令

| 命令 | 作用 |
|---|---|
| `plan` | 显示版本、标签、tag 提交、构建命令、附件、说明文件 |
| `doctor` | 检查工作区是否干净、分支、远端、tag 提交是否为当前分支祖先、安装包回执的源码提交是否与 tag 提交一致、附件和发布工具是否齐备 |
| `build` | 按 `platforms.<平台>.build` 顺序执行构建命令 |
| `tag --push` | 在 `tag_commit` 指向的提交上创建注解标签并推送 |
| `publish [--dry-run]` | 生成 SHA256SUMS，创建 GitHub Release 并上传附件 |
| `all` | doctor → build → doctor → tag --push → publish |

发布类动作（`tag`、`publish`）需要加 `--yes`；`publish --dry-run` 只打印动作，不写文件、
不上传。

## 描述文件格式

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
  "platforms": {
    "windows": {
      "build": ["python -B installer/windows/build.py"],
      "assets": ["dist/*-{version}-*.exe", "dist/*.receipt.txt"],
      "extra_assets": ["third_party/Detours/LICENSE"],
      "sha256sums": "dist/SHA256SUMS-{version}.txt",
      "receipt": "dist/*.receipt.txt",
      "receipt_commit_pattern": "源码提交：([0-9a-f]{40})"
    },
    "linux": {
      "build": ["python3 build.py"],
      "assets": ["dist/*.deb"],
      "extra_assets": []
    }
  },
  "release": { "draft": false, "prerelease": false, "latest": true }
}
```

字段说明：

- `version`：本次版本号，模板里用 `{version}`；
- `tag`：标签名模板，默认 `v{version}`；
- `tag_commit`：标签指向的提交。**重要**：发布包里嵌入的源码提交/回执要和它一致；
  如果安装包是从当前 HEAD 构建且之后没有追加提交，可以写 `HEAD` 或删掉该字段。脚本的
  `doctor` 会读取回执里的源码提交做交叉校验；
- `notes`：发布说明文件，支持 `{version}`；
- `platforms.<平台>.build`：构建命令列表，按顺序执行；
- `assets`：发布附件 glob，支持 `{version}`，用于上传的安装包等；
- `extra_assets`：许可证、声明等额外附件；
- `sha256sums`：脚本据此生成校验文件并一起上传；
- `receipt` + `receipt_commit_pattern`：可选，用于 doctor 校验安装包回执里的源码提交。

## 在其它项目里使用

脚本本身不包含项目信息，复制 `tools/release/release.py` 到任意仓库（或直接引用本仓库的
脚本），在该仓库根目录写自己的 `release.json` 即可；Windows/Linux 共用同一份脚本。用
`--config` 可以指向别处的描述文件：

```bash
python3 /path/to/release.py --config release.linux.json --platform linux plan
```

`gh` 不可用时，脚本会回退到 GitHub REST API（读取 `GH_TOKEN`/`GITHUB_TOKEN`）。
