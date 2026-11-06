"""Native prerequisite supervisor. No trading clients or financial stores."""
from dataclasses import dataclass
import json
from pathlib import Path
import subprocess
import sys
from runtime_contract import RuntimeContract, child_environment, validate_contract


@dataclass(frozen=True)
class ProbeResult:
    runtimes: dict
    prerequisite_pass: bool
    inference_accepted: bool = False


def validate_observation(name, expected, observed):
    if (Path(observed['python']).resolve() != Path(expected['python']).resolve() or
        observed['prefix'] != expected['prefix'] or observed['platform'] != 'win32' or
        observed['python_version'] != [3,14,0] or observed.get('import_ok') is not True):
        raise ValueError('observed runtime mismatch')
    if name != 'hermes':
        major = int(observed['pandas_version'].split('.')[0])
        if (name == 'vibe-trading' and major != 2) or (name == 'tradingagents' and major < 3):
            raise ValueError('conflicting pandas runtime')
        if observed.get('computation_ok') is not True or observed.get('synthetic_public_input') is not True:
            raise ValueError('native computation missing')
    elif not (observed.get('bootstrap_ok') is True and observed.get('no_auth_refused') is True
              and observed.get('auth_code') == 'codex_auth_missing' and observed.get('inference_accepted') is False):
        raise ValueError('Hermes bootstrap/no-auth proof incomplete')
    return observed


def probe_runtime(contract: RuntimeContract, evidence_root=None) -> ProbeResult:
    validate_contract(contract)
    root = Path(evidence_root).resolve()
    observations = {}
    for name, row in contract.runtimes.items():
        home = root/'build'/name/'probe-home'
        home.mkdir(parents=True, exist_ok=False)
        env = child_environment(home, row['python'], row['source'] if name == 'hermes' else None)
        for key in ('TEMP','APPDATA','LOCALAPPDATA','HERMES_HOME'): Path(env[key]).mkdir(parents=True, exist_ok=True)
        # Fresh explicit profile; no fallback, token adoption, hooks or lazy installs.
        if name == 'hermes':
            (Path(env['HERMES_HOME'])/'config.yaml').write_text(
                'model:\n  default: gpt-6.1-sol\n  provider: openai-codex\n'
                'auth:\n  adopt_external_logins: false\nsecurity:\n  allow_lazy_installs: false\n'
                'fallback_models: []\n', encoding='utf-8')
        output = root/'evidence'/(name+'-probe.json')
        command = [row['python'],'-I',str(Path(__file__).with_name('native_probe.py')),
                   name,str(root/'closure/runtime-contract.json'),str(output)]
        result = subprocess.run(command,cwd=home,env=env,capture_output=True,text=True,encoding='utf-8',timeout=150)
        (root/'evidence'/(name+'-stdout.log')).write_text(result.stdout, encoding='utf-8')
        (root/'evidence'/(name+'-stderr.log')).write_text(result.stderr, encoding='utf-8')
        (root/'evidence'/(name+'-exit.json')).write_text(json.dumps({'exitCode':result.returncode,'command':command}))
        if result.returncode != 0: raise RuntimeError(name+' native probe failed; see retained logs')
        observed = json.loads(output.read_text())
        observations[name] = validate_observation(name,row,observed)
    return ProbeResult(observations, True)
