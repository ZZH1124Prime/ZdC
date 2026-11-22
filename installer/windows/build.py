"""Build the ZdC Windows x64 Setup from a clean checkout.

Usage (from the repository root):
    python -B installer/windows/build.py
    python -B installer/windows/build.py --allow-dirty --keep-work

The script performs the whole release build in one command:

1. verifies the Git worktree is clean (a release build must be reproducible);
2. builds the native controller/hook/probe binaries with MSVC into artifacts/native;
3. publishes the .NET application into a temporary payload directory and verifies
   its version and source revision stamps;
4. generates the WiX file list and builds ZdC.msi with `wix build`;
5. re-extracts every file from the MSI cabinet and compares its SHA-256 with the
   payload, then checks the MSI identity and reads its PackageCode;
6. compiles the launcher (embedded MSI, visual assets, package hash) and writes
   the single-file Setup EXE plus a build receipt into artifacts/windows;
7. removes all temporary build directories.

Requirements: Windows x64, .NET SDK 9, .NET Framework 4.8 reference assemblies,
Visual Studio 2022 C++ tools, Python 3.10+, 7-Zip and WiX 5:

    git submodule update --init third_party/Detours
    dotnet tool install --global wix --version 5.0.2
"""
import argparse
import base64
import hashlib
import html
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[1]
LAUNCHER = ROOT / 'launcher'
ARTIFACTS = REPO / 'artifacts'
NATIVE_OUT = ARTIFACTS / 'native'
WINDOWS_OUT = ARTIFACTS / 'windows'

FAMILY = '{D04744F1-2909-4A80-B108-295247AFBF11}'
PAYLOAD_FILES = (
    'ZdC.Core.dll', 'ZdC.Core.pdb', 'ZdC.deps.json', 'ZdC.dll', 'ZdC.exe',
    'ZdC.pdb', 'ZdC.runtimeconfig.json', 'ZdC.SocketController.exe',
    'ZdC.SocketHook32.dll', 'ZdC.SocketHook64.dll',
)
NATIVE_FILES = ('ZdC.SocketController.exe', 'ZdC.SocketHook32.dll', 'ZdC.SocketHook64.dll')


def log(message: str) -> None:
    print(message, flush=True)


def fail(message: str):
    print(f'\n构建失败：{message}', file=sys.stderr, flush=True)
    raise SystemExit(1)


def run(arguments, cwd=None, capture=False):
    command = [str(item) for item in arguments]
    if not capture:
        log('> ' + ' '.join(command))
    try:
        result = subprocess.run(command, cwd=cwd, text=True,
                                capture_output=capture, check=True)
    except FileNotFoundError:
        fail(f'找不到可执行文件 {command[0]}，请检查构建环境。')
    except subprocess.CalledProcessError as error:
        detail = ''
        if capture:
            detail = (error.stderr or error.stdout or '').strip()
        fail(f'命令失败（{error.returncode}）：{" ".join(command)}' + (f'\n{detail}' if detail else ''))
    return result.stdout.strip() if capture else None


def git(*arguments) -> str:
    return run(['git', '-C', str(REPO), *arguments], capture=True)


