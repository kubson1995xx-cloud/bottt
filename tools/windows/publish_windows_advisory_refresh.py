"""Task2 explicit separate-index overlay; only owned branch, no main changes."""
import argparse,hashlib,json,os,subprocess,time
from pathlib import Path
REPO=Path(__file__).resolve().parents[2]
BRANCH='apex-demo/controller-windows-admission-20261105'
WORKFLOW='.github/workflows/windows-demo.yml'
def run(*args,**kw):return subprocess.check_output(args,cwd=REPO,**kw).decode().strip()
def main():
    parser=argparse.ArgumentParser();parser.add_argument('lane');parser.add_argument('--base',required=True);parser.add_argument('--phase',choices=['red','green'],default='green');args=parser.parse_args()
    if not args.lane.startswith('task2-') or not args.lane.replace('-','').isalnum():raise ValueError('lane refused')
    root=REPO/'artifacts/operational-windows-advisory'/args.lane;root.mkdir(parents=True,exist_ok=False)
    for part in ('source','build','evidence'):(root/part).mkdir()
    head=run('git','rev-parse','HEAD');index=REPO/'.git/index';index_hash=hashlib.sha256(index.read_bytes()).hexdigest()
    if head!='c5553b13aede8ee9df18ad1c9504ab21f5a4de2a' or index_hash!='463b98fe455cd5381672b8f7951a85ea7dae7ae4a95d1dcfb3547d0e57e42b16':raise ValueError('main identity drift')
    base=run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]
    if base!=args.base:raise ValueError('owned branch drift')
    run('git','fetch','origin',base)
    env=dict(os.environ,GIT_INDEX_FILE=str(root/'build/index'));run('git','read-tree',base,env=env);rows=[]
    def add(rel,data):
        target=root/'source'/rel;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
        blob=run('git','hash-object','-w','--stdin',input=data);run('git','update-index','--add','--cacheinfo','100644',blob,rel,env=env)
        rows.append({'path':rel,'sha256':hashlib.sha256(data).hexdigest()})
    # Only explicit Task2 files, never capture generated bin/obj or accepted dirty code.
    files=['market_contract.py','budget.py','reports.py','ipc.py','supervisor.py','worker.py','store.py','vibe_compat.py','fixture_inference.py','inference_process.py','hermes_transport.py','native_reuse.py','native_graphs.py','evidence_run.py','REFRESH.md','tests/test_market_contract.py','tests/test_budget.py','tests/test_supervisor.py','tests/test_transport_compat.py','StrategyExport/Program.cs','StrategyExport/StrategyExport.csproj']
    for rel in files:add('tools/advisory/'+rel,(REPO/'tools/advisory'/rel).read_bytes())
    for rel in ['src/Apex.Desktop/Okx/OkxAdvisoryCanonicalData.cs','tools/windows/Test-WindowsAdvisoryRefresh.ps1','tools/windows/advisory-refresh-job.yml','tools/windows/publish_windows_advisory_refresh.py']:
        add(rel,(REPO/rel).read_bytes())
    workflow=run('git','show',base+':'+WORKFLOW)+'\n'
    if 'windows-advisory-refresh,' not in workflow:
        workflow=workflow.replace('options: [windows-advisory-prerequisite,','options: [windows-advisory-refresh, windows-advisory-prerequisite,',1)
        if 'options: [windows-advisory-refresh,' not in workflow:raise ValueError('workflow mode drift')
    marker='\n  windowsAdvisoryRefresh:\n'
    if marker in workflow:
        if workflow.count(marker)!=1:raise ValueError('job drift')
        workflow=workflow.split(marker)[0]+'\n'
    workflow+='\n'+(REPO/'tools/windows/advisory-refresh-job.yml').read_text()
    add(WORKFLOW,workflow.encode())
    attributes=run('git','show',base+':.gitattributes')+'\n'
    for rel in ['src/Apex.Desktop/Okx/OkxAdvisoryCanonicalData.cs','tools/windows/Test-WindowsAdvisoryRefresh.ps1','tools/windows/advisory-refresh-job.yml','tools/windows/publish_windows_advisory_refresh.py']:
        line='/'+rel+' text eol=lf'
        if line not in attributes.splitlines():attributes+=line+'\n'
    add('.gitattributes',attributes.encode());tree=run('git','write-tree',env=env)
    commit=run('git','commit-tree',tree,'-p',base,input=('feat: bounded Windows advisory refresh '+args.lane+'\n').encode())
    record={'base':base,'commit':commit,'tree':tree,'inputs':rows,'mainHead':head,'mainIndex':index_hash,'phase':args.phase}
    (root/'evidence/publication.json').write_text(json.dumps(record,indent=2))
    run('git','push','origin',commit+':refs/heads/'+BRANCH)
    assert run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]==commit
    assert run('git','rev-parse','HEAD')==head and hashlib.sha256(index.read_bytes()).hexdigest()==index_hash
    run('gh','workflow','run','windows-demo.yml','--ref',BRANCH,'-f','mode=windows-advisory-refresh','-f','phase='+args.phase)
    for _ in range(20):
        candidates=json.loads(run('gh','run','list','--workflow','windows-demo.yml','--branch',BRANCH,'--limit','8','--json','databaseId,headSha,status,url'))
        match=[r for r in candidates if r['headSha']==commit]
        if match:record['run']=match[0];break
        time.sleep(2)
    (root/'evidence/publication.json').write_text(json.dumps(record,indent=2));print(json.dumps(record,indent=2))
if __name__=='__main__':main()
