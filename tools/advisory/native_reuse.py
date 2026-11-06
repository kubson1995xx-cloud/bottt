"""Fresh native venvs from accepted Task1 REAL Windows wheel archives only.

No copied Linux/Windows venv, resolver, upstream mutation or fabricated hashes.
Read accepted source-map and locks before installation. This does not rerun or
reclaim the accepted Task1 bootstrap/auth gate.
"""
from pathlib import Path
import argparse,json,shutil,sys
from dataclasses import asdict
from runtime_contract import PINS,RuntimeContract,sha256,validate_platform,child_environment,validate_contract
from native_setup import command,HERE

def reuse(root,prior):
    validate_platform(sys.platform)
    if sys.version_info[:3]!=(3,14,0):raise ValueError('Python 3.14.0 required')
    rows={}
    for name,pin in PINS.items():
        build=root/'build'/name;build.mkdir(parents=True)
        source=prior/'source/upstream'/name
        mapping=json.loads((prior/'build'/name/'source-map-before.json').read_text())
        for row in mapping:
            if sha256(source/row['path'])!=row['sha256']:raise ValueError('accepted source changed')
        lock=HERE/'locks'/(name+'-windows-py314.lock')
        if sha256(lock)!=sha256(prior/'closure'/name/'requirements.lock'):raise ValueError('accepted lock differs')
        wheels=prior/'closure'/name/'wheels';home=build/'home';home.mkdir()
        env=child_environment(home,sys.executable)
        for key in ('TEMP','APPDATA','LOCALAPPDATA'):Path(env[key]).mkdir(parents=True,exist_ok=True)
        runtime=root/'closure'/name/'env';runtime.parent.mkdir(parents=True)
        command([sys.executable,'-m','venv',runtime],build/'venv.log',env=env,cwd=home)
        python=runtime/'Scripts/python.exe'
        command([python,'-m','pip','--isolated','install','--no-index','--find-links',wheels,'--only-binary=:all:','--require-hashes','--report',build/'install.json','-r',lock],build/'offline-install.log',env=env,cwd=home)
        command([python,'-m','pip','check'],build/'pip-check.log',env=env,cwd=home)
        rows[name]={'source':str(source.resolve()),'commit':pin['commit'],'python':str(python.resolve()),'python_sha256':sha256(python),'prefix':str(runtime.resolve()),'lock':str(lock.resolve()),'lock_sha256':sha256(lock)}
        (build/'consumed-prior.json').write_text(json.dumps({'accepted_run':42236978414,'source':str(source),'source_map':mapping,'wheels':[{'path':str(p),'sha256':sha256(p)} for p in wheels.glob('*.whl')],'lock_sha256':sha256(lock)},indent=2))
    contract=RuntimeContract(rows);validate_contract(contract)
    (root/'closure/runtime-contract.json').write_text(json.dumps(asdict(contract),indent=2))
    return contract
if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--root',required=True,type=Path);parser.add_argument('--prior',required=True,type=Path);args=parser.parse_args()
    reuse(args.root.resolve(),args.prior.resolve())
