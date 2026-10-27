"""Build the native, self-contained Windows release. SPDX-License-Identifier: GPL-3.0-only."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys
import zipfile

root = Path(__file__).resolve().parent
output = root / 'dist/fluent'
env = os.environ.copy()
for key, folder in {'DOTNET_CLI_HOME':'dotnet-home', 'NUGET_HTTP_CACHE_PATH':'nuget-http', 'NUGET_PLUGINS_CACHE_PATH':'nuget-plugins', 'NUGET_SCRATCH':'nuget-scratch'}.items():
    env[key] = str(root / 'build' / folder)
env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
env['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = str(root / 'build/bundle-cache')
command = ['dotnet','publish',str(root/'desktop/FanPanel/FanPanel.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-o',str(output),
           '-p:RuntimeFrameworkVersion=10.0.14','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',
           '-p:EnableCompressionInSingleFile=true','-p:DebugType=embedded','-p:DebugSymbols=false','-p:PublishTrimmed=false',
           '-p:PublishReadyToRun=false','-p:UseSharedCompilation=false','-nodeReuse:false','-p:RestorePackagesPath='+str(root/'build/nuget')]
exe=output/'Mechrevo-Fan.exe'
if '--package-only' not in sys.argv:
    subprocess.run(command,cwd=root,env=env,check=True)
    subprocess.run([str(exe),'--self-test',str(root/'validation/native-tests.json')],cwd=root,env=env,check=True,timeout=90)
receipt={'version':'2.0.1','runtime':'10.0.14','self_contained':True,'single_executable':True,'bytes':exe.stat().st_size,'sha256':hashlib.sha256(exe.read_bytes()).hexdigest()}
(root/'validation/build.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
print(json.dumps(receipt,indent=2))


def package():
    files={exe:'Mechrevo-Fan.exe'}
    for name in ('README.md','LICENSE','THIRD_PARTY_NOTICES.md','build_fluent.py'):
        files[root/name]=name
    for folder in ('desktop/FanPanel','docs','validation'):
        for path in (root/folder).rglob('*'):
            if path.is_file() and not {'bin','obj','state','portable-isolated'}.intersection(path.relative_to(root).parts) and path.name!='v1-readme.md':
                files[path]=path.relative_to(root).as_posix()
    sums='\n'.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+name for p,name in sorted(files.items(),key=lambda x:x[1]))+'\n'
    archive=root/'dist/Mechrevo-Fan-Fluent-v2.0.1.zip'
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for path,name in files.items(): z.write(path,name)
        z.writestr('SHA256SUMS.txt',sums)
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None
        for line in sums.splitlines():
            digest,name=line.split('  ',1)
            assert hashlib.sha256(z.read(name)).hexdigest()==digest,name
    (archive.with_suffix('.zip.sha256')).write_text(hashlib.sha256(archive.read_bytes()).hexdigest()+'  '+archive.name+'\n')
    print('Verified package:',archive)


if (root/'THIRD_PARTY_NOTICES.md').exists():
    package()
