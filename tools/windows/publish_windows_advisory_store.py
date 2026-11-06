"""Explicit I1 overlay/FF only; leaves main index/HEAD and inherited edits intact."""
import argparse,hashlib,json,os,subprocess,time
from pathlib import Path
REPO=Path(__file__).resolve().parents[2]
BRANCH='apex-demo/controller-windows-admission-20261105'
def run(*args,**kw):return subprocess.check_output(args,cwd=REPO,**kw).decode().strip()
def main():
 p=argparse.ArgumentParser();p.add_argument('lane');p.add_argument('--base',required=True);p.add_argument('--phase',choices=['red','green'],required=True);a=p.parse_args()
 if not a.lane.startswith('task2-store-fix-') or not a.lane.replace('-','').isalnum():raise ValueError('lane refused')
 root=REPO/'artifacts/operational-windows-advisory'/a.lane;root.mkdir(parents=True,exist_ok=False)
 for part in ('source','build','evidence'):(root/part).mkdir()
 head=run('git','rev-parse','HEAD');index=REPO/'.git/index';ih=hashlib.sha256(index.read_bytes()).hexdigest()
 assert head=='c5553b13aede8ee9df18ad1c9504ab21f5a4de2a' and ih=='463b98fe455cd5381672b8f7951a85ea7dae7ae4a95d1dcfb3547d0e57e42b16'
 assert run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]==a.base
 run('git','fetch','origin',a.base);env=dict(os.environ,GIT_INDEX_FILE=str(root/'build/index'));run('git','read-tree',a.base,env=env);rows=[]
 def add(rel,data):
  out=root/'source'/rel;out.parent.mkdir(parents=True,exist_ok=True);out.write_bytes(data)
  blob=run('git','hash-object','-w','--stdin',input=data);run('git','update-index','--add','--cacheinfo','100644',blob,rel,env=env);rows.append({'path':rel,'sha256':hashlib.sha256(data).hexdigest()})
 files=['tools/advisory/'+r for r in ['store.py','budget.py','reports.py','supervisor.py','REFRESH.md','tests/test_store_descendants.py','StrategyExport/Program.cs']]
 if (REPO/'tools/advisory/StrategyExport/AdvisoryStoreGuard.cs').exists():files.append('tools/advisory/StrategyExport/AdvisoryStoreGuard.cs')
 files+=['tools/windows/'+r for r in ['Test-WindowsAdvisoryStore.ps1','advisory-store-job.yml','publish_windows_advisory_store.py']]
 for rel in files:add(rel,(REPO/rel).read_bytes())
 workflow=run('git','show',a.base+':.github/workflows/windows-demo.yml')+'\n';marker='\n  windowsAdvisoryRefresh:\n'
 assert workflow.count(marker)==1
 add('.github/workflows/windows-demo.yml',(workflow.split(marker)[0]+'\n'+(REPO/'tools/windows/advisory-store-job.yml').read_text()).encode())
 tree=run('git','write-tree',env=env);commit=run('git','commit-tree',tree,'-p',a.base,input=('fix: advisory descendant admission '+a.phase+'\n').encode())
 record={'base':a.base,'commit':commit,'tree':tree,'inputs':rows,'mainHead':head,'mainIndex':ih,'phase':a.phase}
 (root/'evidence/publication.json').write_text(json.dumps(record,indent=2));(root/'evidence/fix.diff').write_text(run('git','diff',a.base,commit))
 run('git','push','origin',commit+':refs/heads/'+BRANCH)
 assert run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]==commit
 assert run('git','rev-parse','HEAD')==head and hashlib.sha256(index.read_bytes()).hexdigest()==ih
 run('gh','workflow','run','windows-demo.yml','--ref',BRANCH,'-f','mode=windows-advisory-refresh','-f','phase='+a.phase)
 for _ in range(20):
  candidates=json.loads(run('gh','run','list','--workflow','windows-demo.yml','--branch',BRANCH,'--limit','6','--json','databaseId,headSha,status,url'))
  matches=[r for r in candidates if r['headSha']==commit]
  if matches:record['run']=matches[0];break
  time.sleep(2)
 (root/'evidence/publication.json').write_text(json.dumps(record,indent=2));print(json.dumps(record,indent=2))
if __name__=='__main__':main()
