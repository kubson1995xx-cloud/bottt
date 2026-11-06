"""One-time offline Windows companion installation; never reads or copies auth."""
import json,hashlib,os,re,shutil,subprocess,sys
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE))
from runtime_contract import PINS,RuntimeContract,sha256,child_environment,validate_contract,validate_platform
from reports import atomic_write,encoded
STAGE='bundle-verification'
def checkpoint(name):
 global STAGE
 STAGE=name
 print('AI_INSTALL_STAGE_'+name, file=sys.stderr,flush=True)

def verify_bundle(root):
 root=Path(root);m=json.loads((root/'bundle.json').read_text(encoding='utf-8-sig'))
 if type(m)is not dict or set(m)!={'Schema','Files'} or m['Schema']!='apex-advisory-bundle-v1' or type(m['Files'])is not list or not m['Files']:raise ValueError('closed bundle required')
 seen=set()
 for row in m['Files']:
  if type(row)is not dict or set(row)!={'Path','Sha256'}:raise ValueError('closed inventory row')
  rel=row['Path'];digest=row['Sha256']
  if type(rel)is not str or not rel or rel.startswith('/') or any(x in ('','..','.') for x in rel.split('/')) or '\\' in rel or ':' in rel or rel.casefold() in seen or not re.fullmatch('[a-f0-9]{64}',digest):raise ValueError('bundle path/hash refused')
  seen.add(rel.casefold());p=root/rel
  if p.is_symlink() or not p.is_file() or sha256(p)!=digest:raise ValueError('bundle integrity refused')
 actual=set()
 for folder,dirs,files in os.walk(root,followlinks=False):
  for name in dirs+files:
   p=Path(folder)/name
   if p.is_symlink() or (os.name=='nt' and p.lstat().st_file_attributes & 0x400):raise ValueError('bundle reparse refused')
  for name in files:actual.add((Path(folder)/name).relative_to(root).as_posix().casefold())
 if actual!=seen|{'bundle.json'}:raise ValueError('bundle contains unlisted files')
 return m

def cli(install,home,*args,timeout=60,quiet=True):
 row=json.loads((install/'runtime-contract.json').read_text())['runtimes']['hermes'];env=child_environment(home,row['python'],row['source'])
 for key in ('TEMP','APPDATA','LOCALAPPDATA','HERMES_HOME'):Path(env[key]).mkdir(parents=True,exist_ok=True)
 result=subprocess.run([row['python'],'-s','-m','hermes_cli.main',*args],cwd=row['source'],env=env,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL if quiet else None,stderr=subprocess.DEVNULL if quiet else None,timeout=timeout)
 if result.returncode:raise ValueError('supported Hermes CLI refused')

def install(bundle,root):
 checkpoint('platform')
 validate_platform(sys.platform)
 if sys.version_info[:3]!=(3,14,0):raise ValueError('exact Python required')
 bundle=Path(bundle).resolve();root=Path(root).absolute();verify_bundle(bundle)
 from store import StoreLease
 # Guard is the independently published self-contained source-linked native inspector.
 checkpoint('native-root-admission');with_store=StoreLease(root,bundle/'ApexAdvisoryStoreGuard.exe')
 target=root/'installed'
 try:
  if target.exists():
   if not (target/'installation.json').is_file():raise ValueError('partial installation preserved; not ready')
   contract=RuntimeContract(**json.loads((target/'runtime-contract.json').read_text()));validate_contract(contract);return target
  with_store.directory(target);atomic_write(target/'pending.json',encoded({'Schema':'apex-advisory-install-pending-v1'}),store=with_store)
  # Venv launchers contain absolute paths: CREATE THEM AT FINAL LOCATION; never copy/rebase a venv.
  checkpoint('copy-pristine-resources');shutil.copytree(bundle/'python-base',target/'python-base')
  shutil.copytree(bundle/'sources',target/'sources');shutil.copytree(bundle/'bridge',target/'bridge')
  shutil.copyfile(bundle/'ApexAdvisoryStoreGuard.exe',target/'ApexAdvisoryStoreGuard.exe')
  rows={};base=target/'python-base/python.exe';home=root/'inference-home';with_store.directory(home)
  for name,pin in PINS.items():
   checkpoint('create-'+name)
   source=target/'sources'/name;env=child_environment(home,base);venv=target/name/'env';venv.parent.mkdir()
   for key in ('TEMP','APPDATA','LOCALAPPDATA'):Path(env[key]).mkdir(parents=True,exist_ok=True)
   def run(args):
    r=subprocess.run([str(a) for a in args],cwd=target,env=env,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=600)
    if r.returncode:raise ValueError('offline pinned runtime install failed')
   run([base,'-I','-m','venv',venv]);python=venv/'Scripts/python.exe';lock=target/name/'requirements.lock';shutil.copyfile(bundle/'locks'/(name+'-windows-py314.lock'),lock)
   checkpoint('offline-pip-'+name);run([python,'-I','-m','pip','--isolated','install','--no-index','--find-links',bundle/'wheels'/name,'--only-binary=:all:','--require-hashes','-r',lock]);run([python,'-I','-m','pip','check'])
   rows[name]={'source':str(source),'commit':pin['commit'],'python':str(python),'python_sha256':sha256(python),'prefix':str(venv),'lock':str(lock),'lock_sha256':sha256(lock)}
  checkpoint('validate-installed-contract');contract=RuntimeContract(rows);validate_contract(contract)
  from dataclasses import asdict
  atomic_write(target/'runtime-contract.json',encoded(asdict(contract)),store=with_store)
  for key,value in [('model.default','gpt-6.1-sol'),('model.provider','openai-codex'),('auth.adopt_external_logins','false'),('security.allow_lazy_installs','false'),('fallback_providers','[]'),('fallback_model','null')]:
   checkpoint('supported-config-'+key);cli(target,home,'config','set',key,value)
  checkpoint('native-final-scan');with_store.scan()
  atomic_write(target/'installation.json',encoded({'Schema':'apex-advisory-installed-v1','PythonSha256':sha256(target/'hermes/env/Scripts/python.exe'),'BridgeSha256':sha256(target/'bridge/local_companion.py'),'GuardSha256':sha256(target/'ApexAdvisoryStoreGuard.exe')}),store=with_store)
  (target/'pending.json').unlink();checkpoint('ready');return target
 finally:with_store.close()
if __name__=='__main__':
 try:install(Path(sys.argv[1]),Path(sys.argv[2]));print('AI_INSTALLED_LOCAL_SIGN_IN_REQUIRED')
 except Exception as error:
  print('AI_INSTALL_FAILURE_'+STAGE+'_'+type(error).__name__,file=sys.stderr,flush=True)
  print('AI_INSTALL_REFUSED_PARTIAL_PRESERVED');sys.exit(2)
