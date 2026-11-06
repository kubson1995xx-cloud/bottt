"""Retain contemporaneous inputs and complete command output, never overwrite."""
import hashlib,json,os,subprocess,sys
from pathlib import Path

def run(lane,command):
    repo=Path(__file__).resolve().parents[2];root=repo/'artifacts/operational-windows-advisory'/lane
    root.mkdir(parents=True,exist_ok=False)
    files=list((repo/'tools/advisory').rglob('*'))+list((repo/'src/Apex.Core').rglob('*'))+list((repo/'src/Apex.Shared').rglob('*'))
    files += [repo/'src/Apex.Desktop/Okx/OkxPublicMarketFeed.cs',repo/'src/Apex.Desktop/Okx/OkxAdvisoryCanonicalData.cs',repo/'Directory.Build.props',repo/'Directory.Build.targets',repo/'global.json']
    mapping=[]
    for p in files:
        if not p.is_file() or set(p.parts)&{'bin','obj','__pycache__'}:continue
        rel=p.relative_to(repo);out=root/'source'/rel;out.parent.mkdir(parents=True,exist_ok=True);out.write_bytes(p.read_bytes())
        mapping.append({'path':str(rel),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()})
    (root/'input-map.json').write_text(json.dumps(mapping,indent=2))
    env=dict(os.environ,ADVISORY_PARITY_EVIDENCE=str(root/'parity'))
    result=subprocess.run(command,cwd=repo,env=env,capture_output=True,text=True)
    (root/'tests.log').write_text(result.stdout+result.stderr)
    (root/'exit.json').write_text(json.dumps({'command':command,'exit':result.returncode}))
    print(result.stdout+result.stderr);return result.returncode
if __name__=='__main__':sys.exit(run(sys.argv[1],sys.argv[2:]))
