"""Owned explicit overlay + narrow prerequisite dispatch; never main index/HEAD.

Only the existing publication branch is writable. Derive workflow from remote
accepted bytes, adding one mode/job; never publish the older dirty local workflow.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

REPO = Path(__file__).resolve().parents[2]
BRANCH = 'apex-demo/controller-windows-admission-20261105'
WORKFLOW = '.github/workflows/windows-demo.yml'


def run(*args, **kwargs):
    return subprocess.check_output(args, cwd=REPO, **kwargs).decode().strip()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('lane')
    parser.add_argument('--phase', choices=['red','green'], required=True)
    parser.add_argument('files', nargs='+')
    args = parser.parse_args()
    if not args.lane.replace('-', '').isalnum(): raise ValueError('invalid lane')
    lane = REPO / 'artifacts/operational-windows-advisory' / args.lane
    lane.mkdir(parents=True, exist_ok=False)
    for part in ['source','build','closure','evidence']: (lane/part).mkdir()
    head = run('git','rev-parse','HEAD')
    index = REPO/'.git/index'; index_hash = hashlib.sha256(index.read_bytes()).hexdigest()
    base = run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]
    run('git','fetch','origin',base)
    env = dict(os.environ, GIT_INDEX_FILE=str(lane/'build/index'))
    run('git','read-tree',base,env=env)
    rows=[]
    def add(rel,data):
        out=lane/'source'/rel;out.parent.mkdir(parents=True,exist_ok=True);out.write_bytes(data)
        blob=run('git','hash-object','-w','--stdin',input=data)
        run('git','update-index','--add','--cacheinfo','100644',blob,rel,env=env)
        rows.append({'path':rel,'sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data)})
    for rel in args.files:
        if not (rel.startswith('tools/advisory/') or rel in ['tools/windows/Test-WindowsAdvisoryPrerequisite.ps1','tools/windows/publish_windows_advisory_overlay.py','tools/windows/advisory-prerequisite-job.yml']):
            raise ValueError('unowned overlay path: '+rel)
        if '..' in Path(rel).parts: raise ValueError('traversal')
        add(rel,(REPO/rel).read_bytes())
    workflow=run('git','show',base+':'+WORKFLOW)+'\n'
    if 'windows-advisory-prerequisite' not in workflow:
        workflow=workflow.replace('options: [windows-local-package,','options: [windows-advisory-prerequisite, windows-local-package,',1)
        if 'options: [windows-advisory-prerequisite,' not in workflow: raise ValueError('remote workflow drift')
        workflow+='\n'+(REPO/'tools/windows/advisory-prerequisite-job.yml').read_text()
    else:
        # Only the owned trailing job may be replaced on subsequent publication.
        marker='\n  windowsAdvisoryPrerequisite:\n'
        if workflow.count(marker)!=1: raise ValueError('owned job drift')
        workflow=workflow.split(marker)[0]+'\n'+(REPO/'tools/windows/advisory-prerequisite-job.yml').read_text()
    add(WORKFLOW,workflow.encode())
    # Preserve accepted remote attributes and add LF only for owned new inputs.
    attributes=run('git','show',base+':.gitattributes')+'\n'
    owned_attributes=['/tools/advisory/** text eol=lf',
                      '/tools/windows/Test-WindowsAdvisoryPrerequisite.ps1 text eol=lf',
                      '/tools/windows/advisory-prerequisite-job.yml text eol=lf',
                      '/tools/windows/publish_windows_advisory_overlay.py text eol=lf']
    for line in owned_attributes:
        if line not in attributes.splitlines(): attributes+=line+'\n'
    add('.gitattributes',attributes.encode())
    tree=run('git','write-tree',env=env)
    commit=run('git','commit-tree',tree,'-p',base,input=('test: Windows advisory prerequisite '+args.lane+'\n').encode())
    record={'base':base,'commit':commit,'tree':tree,'overlays':rows,'phase':args.phase,'mainHead':head,'mainIndexSha256':index_hash}
    (lane/'evidence/publication.json').write_text(json.dumps(record,indent=2)+'\n')
    run('git','push','origin',commit+':refs/heads/'+BRANCH)
    assert run('git','ls-remote','origin','refs/heads/'+BRANCH).split()[0]==commit
    assert run('git','rev-parse','HEAD')==head and hashlib.sha256(index.read_bytes()).hexdigest()==index_hash
    run('gh','workflow','run','windows-demo.yml','--ref',BRANCH,'-f','mode=windows-advisory-prerequisite','-f','phase='+args.phase)
    # Record exact run identity, not just a successful dispatch API result.
    for _ in range(15):
        runs=json.loads(run('gh','run','list','--workflow','windows-demo.yml','--branch',BRANCH,'--limit','10','--json','databaseId,headSha,status,url,createdAt'))
        matches=[r for r in runs if r['headSha']==commit]
        if matches:
            record['run']=matches[0]; break
        time.sleep(2)
    (lane/'evidence/publication.json').write_text(json.dumps(record,indent=2)+'\n')
    print(json.dumps({'lane':str(lane),**record},indent=2))


if __name__=='__main__': main()
