"""Supported operator-only Windows login / actual-inference smoke entry.

Never run authentication in CI. No token reads/copies; the pinned Hermes CLI owns
its local store. This is a developer prerequisite entry, not the Task 5 wizard.
"""
import argparse
import asyncio
import json
import os
from pathlib import Path
import subprocess
import sys
from runtime_contract import RuntimeContract, child_environment, validate_contract, MODEL, PROVIDER


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--contract',type=Path,required=True)
    parser.add_argument('--home',type=Path,required=True,help='Dedicated local Windows Hermes home; never a copied Linux auth store')
    action=parser.add_mutually_exclusive_group(required=True)
    action.add_argument('--login',action='store_true')
    action.add_argument('--infer',action='store_true')
    parser.add_argument('--seconds',type=float,default=45)
    args=parser.parse_args()
    contract=RuntimeContract(**json.loads(args.contract.read_text(encoding='utf-8')))
    validate_contract(contract)  # Refuses Linux before writing any home/config.
    if os.environ.get('CI'): raise ValueError('operator subscription smoke is forbidden in CI')
    row=contract.runtimes['hermes']; home=args.home.resolve()
    # All children use only explicit OS/runtime paths; credentials remain in Hermes.
    env=child_environment(home.parent/'operator-runtime',row['python'],row['source'])
    env['HERMES_HOME']=str(home)
    for key in ('TEMP','APPDATA','LOCALAPPDATA','HERMES_HOME'): Path(env[key]).mkdir(parents=True,exist_ok=True)
    base=[row['python'],'-X','utf8','-m','hermes_cli.main','--profile','default']
    if args.login:
        # Never overwrite an existing profile. The first initialization uses the
        # official configuration CLI, not hand-written operator YAML.
        if not (home/'config.yaml').exists():
            for key,value in [('model.default',MODEL),('model.provider',PROVIDER),
                              ('fallback_providers','[]'),('fallback_model','null'),
                              ('auth.adopt_external_logins','false'),('security.allow_lazy_installs','false')]:
                subprocess.run([*base,'config','set',key,value],env=env,cwd=row['source'],check=True,timeout=30)
        # Browser-based authorization is interactive and entirely Hermes-owned.
        return subprocess.run([*base,'auth','add',PROVIDER,'--browser'],env=env,cwd=row['source'],timeout=300).returncode
    # Re-exec under the actual trusted Hermes environment, without importing its
    # credential management into the caller/third-party environments.
    child=[row['python'],'-X','utf8',str(Path(__file__).with_name('smoke_request.py')),str(args.seconds)]
    return subprocess.run(child,env=env,cwd=row['source'],timeout=args.seconds+10).returncode


if __name__=='__main__': sys.exit(main())
