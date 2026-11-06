"""Offline accounting proof; no inference/provider/upstream dependency."""
import json,os,sqlite3,subprocess,sys,tempfile,unittest
from dataclasses import asdict,replace
from contextlib import closing
from datetime import datetime,timezone,timedelta
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from budget import Budget,Reservation,Refusal
NOW=datetime(2026,11,6,12,tzinfo=timezone.utc)
DAY='2026-11-06'

class SettlementTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.path=Path(self.temp.name)/'budget.sqlite';self.b=Budget(self.path,lambda:NOW)
    def tearDown(self):self.temp.cleanup()
    def attempt(self,pair='BTC-EUR',tool='vibe-trading'):
        return self.b.reserve(DAY,pair,tool,NOW+timedelta(hours=1),'a'*64)
    def test_six_graph_36_request_schedule_with_60_second_deadlines(self):
        # Same retained topology: Vibe=2, TradingAgents=10 per pair.
        elapsed=[10001+i*13 for i in range(36)];issued=0
        for pair in ('BTC-EUR','ETH-EUR','SOL-EUR'):
            for tool,n in (('vibe-trading',2),('tradingagents',10)):
                a=self.attempt(pair,tool)
                for _ in range(n):
                    r=self.b.reserve_request(a,60000)
                    self.assertNotIsInstance(r,Refusal,f'global request {issued+1}: {r}')
                    self.assertEqual(60000,r.milliseconds)
                    self.b.complete_request(r,elapsed[issued],'ok');issued+=1
                self.b.finish(a,'ok')
        u=self.b.usage(DAY)
        self.assertEqual(36,u['supervised_requests']);self.assertEqual(2160000,u['reserved_ms'])
        self.assertEqual(sum(elapsed),u['charged_ms']);self.assertEqual(sum(elapsed),u['observed_ms'])
        self.assertEqual(0,u['uncertain_requests']);self.assertLess(u['charged_ms'],900000)

    def test_request_49_refuses_after_short_successes(self):
        a=self.attempt()
        for _ in range(48):
            r=self.b.reserve_request(a,60000);self.assertNotIsInstance(r,Refusal);self.b.complete_request(r,0,'ok')
        self.assertEqual('DAILY_BUDGET_EXHAUSTED',self.b.reserve_request(a,60000).reason)
        self.assertEqual(48,self.b.usage(DAY)['charged_ms'])
    def test_actual_time_exhaustion_includes_inflight_full_slice(self):
        a=self.attempt()
        for _ in range(14):
            r=self.b.reserve_request(a,60000);self.b.complete_request(r,60000,'ok')
        last=self.b.reserve_request(a,60000)
        self.assertEqual('DAILY_BUDGET_EXHAUSTED',self.b.reserve_request(a,1).reason)
        self.b.complete_request(last,60000,'ok')
        self.assertEqual('DAILY_BUDGET_EXHAUSTED',self.b.reserve_request(a,1).reason)
        self.assertEqual(900000,self.b.usage(DAY)['charged_ms'])
    def test_wrong_identity_duplicate_and_invalid_timing_never_release(self):
        a=self.attempt();r=self.b.reserve_request(a,60000)
        for wrong in [replace(r,id='wrong'),replace(r,attempt='wrong'),replace(r,day='2026-11-07'),replace(r,milliseconds=1)]:
            with self.subTest(wrong=wrong),self.assertRaises(ValueError):self.b.complete_request(wrong,1,'ok')
            self.assertEqual(60000,self.b.usage(DAY)['charged_ms'])
        for ms in [-1,float('nan'),float('inf'),float('-inf'),1.1,True,60001]:
            with self.subTest(ms=ms),self.assertRaises(ValueError):self.b.complete_request(r,ms,'ok')
            self.assertEqual(60000,self.b.usage(DAY)['charged_ms'])
        self.b.complete_request(r,1,'ok')
        with self.assertRaises(ValueError):self.b.complete_request(r,0,'ok')
        self.assertEqual(1,self.b.usage(DAY)['charged_ms'])
    def test_failure_cancel_timeout_keep_full_charge_and_halt_after_restart(self):
        for outcome,ms in [('failed',3),('cancelled',0),('failed',60001)]:
            with self.subTest(outcome=outcome,ms=ms):
                b=Budget(Path(self.temp.name)/(outcome+str(ms)),lambda:NOW)
                a=b.reserve(DAY,'BTC-EUR','vibe-trading',NOW+timedelta(hours=1));r=b.reserve_request(a,60000)
                b.complete_request(r,ms,outcome);b.finish(a,'failed');b=Budget(b.path,lambda:NOW)
                other=b.reserve(DAY,'ETH-EUR','tradingagents',NOW+timedelta(hours=1))
                self.assertEqual('GLOBAL_INFERENCE_HALTED',b.reserve_request(other,60000).reason)
                self.assertEqual(60000,b.usage(DAY)['charged_ms']);self.assertEqual(ms,b.usage(DAY)['observed_ms'])
    def legacy(self):
        # Exact six-column schema used by frozen103, including historic states.
        path=Path(self.temp.name)/'legacy.sqlite'
        with closing(sqlite3.connect(path)) as db,db:
            db.execute('CREATE TABLE attempts(id TEXT PRIMARY KEY,day TEXT NOT NULL,pair TEXT NOT NULL,tool TEXT NOT NULL,deadline TEXT NOT NULL,digest TEXT NOT NULL,status TEXT NOT NULL,UNIQUE(day,pair,tool))')
            db.execute('CREATE TABLE requests(id TEXT PRIMARY KEY,attempt TEXT NOT NULL,day TEXT NOT NULL,milliseconds INTEGER NOT NULL CHECK(milliseconds>0),observed_ms INTEGER,outcome TEXT NOT NULL)')
            db.execute('INSERT INTO attempts VALUES(?,?,?,?,?,?,?)',('old-attempt',DAY,'BTC-EUR','vibe-trading',(NOW+timedelta(hours=1)).isoformat(),'a'*64,'reserved'))
            rows=[('old-ok','old-attempt',DAY,60000,1,'ok'),('old-pending','old-attempt',DAY,60000,None,'uncertain')]
            rows += [('prior-'+state,'prior','2026-11-05',25000,obs,state) for state,obs in [('ok',1),('failed',24000),('cancelled',3),('uncertain',None)]]
            db.executemany('INSERT INTO requests VALUES(?,?,?,?,?,?)',rows)
        return path,rows
    def test_legacy_migration_never_reprices_old_success_or_uncertainty(self):
        path,rows=self.legacy();b=Budget(path,lambda:NOW)
        with b._transaction() as db:
            self.assertEqual(rows,db.execute('SELECT id,attempt,day,milliseconds,observed_ms,outcome FROM requests ORDER BY rowid').fetchall())
            self.assertEqual([('legacy-full-v1',None)]*len(rows),db.execute('SELECT charge_policy,charged_ms FROM requests').fetchall())
        self.assertEqual(120000,b.usage(DAY)['charged_ms']);self.assertEqual(100000,b.usage('2026-11-05')['charged_ms'])
        from budget import RequestReservation
        b.complete_request(RequestReservation('old-pending',60000,'old-attempt',DAY),1,'ok')
        self.assertEqual(120000,b.usage(DAY)['charged_ms'])
        a=b.reserve(DAY,'ETH-EUR','tradingagents',NOW+timedelta(hours=1));r=b.reserve_request(a,60000);b.complete_request(r,12345,'ok')
        b=Budget(path,lambda:NOW)
        self.assertEqual(132345,b.usage(DAY)['charged_ms']);self.assertEqual(180000,b.usage(DAY)['reserved_ms'])
        self.assertIsInstance(b.reserve(DAY,'BTC-EUR','vibe-trading',NOW+timedelta(hours=1)),Refusal)
    def test_legacy_failure_still_halts_after_migration(self):
        path,rows=self.legacy()
        with closing(sqlite3.connect(path)) as db,db:db.execute("UPDATE requests SET day=? WHERE id='prior-failed'",(DAY,))
        b=Budget(path,lambda:NOW);a=b.reserve(DAY,'SOL-EUR','tradingagents',NOW+timedelta(hours=1))
        self.assertEqual('GLOBAL_INFERENCE_HALTED',b.reserve_request(a,60000).reason)
        self.assertEqual(145000,b.usage(DAY)['charged_ms'])
    def test_concurrent_migration_preserves_all_legacy_rows(self):
        path,rows=self.legacy()
        ps=[subprocess.Popen([sys.executable,__file__,'migrate',str(path)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True) for _ in range(6)]
        outputs=[p.communicate(timeout=30) for p in ps]
        self.assertTrue(all(p.returncode==0 for p in ps),outputs)
        self.assertTrue(all(o.strip()=='120000' for o,e in outputs),outputs)
        with closing(sqlite3.connect(path)) as db,db:self.assertEqual(rows,db.execute('SELECT id,attempt,day,milliseconds,observed_ms,outcome FROM requests ORDER BY rowid').fetchall())
    def test_mixed_legacy_settled_and_abrupt_crash_remain_charged(self):
        path,rows=self.legacy();b=Budget(path,lambda:NOW)
        a=b.reserve(DAY,'ETH-EUR','vibe-trading',NOW+timedelta(hours=1));r=b.reserve_request(a,60000);b.complete_request(r,10001,'ok')
        p=subprocess.run([sys.executable,__file__,'crash',str(path)],capture_output=True,text=True,timeout=30)
        self.assertEqual(23,p.returncode,p.stderr)
        b=Budget(path,lambda:NOW);u=b.usage(DAY)
        self.assertEqual(190001,u['charged_ms']);self.assertEqual(240000,u['reserved_ms']);self.assertEqual(2,u['uncertain_requests'])
        self.assertIsInstance(b.reserve(DAY,'SOL-EUR','tradingagents',NOW+timedelta(hours=1)),Refusal)
    def test_distinct_process_requests_share_remaining_capacity(self):
        a=self.attempt()
        for _ in range(7):
            r=self.b.reserve_request(a,120000);self.b.complete_request(r,120000,'ok')
        attempts=[a]+[self.attempt(p,t) for p,t in [('BTC-EUR','tradingagents'),('ETH-EUR','vibe-trading'),('ETH-EUR','tradingagents'),('SOL-EUR','vibe-trading'),('SOL-EUR','tradingagents')]]
        ps=[subprocess.Popen([sys.executable,__file__,'race',str(self.path),json.dumps(asdict(a))],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True) for a in attempts]
        out=[p.communicate(timeout=30) for p in ps]
        self.assertTrue(all(p.returncode==0 for p in ps),out)
        self.assertEqual(1,sum(o.strip()=='admitted' for o,e in out),out)
        self.assertEqual(900000,self.b.usage(DAY)['charged_ms'])
        self.assertEqual(1,self.b.usage(DAY)['uncertain_requests'])

if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1]=='crash':
        b=Budget(sys.argv[2],lambda:NOW);a=b.reserve(DAY,'SOL-EUR','tradingagents',NOW+timedelta(hours=1));r=b.reserve_request(a,60000)
        if isinstance(r,Refusal):raise AssertionError(r)
        os._exit(23)
    elif len(sys.argv)>1 and sys.argv[1]=='migrate':
        print(Budget(sys.argv[2],lambda:NOW).usage(DAY)['charged_ms'])
    elif len(sys.argv)>1 and sys.argv[1]=='race':
        b=Budget(sys.argv[2],lambda:NOW);r=b.reserve_request(Reservation(**json.loads(sys.argv[3])),60000);print('refused' if isinstance(r,Refusal) else 'admitted')
    else:unittest.main()
