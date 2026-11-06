"""Source-bound native Windows advisory runtime admission (no financial authority)."""
import ctypes
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import sys

PINS = json.loads((Path(__file__).parent/'upstream-pins.json').read_text())
MODEL = 'gpt-6.1-sol'
PROVIDER = 'openai-codex'


@dataclass(frozen=True)
class RuntimeContract:
    runtimes: dict
    model: str = MODEL
    provider: str = PROVIDER


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def validate_contract(contract, *, platform=None):
    validate_platform(sys.platform if platform is None else platform)
    if (contract.model, contract.provider) != (MODEL, PROVIDER):
        raise ValueError('unsupported inference route')
    if set(contract.runtimes) != set(PINS):
        raise ValueError('runtime set mismatch')
    executables = [Path(row['python']).resolve() for row in contract.runtimes.values()]
    if len(set(executables)) != len(PINS) or len({p.parent for p in executables}) != len(PINS):
        raise ValueError('conflicting environments')
    for name, row in contract.runtimes.items():
        if row['commit'] != PINS[name]['commit']:
            raise ValueError('source revision mismatch')
        if not Path(row['python']).is_absolute() or sha256(row['python']) != row['python_sha256']:
            raise ValueError('runtime hash mismatch')
        if sha256(row['lock']) != row['lock_sha256']:
            raise ValueError('dependency hash mismatch')
        lines = Path(row['lock']).read_text().splitlines()
        if not lines or any(not re.fullmatch(r'[A-Za-z0-9_.-]+==[^\s]+ --hash=sha256:[a-f0-9]{64}', s) for s in lines):
            raise ValueError('unlocked dependency')
        for rel, digest in PINS[name]['files'].items():
            if sha256(Path(row['source'])/rel) != digest:
                raise ValueError('source hash mismatch')
    return contract


def child_environment(home, runtime_python, source=None):
    """Explicit process allowlist, NOT a filesystem/network sandbox."""
    home = Path(home).resolve()
    if os.name == 'nt':
        buffer = ctypes.create_unicode_buffer(32768)
        if not ctypes.windll.kernel32.GetWindowsDirectoryW(buffer, len(buffer)):
            raise OSError('cannot resolve Windows directory')
        windows = buffer.value
    else:  # Offline tests only; real probes always validate native platform.
        windows = '/non-windows-fixture'
    env = {'SYSTEMROOT': windows, 'WINDIR': windows, 'COMSPEC': str(Path(windows)/'System32/cmd.exe'),
           'PATH': os.pathsep.join([str(Path(runtime_python).parent), str(Path(windows)/'System32'), windows]),
           'HOME': str(home), 'USERPROFILE': str(home), 'HERMES_HOME': str(home/'hermes'),
           'APPDATA': str(home/'AppData/Roaming'), 'LOCALAPPDATA': str(home/'AppData/Local'),
           'TEMP': str(home/'tmp'), 'TMP': str(home/'tmp'), 'PYTHONUTF8':'1',
           'PYTHONIOENCODING':'utf-8', 'PYTHONNOUSERSITE':'1', 'PIP_CONFIG_FILE':os.devnull,
           'PIP_DISABLE_PIP_VERSION_CHECK':'1', 'NO_COLOR':'1'}
    if source is not None: env['PYTHONPATH'] = str(Path(source).resolve())
    return env


def validate_platform(platform):
    if platform != 'win32':
        raise ValueError('native Windows required')
