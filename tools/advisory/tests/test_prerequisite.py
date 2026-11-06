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

    def test_no_secret_or_environment_inheritance(self):
        sentinel = {'OKX_SECRET':'not-a-real-secret', 'OPENAI_API_KEY':'not-a-real-key',
                    'PYTHONPATH':'untrusted', 'HERMES_TUI_GATEWAY_URL':'ws://untrusted',
                    'HTTP_PROXY':'http://untrusted', 'PATH':'untrusted', 'HOME':'untrusted'}
        builder = getattr(runtime, 'child_environment', lambda *a: dict(os.environ))
        with patch.dict(os.environ, sentinel), tempfile.TemporaryDirectory() as d:
            env = builder(Path(d), Path(sys.executable))
            for key in ('OKX_SECRET','OPENAI_API_KEY','PYTHONPATH','HERMES_TUI_GATEWAY_URL','HTTP_PROXY'):
                self.assertNotIn(key, env)
            self.assertNotEqual(env['PATH'], 'untrusted')
            self.assertEqual(env['HOME'], str(Path(d).resolve()))
            self.assertIn('SYSTEMROOT', env)

    def test_child_process_does_not_inherit_sentinel_keys(self):
        import subprocess
        builder=runtime.child_environment
        with tempfile.TemporaryDirectory() as d, patch.dict(os.environ, {'OKX_SECRET':'synthetic-sentinel','OPENAI_API_KEY':'synthetic-sentinel','HERMES_TUI_GATEWAY_URL':'ws://synthetic'}):
            env=builder(Path(d),Path(sys.executable))
            # Executes real native stdlib/socket loading, not just dictionary checks.
            code="import os,json,socket; s=socket.socket();s.close();print(json.dumps(sorted(os.environ)))"
            child=subprocess.run([sys.executable,'-I','-c',code],env=env,capture_output=True,text=True,timeout=10,check=True)
            keys=json.loads(child.stdout)
            for key in ('OKX_SECRET','OPENAI_API_KEY','HERMES_TUI_GATEWAY_URL'): self.assertNotIn(key,keys)


