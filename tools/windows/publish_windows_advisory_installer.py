"""Publish only the installer worker's explicit files to the owned FF branch."""
import argparse,hashlib,json,os,subprocess,time
from pathlib import Path
REPO=Path(__file__).resolve().parents[2]
BRANCH='apex-demo/controller-windows-admission-20261105'
FILES=['tools/advisory/StrategyExport/AdvisoryStoreGuard.cs','tools/advisory/StrategyExport/Program.cs','tools/advisory/store.py','tools/advisory/local_install.py','tests/Apex.Tests/OkxWindowsAdvisorySetupTests.cs','tools/advisory/tests/test_native_installer_paths.py','tools/windows/Test-WindowsAdvisoryInstaller.ps1','tools/windows/publish_windows_advisory_installer.py']
def run(*args,**kwargs):return subprocess.check_output(args,cwd=REPO,**kwargs).decode().strip()
def main():
 p=argparse.ArgumentParser();p.add_argument('lane');p.add_argument('--base',required=True);p.add_argument('--delivery',action='store_true');a=p.parse_args()
 assert a.lane.startswith('integration-') and a.lane.replace('-','').isalnum()
 root=REPO/'artifacts/operational-windows-advisory'/a.lane;root.mkdir(exist_ok=False)
 head=run('git','rev-parse','HEAD');index=REPO/'.git/index';ih=hashlib.sha256(index.read_bytes()).hexdigest()
 assert head=='c5553b13aede8ee9df18ad1c9504ab21f5a4de2a' and ih=='463b98fe455cd5381672b8f7951a85ea7dae7ae4a95d1dcfb3547d0e57e42b16'
 assert run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]==a.base
 run('git','fetch','origin',a.base);env=dict(os.environ,GIT_INDEX_FILE=str(root/'index'));run('git','read-tree',a.base,env=env)
 rows=[]
 def add(rel,data):
  dest=root/'source'/rel;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(data)
  blob=run('git','hash-object','-w','--stdin',input=data);run('git','update-index','--add','--cacheinfo','100644',blob,rel,env=env)
  rows.append({'path':rel,'sha256':hashlib.sha256(data).hexdigest()})
 for rel in (['tools/windows/publish_windows_advisory_installer.py'] if a.delivery else FILES):add(rel,(REPO/rel).read_bytes())
 workflow=run('git','show',a.base+':.github/workflows/windows-demo.yml')+'\n';marker='\n  windowsAdvisoryRefresh:\n'
 if workflow.count(marker)==0 and workflow.count('\nwindowsAdvisoryRefresh:\n')==1:marker='\nwindowsAdvisoryRefresh:\n' # Exact previously refused indentation-only publication.
 assert workflow.count(marker)==1
 job='''  windowsAdvisoryRefresh:
    if: github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/apex-demo/controller-windows-admission-20261105' && inputs.mode == 'windows-advisory-refresh'
    runs-on: windows-latest
    timeout-minutes: 35
    permissions:
      contents: read
      actions: read
    steps:
      - uses: actions/checkout@v4
        with:
          persist-credentials: false
      - uses: actions/setup-python@v5
        with:
          python-version: '3.14.0'
          architecture: x64
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.427'
      - uses: actions/download-artifact@v4
        with:
          name: Windows-Advisory-Delivery-42268838572-1
          run-id: 42268838572
          github-token: ${{ github.token }}
          path: ${{ runner.temp }}/installer-retained-prior
      - name: Narrow actual native installer replay, no EXE rebuild or inference
        shell: pwsh
        run: ./tools/windows/Test-WindowsAdvisoryInstaller.ps1 -Prior '${{ runner.temp }}/installer-retained-prior'
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: Windows-Advisory-Installer-${{ github.run_id }}-${{ github.run_attempt }}
          path: ${{ runner.temp }}/windows-advisory-installer/
          include-hidden-files: true
          if-no-files-found: error
          retention-days: 30
'''
 if a.delivery:job=subprocess.check_output(['git','show',a.base+':tools/windows/advisory-delivery-job.yml'],cwd=REPO).decode()
 assert job.startswith('  windowsAdvisoryRefresh:\n')
 updated=workflow.split(marker)[0]+'\n'+job
 import yaml
 parsed=yaml.safe_load(updated);assert 'windowsAdvisoryRefresh' in parsed['jobs'] and 'windowsAdvisoryRefresh' not in parsed
 add('.github/workflows/windows-demo.yml',updated.encode())
 tree=run('git','write-tree',env=env);commit=run('git','commit-tree',tree,'-p',a.base,input=b'fix: isolate native advisory installer rejection\n')
 record={'base':a.base,'commit':commit,'tree':tree,'inputs':rows,'mainHead':head,'mainIndex':ih}
 (root/'publication.json').write_text(json.dumps(record,indent=2));(root/'installer.diff').write_text(run('git','diff',a.base,commit))
 run('git','push','origin',commit+':refs/heads/'+BRANCH)
 assert run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]==commit
 assert run('git','rev-parse','HEAD')==head and hashlib.sha256(index.read_bytes()).hexdigest()==ih
 run('gh','workflow','run','windows-demo.yml','--ref',BRANCH,'-f','mode=windows-advisory-refresh','-f','phase=green')
 for _ in range(30):
  matches=[r for r in json.loads(run('gh','run','list','--workflow','windows-demo.yml','--branch',BRANCH,'--limit','8','--json','databaseId,headSha,status,url')) if r['headSha']==commit]
  if matches:record['run']=matches[0];break
  time.sleep(2)
 (root/'publication.json').write_text(json.dumps(record,indent=2));print(json.dumps(record,indent=2))
if __name__=='__main__':main()