def powershell(script: str, cwd=None) -> str:
    executable = Path(os.environ['SystemRoot']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
    encoded = base64.b64encode(script.encode('utf-16-le')).decode('ascii')
    return run([str(executable), '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
               cwd=cwd, capture=True)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b''):
            digest.update(chunk)
    return digest.hexdigest().upper()


def guid(name: str) -> str:
    return '{' + str(uuid.uuid5(uuid.UUID(FAMILY), name)).upper() + '}'


def file_key(name: str) -> str:
    return 'F_' + re.sub(r'[^A-Za-z0-9_]', '_', name)


def read_version() -> str:
    project = ET.parse(REPO / 'src' / 'ZdC.App' / 'ZdC.App.csproj')
    version = project.findtext('.//Version', '')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        fail('src/ZdC.App/ZdC.App.csproj 需要三段数字版本号。')
    if any(int(number) > limit for number, limit in zip(version.split('.'), (255, 255, 65535))):
        fail('产品版本超出 Windows Installer 的限制。')
    return version


def worktree_state():
    commit = git('rev-parse', 'HEAD')
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        fail('无法取得当前 Git 提交。')
    dirty = bool(git('status', '--porcelain', '--untracked-files=normal'))
    return commit, dirty


def wix_path() -> Path:
    candidate = shutil.which('wix.exe')
    if candidate:
        return Path(candidate)
    local = Path.home() / '.dotnet' / 'tools' / 'wix.exe'
    if local.is_file():
        return local
    fail('找不到 WiX。请执行：dotnet tool install --global wix --version 5.0.2')


def seven_zip() -> Path:
    candidate = shutil.which('7z.exe')
    if candidate:
        return Path(candidate)
    default = Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / '7-Zip' / '7z.exe'
    if default.is_file():
        return default
    fail('找不到 7-Zip（7z.exe）。')


def compiler() -> Path:
    version = run(['dotnet', '--version'], cwd=REPO, capture=True)
    sdks = run(['dotnet', '--list-sdks'], cwd=REPO, capture=True)
    for line in sdks.splitlines():
        match = re.fullmatch(r'(\S+) \[(.+)\]', line)
        if match and match[1] == version:
            return Path(match[2]) / version / 'Roslyn' / 'bincore' / 'csc.dll'
    fail(f'找不到选中的 .NET SDK {version} 的编译器。')


def check_toolchain() -> None:
    if not shutil.which('dotnet'):
        fail('缺少 .NET SDK 9（dotnet）。')
    seven_zip()
    wix_path()
    framework = Path(os.environ.get('ProgramFiles(x86)', 'C:/Program Files (x86)')) / \
        'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8/mscorlib.dll'
    if not framework.is_file():
        fail('缺少 .NET Framework 4.8 引用程序集（Developer Pack）。')
    compiler()


def build_native() -> None:
    log('\n[1/6] 编译原生控制器 / Hook / 探针')
    if NATIVE_OUT.exists():
        shutil.rmtree(NATIVE_OUT)
    run([sys.executable, '-B', str(REPO / 'src' / 'native' / 'build.py'), '--output', str(NATIVE_OUT)])
    for name in NATIVE_FILES:
        if not (NATIVE_OUT / name).is_file():
            fail(f'原生构建缺少 {name}。')


def publish_payload(payload: Path, commit: str) -> None:
    log('\n[2/6] 发布 .NET 载荷')
    if payload.exists():
        shutil.rmtree(payload)
    run([
        'dotnet', 'publish', str(REPO / 'src' / 'ZdC.App' / 'ZdC.App.csproj'),
        '--configuration', 'Release', '--output', str(payload),
        '-p:SourceRevisionId=' + commit, '-p:NuGetAudit=false',
    ])


def verify_payload(payload: Path, version: str, commit: str) -> dict:
    log('\n[3/6] 校验载荷身份与内容')
    names = sorted(item.name for item in payload.iterdir())
    if names != sorted(PAYLOAD_FILES):
        fail(f'载荷目录与预期清单不一致：{names}')
    for name in NATIVE_FILES:
        if sha256(payload / name) != sha256(NATIVE_OUT / name):
            fail(f'载荷中的原生文件与本次编译结果不一致：{name}')
    command = (
        "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
        "$rows = foreach ($name in 'ZdC.exe','ZdC.dll') { "
        "$v=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PWD $name)); "
        "[pscustomobject]@{Name=$name; Numeric=@($v.FileMajorPart,$v.FileMinorPart,$v.FileBuildPart); "
        "Product=$v.ProductVersion} }; ConvertTo-Json -Compress -InputObject @($rows)"
    )
    rows = json.loads(powershell(command, cwd=payload))
    expected = tuple(map(int, version.split('.')))
    for row in rows:
        product, _, metadata = (row['Product'] or '').partition('+')
        if tuple(row['Numeric']) != expected or product not in (version, version + '.0'):
            fail(f"{row['Name']} 的产品版本 {row['Product']!r} 与 {version} 不一致。")
        stamps = re.findall(r'(?<![0-9a-f])[0-9a-f]{40}(?![0-9a-f])', metadata.lower())
        if any(stamp != commit for stamp in stamps):
            fail(f"{row['Name']} 的源码修订与当前提交 {commit} 不一致。")
    deps = json.loads((payload / 'ZdC.deps.json').read_text(encoding='utf-8-sig'))
    target = deps['targets'][deps['runtimeTarget']['name']]
    app = f'ZdC/{version}'
    if app not in target or deps['libraries'].get(app, {}).get('type') != 'project':
        fail(f'ZdC.deps.json 没有声明应用版本 {version}。')
    return {name: sha256(payload / name) for name in PAYLOAD_FILES}


def write_files_fragment(work: Path) -> Path:
    components = []
    for name in sorted(PAYLOAD_FILES):
        if name == 'ZdC.exe':
            continue
        key = file_key(name)
        components.append(
            f'    <Component Id="C_{key[2:]}" Directory="INSTALLDIR" Guid="{guid("file:" + name.lower())}">\n'
            f'      <File Id="{key}" Source="$(var.PayloadDir)\\{html.escape(name)}" KeyPath="yes" />\n'
            '    </Component>')
    components.append(
        f'    <Component Id="C_ZdC_exe" Directory="INSTALLDIR" Guid="{guid("file:zdc.exe")}">\n'
        '      <File Id="F_ZdC_exe" Source="$(var.PayloadDir)\\ZdC.exe" KeyPath="yes" />\n'
        '      <Shortcut Id="MenuShortcut" Directory="ZdCMenu" Name="ZdC" Target="[#F_ZdC_exe]" WorkingDirectory="INSTALLDIR" />\n'
        '      <RemoveFolder Id="RemoveZdCMenu" Directory="ZdCMenu" On="uninstall" />\n'
        '      <RemoveFile Id="RemoveLegacySocketHook" Name="ZdC.SocketHook.dll" On="install" />\n'
        '    </Component>')
    fragment = ('<?xml version="1.0" encoding="utf-8"?>\n'
                '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">\n  <Fragment>\n'
                '    <ComponentGroup Id="Files">\n'
                + '\n'.join(components) +
                '\n    </ComponentGroup>\n  </Fragment>\n</Wix>\n')
    path = work / 'Files.wxs'
    path.write_text(fragment, encoding='utf-8')
    return path


def build_msi(work: Path, payload: Path, version: str, product: str) -> Path:
    log('\n[4/6] 生成 WiX 文件清单并构建 MSI')
    files_fragment = write_files_fragment(work)
    msi = work / 'ZdC.msi'
    run([
        str(wix_path()), 'build', '-arch', 'x64', '-pdbtype', 'none',
        '-d', f'Version={version}',
        '-d', f'ProductCode={product}',
        '-d', f'UpgradeCode={FAMILY}',
        '-d', f'PayloadDir={payload}',
        '-o', str(msi), str(ROOT / 'Package.wxs'), str(files_fragment),
    ])
    if not msi.is_file():
        fail('wix build 没有生成 MSI。')
    return msi


def msi_identity(msi: Path) -> dict:
    escaped = str(msi).replace("'", "''")
    command = (
        "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
        f"$i=New-Object -ComObject WindowsInstaller.Installer; $db=$i.OpenDatabase('{escaped}',0); "
        "$si=$db.SummaryInformation(0); $props=@{}; "
        "$v=$db.OpenView('SELECT `Property`,`Value` FROM `Property`'); $v.Execute(); "
        "while($r=$v.Fetch()){ $props[$r.StringData(1)]=$r.StringData(2) }; $v.Close(); "
        "[pscustomobject]@{ProductCode=$props['ProductCode']; ProductVersion=$props['ProductVersion']; "
        "UpgradeCode=$props['UpgradeCode']; PackageCode=$si.Property(9); Template=$si.Property(7)} "
        '| ConvertTo-Json -Compress'
    )
    return json.loads(powershell(command))


def verify_msi(work: Path, msi: Path, payload: Path, version: str, product: str):
    log('\n[5/6] 校验 MSI 身份、包码和每个内嵌文件')
    identity = msi_identity(msi)
    if identity['ProductCode'].upper() != product.upper():
        fail(f"MSI 的 ProductCode 是 {identity['ProductCode']}，预期 {product}。")
    if identity['ProductVersion'] != version:
        fail(f"MSI 的 ProductVersion 是 {identity['ProductVersion']}，预期 {version}。")
    if identity['UpgradeCode'].upper() != FAMILY.upper():
        fail(f"MSI 的 UpgradeCode 是 {identity['UpgradeCode']}，预期 {FAMILY}。")
    if not str(identity['Template']).lower().startswith('x64;'):
        fail(f"MSI 摘要信息不是 x64：{identity['Template']}。")
    if not re.fullmatch(r'\{[0-9A-Fa-f-]{36}\}', identity['PackageCode'] or ''):
        fail(f"无法读取 PackageCode：{identity['PackageCode']!r}。")

    extract = work / 'msi-files'
    extract.mkdir()
    run([str(seven_zip()), 'x', '-y', '-o' + str(extract), str(msi)])
    extracted = sorted(item.name for item in extract.iterdir() if item.is_file())
    expected_keys = sorted(file_key(name) for name in PAYLOAD_FILES)
    if extracted != expected_keys:
        fail(f'MSI 解包文件与预期不一致：{extracted}')
    for name in PAYLOAD_FILES:
        if sha256(extract / file_key(name)) != sha256(payload / name):
            fail(f'MSI 内嵌文件与载荷不一致：{name}')
    return identity, sha256(msi)


def extract_visuals(work: Path) -> None:
    """Take only the immutable visual resources needed by the launcher."""
    app_source = REPO / 'src' / 'ZdC.App'
    for name in ('ZdC.png', 'ZdCMark.png'):
        (work / name).write_bytes((app_source / 'Assets' / name).read_bytes())
    namespaces = {
        'p': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation',
        'x': 'http://schemas.microsoft.com/winfx/2006/xaml',
    }
    ET.register_namespace('', namespaces['p'])
    ET.register_namespace('x', namespaces['x'])
    theme = ET.Element('{' + namespaces['p'] + '}ResourceDictionary')
    app_xaml = ET.fromstring((app_source / 'App.xaml').read_bytes())
    for node in app_xaml.find('p:Application.Resources', namespaces):
        key = node.get('{' + namespaces['x'] + '}Key')
        if key in ('Ink', 'StoneGlyph', 'Canvas', 'InsetSurface', 'Line', 'FocusBorder',
                   'KeyboardOnlyFocus') or (not key and node.get('TargetType') in ('TextBlock', 'TextBox')):
            theme.append(node)
    (work / 'theme.xaml').write_bytes(ET.tostring(theme, encoding='utf-8'))
    main = ET.fromstring((app_source / 'MainWindow.xaml').read_bytes())
    named = next(element for element in main.iter()
                 if element.get('{' + namespaces['x'] + '}Name') == 'StoneMass')
    mass = next(iter(named.find('p:Path.Fill', namespaces)))
    (work / 'mass.xaml').write_bytes(ET.tostring(mass, encoding='utf-8'))
    backend = (app_source / 'MainWindow.xaml.cs').read_text(encoding='utf-8-sig')
    start = backend.index('    private void StoneFace_SizeChanged(')
    end = backend.index('    private void Minimize_Click', start)
    outline = ('using System.Collections.Generic;\nusing System.Linq;\nusing System.Windows;\n'
               'using System.Windows.Media;\npartial class SetupWindow {\n'
               + backend[start:end] + '}\n')
    (work / 'Outline.cs').write_text(outline, encoding='utf-8-sig')


def build_launcher(work: Path, version: str, identity: dict, package_sha: str,
                   payload_size: int) -> Path:
    log('\n[6/6] 编译单页安装器')
    extract_visuals(work)
    manifest = (LAUNCHER / 'setup.manifest').read_text(encoding='utf-8-sig') \
        .replace('@FILE_VERSION@', version + '.0')
    (work / 'setup.manifest').write_text(manifest, encoding='utf-8')
    identity_source = (
        f'[assembly:System.Reflection.AssemblyVersion("{version}.0")]\n'
        f'[assembly:System.Reflection.AssemblyFileVersion("{version}.0")]\n'
        'internal static class Identity { '
        f'internal const string Family="{FAMILY}", Product="{identity["ProductCode"]}", '
        f'PackageCode="{identity["PackageCode"]}", Version="{version}", PackageSha="{package_sha}"; '
        f'internal const long Size={payload_size}L; }}\n')
    (work / 'Identity.cs').write_text(identity_source, encoding='ascii')

    framework = Path(os.environ.get('ProgramFiles(x86)', 'C:/Program Files (x86)')) / \
        'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
    exe = work / 'setup.exe'
    arguments = ['dotnet', str(compiler()), '/noconfig', '/nostdlib+', '/target:winexe',
                 '/platform:x64', '/optimize+', '/deterministic+', '/langversion:latest',
                 '/warnaserror+', '/out:' + str(exe),
                 '/win32manifest:' + str(work / 'setup.manifest')]
    for reference in ('mscorlib', 'System', 'System.Core', 'System.Xaml', 'WindowsBase',
                      'PresentationCore', 'PresentationFramework', 'System.Windows.Forms',
                      'System.Drawing', 'System.Xml'):
        arguments.append('/reference:' + str(framework / (reference + '.dll')))
    for name in ('ZdC.msi', 'ZdC.png', 'ZdCMark.png', 'theme.xaml', 'mass.xaml'):
        arguments.append('/resource:' + str(work / name) + ',' + name)
    arguments.extend(str(path) for path in (
        LAUNCHER / 'SetupWindow.cs', LAUNCHER / 'InstallEngine.cs',
        work / 'Outline.cs', work / 'Identity.cs'))
    run(arguments)
    return exe


def write_receipt(final: Path, version: str, commit: str, dirty: bool, identity: dict,
                  package_sha: str, setup_sha: str, payload_hashes: dict,
                  native_hashes: dict) -> None:
    receipt = final.with_name(final.stem + '.receipt.txt')
    lines = [
        'ZdC Windows 安装包构建回执',
        '=' * 60,
        f'构建时间：{datetime.now().astimezone().isoformat(timespec="seconds")}',
        f'源码提交：{commit}{"（工作区有未提交改动）" if dirty else "（工作区干净）"}',
        f'产品版本：{version}',
        '',
        f'安装包：{final.name}',
        f'安装包 SHA-256：{setup_sha}',
        '',
        'MSI 身份：',
        f'  ProductCode  {identity["ProductCode"]}',
        f'  UpgradeCode  {identity["UpgradeCode"]}',
        f'  PackageCode  {identity["PackageCode"]}',
        f'  Template     {identity["Template"]}',
        f'  内嵌 MSI SHA-256  {package_sha}',
        '',
        '原生二进制：',
        *[f'  {name}  {digest}' for name, digest in sorted(native_hashes.items())],
        '',
        '载荷文件：',
        *[f'  {name}  {digest}' for name, digest in sorted(payload_hashes.items())],
        '',
        '校验：载荷清单/哈希/提交戳，MSI 身份与 x64 模板，MSI 内 10 个文件哈希，',
        '单页安装器编译（/warnaserror+）。本轮未执行实际安装，安装包未签名。',
    ]
    receipt.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--allow-dirty', action='store_true',
                        help='允许从有未提交改动的工作区构建（产物名带 -dirty）')
    parser.add_argument('--keep-work', action='store_true',
                        help='保留临时目录便于排查')
    options = parser.parse_args()

    commit, dirty = worktree_state()
    if dirty and not options.allow_dirty:
        fail('工作区有未提交改动。先提交改动，或用 --allow-dirty 构建测试包。')
    version = read_version()
    check_toolchain()
    product = guid('product:x64:' + version)
    log(f'ZdC {version} Windows x64 · 源码提交 {commit[:7]}{"（dirty）" if dirty else ""}')

    ARTIFACTS.mkdir(exist_ok=True)
    WINDOWS_OUT.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='.zdc-build-', dir=ARTIFACTS))
    try:
        build_native()
        payload = work / 'payload'
        publish_payload(payload, commit)
        payload_hashes = verify_payload(payload, version, commit)
        payload_size = sum((payload / name).stat().st_size for name in PAYLOAD_FILES)
        native_hashes = {name: sha256(NATIVE_OUT / name) for name in NATIVE_FILES}
        msi = build_msi(work, payload, version, product)
        identity, package_sha = verify_msi(work, msi, payload, version, product)
        final_name = (f'ZdC-Setup-{version}-Windows-x64-{commit[:7]}'
                      + ('-dirty' if dirty else '') + '.exe')
        final = WINDOWS_OUT / final_name
        if final.exists():
            fail(f'目标文件已存在：{final}。先移走旧包或换一个提交。')
        setup = build_launcher(work, version, identity, package_sha, payload_size)
        setup_sha = sha256(setup)
        shutil.move(str(setup), str(final))
        write_receipt(final, version, commit, dirty, identity, package_sha, setup_sha,
                      payload_hashes, native_hashes)
        log(f'\n完成：{final}')
        log(f'SHA-256 {setup_sha}')
        return 0
    finally:
        if options.keep_work:
            log(f'临时目录保留在：{work}')
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