class TransportTests(unittest.TestCase):
    def transport(self):
        from types import SimpleNamespace
        try: import hermes_transport; return hermes_transport
        except ModuleNotFoundError: return SimpleNamespace()

    def info(self):
        return {'model':'gpt-6.1-sol','provider':'openai-codex','enabled_toolsets':[], 'tool_count':0,
                'launch_request':{'safe_mode':True,'toolsets':[],'max_turns':1}}

    def test_unsupported_route_or_tool_authority_refused(self):
        check = getattr(self.transport(), 'check_route', lambda info: info)
        self.assertEqual(check(self.info())['model'], 'gpt-6.1-sol')
        for key, value in [('model','wrong'),('provider','wrong'),('enabled_toolsets',['terminal']),('tool_count',1),('launch_request',{})]:
            with self.subTest(key=key), self.assertRaises(ValueError): check({**self.info(), key:value})

    def test_missing_auth_is_typed_and_no_other_failure_is_auth_pass(self):
        from types import SimpleNamespace
        check = getattr(self.transport(), 'is_missing_auth', lambda error: False)
        self.assertTrue(check(SimpleNamespace(provider='openai-codex',code='codex_auth_missing',relogin_required=True)))
        self.assertFalse(check(SimpleNamespace(provider='other',code='codex_auth_missing',relogin_required=True)))
        self.assertFalse(check(RuntimeError('something else')))

    def test_no_fallback_or_custom_transport_policy(self):
        transport=self.transport()
        check=getattr(transport,'check_policy',lambda c:c)
        good={'model':{'default':'gpt-6.1-sol','provider':'openai-codex'},'fallback_providers':[],'fallback_model':None}
        check(good)
        for bad in [{**good,'fallback_providers':[{'provider':'other'}]}, {**good,'fallback_model':{'model':'other'}},
                    {**good,'model':{**good['model'],'base_url':'http://other'}},
                    {**good,'model':{**good['model'],'openai_runtime':'codex_app_server'}}]:
            with self.assertRaises(ValueError): check(bad)

    def test_canonical_request_exact_route_and_readback(self):
        import asyncio
        import types
        from hermes_transport import request
        calls=[]; views=[]; info=self.info()
        class Client:
            async def rpc(self,method,**kwargs):
                calls.append((method,kwargs))
                return {'stored_session_id':'synthetic-session','info':info}
        client=Client()
        class Connection:
            async def __aenter__(self): return client
            async def __aexit__(self,*args): pass
        class View:
            def __init__(self,client,snapshot,**kwargs): self.emitter=kwargs['emitter'];views.append(self)
            async def run(self,prompt,oneshot):
                print(json.dumps({'type':'system','subtype':'init','model':'gpt-6.1-sol'}))
                print(json.dumps({'type':'result','exit_code':0,'error':None,'text':'synthetic RPC fixture'}))
                return 0
        modules={
            'hermes_cli.config':types.SimpleNamespace(load_config=lambda:{'model':{'default':'gpt-6.1-sol','provider':'openai-codex'}}),
            'hermes_cli.gateway_client':types.SimpleNamespace(connect_gateway=lambda:Connection()),
            'hermes_cli.gateway_chat_view':types.SimpleNamespace(GatewayChatView=View),
            'hermes_cli.stream_json':types.SimpleNamespace(StreamJsonEmitter=lambda **kw:None)}
        with patch.dict(sys.modules,modules):
            result=asyncio.run(request('synthetic public prompt',1))
            self.assertEqual(result['text'],'synthetic RPC fixture')
            self.assertEqual([c[0] for c in calls],['session.create','session.resume'])
            create=calls[0][1]
            for key,value in [('model','gpt-6.1-sol'),('provider','openai-codex'),('safe_mode',True),('toolsets',[]),('max_turns',1)]:
                self.assertEqual(create[key],value)
            self.assertTrue(views[0].unattended)
            calls.clear();views.clear();info={**info,'provider':'wrong'}
            with self.assertRaisesRegex(ValueError,'model/provider'): asyncio.run(request('synthetic public prompt',1))
            self.assertEqual(len(views),0)
            info=self.info(); calls.clear()
            async def parked(*args,**kwargs): await asyncio.sleep(10)
            async def cancelled():
                task=asyncio.create_task(request('synthetic public prompt',1))
                await asyncio.sleep(.01);task.cancel()
                with self.assertRaises(asyncio.CancelledError): await task
            with patch.object(View,'run',parked): asyncio.run(cancelled())
            self.assertEqual([c[0] for c in calls],['session.create','session.interrupt'])

    def test_request_bounds_before_gateway_import(self):
        import asyncio
        from hermes_transport import request
        for prompt,seconds in [('',1),('x',0),('x',float('nan')),('x',121),('x',True)]:
            with self.assertRaises(ValueError): asyncio.run(request(prompt,seconds))

    def test_tool_events_and_error_stream_are_refused(self):
        parse = getattr(self.transport(), 'parse_stream', lambda text: {})
        valid = [{'type':'system','subtype':'init','model':'gpt-6.1-sol'},
                 {'type':'result','exit_code':0,'text':'synthetic transport fixture','error':None}]
        for extra in [{'type':'tool_use'},{'type':'tool_result'}]:
            with self.assertRaises(ValueError): parse('\n'.join(map(json.dumps,valid+[extra])))
        for change in [{'exit_code':1},{'error':'unavailable'},{'text':''}]:
            with self.assertRaises(ValueError): parse('\n'.join(map(json.dumps,[valid[0],{**valid[1],**change}])))
        self.assertEqual(parse('\n'.join(map(json.dumps,valid)))['text'],'synthetic transport fixture')

