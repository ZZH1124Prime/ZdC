"""Build the ZdC Windows x64 Setup in this checkout."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET

import msilib as m
from msilib import schema

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[1]
FAMILY = '{D04744F1-2909-4A80-B108-295247AFBF11}'


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def guid(name):
    return '{' + str(uuid.uuid5(uuid.UUID(FAMILY), name)).upper() + '}'


def baseline(commit, path):
    return subprocess.check_output(['git', '-C', str(REPO), 'show', commit + ':' + path])


def repo_path(path):
    resolved = path.resolve()
    if not resolved.is_relative_to(REPO):
        raise ValueError(f'Path is outside the repository: {path}')
    return resolved


def compiler():
    version = subprocess.check_output(['dotnet', '--version'], cwd=REPO, text=True).strip()
    sdks = subprocess.check_output(['dotnet', '--list-sdks'], cwd=REPO, text=True)
    for line in sdks.splitlines():
        match = re.fullmatch(r'(\S+) \[(.+)\]', line)
        if match and match[1] == version:
            return Path(match[2]) / version / 'Roslyn/bincore/csc.dll'
    raise RuntimeError(f'Cannot locate the selected .NET SDK {version}')


def build(work, release, commit, version, files, filename):
    payload = work / 'verified'
    payload.mkdir()
    expected = {f['Name'] for f in files}
    if len(expected) != len(files) or {p.name for p in release.iterdir()} != expected:
        raise ValueError('Release directory inventory differs from the manifest')
    for item in files:
        name = item['Name']
        if not name or Path(name).name != name or not re.fullmatch(r'[0-9a-fA-F]{64}', item['After']):
            raise ValueError('Invalid release manifest entry')
        raw = (repo_path(release / name)).read_bytes()
        if digest(raw) != item['After'].upper():
            raise ValueError(f'Unverified release input: {name}')
        (payload / name).write_bytes(raw)

    msi = work / 'ZdC.msi'
    product = guid('product:x64:' + version)
    db = m.init_database(str(msi), schema, 'ZdC', product, version, 'ZdC')
    def rows(name, data):
        m.add_data(db, name, data)
    si = db.GetSummaryInformation(10)
    si.SetProperty(m.PID_TEMPLATE, 'x64;1033')
    si.SetProperty(m.PID_PAGECOUNT, 500)
    si.Persist()
    del si
    rows('Property', [('UpgradeCode',FAMILY), ('ALLUSERS','1'), ('INSTALLLEVEL','1'),
          ('ARPNOMODIFY','1'), ('MSIRESTARTMANAGERCONTROL','DisableShutdown'),
          ('SecureCustomProperties','INSTALLDIR;OLDPRODUCTS;NEWERPRODUCTS')])
    rows('Directory',[('TARGETDIR',None,'SourceDir'),('ProgramFiles64Folder','TARGETDIR','.'),
          ('INSTALLDIR','ProgramFiles64Folder','ZdC'),('ProgramMenuFolder','TARGETDIR','.'),
          ('ZdCMenu','ProgramMenuFolder','ZdC'),('DesktopFolder','TARGETDIR','.')])
    rows('Feature',[('Main',None,'ZdC',None,1,1,'INSTALLDIR',0),
                    ('Desktop',None,None,None,0,2,'DesktopFolder',0)])
    cab = m.CAB('ZdC.cab')
    component_names = {}
    for n,f in enumerate(files,1):
        name=f['Name']
        dest=payload/name
        key='F_' + re.sub(r'[^A-Za-z0-9_]','_',name)
        comp='C_' + key[2:]
        component_names[name]=(comp,key)
        rows('Component',[(comp,guid('file:'+name.lower()),'INSTALLDIR',256,None,key)])
        rows('FeatureComponents',[('Main',comp)])
        short='Z%06d.%s' % (n,name.rsplit('.',1)[1][:3].upper())
        rows('File',[(key,comp,short+'|'+name,dest.stat().st_size,None,None,512,n)])
        cab.append(str(dest),name,key)
    cab.commit(db)
    app,filekey=component_names['ZdC.exe']
    rows('Registry',[('DesktopKey',2,r'Software\ZdC\Installer','DesktopShortcut','#1','DesktopLink')])
    rows('Component',[('DesktopLink',guid('shortcut:desktop'),'DesktopFolder',260,None,'DesktopKey')])
    rows('FeatureComponents',[('Desktop','DesktopLink')])
    rows('Shortcut',[(key,folder,'ZdC',comp,'[#'+filekey+']',None,None,None,None,None,1,'INSTALLDIR')
           for key,folder,comp in [('MenuShortcut','ZdCMenu',app),('DesktopShortcut','DesktopFolder','DesktopLink')]])
    rows('RemoveFile',[('EmptyMenu',app,None,'ZdCMenu',2)])
    rows('Upgrade',[(FAMILY,None,version,None,0,None,'OLDPRODUCTS'),(FAMILY,version,None,None,2,None,'NEWERPRODUCTS')])
    # The launcher uses only these approved messages. The internal database is never a UI delivery surface.
    rows('LaunchCondition',[('Installed OR (VersionNT64 AND NOT NEWERPRODUCTS)','ZdC')])
    # Standard type 51 sets a property; it runs no program.
    rows('CustomAction',[('RememberInstallDirectory',51,'ARPINSTALLLOCATION','[INSTALLDIR]')])
    execute=[('FindRelatedProducts',25),('LaunchConditions',100),('CostInitialize',800),('FileCost',900),('CostFinalize',1000),
        ('RememberInstallDirectory',1010),('InstallValidate',1400),('InstallInitialize',1500),('RemoveExistingProducts',1510),
        ('ProcessComponents',1600),('UnpublishFeatures',1800),('RemoveRegistryValues',2600),('RemoveShortcuts',3200),
        ('RemoveFiles',3500),('RemoveFolders',3600),('InstallFiles',4000),('CreateShortcuts',4500),('WriteRegistryValues',5000),
        ('RegisterUser',6000),('RegisterProduct',6100),('PublishFeatures',6300),('PublishProduct',6400),('InstallFinalize',6600)]
    rows('InstallExecuteSequence',[(a, 'OLDPRODUCTS' if a=='RemoveExistingProducts' else None,n) for a,n in execute])
    db.Commit()
    del db
    # Copy only immutable visuals from the pinned Git tree. Use the original static XAML and size-dependent outline.
    src='src/ZdC.App/'
    for name in ['ZdC.png','ZdCMark.png']:
        (work/name).write_bytes(baseline(commit,src+'Assets/'+name))
    ns={'p':'http://schemas.microsoft.com/winfx/2006/xaml/presentation','x':'http://schemas.microsoft.com/winfx/2006/xaml'}
    ET.register_namespace('',ns['p'])
    ET.register_namespace('x',ns['x'])
    theme=ET.Element('{'+ns['p']+'}ResourceDictionary')
    for node in ET.fromstring(baseline(commit,src+'App.xaml')).find('p:Application.Resources',ns):
        key=node.get('{'+ns['x']+'}Key')
        if key in ('Ink','StoneGlyph','Canvas','InsetSurface','Line','FocusBorder','KeyboardOnlyFocus') or (
            not key and node.get('TargetType') in ('TextBlock','TextBox')):
            theme.append(node)
    (work/'theme.xaml').write_bytes(ET.tostring(theme,encoding='utf-8'))
    main=ET.fromstring(baseline(commit,src+'MainWindow.xaml'))
    def named(tag):
        return next(e for e in main.iter() if e.get('{'+ns['x']+'}Name')==tag)
    mass=next(iter(named('StoneMass').find('p:Path.Fill',ns)))
    (work/'mass.xaml').write_bytes(ET.tostring(mass,encoding='utf-8'))
    backend=baseline(commit,src+'MainWindow.xaml.cs').decode('utf-8-sig')
    start=backend.index('    private void StoneFace_SizeChanged(')
    end=backend.index('    private void Minimize_Click',start)
    outline='using System.Collections.Generic;\nusing System.Linq;\nusing System.Windows;\nusing System.Windows.Media;\npartial class SetupWindow {\n'+backend[start:end]+'}\n'
    (work/'Outline.cs').write_text(outline,encoding='utf-8-sig')
    identity = ('[assembly:System.Reflection.AssemblyVersion("'+version+'.0")]\n' +
                '[assembly:System.Reflection.AssemblyFileVersion("'+version+'.0")]\n')
    identity += 'internal static class Identity { internal const string Family="'+FAMILY+'", Product="'+product+'", Version="'+version+'", PackageSha="'+digest(msi.read_bytes())+'"; internal const long Size='+str(sum((payload/f['Name']).stat().st_size for f in files))+'L; }'
    (work/'Identity.cs').write_text(identity,encoding='ascii')

    exe = work / filename
    framework = Path(os.environ['ProgramFiles(x86)']) / 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
    win_manifest = (ROOT/'setup.manifest').read_text(encoding='utf-8-sig')
    (work/'setup.manifest').write_text(win_manifest.replace('@FILE_VERSION@',version+'.0'),encoding='utf-8')
    args = ['dotnet',str(compiler()),'/noconfig','/nostdlib+','/target:winexe','/platform:x64',
            '/optimize+','/deterministic+','/langversion:latest','/warnaserror+',
            '/out:'+str(exe),'/win32manifest:'+str(work/'setup.manifest')]
    for ref in ['mscorlib','System','System.Core','System.Xaml','WindowsBase','PresentationCore','PresentationFramework','System.Windows.Forms','System.Drawing','System.Xml']:
        args.append('/reference:'+str(framework/(ref+'.dll')))
    for name in ['ZdC.msi','ZdC.png','ZdCMark.png','theme.xaml','mass.xaml']:
        args.append('/resource:'+str(work/name)+','+name)
    args.extend(str(p) for p in [ROOT/'SetupWindow.cs',ROOT/'InstallEngine.cs',work/'Outline.cs',work/'Identity.cs'])
    subprocess.run(args,check=True)
    return exe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',type=Path,default=ROOT/'release.json',
                        help='Release manifest in the checkout (Commit, Files[].Name, Files[].After)')
    parser.add_argument('--payload',type=Path,default=REPO/'artifacts/ZdC',
                        help='Verified application release directory in the checkout')
    parser.add_argument('--check-build',action='store_true',
                        help='Build and check the package without replacing or publishing a release artifact')
    options = parser.parse_args()
    manifest = json.loads(repo_path(options.manifest).read_text(encoding='utf-8-sig'))
    commit = manifest['Commit']
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Expected a full Git commit ID')
    project = ET.fromstring(baseline(commit,'src/ZdC.App/ZdC.App.csproj'))
    version = project.findtext('.//Version','')
    if not re.fullmatch(r'\d+\.\d+\.\d+',version):
        raise ValueError('Expected a three-part Windows product version in ZdC.App.csproj')
    if any(int(number)>limit for number,limit in zip(version.split('.'),(255,255,65535))):
        raise ValueError('Product version exceeds Windows Installer limits')
    files = sorted(manifest['Files'],key=lambda f:f['Name'])
    release = repo_path(options.payload)
    artifacts = repo_path(REPO/'artifacts')
    output = repo_path(artifacts/'windows')
    output.mkdir(parents=True,exist_ok=True)
    filename = f'ZdC-Setup-{version}-Windows-x64-{commit[:7]}.exe'
    final = output/filename
    if final.exists() and not options.check_build:
        sys.exit(f'Already exists: {final}. Use --check-build for an isolated rebuild.')
    with tempfile.TemporaryDirectory(prefix='.zdc-windows-build-',dir=artifacts) as directory:
        work = repo_path(Path(directory))
        old_temp = tempfile.tempdir
        tempfile.tempdir = str(work)
        try:
            exe = build(work,release,commit,version,files,filename)
            # 7-Zip decodes the embedded cabinet into stdout. Check each file without installing anything.
            seven = shutil.which('7z.exe') or str(Path(os.environ['ProgramFiles'])/'7-Zip/7z.exe')
            for item in files:
                key = 'F_'+re.sub(r'[^A-Za-z0-9_]','_',item['Name'])
                data = subprocess.check_output([seven,'x','-so',str(work/'ZdC.msi'),key])
                if digest(data) != item['After'].upper():
                    raise ValueError('Packaged file differs from manifest: '+item['Name'])
            value = digest(exe.read_bytes())
            if options.check_build:
                print(f'Check build passed: {len(files)} embedded files. Candidate SHA-256 {value}')
            else:
                # Windows rename is atomic on this volume and refuses an existing destination.
                os.rename(exe,final)
                print(f'{final}\nSHA-256 {value}')
        finally:
            tempfile.tempdir = old_temp


if __name__ == '__main__':
    main()
