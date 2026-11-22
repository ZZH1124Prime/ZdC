#!/usr/bin/env python3
"""ZdC 一键发布脚本（跨平台）。

用法：先按需修改仓库根目录的 ``release.json``（版本号、tag 提交、发布说明等），
然后在仓库根目录直接运行：

    python tools/release/release.py

脚本按顺序完成：检查工作区 → 按需构建 → 按需创建并推送 tag → 创建 GitHub Release
并上传附件。已经存在且正确的构建产物、tag、Release 会自动跳过，因此可以重复运行。

调试时设置环境变量 ``RELEASE_DRY_RUN=1`` 只打印计划、不做任何修改。
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

CONFIG_NAME = "release.json"
RELEASE_API = "https://api.github.com"
DRY_RUN_ENV = "RELEASE_DRY_RUN"

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------

def log(message: str = "") -> None:
    print(message, flush=True)


def fail(message: str) -> None:
    print(f"\n错误：{message}", file=sys.stderr, flush=True)
    raise SystemExit(1)


def run(command: list[str] | str, cwd: Path, check: bool = True,
        capture: bool = False) -> str:
    """运行命令。字符串命令用于构建步骤（交给 shell），列表用于 git 等固定命令。"""
    display = command if isinstance(command, str) else " ".join(command)
    log(f"> {display}")
    try:
        result = subprocess.run(
            command, cwd=cwd, text=True, capture_output=capture, check=check,
            shell=isinstance(command, str),
        )
    except FileNotFoundError:
        fail(f"找不到可执行文件：{display}")
    except subprocess.CalledProcessError as error:
        detail = (error.stderr or error.stdout or "").strip() if capture else ""
        fail(f"命令失败（{error.returncode}）：{display}" + (f"\n{detail}" if detail else ""))
    return (result.stdout or "").strip() if capture else ""


def git(repo: Path, *arguments: str) -> str:
    return run(["git", *arguments], cwd=repo, capture=True)


def repository_root() -> Path:
    if shutil.which("git") is None:
        fail("找不到 git。")
    top = run(["git", "rev-parse", "--show-toplevel"], cwd=Path.cwd(), capture=True)
    if not top:
        fail("当前目录不在 Git 仓库中。")
    return Path(top).resolve()


def expand(template: str, values: dict[str, str]) -> str:
    try:
        return template.format(**values)
    except KeyError as error:
        fail(f"release.json 的占位符缺少值：{{{error.args[0]}}}")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def is_true(value: str | None) -> bool:
    return str(value or "").strip().lower() in {"1", "true", "yes", "on"}


# ---------------------------------------------------------------------------
# 发布描述
# ---------------------------------------------------------------------------

@dataclass
class Release:
    """release.json 解析后的发布描述。"""

    repo: Path
    config_path: Path
    data: dict

    def __post_init__(self) -> None:
        self.version = str(self.data.get("version", "")).strip()
        if not self.version:
            fail("release.json 缺少 version。")
        values = {"version": self.version}
        self.project = str(self.data.get("project", self.repo.name))
        self.tag = expand(str(self.data.get("tag", "v{version}")), values)
        self.title = expand(str(self.data.get("title", "{project} {version}")),
                            {**values, "project": self.project})
        self.branch = str(self.data.get("branch", "master"))
        self.remote = str(self.data.get("remote", "origin"))
        self.repository = str(self.data.get("repo", "")).strip()
        self.notes = self._path(expand(str(self.data["notes"]), values)
                                if self.data.get("notes") else "")
        self.tag_commit_override = str(self.data.get("tag_commit", "") or "HEAD")
        self.force_build = bool(self.data.get("force_build", False))
        self.force_tag = bool(self.data.get("force_tag", False))
        self.dry_run_config = bool(self.data.get("dry_run", False))
        self.release_options = dict(self.data.get("release", {}))

        platform = str(self.data.get("platform", "auto"))
        if platform == "auto":
            platform = "windows" if sys.platform.startswith("win") else "linux"
        self.platform = platform
        platforms = self.data.get("platforms", {})
        if platform not in platforms:
            fail(f"release.json 没有平台 {platform!r} 的配置（可用：{sorted(platforms)}）")
        self.settings = platforms[platform] or {}

    def _path(self, relative: str) -> Path | None:
        return (self.repo / relative).resolve() if relative else None

    @property
    def dry_run(self) -> bool:
        return self.dry_run_config or is_true(os.environ.get(DRY_RUN_ENV))

    def tag_commit(self) -> str:
        return git(self.repo, "rev-parse", "--verify",
                   f"{self.tag_commit_override}^{{commit}}").strip()

    def find(self, patterns: list[str] | None) -> list[Path]:
        found: list[Path] = []
        for pattern in patterns or []:
            relative = expand(str(pattern), {"version": self.version})
            matches = sorted(self.repo.glob(relative))
            if not matches:
                log(f"  警告：没有匹配到附件：{relative}")
            found.extend(item for item in matches if item.is_file() and item not in found)
        return found

    def assets(self) -> list[Path]:
        """安装包等主要附件；校验文件只针对这些文件生成。"""
        return self.find(self.settings.get("assets"))

    def extra_entries(self) -> list[tuple[Path, str]]:
        """额外附件。字符串直接使用文件名；对象可用 name 指定上传名。

        例如：{"path": "third_party/Detours/LICENSE", "name": "Detours-LICENSE.txt"}
        """
        entries: list[tuple[Path, str]] = []
        for entry in self.settings.get("extra_assets") or []:
            if isinstance(entry, str):
                entries.extend((item, item.name) for item in self.find([entry]))
            elif isinstance(entry, dict):
                pattern = entry.get("path")
                if not pattern:
                    fail("extra_assets 的对象缺少 path。")
                matches = self.find([str(pattern)])
                preferred = str(entry.get("name") or "").strip()
                for index, item in enumerate(matches, 1):
                    name = preferred or item.name
                    if len(matches) > 1:
                        stem, suffix = Path(name).stem, Path(name).suffix
                        name = f"{stem}-{index}{suffix}"
                    entries.append((item, name))
            else:
                fail("extra_assets 只能是字符串或 {path, name} 对象。")
        return entries

    def upload_entries(self) -> list[tuple[Path, str]]:
        """返回 (实际上传文件, 上传名)。同名附件会自动按父目录/序号改名。"""
        entries = [(item, item.name) for item in self.assets()] + self.extra_entries()
        counts = Counter(name for _, name in entries)
        used: Counter[str] = Counter()
        staging = self.repo / "artifacts" / "release-assets" / self.version
        prepared: list[tuple[Path, str]] = []
        for source, name in entries:
            final = name
            if counts[name] > 1:
                stem, suffix = Path(name).stem, Path(name).suffix
                final = f"{source.parent.name}-{stem}{suffix}"
            if used[final]:
                stem, suffix = Path(final).stem, Path(final).suffix
                final = f"{stem}-{used[final] + 1}{suffix}"
            used[final] += 1
            if final == source.name:
                prepared.append((source, final))
                continue
            staging.mkdir(parents=True, exist_ok=True)
            target = staging / final
            if not target.is_file() or sha256(target) != sha256(source):
                shutil.copyfile(source, target)
            prepared.append((target, final))
        return prepared

    def checksums_path(self) -> Path | None:
        relative = self.settings.get("sha256sums")
        return self._path(expand(str(relative), {"version": self.version})) if relative else None

    def receipt_commit(self) -> str:
        """从构建回执里读出源码提交，用于确认产物和 tag 指向同一提交。"""
        pattern = self.settings.get("receipt")
        regex = self.settings.get("receipt_commit_pattern", r"源码提交：([0-9a-f]{40})")
        if not pattern:
            return ""
        matches = sorted(self.repo.glob(expand(str(pattern), {"version": self.version})))
        if not matches:
            return ""
        found = re.search(regex, matches[0].read_text(encoding="utf-8", errors="replace"))
        return found.group(1) if found else ""

    def notes_text(self) -> str:
        if self.notes is None:
            fail("release.json 没有配置 notes（发布说明文件）。")
        if not self.notes.is_file():
            fail(f"发布说明文件不存在：{self.notes}")
        text = self.notes.read_text(encoding="utf-8").strip()
        if not text:
            fail("发布说明文件为空。")
        return text


def load_release(repo: Path) -> Release:
    path = repo / CONFIG_NAME
    if not path.is_file():
        fail(f"找不到 {path}。")
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        fail(f"release.json 不是合法 JSON：{error}")
    return Release(repo, path, data)


# ---------------------------------------------------------------------------
# 仓库检查
# ---------------------------------------------------------------------------

def check_worktree(release: Release) -> None:
    status = git(release.repo, "status", "--porcelain", "--untracked-files=normal")
    if status.strip():
        fail("工作区有未提交改动，先提交再发布：\n" + status)
    branch = git(release.repo, "rev-parse", "--abbrev-ref", "HEAD").strip()
    if release.branch and branch != release.branch:
        fail(f"当前分支 {branch!r} 与 release.json 的 {release.branch!r} 不一致。")
    log(f"  工作区干净，分支 {branch}")


def check_tag_commit(release: Release, commit: str) -> None:
    run(["git", "merge-base", "--is-ancestor", commit, "HEAD"], cwd=release.repo, capture=True)
    receipt = release.receipt_commit()
    if receipt and not commit.startswith(receipt) and not receipt.startswith(commit[:len(receipt)]):
        fail(f"安装包回执的源码提交 {receipt} 与 tag_commit {commit} 不一致；"
             "先确认产物或修改 release.json 的 tag_commit。")
    log(f"  产物与 tag 提交一致：{commit[:12]}")


def check_tag(release: Release, commit: str, create: bool) -> None:
    """确保 tag 存在且指向正确提交；需要时创建并推送。"""
    existing = git(release.repo, "tag", "--list", release.tag).strip()
    if existing:
        target = git(release.repo, "rev-parse", f"{release.tag}^{{commit}}").strip()
        if target != commit:
            fail(f"标签 {release.tag} 已存在但指向 {target}，预期 {commit}。"
                 "如需重新发布请升级版本号；不要强制改已公开的标签。")
        log(f"  tag 已存在且指向正确提交，跳过创建")
        return
    if release.dry_run or not create:
        log(f"  [计划] 创建并推送标签 {release.tag} -> {commit[:12]}")
        return
    git(release.repo, "tag", "-a", release.tag, "-m", f"{release.project} {release.version}",
        commit, capture=False)
    git(release.repo, "push", release.remote, release.tag, capture=False)
    log(f"  已创建并推送标签 {release.tag}")


# ---------------------------------------------------------------------------
# 构建与校验
# ---------------------------------------------------------------------------

def build_if_needed(release: Release, commit: str) -> None:
    assets = release.assets()
    receipt = release.receipt_commit()
    ready = bool(assets) and (not receipt or receipt == commit or commit.startswith(receipt))
    if ready and not release.force_build:
        log("  已存在匹配当前 tag 提交的构建产物，跳过构建")
        return
    commands = release.settings.get("build") or []
    if not commands:
        if not ready:
            fail("缺少构建命令，也没有可用的构建产物。")
        return
    if release.dry_run:
        log(f"  [计划] 运行构建命令：{commands}")
        return
    for command in commands:
        log(f"  构建：{command}")
        run(command, cwd=release.repo, capture=False)
    assets = release.assets()
    if not assets:
        fail("构建完成后仍没有匹配到发布附件。")
    check_tag_commit(release, commit)


def write_checksums(release: Release) -> Path | None:
    target = release.checksums_path()
    if target is None:
        return None
    files = release.find(release.settings.get("assets"))
    lines = [f"{sha256(item)}  {item.name}" for item in files]
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("\n".join(lines) + "\n", encoding="ascii")
    log(f"  写入校验文件：{target.relative_to(release.repo)}")
    return target


# ---------------------------------------------------------------------------
# GitHub Release
# ---------------------------------------------------------------------------

def gh_path() -> str:
    """返回 gh 可执行文件；优先 PATH，其次 Windows 常见安装目录。"""
    found = shutil.which("gh")
    if found:
        return found
    candidates = []
    program_files = os.environ.get("ProgramFiles")
    if program_files:
        candidates.append(Path(program_files) / "GitHub CLI" / "gh.exe")
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        candidates.append(Path(local_app_data) / "Programs" / "GitHub CLI" / "gh.exe")
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    return ""


def gh_available() -> bool:
    return bool(gh_path())


def github_token() -> str:
    return os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN") or ""


def release_asset_names(release: Release) -> list[str] | None:
    """返回已有 Release 的附件名；Release 不存在时返回 None。"""
    if gh_available():
        result = subprocess.run(
            [gh_path(), "release", "view", release.tag, "--json", "assets"],
            cwd=release.repo, text=True, capture_output=True,
        )
        if result.returncode != 0:
            return None
        try:
            return [str(item["name"]) for item in json.loads(result.stdout).get("assets", [])]
        except (json.JSONDecodeError, KeyError):
            return None
    token = github_token()
    if not token:
        return None
    request = urllib.request.Request(
        f"{RELEASE_API}/repos/{release.repository}/releases/tags/{release.tag}",
        method="GET",
    )
    request.add_header("Authorization", f"Bearer {token}")
    request.add_header("Accept", "application/vnd.github+json")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = json.loads(response.read().decode("utf-8"))
            return [str(item["name"]) for item in payload.get("assets", [])]
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        fail(f"查询 Release 失败：{error.code}")


def publish_with_gh(release: Release, entries: list[tuple[Path, str]], notes: str) -> None:
    command = [gh_path(), "release", "create", release.tag,
               *[str(path) for path, _ in entries],
               "--title", release.title,
               "--notes-file", str(release.notes)]
    if release.release_options.get("draft"):
        command.append("--draft")
    if release.release_options.get("prerelease"):
        command.append("--prerelease")
    if release.release_options.get("latest", True):
        command.append("--latest")
    run(command, cwd=release.repo, capture=False)


def upload_missing_with_gh(release: Release, entries: list[tuple[Path, str]]) -> None:
    command = [gh_path(), "release", "upload", release.tag,
               *[str(path) for path, _ in entries], "--clobber"]
    run(command, cwd=release.repo, capture=False)


def api_request(url: str, token: str, method: str, data: bytes | None = None,
                content_type: str = "application/json") -> str:
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Authorization", f"Bearer {token}")
    request.add_header("Accept", "application/vnd.github+json")
    request.add_header("X-GitHub-Api-Version", "2022-11-28")
    if data is not None:
        request.add_header("Content-Type", content_type)
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        fail(f"GitHub API {method} {url} 失败：{error.code} "
             f"{error.read().decode('utf-8', 'replace')}")


def upload_asset_with_api(upload_url: str, name: str, path: Path, token: str) -> None:
    url = f"{upload_url}?name={urllib.parse.quote(name)}"
    api_request(url, token, "POST", path.read_bytes(), "application/octet-stream")
    log(f"  已上传：{name}")


def publish_with_api(release: Release, entries: list[tuple[Path, str]], notes: str,
                     token: str) -> None:
    payload = json.dumps({
        "tag_name": release.tag,
        "target_commitish": release.branch,
        "name": release.title,
        "body": notes,
        "draft": bool(release.release_options.get("draft")),
        "prerelease": bool(release.release_options.get("prerelease")),
        "make_latest": "true" if release.release_options.get("latest", True) else "false",
    }).encode("utf-8")
    response = api_request(f"{RELEASE_API}/repos/{release.repository}/releases",
                           token, "POST", payload)
    created = json.loads(response)
    log(f"  已创建 Release：{created.get('html_url')}")
    upload_url = str(created["upload_url"]).split("{")[0]
    for path, name in entries:
        upload_asset_with_api(upload_url, name, path, token)


def upload_to_existing_with_api(release: Release, entries: list[tuple[Path, str]],
                                token: str) -> None:
    response = api_request(
        f"{RELEASE_API}/repos/{release.repository}/releases/tags/{release.tag}", token, "GET")
    existing = json.loads(response)
    asset_ids = {str(item["name"]): int(item["id"]) for item in existing.get("assets", [])}
    upload_url = str(existing["upload_url"]).split("{")[0]
    for path, name in entries:
        if name in asset_ids:
            api_request(f"{RELEASE_API}/repos/{release.repository}/releases/assets/"
                        f"{asset_ids[name]}", token, "DELETE")
            log(f"  已替换旧附件：{name}")
        upload_asset_with_api(upload_url, name, path, token)


def publish_if_needed(release: Release) -> None:
    existing_names = release_asset_names(release)
    checksums = None if release.dry_run else write_checksums(release)
    entries = release.upload_entries()
    if checksums is not None:
        entries.append((checksums, checksums.name))
    notes = release.notes_text()

    if release.dry_run:
        if existing_names is None:
            log(f"  [计划] 创建 Release {release.tag}，上传 {len(entries)} 个附件")
        else:
            log(f"  [计划] Release {release.tag} 已存在，补传/替换 {len(entries)} 个附件")
        if release.checksums_path() is not None:
            log(f"  [计划] 生成校验文件：{release.checksums_path().relative_to(release.repo)}")
        for _, name in entries:
            log(f"    {name}")
        return

    if existing_names is not None:
        complete = all(name in existing_names for _, name in entries)
        if complete:
            log(f"  Release {release.tag} 已存在且附件完整，跳过")
            return
        log(f"  Release {release.tag} 已存在但附件不完整，补传并覆盖同名附件")
        if gh_available():
            upload_missing_with_gh(release, entries)
        else:
            token = github_token()
            if not token:
                fail("没有 gh CLI，也没有 GH_TOKEN/GITHUB_TOKEN。")
            upload_to_existing_with_api(release, entries, token)
        return

    if gh_available():
        publish_with_gh(release, entries, notes)
        return
    token = github_token()
    if not token:
        fail("没有 gh CLI，也没有 GH_TOKEN/GITHUB_TOKEN。"
             "安装 gh（winget install --id GitHub.cli）后运行 gh auth login，"
             "或设置 token 环境变量。")
    publish_with_api(release, entries, notes, token)


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

def print_plan(release: Release, commit: str) -> None:
    log(f"项目：{release.project}")
    log(f"平台：{release.platform}")
    log(f"版本：{release.version}")
    log(f"标签：{release.tag} -> {commit[:12]}")
    log(f"标题：{release.title}")
    log(f"说明：{release.notes}")
    log(f"附件：{len(release.assets())} 个")
    if release.dry_run:
        log("模式：dry-run（只打印计划，不修改任何内容）")


def main() -> int:
    if len(sys.argv) > 1:
        fail("这个脚本不需要参数。请修改仓库根目录的 release.json 后直接运行。")
    repo = repository_root()
    release = load_release(repo)
    commit = release.tag_commit()
    print_plan(release, commit)
    log("\n[1/4] 检查仓库")
    check_worktree(release)
    check_tag_commit(release, commit)

    log("\n[2/4] 构建产物")
    build_if_needed(release, commit)

    log("\n[3/4] 标签")
    check_tag(release, commit, create=True)

    log("\n[4/4] GitHub Release")
    publish_if_needed(release)

    log("\n完成。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