class ProbeTests(unittest.TestCase):
    def assert_missing_root_refused(self, *, omitted):
        import prerequisite
        import runtime_contract as runtime
        contract = runtime.RuntimeContract({})
        # Admission would succeed: refusal must be at the public API boundary,
        # not an unrelated Windows/source/hash guard or Path(None) conversion.
        with patch.object(prerequisite, 'validate_contract', return_value=contract) as admission, \
             patch.object(prerequisite, 'Path', wraps=Path) as filesystem, \
             patch.object(prerequisite.subprocess, 'run') as launch:
            error = None
            try:
                if omitted:
                    prerequisite.probe_runtime(contract)
                else:
                    prerequisite.probe_runtime(contract, None)
            except Exception as caught:
                error = caught
            admission.assert_not_called()
            filesystem.assert_not_called()
            launch.assert_not_called()
            self.assertIsInstance(error, TypeError if omitted else ValueError)
            self.assertIn('evidence_root', str(error))

    def test_probe_omitted_root_refuses_before_admission_or_io(self):
        self.assert_missing_root_refused(omitted=True)

    def test_probe_none_root_refuses_before_admission_or_io(self):
        self.assert_missing_root_refused(omitted=False)

    def test_probe_explicit_root_runs_admitted_supervisor(self):
        import prerequisite
        import runtime_contract as runtime
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as d:
            base = Path(d).resolve()
            contract, pins = ContractTests().fixture(base / 'fixture')
            contract = runtime.RuntimeContract(contract.runtimes)
            for name, row in contract.runtimes.items():
                row['prefix'] = str(base / name / 'env')
            root = base / 'caller-owned-evidence'
            (root / 'evidence').mkdir(parents=True)
            (root / 'closure').mkdir()
            (root / 'closure/runtime-contract.json').write_text(json.dumps(dataclasses.asdict(contract)))
            events = []
            expected = {}
            for name, row in contract.runtimes.items():
                observed = {'python': row['python'], 'prefix': row['prefix'], 'platform': 'win32',
                            'python_version': [3,14,0], 'import_ok': True}
                if name == 'hermes':
                    observed.update(bootstrap_ok=True, no_auth_refused=True,
                                    auth_code='codex_auth_missing', inference_accepted=False)
                else:
                    observed.update(computation_ok=True, synthetic_public_input=True,
                                    pandas_version='2.3.3' if name == 'vibe-trading' else '3.1.1')
                expected[name] = observed

            def admit(value):
                events.append('admission')
                return runtime.validate_contract(value, platform='win32')

            def child(command, **kwargs):
                name = command[3]
                events.append(name)
                self.assertEqual(command, [contract.runtimes[name]['python'], '-I',
                    str(ROOT / 'native_probe.py'), name,
                    str(root / 'closure/runtime-contract.json'), str(root / 'evidence' / (name + '-probe.json'))])
                home = root / 'build' / name / 'probe-home'
                self.assertEqual(kwargs['cwd'], home)
                self.assertTrue(home.is_dir())
                self.assertEqual(kwargs['env']['HOME'], str(home.resolve()))
                # Only the external native child is stubbed; production admission,
                # isolated directories/config, logs and observation checks run.
                Path(command[-1]).write_text(json.dumps(expected[name]))
                return SimpleNamespace(returncode=0, stdout='synthetic child observation\n', stderr='')

            with patch.object(runtime, 'PINS', pins), \
                 patch.object(prerequisite, 'validate_contract', side_effect=admit), \
                 patch.object(prerequisite.subprocess, 'run', side_effect=child):
                result = prerequisite.probe_runtime(contract, root)
            self.assertEqual(events, ['admission', 'vibe-trading', 'tradingagents', 'hermes'])
            self.assertEqual(result, prerequisite.ProbeResult(expected, True, False))
            for name in expected:
                self.assertEqual(json.loads((root / 'evidence' / (name + '-exit.json')).read_text())['exitCode'], 0)
                self.assertEqual((root / 'evidence' / (name + '-stdout.log')).read_text(), 'synthetic child observation\n')
            self.assertIn('adopt_external_logins: false',
                          (root / 'build/hermes/probe-home/hermes/config.yaml').read_text())

    def test_observed_runtime_and_computation_must_match_contract(self):
        try: import prerequisite; validator = prerequisite.validate_observation
        except ModuleNotFoundError: validator = lambda *a: None
        expected = {'python':str(Path(sys.executable).resolve()),'prefix':'isolated-vibe'}
        observation = {'python':expected['python'],'prefix':expected['prefix'],'platform':'win32',
                       'python_version':[3,14,0],'import_ok':True,'computation_ok':True,
                       'pandas_version':'2.3.3','synthetic_public_input':True}
        validator('vibe-trading', expected, observation)
        for key, value in [('python','wrong'),('prefix','wrong'),('platform','linux'),('python_version',[3,11,0]),
                           ('import_ok',False),('computation_ok',False),('pandas_version','3.0.6'),('synthetic_public_input',False)]:
            with self.subTest(key=key), self.assertRaises(ValueError): validator('vibe-trading',expected,{**observation,key:value})
        validator('tradingagents', expected, {**observation,'pandas_version':'3.0.6'})
        with self.assertRaises(ValueError): validator('tradingagents',expected,observation)


if __name__ == '__main__':
    unittest.main(verbosity=2)
