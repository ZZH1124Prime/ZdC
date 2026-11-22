#!/usr/bin/env python3
"""Generic, cross-platform release helper.

A project describes itself in a JSON file (default: ``release.json`` in the
repository root).  The same script then works for this repository on Windows
and on Linux, and can be copied or referenced from other repositories.

Typical flow (from the repository root):

    python tools/release/release.py plan
    python tools/release/release.py build
    python tools/release/release.py doctor
    python tools/release/release.py tag --push --yes
    python tools/release/release.py publish --yes

Run ``python tools/release/release.py --help`` for every command.
"""
from __future__ import annotations

import argparse
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
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


def log(message: str = "") -> None:
    print(message, flush=True)


def fail(message: str, code: int = 1) -> "NoReturn":
    print(f"\n错误：{message}", file=sys.stderr, flush=True)
    raise SystemExit(code)


def run(command, cwd: Path, check: bool = True, capture: bool = False):
    if not capture:
        log("> " + " ".join(str(part) for part in command))
    try:
        result = subprocess.run(
            command, cwd=cwd, text=True, capture_output=capture, check=check
        )
    except FileNotFoundError:
        fail(f"找不到可执行文件：{command[0]}")
    except subprocess.CalledProcessError as error:
        detail = (error.stderr or error.stdout or "").strip() if capture else ""
        fail(f"命令失败（{error.returncode}）：{' '.join(map(str, command))}"
             + (f"\n{detail}" if detail else ""))
    return result.stdout.strip() if capture else None


def git(repo: Path, *arguments: str, capture: bool = True) -> str:
    return run(["git", *arguments], cwd=repo, capture=capture) or ""


def render(template: str, values: dict) -> str:
    try:
        return template.format(**values)
    except KeyError as error:
        fail(f"描述文件占位符缺少值：{{{error.args[0]}}}")


def repository_root() -> Path:
    location = Path.cwd()
    if shutil.which("git") is None:
        fail("找不到 git。")
    top = run(["git", "rev-parse", "--show-toplevel"], cwd=location, capture=True)
    if not top:
        fail("当前目录不在 Git 仓库中。")
    return Path(top).resolve()


class Project:
    def __init__(self, repo: Path, config_path: Path, config: dict, platform: str):
        self.repo = repo
        self.config_path = config_path
        self.config = config
        self.platform = platform
        self.version = str(config.get("version", "")).strip()
        if not self.version:
            fail("描述文件缺少 version。")
        self.tag = render(str(config.get("tag", "v{version}")), {"version": self.version})
        self.title = render(str(config.get("title", "{project} {version}")), {
            "project": config.get("project", repo.name), "version": self.version})
        notes_relative = (render(str(config.get("notes", "")), {"version": self.version})
                          if config.get("notes") else "")
        self.notes = self._path(notes_relative)
        self.platforms = config.get("platforms", {})
        if self.platform not in self.platforms:
            fail(f"描述文件没有平台 {self.platform!r} 的配置：{sorted(self.platforms)}")
        self.settings = self.platforms[self.platform] or {}

    def _path(self, relative: str) -> Path:
        return (self.repo / relative).resolve() if relative else None

    def expand(self, patterns) -> list[Path]:
        result: list[Path] = []
        for pattern in patterns or []:
            relative = render(str(pattern), {"version": self.version})
            matches = sorted(self.repo.glob(relative))
            for match in matches:
                if match.is_file() and match not in result:
                    result.append(match)
            if not matches:
                log(f"  警告：没有匹配到附件：{relative}")
        return result

    def assets(self) -> list[Path]:
        files = self.expand(self.settings.get("assets"))
        files += self.expand(self.settings.get("extra_assets"))
        return files

    def notes_text(self) -> str:
        if not self.notes:
            return ""
        if not self.notes.is_file():
            fail(f"发布说明文件不存在：{self.notes}")
        return self.notes.read_text(encoding="utf-8")

    def sha256sums_path(self) -> Path:
        relative = self.settings.get("sha256sums")
        if not relative:
            return None
        return self._path(render(str(relative), {"version": self.version}))

    def write_sha256sums(self) -> Path:
        target = self.sha256sums_path()
        if target is None:
            return None
        files = [item for item in self.expand(self.settings.get("assets")) if item.is_file()]
        lines = []
        for item in files:
            digest = hashlib.sha256(item.read_bytes()).hexdigest().upper()
            lines.append(f"{digest}  {item.name}")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("\n".join(lines) + "\n", encoding="ascii")
        log(f"写入校验文件：{target}")
        return target

    def receipt_commit(self) -> str:
        relative = render(str(self.settings.get("receipt", "")), {"version": self.version})
        pattern = self.settings.get("receipt_commit_pattern", r"源码提交：([0-9a-f]{40})")
        if not relative:
            return ""
        matches = sorted(self.repo.glob(relative))
        if not matches:
            return ""
        text = matches[0].read_text(encoding="utf-8", errors="replace")
        found = re.search(pattern, text)
        return found.group(1) if found else ""

    def tag_commit(self, override: str = "") -> str:
        value = override or str(self.config.get("tag_commit", "") or "HEAD")
        resolved = git(self.repo, "rev-parse", "--verify", f"{value}^{{commit}}").strip()
        if not resolved:
            fail(f"无法解析 tag_commit：{value}")
        return resolved


