"""Offline contract tests; no real auth, broker, network or gateway invoked."""
import dataclasses
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
try:
    import runtime_contract as runtime
except ModuleNotFoundError:
    runtime = None


class ContractTests(unittest.TestCase):
    def test_wrong_platform_refuses_before_launch(self):
        # Missing old boundary is deliberately a no-op, not an import/setup RED.
        validator = runtime.validate_platform if runtime else lambda value: None
        with self.assertRaisesRegex(ValueError, 'native Windows required'):
            validator('linux')

    def fixture(self, root):
        from types import SimpleNamespace
        identities = {}
        pins = {}
        for name in ('vibe-trading', 'tradingagents', 'hermes'):
            source = root / name / 'source'; source.mkdir(parents=True)
            (source / 'entry.py').write_text('# synthetic source identity\n')
            executable = root / name / 'env' / 'python.exe'; executable.parent.mkdir()
            executable.write_bytes(name.encode())
            lock = root / name / 'requirements.lock'
            lock.write_text('synthetic==1.0 --hash=sha256:' + 'a' * 64 + '\n')
            pins[name] = {'commit': 'b' * 40, 'files': {'entry.py': hashlib.sha256((source/'entry.py').read_bytes()).hexdigest()}}
            identities[name] = {'source': str(source), 'commit': 'b'*40,
                                'python': str(executable), 'python_sha256': hashlib.sha256(executable.read_bytes()).hexdigest(),
                                'lock': str(lock), 'lock_sha256': hashlib.sha256(lock.read_bytes()).hexdigest()}
        return SimpleNamespace(runtimes=identities, model='gpt-6.1-sol', provider='openai-codex'), pins

    def validate(self, contract, pins):
        validator = getattr(runtime, 'validate_contract', lambda *a, **kw: contract)
        with patch.object(runtime, 'PINS', pins, create=True):
            return validator(contract, platform='win32')

    def test_valid_contract(self):
        with tempfile.TemporaryDirectory() as d:
            contract, pins = self.fixture(Path(d))
            self.assertIsNotNone(self.validate(contract, pins))

    def test_runtime_source_hash_and_environment_negatives(self):
        for mutation, reason in [('python', 'runtime hash'), ('source', 'source hash'),
                                 ('commit', 'source revision'), ('lock', 'dependency hash'),
                                 ('conflict', 'conflicting environments'), ('route', 'unsupported inference route')]:
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as d:
                contract, pins = self.fixture(Path(d))
                row = contract.runtimes['vibe-trading']
                if mutation == 'python': Path(row['python']).write_bytes(b'changed')
                if mutation == 'source': (Path(row['source'])/'entry.py').write_bytes(b'changed')
                if mutation == 'commit': row['commit'] = 'c'*40
                if mutation == 'lock': Path(row['lock']).write_text('unlocked')
                if mutation == 'conflict': contract.runtimes['tradingagents']['python'] = row['python']; contract.runtimes['tradingagents']['python_sha256'] = row['python_sha256']
                if mutation == 'route': contract.provider = 'other'
                with self.assertRaisesRegex(ValueError, reason): self.validate(contract, pins)


if __name__ == '__main__':
    unittest.main(verbosity=2)
