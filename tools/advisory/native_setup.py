"""Native-only genuine wheel resolution -> hash lock -> offline fresh install.

No upstream build/install hooks are executed. Source checkouts are imported at
exact commits; dependency wheels only, with retained PyPI report, URLs, archive
SHA256, wheel hook inventory, actual installed bytes and interpreter identities.
"""
import argparse
from dataclasses import asdict
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tomllib
import urllib.parse
import urllib.request
import zipfile
from runtime_contract import PINS, RuntimeContract, child_environment, sha256, validate_platform
from prerequisite import probe_runtime

HERE=Path(__file__).resolve().parent
# Small Vibe backend/AgentLoop/indicator dependency slice; no UI, MCP server,
# PDF renderer, exchange setup or provider login. Each selected constraint is
# read verbatim from the pinned upstream manifest, not invented/faked metadata.
VIBE_DEPS={'rich','pyyaml','langchain','langchain-core','langchain-openai','langgraph',
           'langgraph-checkpoint','python-dotenv','httpx','h2','defusedxml','oauth-cli-kit',
           'pandas','numpy','scipy','pydantic','requests','cryptography','ccxt','yfinance'}


def command(args, log, *, env, cwd):
    with Path(log).open('w',encoding='utf-8') as out:
        result=subprocess.run([str(a) for a in args],cwd=cwd,env=env,stdout=out,stderr=subprocess.STDOUT,timeout=600)
    if result.returncode: raise RuntimeError(f'command exit {result.returncode}; see {log}')


