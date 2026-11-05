"""Verify immutable checkout inputs and exact known frontend regeneration."""
from pathlib import Path
import hashlib,json,sys

def digest(b):return hashlib.sha256(b).hexdigest()
def at(root,name):
 p=root
 for part in name.replace('\\','/').split('/'):
  exact=p/part
  if exact.exists():p=exact;continue
  matches=[v for v in p.iterdir() if v.name.casefold()==part.casefold()]
  assert len(matches)==1
  p=matches[0]
 return p
def closed(p):
 def pairs(items):
  d={}
  for k,v in items:
   if k in d:raise ValueError('DUPLICATE')
   d[k]=v
  return d
 return json.loads(p.read_text(encoding='utf-8-sig'),object_pairs_hook=pairs)
def verify(e,repo):
 before=closed(e/'source-inputs-before-build.json');generated_path=e/'generated-assets-before-compile.json'
 generated={r['path']:r for r in closed(generated_path)} if generated_path.exists() else {}
 prefix='src/Apex.Desktop/wwwroot/naval/';changed=[];reasons={}
 for row in before:
  name=row['path'];old=at(e/'source',name).read_bytes();current=at(repo,name).read_bytes()
  assert len(old)==row['bytes'] and digest(old)==row['sha256']
  if digest(current)==row['sha256']:continue
  changed.append(name)
  assert name in generated and name.startswith(prefix)
  assert len(current)==generated[name]['bytes'] and digest(current)==generated[name]['sha256']
  if name!=prefix+'build-manifest.json':
   assert current.replace(b'\r\n',b'\n')==old.replace(b'\r\n',b'\n')
   reasons[name]='exact CRLF-normalized bytes'
   continue
  a=closed(at(e/'source',name));b=closed(at(repo,name))
  assert set(a)==set(b)=={'schema','artifacts'} and a['schema']==b['schema']=='naval-build-v1'
  assert len(a['artifacts'])==len(b['artifacts'])==4
  for previous,actual in zip(a['artifacts'],b['artifacts']):
   assert set(previous)==set(actual)=={'path','bytes','sha256'} and previous['path']==actual['path']
   asset=previous['path'];assert asset=='index.html' or asset.startswith('assets/') and '/' not in asset[7:]
   old_bytes=at(e/'source',prefix+asset).read_bytes().replace(b'\r\n',b'\n');new_bytes=at(repo,prefix+asset).read_bytes()
   assert previous['bytes']==len(old_bytes) and previous['sha256']==digest(old_bytes)
   assert actual['bytes']==len(new_bytes) and actual['sha256']==digest(new_bytes)
   if asset!='index.html':assert previous==actual
   else:assert old_bytes==new_bytes.replace(b'\r\n',b'\n')
  reasons[name]='exact artifact bindings; only index.html producer EOL binding may differ'
 return {'unchanged':not changed,'changedPaths':changed,'onlyValidatedGeneratedChanges':bool(changed),'admittedReasons':reasons,'unexpectedChanges':[]}
if __name__=='__main__':
 e,repo=map(Path,sys.argv[1:3])
 try:result=verify(e,repo)
 except Exception:
  (e/'source-after-tests.json').write_text(json.dumps({'unchanged':False,'verification':'SOURCE_INPUTS_CHANGED'})+'\n');print('SOURCE_INPUTS_CHANGED');raise SystemExit(1)
 (e/'source-after-tests.json').write_text(json.dumps(result,indent=2)+'\n');print('SOURCE_INPUTS_AND_GENERATED_BINDINGS_VERIFIED')
