"""Build relocatable installation RESOURCES from accepted Windows bytes; never copy venvs."""
import argparse,json,shutil,hashlib,sys
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE))
from runtime_contract import PINS,sha256

def build(prior,out,guard):
 if out.exists():raise ValueError('fresh bundle path required')
 out.mkdir(parents=True)
 # Check the genuine retained interpreter before copying it. No synthesized closure.
 base=prior/'closure/python-base';mapping=json.loads((prior/'build/python-base-map-before.json').read_text())['files']
 for row in mapping:
  if sha256(base/row['path'])!=row['sha256']:raise ValueError('accepted Python closure changed')
 shutil.copytree(base,out/'python-base')
 for name,pin in PINS.items():
  source=prior/'source/upstream'/name;dest=out/'sources'/name;dest.mkdir(parents=True)
  mapping=json.loads((prior/'build'/name/'source-map-before.json').read_text())
  for row in mapping:
   p=source/row['path'];rel=Path(row['path'])
   if rel.is_absolute() or '..' in rel.parts or sha256(p)!=row['sha256']:raise ValueError('accepted source mismatch')
   target=dest/rel;target.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(p,target)
  for rel,digest in pin['files'].items():
   if sha256(dest/rel)!=digest:raise ValueError('pinned upstream source mismatch')
  lock=HERE/'locks'/(name+'-windows-py314.lock')
  if sha256(lock)!=sha256(prior/'closure'/name/'requirements.lock'):raise ValueError('accepted hash lock mismatch')
  (out/'locks').mkdir(exist_ok=True);shutil.copyfile(lock,out/'locks'/lock.name)
  wheels=out/'wheels'/name;wheels.mkdir(parents=True)
  expected={line.rsplit('sha256:',1)[1] for line in lock.read_text().splitlines() if line}
  actual=set()
  for wheel in (prior/'closure'/name/'wheels').glob('*.whl'):
   digest=sha256(wheel)
   if digest not in expected:raise ValueError('unlisted wheel')
   actual.add(digest);shutil.copyfile(wheel,wheels/wheel.name)
  if actual!=expected:raise ValueError('incomplete locked wheels')
 bridge=out/'bridge';bridge.mkdir()
 for p in HERE.glob('*.py'):shutil.copyfile(p,bridge/p.name)
 shutil.copyfile(HERE/'upstream-pins.json',bridge/'upstream-pins.json');shutil.copytree(HERE/'vendor',bridge/'vendor',ignore=shutil.ignore_patterns('__pycache__'))
 shutil.copyfile(guard,out/'ApexAdvisoryStoreGuard.exe')
 # Include source/license/lock provenance. All file hashes derive from actual retained bytes.
 (out/'provenance.json').write_text(json.dumps({'Schema':'apex-advisory-resource-provenance-v1','AcceptedRun':42236978414,'Pins':PINS,'VenvsCopied':False,'AuthCopied':False,'Scope':'tool-free Hermes closure; only incompatible unrelated browser-harness omitted'},indent=2))
 rows=[{'Path':p.relative_to(out).as_posix(),'Sha256':sha256(p)} for p in sorted(out.rglob('*')) if p.is_file()]
 (out/'bundle.json').write_text(json.dumps({'Schema':'apex-advisory-bundle-v1','Files':rows},separators=(',',':')))
 print(json.dumps({'files':len(rows),'bytes':sum(p.stat().st_size for p in out.rglob('*') if p.is_file()),'manifestSha256':sha256(out/'bundle.json')}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--prior',type=Path,required=True);p.add_argument('--out',type=Path,required=True);p.add_argument('--guard',type=Path,required=True);a=p.parse_args();build(a.prior,a.out,a.guard)