def prepare(root):
    validate_platform(sys.platform)
    if sys.version_info[:3]!=(3,14,0): raise ValueError('exact Python 3.14.0 required')
    git=shutil.which('git')
    if not git: raise ValueError('public source acquisition requires Git')
    rows={}
    for name,pin in PINS.items():
        build=root/'build'/name; build.mkdir()
        source=root/'source'/'upstream'/name; source.mkdir(parents=True)
        home=build/'setup-home'; home.mkdir()
        env=child_environment(home,sys.executable)
        for key in ('TEMP','APPDATA','LOCALAPPDATA','HERMES_HOME'): Path(env[key]).mkdir(parents=True,exist_ok=True)
        env.update({'GIT_CONFIG_NOSYSTEM':'1','GIT_CONFIG_GLOBAL':os.devnull,'GIT_TERMINAL_PROMPT':'0'})
        for i,argv in enumerate([[git,'init',str(source)],
                                [git,'-C',str(source),'config','core.autocrlf','false'],
                                [git,'-C',str(source),'fetch','--depth','1',pin['repository'],pin['commit']],
                                [git,'-C',str(source),'checkout','--detach','FETCH_HEAD']]):
            command(argv,build/f'git-{i}.log',env=env,cwd=home)
        actual=subprocess.check_output([git,'-C',str(source),'rev-parse','HEAD'],env=env).decode().strip()
        tree=subprocess.check_output([git,'-C',str(source),'rev-parse','HEAD^{tree}'],env=env).decode().strip()
        if actual!=pin['commit'] or tree!=pin['tree']: raise ValueError('upstream git identity mismatch')
        for rel,digest in pin['files'].items():
            if sha256(source/rel)!=digest: raise ValueError('pinned source bytes mismatch: '+name+'/'+rel)
        tracked=subprocess.check_output([git,'-C',str(source),'ls-files','-z'],env=env).decode().split('\0')
        mapping=[{'path':rel,'sha256':sha256(source/rel)} for rel in tracked if rel and (source/rel).is_file()]
        (build/'source-map-before.json').write_text(json.dumps(mapping,indent=2))
        manifest=tomllib.loads((source/'pyproject.toml').read_text(encoding='utf-8'))
        deps=manifest['project']['dependencies']
        if name=='vibe-trading':
            deps=[d for d in deps if re.split(r'[<>=!\[; @]',d,maxsplit=1)[0].lower() in VIBE_DEPS]
            if len(deps)!=len(VIBE_DEPS): raise ValueError('Vibe dependency manifest drift')
        dependencies=build/'dependencies.in';dependencies.write_text('\n'.join(deps)+'\n')
        runtime=root/'closure'/name/'env'
        command([sys.executable,'-m','venv',runtime],build/'venv.log',env=env,cwd=home)
        python=runtime/'Scripts/python.exe'
        report=build/'resolution.json'
        frozen=HERE/'locks'/(name+'-windows-py314.lock')
        lock=root/'closure'/name/'requirements.lock'
        wheels=root/'closure'/name/'wheels'; wheels.mkdir()
        if frozen.exists():
            shutil.copyfile(frozen,lock)
            command([python,'-m','pip','--isolated','download','--index-url','https://pypi.org/simple',
                     '--only-binary=:all:','--require-hashes','-r',lock,'--dest',wheels],build/'download-frozen.log',env=env,cwd=home)
        else:
            command([python,'-m','pip','--isolated','install','--dry-run','--ignore-installed','--report',report,
                     '--only-binary=:all:','--index-url','https://pypi.org/simple','-r',dependencies],build/'resolution.log',env=env,cwd=home)
            resolved=json.loads(report.read_text(encoding='utf-8'))['install']; lines=[]
            for package in resolved:
                download=package['download_info'];url=download['url'];parsed=urllib.parse.urlsplit(url)
                filename=Path(urllib.parse.unquote(parsed.path)).name
                if parsed.scheme!='https' or parsed.hostname!='files.pythonhosted.org' or not filename.endswith('.whl'):
                    raise ValueError('unreviewed dependency build/source URL')
                digest=download['archive_info']['hashes']['sha256']
                with urllib.request.urlopen(url,timeout=60) as response: data=response.read()
                if hashlib.sha256(data).hexdigest()!=digest: raise ValueError('PyPI wheel integrity failure')
                (wheels/filename).write_bytes(data)
                lines.append(f"{package['metadata']['name']}=={package['metadata']['version']} --hash=sha256:{digest}")
            lock.write_text('\n'.join(sorted(lines,key=str.lower))+'\n')
        # Inspect executable .pth and wheel script entries before installation.
        hooks=[]
        for wheel in wheels.glob('*.whl'):
            with zipfile.ZipFile(wheel) as archive:
                entries=[s for s in archive.namelist() if s.endswith('.pth') or '.data/scripts/' in s]
                hooks.append({'file':wheel.name,'sha256':sha256(wheel),'entries':{s:archive.read(s).decode('utf-8',errors='replace')[:16000] for s in entries}})
        (build/'wheel-hooks-inspected.json').write_text(json.dumps(hooks,indent=2))
        command([python,'-m','pip','--isolated','install','--no-index','--find-links',wheels,
                 '--only-binary=:all:','--require-hashes','--report',build/'installed-report.json','-r',lock],build/'offline-install.log',env=env,cwd=home)
        command([python,'-m','pip','check'],build/'pip-check.log',env=env,cwd=home)
        rows[name]={'source':str(source),'commit':pin['commit'],'python':str(python.resolve()),
                    'python_sha256':sha256(python),'prefix':str(runtime.resolve()),'lock':str(lock),'lock_sha256':sha256(lock)}
        # Capture actual dependency/interpreter payload before any native probe.
        closure=[{'path':str(p.relative_to(root)),'sha256':sha256(p)} for p in runtime.rglob('*') if p.is_file() and '__pycache__' not in str(p)]
        (build/'runtime-map-before.json').write_text(json.dumps(closure,indent=2))
    contract=RuntimeContract(rows)
    (root/'closure/runtime-contract.json').write_text(json.dumps(asdict(contract),indent=2))
    return contract


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--root',type=Path,required=True);args=parser.parse_args()
    root=args.root.resolve()
    try:
        contract=prepare(root)
        result=probe_runtime(contract,root)
        (root/'evidence/result.json').write_text(json.dumps(asdict(result),indent=2))
        print('NATIVE_PREREQUISITE_PASS; REAL_LOCAL_INFERENCE_PENDING')
    except Exception as error:
        (root/'evidence/failure.json').write_text(json.dumps({'type':type(error).__name__,'reason':str(error),'prerequisite_pass':False,'inference_accepted':False},indent=2))
        raise


if __name__=='__main__': main()
