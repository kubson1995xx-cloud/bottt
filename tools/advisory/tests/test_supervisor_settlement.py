"""Exercise the real Supervisor exchange and SQLite settlement, no model calls."""
import os,shutil,sys,tempfile,threading,unittest,uuid
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from budget import Refusal
from supervisor import Supervisor
from runtime_contract import RuntimeContract
from market_contract import canonical_snapshot,utc,TOOLS
from test_market_contract import fixture
from test_supervisor import fixture_native
from datetime import timedelta
CUTOFF='2026-11-06T00:00:00Z';NOW=utc(CUTOFF)+timedelta(hours=12);DAY=CUTOFF[:10]
PAYLOAD={'messages':[{'role':'human','content':'offline accounting fixture'}],'tools':[],'schema':None}
RESPONSE={'fixture':True,'text':'{"content":"offline fixture","tool_calls":[]}'}

class SupervisorSettlementTests(unittest.TestCase):
    def setUp(self):
        self.base=None;self.temp=None
        if os.name=='nt':
            from store import StoreLease
            self.root=Path(tempfile.gettempdir()).resolve()/('apex-budget-'+uuid.uuid4().hex);self.base=StoreLease(self.root)
        else:self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name)
        rows={t:{'python':sys.executable} for t in TOOLS}
        self.s=Supervisor(self.root/'store',RuntimeContract(rows),fixture=True,clock=lambda:NOW,inference=lambda *a:dict(RESPONSE))
    def tearDown(self):
        self.s.close()
        if self.base:self.base.close();shutil.rmtree(self.root)
        if self.temp:self.temp.cleanup()
    def run_exchange(self,*,elapsed_ns=1000001,inference=None,cancel=False):
        s=self.s
        if inference:s.inference=inference
        raw=canonical_snapshot('BTC-EUR',CUTOFF,fixture());a=s.budget.reserve(DAY,'BTC-EUR','vibe-trading',NOW+timedelta(hours=1))
        s.store_lease.directory(s.root/'BTC-EUR');s.store_lease.directory(s.root/'BTC-EUR/attempts')
        def worker(command,env,work,log,exchange,*args):
            reply=exchange(PAYLOAD);self.assertEqual('offline fixture',reply['reply']['content']);return 'result'
        # Both clocks are supplied so the exact same operands reproduce old floor.
        with patch('supervisor.run_worker',side_effect=worker),patch('supervisor.time.monotonic',side_effect=[10,10+elapsed_ns/1e9]),patch('supervisor.time.monotonic_ns',side_effect=[10000000000,10000000000+elapsed_ns]):
            return s._analysis('vibe-trading','BTC-EUR',CUTOFF,raw,a,s.root/'BTC-EUR/attempts'/a.id)
    def test_success_rounds_up_not_down_and_keeps_original_reservation(self):
        self.assertEqual('result',self.run_exchange())
        u=self.s.budget.usage(DAY)
        self.assertEqual(2,u['observed_ms']);self.assertEqual(2,u['charged_ms']);self.assertEqual(60000,u['reserved_ms'])
    def test_supervisor_six_graph_fixture_path_settles_36_requests(self):
        s=self.s;seen=[]
        def worker(command,env,work,log,exchange,*args):
            tool=command[3];pair=command[5];raw=canonical_snapshot(pair,CUTOFF,fixture())
            for _ in range(2 if tool=='vibe-trading' else 10):exchange(PAYLOAD);seen.append((pair,tool))
            return fixture_native(tool,raw)
        ticks=[]
        for i in range(36):ticks.extend([i*20000000000,i*20000000000+10001000001])
        with patch('supervisor.validate_contract'),patch('supervisor.run_worker',side_effect=worker),patch('supervisor.time.monotonic_ns',side_effect=ticks):
            for pair in ('BTC-EUR','ETH-EUR','SOL-EUR'):
                raw=canonical_snapshot(pair,CUTOFF,fixture());result=s.refresh(pair,CUTOFF,raw)
                self.assertEqual('fixture',result.state)
                self.assertTrue(all(x['State']=='completed' for x in result.manifest['Tools'].values()))
        self.assertEqual(36,len(seen));self.assertEqual(360072,s.budget.usage(DAY)['charged_ms'])
        self.assertEqual(2160000,s.budget.usage(DAY)['reserved_ms'])
        s.close();s=Supervisor(self.root/'store',self.s.contract,fixture=True,clock=lambda:NOW,inference=lambda *a:self.fail('restart cannot retry'))
        try:
            for pair in ('BTC-EUR','ETH-EUR','SOL-EUR'):
                self.assertEqual('REUSED',s.refresh(pair,CUTOFF,canonical_snapshot(pair,CUTOFF,fixture())).reason)
            self.assertEqual(36,s.budget.usage(DAY)['supervised_requests'])
        finally:s.close()
    def test_timeout_cancel_parse_and_overshoot_keep_full_charge_and_halt(self):
        def timeout(*a):raise TimeoutError('offline timeout')
        def cancelled(*a):self.s.cancel.set();return dict(RESPONSE)
        for name,fn,elapsed in [('timeout',timeout,60000000001),('cancel',cancelled,1000001),('parse',lambda *a:{'fixture':True,'text':'invalid'},1000001),('overshoot',lambda *a:dict(RESPONSE),60000000001)]:
            with self.subTest(name=name):
                # Separate service-owned store: no reset of the tested ledger.
                self.s.close();self.s=Supervisor(self.root/name,RuntimeContract({t:{'python':sys.executable} for t in TOOLS}),fixture=True,clock=lambda:NOW)
                with self.assertRaises((TimeoutError,ValueError)):self.run_exchange(elapsed_ns=elapsed,inference=fn)
                u=self.s.budget.usage(DAY);self.assertEqual(60000,u['charged_ms'])
                other=self.s.budget.reserve(DAY,'ETH-EUR','tradingagents',NOW+timedelta(hours=1))
                self.assertEqual('GLOBAL_INFERENCE_HALTED',self.s.budget.reserve_request(other,60000).reason)

    def test_invalid_negative_monotonic_span_stays_uncertain(self):
        with self.assertRaises(ValueError):self.run_exchange(elapsed_ns=-1)
        u=self.s.budget.usage(DAY);self.assertEqual(60000,u['charged_ms']);self.assertEqual(1,u['uncertain_requests'])
    def test_supervisor_process_crash_keeps_full_reservation_after_restart(self):
        import subprocess
        path=self.root/'crashed'
        p=subprocess.run([sys.executable,__file__,'crash',str(path)],capture_output=True,text=True,timeout=40)
        self.assertEqual(23,p.returncode,p.stderr)
        s=Supervisor(path,self.s.contract,fixture=True,clock=lambda:NOW)
        try:
            u=s.budget.usage(DAY);self.assertEqual(60000,u['charged_ms']);self.assertEqual(1,u['uncertain_requests'])
            self.assertIsInstance(s.budget.reserve(DAY,'BTC-EUR','vibe-trading',NOW+timedelta(hours=1)),Refusal)
        finally:s.close()
    def test_settlement_storage_failure_leaves_full_uncertain_charge(self):
        original=self.s.budget.complete_request
        def refuse(*args):raise OSError('injected before settlement transaction')
        with patch.object(self.s.budget,'complete_request',side_effect=refuse),self.assertRaises(OSError):self.run_exchange()
        u=self.s.budget.usage(DAY);self.assertEqual(60000,u['charged_ms']);self.assertEqual(1,u['uncertain_requests'])

if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1]=='crash':
        s=Supervisor(Path(sys.argv[2]),RuntimeContract({t:{'python':sys.executable} for t in TOOLS}),fixture=True,clock=lambda:NOW,inference=lambda *a:os._exit(23))
        a=s.budget.reserve(DAY,'BTC-EUR','vibe-trading',NOW+timedelta(hours=1))
        s.store_lease.directory(s.root/'BTC-EUR');s.store_lease.directory(s.root/'BTC-EUR/attempts')
        with patch('supervisor.run_worker',side_effect=lambda command,env,work,log,exchange,*args:exchange(PAYLOAD)):
            s._analysis('vibe-trading','BTC-EUR',CUTOFF,canonical_snapshot('BTC-EUR',CUTOFF,fixture()),a,s.root/'BTC-EUR/attempts'/a.id)
    else:unittest.main()
