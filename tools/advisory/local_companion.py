"""Installed companion entry. No public fixture mode, tokens, broker or fallback."""
import sys,json,threading,time,asyncio
from pathlib import Path
HERE=Path(__file__).resolve().parent;sys.path.insert(0,str(HERE))
from runtime_contract import RuntimeContract,validate_contract,sha256,child_environment
from hermes_transport import check_policy,is_missing_auth
from local_install import cli

def installed(root):
 target=root/'installed';m=json.loads((target/'installation.json').read_text());
 if set(m)!={'Schema','PythonSha256','BridgeSha256','GuardSha256'} or m['Schema']!='apex-advisory-installed-v1':raise ValueError('installation not ready')
 if sha256(target/'hermes/env/Scripts/python.exe')!=m['PythonSha256'] or sha256(target/'bridge/local_companion.py')!=m['BridgeSha256'] or sha256(target/'ApexAdvisoryStoreGuard.exe')!=m['GuardSha256']:raise ValueError('installed identity mismatch')
 contract=RuntimeContract(**json.loads((target/'runtime-contract.json').read_text()));validate_contract(contract);return target,contract

def auth_status():
 # Supported credential owner, readiness only. Never inspect, persist or return credentials.
 from hermes_cli.config import load_config
 from hermes_cli.auth import resolve_codex_runtime_credentials,AuthError
 check_policy(load_config())
 try:resolve_codex_runtime_credentials(read_only=True)
 except AuthError as e:
  if is_missing_auth(e):return 'AI_LOCAL_SIGN_IN_REQUIRED'
  return 'AI_LOCAL_AUTH_UNAVAILABLE'
 return 'AI_LOCAL_AUTH_READY'

def main():
 mode=sys.argv[1];root=Path(sys.argv[2]);target,contract=installed(root);home=root/'inference-home'
 row=contract.runtimes['hermes'];sys.path.insert(0,row['source'])
 if mode=='login':cli(target,home,'auth','add','openai-codex','--browser','--label','APEX-local',timeout=240);print('AI_LOCAL_LOGIN_COMPLETED');return
 status=auth_status()
 if mode=='status':print(status);return
 if status!='AI_LOCAL_AUTH_READY':print(status);return
 if mode!='refresh':raise ValueError('closed companion mode')
 raw=sys.stdin.buffer.read(1048577)
 if len(raw)>1048576:raise ValueError('bounded canonical input required')
 from market_contract import strict_json
 data=strict_json(raw);cancel=threading.Event();control=Path(sys.argv[3])
 def watch():
  while not cancel.wait(.1):
   if control.exists():cancel.set();return
 threading.Thread(target=watch,daemon=True).start()
 from supervisor import Supervisor
 service=Supervisor(root/'reports',contract,hermes_home=home,request_ms=60000,worker_seconds=600,cancel=cancel,store_guard=target/'ApexAdvisoryStoreGuard.exe')
 try:
  result=service.refresh(data['Instrument'],data['AsOfUtc'],raw)
  print(json.dumps({'state':result.state,'reason':result.reason}))
 finally:service.close()
if __name__=='__main__':
 try:main()
 except Exception:print('AI_LOCAL_COMPANION_REFUSED');sys.exit(2)
