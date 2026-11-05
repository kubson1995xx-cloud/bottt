#!/usr/bin/env python3
"""Retain actual source and payload before a uniquely named local gate."""
import argparse,hashlib,json,shutil,subprocess
from pathlib import Path
REPO=Path(__file__).resolve().parents[2]
BASE=Path('/home/ubuntu/apex-deliveries/okx-demo-fee0/persistent-runner')
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def snapshot(dest):
    names=subprocess.check_output(['git','ls-files','-z'],cwd=REPO).decode().split('\0')
    names+=subprocess.check_output(['git','ls-files','--others','--exclude-standard','-z'],cwd=REPO).decode().split('\0')
    inputs={}
    for n in sorted(set(names)):
        p=REPO/n
        if not n or not p.is_file() or not n.startswith(('src/','tests/','tools/','global.json','NuGet.config')):continue
        inputs[n]=digest(p);out=dest/'source'/n;out.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,out)
    (dest/'inputs.json').write_text(json.dumps({'head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO).decode().strip(),'inputs':inputs},indent=2))
def main():
    p=argparse.ArgumentParser();p.add_argument('label');p.add_argument('--project');p.add_argument('--test',action='store_true');p.add_argument('--filter');p.add_argument('--cli',action='store_true');a=p.parse_args()
    dest=BASE/a.label;dest.mkdir(parents=True,exist_ok=False);snapshot(dest)
    dotnet='/home/ubuntu/.dotnet/dotnet'
    project=a.project or 'tests/Apex.Okx.CliHarness/Apex.Okx.CliHarness.csproj'
    with (dest/'build.log').open('w') as f:r=subprocess.run([dotnet,'build',project,'--no-restore','-v:q'],cwd=REPO,stdout=f,stderr=subprocess.STDOUT)
    print('build',r.returncode,flush=True)
    if r.returncode:return r.returncode
    src=REPO/Path(project).parent/'bin/Debug/net8.0';shutil.copytree(src,dest/'payload')
    (dest/'payload-sha256.json').write_text(json.dumps({str(q.relative_to(dest/'payload')):digest(q) for q in (dest/'payload').rglob('*') if q.is_file()},indent=2))
    if a.cli:cmd=['python3',str(REPO/'tools/okx-demo-acceptance/test_persistent_cli.py'),str(dest/'payload/Apex.Tests.dll'),str(dest)]
    elif a.test:
        cmd=[dotnet,'vstest',str(src/('Apex.Adapters.Okx.Tests.dll' if 'Adapters.Okx.Tests' in project else 'Apex.Tests.dll')),'--logger:trx;LogFileName=results.trx','--ResultsDirectory:'+str(dest)]
        # Existing host/fixture tests discover the repository from AppContext.BaseDirectory.
        # Execute its hash-identical bin output; the immutable pre-run closure is above.
        if a.filter:cmd+=['--TestCaseFilter:'+a.filter]
    else:return 0
    with (dest/'run.log').open('w') as f:r=subprocess.run(cmd,cwd=REPO,stdout=f,stderr=subprocess.STDOUT)
    print('test',r.returncode,flush=True);print((dest/'run.log').read_text()[-7000:]);return r.returncode
if __name__=='__main__':raise SystemExit(main())