def load_project(repo: Path, config_name: str, platform: str) -> Project:
    config_path = Path(config_name)
    if not config_path.is_absolute():
        config_path = repo / config_name
    if not config_path.is_file():
        fail(f"描述文件不存在：{config_path}")
    try:
        config = json.loads(config_path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        fail(f"描述文件不是合法 JSON：{error}")
    selected = platform
    if selected == "auto":
        selected = "windows" if sys.platform.startswith("win") else "linux"
    return Project(repo, config_path, config, selected)


def check_clean(project: Project) -> None:
    status = git(project.repo, "status", "--porcelain", "--untracked-files=normal")
    if status.strip():
        fail("工作区有未提交改动，先提交再发布：\n" + status)


def check_tag(project: Project, remote_tag: bool = False) -> None:
    local = git(project.repo, "tag", "--list", project.tag)
    if not local.strip():
        fail(f"本地标签不存在：{project.tag}，先运行 tag。")
    if remote_tag:
        remote = git(project.repo, "ls-remote", "--tags", project.config.get("remote", "origin"),
                     f"refs/tags/{project.tag}")
        if not remote.strip():
            fail(f"远端还没有标签 {project.tag}，先运行 tag --push。")


def describe(project: Project) -> None:
    log(f"项目：{project.config.get('project', project.repo.name)}")
    log(f"仓库：{project.repo}")
    log(f"描述：{project.config_path}")
    log(f"平台：{project.platform}")
    log(f"版本：{project.version}")
    log(f"标签：{project.tag}")
    log(f"标签提交：{project.tag_commit()}")
    log(f"标题：{project.title}")
    log(f"发布说明：{project.notes or '（无）'}")
    build = project.settings.get("build") or []
    log(f"构建命令：{build or '（无）'}")
    assets = project.assets()
    log(f"附件（{len(assets)}）：")
    for item in assets:
        log(f"  {item.relative_to(project.repo)}")
    sums = project.sha256sums_path()
    if sums is not None:
        log(f"校验文件：{sums.relative_to(project.repo)}")
    release = project.config.get("release", {})
    log(f"发布选项：draft={release.get('draft', False)} "
        f"prerelease={release.get('prerelease', False)} latest={release.get('latest', True)}")


def doctor(project: Project) -> None:
    log("检查发布环境")
    if not (project.repo / ".git").exists() and not git(project.repo, "rev-parse", "--git-dir"):
        fail("不是 Git 仓库。")
    check_clean(project)
    log("  工作区干净")
    branch = git(project.repo, "rev-parse", "--abbrev-ref", "HEAD").strip()
    expected = str(project.config.get("branch", "")).strip()
    if expected and branch != expected:
        fail(f"当前分支 {branch!r} 与描述文件里的 {expected!r} 不一致。")
    log(f"  分支：{branch}")
    remote = str(project.config.get("remote", "origin"))
    url = git(project.repo, "remote", "get-url", remote, capture=True)
    expected_repo = str(project.config.get("repo", "")).strip()
    if expected_repo and expected_repo not in url:
        log(f"  警告：remote {remote} 的地址 {url!r} 不含 {expected_repo!r}")
    log(f"  远端：{url}")

    commit = project.tag_commit()
    run(["git", "merge-base", "--is-ancestor", commit, "HEAD"], cwd=project.repo, capture=True)
    log(f"  标签提交：{commit}")

    receipt = project.receipt_commit()
    if receipt:
        if not commit.startswith(receipt) and not receipt.startswith(commit[:len(receipt)]):
            fail(f"安装包回执的源码提交 {receipt} 与标签提交 {commit} 不一致。")
        log("  安装包回执与标签提交一致")
    tag_local = git(project.repo, "tag", "--list", project.tag).strip()
    log(f"  标签状态：{'已存在' if tag_local else '尚未创建'}")
    if not project.notes:
        log("  警告：未配置发布说明文件")
    elif not project.notes.is_file():
        fail(f"发布说明文件不存在：{project.notes}")
    elif not project.notes_text().strip():
        log("  警告：发布说明为空")
    uploader = "gh" if shutil.which("gh") else (
        "GitHub API" if (os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")) else "缺失")
    log(f"  发布工具：{uploader}")
    if uploader == "缺失":
        log("  警告：没有 gh 也没有 GH_TOKEN/GITHUB_TOKEN，publish 将无法上传。")


def build(project: Project) -> None:
    commands = project.settings.get("build") or []
    if not commands:
        log("描述文件没有配置构建命令，跳过。")
        return
    for command in commands:
        log(f"\n[构建] {command}")
        result = subprocess.run(command, cwd=project.repo, shell=True)
        if result.returncode != 0:
            fail(f"构建命令失败（{result.returncode}）：{command}")


def create_tag(project: Project, push: bool, override: str, yes: bool) -> None:
    check_clean(project)
    commit = project.tag_commit(override)
    existing = git(project.repo, "tag", "--list", project.tag).strip()
    if existing:
        fail(f"标签已存在：{project.tag}（指向 {existing}）。如需重新发布请升级版本号。")
    log(f"将创建标签 {project.tag} -> {commit}")
    if not yes:
        fail("创建/推送标签属于发布动作，确认后加 --yes。")
    git(project.repo, "tag", "-a", project.tag, "-m", f"{project.config.get('project', '')} {project.version}".strip(),
        commit, capture=False)
    log(f"已创建 {project.tag}")
    if push:
        git(project.repo, "push", project.config.get("remote", "origin"), project.tag, capture=False)
        log(f"已推送 {project.tag}")


def tool_available(name: str) -> bool:
    return shutil.which(name) is not None


def publish(project: Project, dry_run: bool, yes: bool, draft: bool | None,
            prerelease: bool | None, latest: bool | None) -> None:
    if dry_run:
        log("--dry-run：只显示发布计划，不检查/修改仓库状态。")
    else:
        check_clean(project)
        check_tag(project, remote_tag=True)
    if not project.notes:
        fail("descriptor 没有配置发布说明文件（notes）。")
    notes = project.notes_text().strip()
    if not notes:
        fail("发布说明为空。")
    sums = None if dry_run else project.write_sha256sums()
    assets = project.assets()
    if sums is not None and sums not in assets:
        assets.append(sums)
    if not assets:
        fail("没有匹配到任何发布附件。")
    for item in assets:
        if not item.is_file():
            fail(f"附件不存在：{item}")
    release = project.config.get("release", {})
    draft = release.get("draft", False) if draft is None else draft
    prerelease = release.get("prerelease", False) if prerelease is None else prerelease
    latest = release.get("latest", True) if latest is None else latest
    tag = project.tag
    log(f"发布 {tag}：{project.title}")
    log(f"附件：{len(assets)} 个")
    for item in assets:
        log(f"  {item.relative_to(project.repo)}")
    if dry_run:
        log("\n--dry-run：只显示将要执行的动作，不上传。")
        return
    if not yes:
        fail("上传 Release 属于发布动作，确认后加 --yes。")

    if tool_available("gh"):
        command = ["gh", "release", "create", tag, *[str(item) for item in assets],
                   "--title", project.title, "--notes-file", str(project.notes)]
        if draft:
            command.append("--draft")
        if prerelease:
            command.append("--prerelease")
        command.append("--latest" if latest else "--latest=false")
        run(command, cwd=project.repo)
        log("Release 已创建（gh）。")
        return

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not token:
        fail("没有 gh，也没有 GH_TOKEN/GITHUB_TOKEN。")
    publish_api(project, assets, notes, draft, prerelease, latest, token)


def api_request(url: str, token: str, method: str = "GET", data: bytes | None = None,
                content_type: str = "application/json"):
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
        fail(f"GitHub API {method} {url} 失败：{error.code} {error.read().decode('utf-8', 'replace')}")


def publish_api(project: Project, assets: list[Path], notes: str, draft: bool,
                prerelease: bool, latest: bool, token: str) -> None:
    repo = str(project.config.get("repo", "")).strip()
    if not repo:
        fail("descriptor 缺少 repo（owner/name）。")
    payload = json.dumps({
        "tag_name": project.tag,
        "target_commitish": str(project.config.get("branch", "master")),
        "name": project.title,
        "body": notes,
        "draft": draft,
        "prerelease": prerelease,
        "make_latest": "true" if latest else "false",
    }).encode("utf-8")
    response = api_request(f"https://api.github.com/repos/{repo}/releases", token, "POST", payload)
    release = json.loads(response)
    log(f"Release 已创建：{release.get('html_url')}")
    upload_url = str(release["upload_url"]).split("{")[0]
    for item in assets:
        content = item.read_bytes()
        suffix = "&label=" + urllib.parse.quote(item.name)
        api_request(upload_url + "?name=" + urllib.parse.quote(item.name) + suffix, token,
                    "POST", content, "application/octet-stream")
        log(f"已上传：{item.name}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", default="release.json",
                        help="项目描述文件（默认仓库根目录 release.json）")
    parser.add_argument("--platform", default="auto",
                        help="平台：auto（默认）、windows、linux 或描述文件中的任意键")
    parser.add_argument("--yes", action="store_true", help="确认执行 tag / publish")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("plan", help="显示解析后的发布计划")
    sub.add_parser("doctor", help="检查工作区、标签、回执、附件和发布工具")
    sub.add_parser("build", help="执行描述文件里的构建命令")
    tag = sub.add_parser("tag", help="创建（并可选推送）发布标签")
    tag.add_argument("--push", action="store_true", help="创建后推送到远端")
    tag.add_argument("--at", default="", help="覆盖描述文件里的 tag_commit")
    pub = sub.add_parser("publish", help="创建 GitHub Release 并上传附件")
    pub.add_argument("--dry-run", action="store_true", help="只显示动作，不上传")
    pub.add_argument("--draft", action="store_true", default=None,
                     help="创建草稿 Release")
    pub.add_argument("--prerelease", action="store_true", default=None,
                     help="标记预发布")
    pub.add_argument("--latest", action="store_true", default=None,
                     help="标记为最新版本")
    sub.add_parser("all", help="doctor -> build -> tag --push -> publish")
    options = parser.parse_args()

    repo = repository_root()
    project = load_project(repo, options.config, options.platform)

    if options.command == "plan":
        describe(project)
        return 0
    if options.command == "doctor":
        describe(project)
        doctor(project)
        return 0
    if options.command == "build":
        build(project)
        return 0
    if options.command == "tag":
        create_tag(project, options.push, options.at, options.yes)
        return 0
    if options.command == "publish":
        publish(project, options.dry_run, options.yes, options.draft,
                options.prerelease, options.latest)
        return 0
    if options.command == "all":
        describe(project)
        doctor(project)
        build(project)
        doctor(project)
        create_tag(project, push=True, override="", yes=options.yes)
        publish(project, dry_run=False, yes=options.yes, draft=None,
                prerelease=None, latest=None)
        return 0
    fail(f"未知命令：{options.command}")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
