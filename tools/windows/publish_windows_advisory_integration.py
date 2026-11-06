"""Explicit integration overlay/FF on the sole authorized branch, separate index."""
import argparse,hashlib,json,os,subprocess,time
from pathlib import Path
REPO=Path(__file__).resolve().parents[2];BRANCH='apex-demo/controller-windows-admission-20261105'
def run(*args,**kw):return subprocess.check_output(args,cwd=REPO,**kw).decode().strip()
def main():
 p=argparse.ArgumentParser();p.add_argument('lane');p.add_argument('--base',required=True);p.add_argument('--phase',choices=['red','green'],required=True);p.add_argument('--delivery',action='store_true');a=p.parse_args()
 assert a.lane.startswith('integration-') and a.lane.replace('-','').isalnum()
 root=REPO/'artifacts/operational-windows-advisory'/a.lane;root.mkdir(parents=True,exist_ok=False)
 head=run('git','rev-parse','HEAD');idx=REPO/'.git/index';ih=hashlib.sha256(idx.read_bytes()).hexdigest()
 assert head=='c5553b13aede8ee9df18ad1c9504ab21f5a4de2a' and ih=='463b98fe455cd5381672b8f7951a85ea7dae7ae4a95d1dcfb3547d0e57e42b16'
 assert run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]==a.base
 run('git','fetch','origin',a.base);env=dict(os.environ,GIT_INDEX_FILE=str(root/'index'));run('git','read-tree',a.base,env=env);rows=[]
 def add(rel,data):
  dest=root/'source'/rel;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(data)
  blob=run('git','hash-object','-w','--stdin',input=data);run('git','update-index','--add','--cacheinfo','100644',blob,rel,env=env);rows.append({'path':rel,'sha256':hashlib.sha256(data).hexdigest()})
 files=['src/Apex.Desktop/Okx/OkxAdvisorySnapshot.cs','tests/Apex.Tests/OkxWindowsAdvisoryConsumerTests.cs','tests/Apex.Tests/OkxWindowsAdvisoryStoreTests.cs','tools/windows/Test-WindowsAdvisoryIntegration.ps1','tools/windows/publish_windows_advisory_integration.py']
 for rel in ['src/Apex.Desktop/Okx/OkxAccountConnectionService.cs','src/Apex.Desktop/Okx/OkxOperationalTradingService.cs','src/Apex.Desktop/Okx/WindowsOkxAdvisoryStore.cs','src/Apex.Desktop/Okx/OkxAdvisoryCoordinator.cs','src/Apex.Desktop/Okx/OkxAdvisoryAdmission.cs','tests/Apex.Tests/OkxWindowsAdvisoryEntryTests.cs']:
  if (REPO/rel).exists():files.append(rel)
 if a.delivery:
  files += ['src/Apex.Desktop/Okx/'+r for r in ['OkxNavalRuntime.cs','OkxOperatorControl.cs','OkxControlEndpoints.cs','WindowsOkxAdvisorySetup.cs','local-panel.html','local-panel.js']]
  files += ['tools/advisory/'+r for r in ['local_install.py','local_companion.py','build_bundle.py','store.py','tests/test_local_install.py']]
  files += ['tools/windows/'+r for r in ['Test-WindowsLocalPackage.ps1','Test-WindowsAdvisoryDelivery.ps1','advisory-delivery-job.yml','AI-START.pl.md']]
  files += ['tests/'+r for r in ['Apex.Tests/OkxWindowsAdvisorySetupTests.cs','Apex.Tests/OkxWindowsLocalLifecycleTests.cs','Apex.Tests/OkxWindowsLocalOwnerTests.cs','Apex.ControllerComposition.Tests/LocalPackagedTests.cs','panel/local-panel.test.cjs','package/safe_extract.py']]
 for rel in files:add(rel,(REPO/rel).read_bytes())
 workflow=run('git','show',a.base+':.github/workflows/windows-demo.yml')+'\n';marker='\n  windowsAdvisoryRefresh:\n';assert workflow.count(marker)==1
 job=(REPO/'tools/windows/advisory-store-job.yml').read_text().replace('I1 native storage RED or full advisory GREEN, no upstream reinstall','Consolidated advisory native production boundary').replace('Test-WindowsAdvisoryStore.ps1','Test-WindowsAdvisoryIntegration.ps1').replace('Windows-Advisory-Store-','Windows-Advisory-Integration-').replace('windows-advisory-store/','windows-advisory-integration/')
 if a.delivery:job=(REPO/'tools/windows/advisory-delivery-job.yml').read_text()
 add('.github/workflows/windows-demo.yml',(workflow.split(marker)[0]+'\n'+job).encode())
 tree=run('git','write-tree',env=env);commit=run('git','commit-tree',tree,'-p',a.base,input=('feat: Windows advisory integration '+a.phase+'\n').encode())
 record={'base':a.base,'commit':commit,'tree':tree,'inputs':rows,'mainHead':head,'mainIndex':ih,'phase':a.phase}
 (root/'publication.json').write_text(json.dumps(record,indent=2));(root/'integration.diff').write_text(run('git','diff',a.base,commit))
 run('git','push','origin',commit+':refs/heads/'+BRANCH);assert run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]==commit
 assert run('git','rev-parse','HEAD')==head and hashlib.sha256(idx.read_bytes()).hexdigest()==ih
 run('gh','workflow','run','windows-demo.yml','--ref',BRANCH,'-f','mode=windows-advisory-refresh','-f','phase='+a.phase)
 for _ in range(20):
  matches=[r for r in json.loads(run('gh','run','list','--workflow','windows-demo.yml','--branch',BRANCH,'--limit','6','--json','databaseId,headSha,status,url')) if r['headSha']==commit]
  if matches:record['run']=matches[0];break
  time.sleep(2)
 (root/'publication.json').write_text(json.dumps(record,indent=2));print(json.dumps(record,indent=2))
if __name__=='__main__':main()
