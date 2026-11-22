"""Build the ZdC native Windows binaries with the Visual C++ toolchain.

Usage (from the repository root):
    python -B src/native/build.py [--output artifacts/native]

The Detours submodule must be checked out first:
    git submodule update --init third_party/Detours

The script locates Visual Studio through vswhere, imports the x64/x86
environment with vcvars, and writes all outputs into the output directory.
"""
import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
DETOURS = REPO / 'third_party' / 'Detours'
DEFAULT_OUTPUT = REPO / 'artifacts' / 'native'

OUTPUTS = (
    'ZdC.SocketController.exe',
    'ZdC.SocketHook32.dll',
    'ZdC.SocketHook64.dll',
    'ZdC.SocketProbe.exe',
)

COMMON = '/nologo /std:c++17 /utf-8 /EHsc /W4 /MT'


def vswhere_path() -> Path:
    candidates = []
    for variable in ('ProgramFiles(x86)', 'ProgramFiles'):
        root = os.environ.get(variable)
        if root:
            candidates.append(Path(root) / 'Microsoft Visual Studio' / 'Installer' / 'vswhere.exe')
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise RuntimeError(
        '鎵句笉鍒?vswhere.exe銆傝瀹夎 Visual Studio 2022锛堟垨 Build Tools锛夊苟鍕鹃€?
        '鈥滀娇鐢?C++ 鐨勬闈㈠紑鍙戔€濆拰 Windows SDK銆?)


def vcvars_path(arch: str) -> Path:
    query = [
        str(vswhere_path()), '-latest', '-products', '*',
        '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
        '-property', 'installationPath',
    ]
    installation = subprocess.check_output(query, text=True).strip()
    if not installation:
        raise RuntimeError('娌℃湁鎵惧埌甯?C++ 宸ュ叿鐨?Visual Studio 瀹夎銆?)
    script = Path(installation) / 'VC' / 'Auxiliary' / 'Build' / f'vcvars{arch}.bat'
    if not script.is_file():
        raise RuntimeError(f'鎵句笉鍒?{script}銆傝瀹夎 Visual Studio C++ 妗岄潰寮€鍙戠粍浠躲€?)
    return script


def compile(arch: str, workdir: Path, arguments: str) -> None:
    script = vcvars_path(arch)
    command = f'call "{script}" >nul && cl {COMMON} {arguments}'
    subprocess.run(['cmd', '/c', command], cwd=workdir, check=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=DEFAULT_OUTPUT,
                        help='Output directory (default: artifacts/native)')
    options = parser.parse_args()

    if not (DETOURS / 'include' / 'detours.h').is_file() or not (DETOURS / 'lib.X64' / 'detours.lib').is_file():
        raise SystemExit(
            'third_party/Detours 鏈鍑恒€傚厛鎵ц锛歡it submodule update --init third_party/Detours')

    include = DETOURS / 'include'
    lib64 = DETOURS / 'lib.X64' / 'detours.lib'
    lib32 = DETOURS / 'lib.X86' / 'detours.lib'
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    hook = HERE / 'SocketHook.cpp'
    wininet = HERE / 'WinInetRouting.cpp'
    controller = HERE / 'SocketController.cpp'
    probe = HERE / 'SocketProbe.cpp'

    compile('64', output,
            f'/I "{include}" /LD "{hook}" "{wininet}" "{lib64}" /Fe:ZdC.SocketHook64.dll')
    compile('32', output,
            f'/I "{include}" /LD "{hook}" "{wininet}" "{lib32}" /Fe:ZdC.SocketHook32.dll')
    compile('64', output,
            f'/I "{include}" "{controller}" "{lib64}" /Fe:ZdC.SocketController.exe /Fo:Controller.obj')
    compile('64', output, f'"{probe}" /Fe:ZdC.SocketProbe.exe /Fo:Probe.obj')

    missing = [name for name in OUTPUTS if not (output / name).is_file()]
    if missing:
        raise SystemExit('鍘熺敓鏋勫缓缂哄皯杈撳嚭锛? + ', '.join(missing))
    for name in OUTPUTS:
        print(f'  {name}  {(output / name).stat().st_size} bytes')
    print(f'鍘熺敓浜岃繘鍒惰緭鍑虹洰褰曪細{output}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
