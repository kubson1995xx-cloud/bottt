import sys,unittest,tempfile,subprocess,os,json
from pathlib import Path
from datetime import datetime,timezone,timedelta
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from budget import Budget,Reservation,Refusal
NOW=datetime(2026,11,6,12,tzinfo=timezone.utc)
def child_reserve(path,crash=False):
    b=Budget(path,lambda:NOW);r=b.reserve('2026-11-06','BTC-EUR','vibe-trading',NOW+timedelta(hours=1),'a'*64)
    print(type(r).__name__,flush=True)
    if isinstance(r,Reservation): b.reserve_request(r,18000)
    if crash:os._exit(23)

class BudgetTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.path=Path(self.temp.name)/'budget.db';self.b=Budget(self.path,lambda:NOW)
    def tearDown(self):self.temp.cleanup()
    def attempt(self,pair='BTC-EUR',tool='vibe-trading'):
        return self.b.reserve('2026-11-06',pair,tool,NOW+timedelta(hours=1),'a'*64)
    def test_request_49_refused(self):
        a=self.attempt()
        for _ in range(48):self.assertNotIsInstance(self.b.reserve_request(a,1000),Refusal)
        self.assertIsInstance(self.b.reserve_request(a,1000),Refusal)
        self.assertEqual(48,self.b.usage('2026-11-06')['supervised_requests'])
    def test_900_seconds_across_pairs_and_tools(self):
        for pair in ('BTC-EUR','ETH-EUR','SOL-EUR'):
            for tool in ('vibe-trading','tradingagents'):
                a=self.attempt(pair,tool)
                self.assertNotIsInstance(self.b.reserve_request(a,120000),Refusal)
        a=self.b.reserve('2026-11-06','SOL-EUR','tradingagents',NOW+timedelta(hours=1))
        self.assertIsInstance(a,Refusal)
        # Last active attempt can issue multiple graph requests, not a new attempt.
        with self.b._transaction() as db:
            row=db.execute('SELECT id,day,pair,tool,deadline,digest FROM attempts WHERE pair=? AND tool=?',('SOL-EUR','tradingagents')).fetchone()
        a=Reservation(*row)
        self.assertNotIsInstance(self.b.reserve_request(a,120000),Refusal)
        self.assertNotIsInstance(self.b.reserve_request(a,60000),Refusal)
        self.assertIsInstance(self.b.reserve_request(a,1),Refusal)
        self.assertEqual(900000,self.b.usage('2026-11-06')['reserved_ms'])
    def test_failure_consumes_attempt_and_full_slice(self):
        a=self.attempt();r=self.b.reserve_request(a,18000);self.b.complete_request(r,3,'failed');self.b.finish(a,'failed')
        self.assertIsInstance(self.attempt(),Refusal)
        self.assertIsInstance(self.b.reserve_request(a,1000),Refusal)
        self.assertEqual(18000,self.b.usage('2026-11-06')['reserved_ms'])
        self.assertEqual(3,self.b.usage('2026-11-06')['observed_ms'])
    def test_failed_inference_halts_shared_day_across_tools_pairs_and_restart(self):
        a=self.attempt();r=self.b.reserve_request(a,25000);self.b.complete_request(r,20000,'failed');self.b.finish(a,'failed')
        b=Budget(self.path,lambda:NOW);other=b.reserve('2026-11-06','ETH-EUR','tradingagents',NOW+timedelta(hours=1),'b'*64)
        denied=b.reserve_request(other,60000);self.assertIsInstance(denied,Refusal);self.assertEqual('GLOBAL_INFERENCE_HALTED',denied.reason)
        self.assertEqual(1,b.usage('2026-11-06')['supervised_requests'])
    def test_crash_restart_no_refund(self):
        p=subprocess.run([sys.executable,__file__,'crash',str(self.path)],capture_output=True,text=True,timeout=20)
        self.assertEqual(23,p.returncode,p.stderr);self.assertEqual('Reservation',p.stdout.strip())
        b=Budget(self.path,lambda:NOW)
        self.assertEqual(18000,b.usage('2026-11-06')['reserved_ms']);self.assertEqual(1,b.usage('2026-11-06')['uncertain_requests'])
        self.assertIsInstance(self.attempt(),Refusal)
    def test_utc_rollover(self):
        a=self.attempt();self.b.reserve_request(a,18000)
        now=NOW+timedelta(days=1);b=Budget(self.path,lambda:now)
        self.assertIsInstance(b.reserve('2026-11-07','BTC-EUR','vibe-trading',now+timedelta(hours=1)),Reservation)
        self.assertIsInstance(b.reserve_request(a,18000),Refusal)
        self.assertEqual(0,b.usage('2026-11-07')['supervised_requests'])
    def test_duplicate_process_only_one_winner(self):
        processes=[subprocess.Popen([sys.executable,__file__,'reserve',str(self.path)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True) for _ in range(8)]
        results=[p.communicate(timeout=30) for p in processes]
        self.assertTrue(all(p.returncode==0 for p in processes),results)
        self.assertEqual(1,sum(out.strip()=='Reservation' for out,err in results),results)
        self.assertEqual(1,self.b.usage('2026-11-06')['supervised_requests'])

    def race_request(self,a,ms):
        from dataclasses import asdict
        processes=[subprocess.Popen([sys.executable,__file__,'request',str(self.path),json.dumps(asdict(a)),str(ms)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True) for _ in range(8)]
        results=[p.communicate(timeout=30) for p in processes]
        self.assertTrue(all(p.returncode==0 for p in processes),results)
        return sum(out.strip()=='RequestReservation' for out,err in results)
    def test_concurrent_requests_cannot_exceed_48(self):
        a=self.attempt()
        for _ in range(47):self.b.reserve_request(a,1000)
        self.assertEqual(1,self.race_request(a,1000));self.assertEqual(48,self.b.usage('2026-11-06')['supervised_requests'])
    def test_concurrent_requests_cannot_exceed_900_seconds(self):
        a=self.attempt()
        for _ in range(7):self.b.reserve_request(a,120000)
        self.b.reserve_request(a,59999)
        self.assertEqual(1,self.race_request(a,1));self.assertEqual(900000,self.b.usage('2026-11-06')['reserved_ms'])

if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1] in ('reserve','crash'):child_reserve(sys.argv[2],sys.argv[1]=='crash')
    elif len(sys.argv)>1 and sys.argv[1]=='request':
        b=Budget(sys.argv[2],lambda:NOW);r=b.reserve_request(Reservation(**json.loads(sys.argv[3])),int(sys.argv[4]));print(type(r).__name__)
    else:unittest.main()
