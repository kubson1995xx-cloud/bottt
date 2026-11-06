import sys,unittest,tempfile,subprocess,threading,time,json,os
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from supervisor import Supervisor,refresh_lock
from runtime_contract import RuntimeContract
from market_contract import *
from reports import *
from ipc import *
from test_market_contract import fixture
NOW=utc('2026-11-06T00:00:00Z')+timedelta(hours=12)
CUTOFF='2026-11-06T00:00:00Z'

def fixture_native(tool,raw):
    return {'native_rating':'WAIT' if tool=='vibe-trading' else 'Hold','recommendation':'WAIT','summary':'Explicit synthetic native result for coordinator test only.','DatasetSha256':dataset_digest(raw),'tool_calls':2}

class SupervisorTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name);self.raw=canonical_snapshot('BTC-EUR',CUTOFF,fixture());self.data=strict_json(self.raw);self.instances=[]
    def tearDown(self):
        for s in self.instances:s.close()
        self.temp.cleanup()
    def service(self):
        s=Supervisor(self.root/'store',RuntimeContract({}),fixture=True,clock=lambda:NOW);self.instances.append(s);return s
    def test_matching_restart_reuses_generation_and_no_second_attempt(self):
        s=self.service()
        with patch('supervisor.validate_contract'),patch.object(s,'_analysis',side_effect=lambda tool,pair,cutoff,raw,*_:fixture_native(tool,raw)) as run:
            first=s.refresh('BTC-EUR',CUTOFF,self.raw);second=s.refresh('BTC-EUR',CUTOFF,self.raw)
        self.assertEqual(2,run.call_count);self.assertEqual(first.manifest['Generation'],second.manifest['Generation']);self.assertEqual('REUSED',second.reason)
        s2=self.service()
        with patch.object(s2,'_analysis',side_effect=AssertionError('must not retry')):self.assertEqual(first.manifest['Generation'],s2.refresh('BTC-EUR',CUTOFF,self.raw).manifest['Generation'])
    def test_changed_same_day_content_refuses_before_graph(self):
        s=self.service()
        with patch('supervisor.validate_contract'),patch.object(s,'_analysis',side_effect=lambda tool,pair,cutoff,raw,*_:fixture_native(tool,raw)):s.refresh('BTC-EUR',CUTOFF,self.raw)
        rows=fixture();rows[-1][4]='102';changed=canonical_snapshot('BTC-EUR',CUTOFF,rows)
        with patch.object(s,'_analysis',side_effect=AssertionError('parity must veto')):result=s.refresh('BTC-EUR',CUTOFF,changed)
        self.assertEqual('CANONICAL_PARITY_MISMATCH',result.reason)
    def test_failure_retained_and_no_retry_after_corrupt_publication(self):
        s=self.service()
        with patch('supervisor.validate_contract'),patch.object(s,'_analysis',side_effect=TimeoutError('fixture failure')) as run:s.refresh('BTC-EUR',CUTOFF,self.raw)
        self.assertEqual(2,run.call_count)
        (s.root/'BTC-EUR/CURRENT.json').write_bytes(b'corrupt')
        with patch.object(s,'_analysis',side_effect=AssertionError('retry forbidden')):s.refresh('BTC-EUR',CUTOFF,self.raw)
        m,results=read_pair(s.root/'BTC-EUR',self.data,NOW);self.assertTrue(all(r['error'] for r in results.values()))
    def test_fixture_never_green_shadow_report_and_original_rating_kept(self):
        entries={t:{'native':fixture_native(t,self.raw),'error':None} for t in TOOLS}
        m=publish_pair(self.root,self.data,entries,True);self.assertEqual('Hold',m['Tools']['tradingagents']['OriginalRecommendation']);self.assertEqual('WAIT',m['Tools']['tradingagents']['MappedRecommendation'])
        for t in TOOLS:
            r=strict_json((self.root/'generations'/m['Generation']/(t+'.json')).read_bytes());self.assertEqual('unavailable',r['Status']);self.assertEqual(FIELDS,set(r))
    def test_mapping_preserved_all_ratings(self):
        self.assertEqual(['BUY','BUY','WAIT','SELL','SELL'],[normalize('tradingagents',x) for x in ['Buy','Overweight','Hold','Underweight','Sell']])
    def test_strict_reports_reject_extra_wrong_route_stale_and_tamper(self):
        entries={t:{'native':fixture_native(t,self.raw),'error':None} for t in TOOLS};m=publish_pair(self.root,self.data,entries,True)
        read_pair(self.root,self.data,NOW)
        p=self.root/'generations'/m['Generation']/'tradingagents.json';r=strict_json(p.read_bytes())
        for key,value in [('extra','x'),('Model','wrong'),('Instrument','ETH-EUR'),('SourceCommit','a'*40)]:
            with self.subTest(key=key),self.assertRaises(ValueError):validate_report({**r,key:value},self.data,NOW)
        with self.assertRaises(ValueError):validate_report(r,self.data,NOW+timedelta(days=1))
        p.write_bytes(p.read_bytes()+b' ')
        with self.assertRaises(ValueError):read_pair(self.root,self.data,NOW)
    def test_atomic_pair_pointer_readers_never_see_partial_generation(self):
        entries={t:{'native':fixture_native(t,self.raw),'error':None} for t in TOOLS};publish_pair(self.root,self.data,entries,True)
        failures=[];done=threading.Event();seen=[]
        def reader():
            while not done.is_set():
                try:seen.append(read_pair(self.root,self.data,NOW)[0]['Generation'])
                except Exception as e:failures.append(repr(e))
        thread=threading.Thread(target=reader);thread.start()
        try:
            for _ in range(15):publish_pair(self.root,self.data,entries,True)
        finally:done.set();thread.join()
        self.assertTrue(seen);self.assertEqual([],failures)
    def test_duplicate_refresh_lock_across_process(self):
        path=self.root/'lock'
        with refresh_lock(path) as admitted:
            self.assertTrue(admitted)
            script="from pathlib import Path;from supervisor import refresh_lock;\nwith refresh_lock(Path("+repr(str(path))+")) as locked:print(locked)"
            result=subprocess.run([sys.executable,'-c',script],env={**os.environ,'PYTHONPATH':str(Path(__file__).resolve().parents[1])},capture_output=True,text=True,timeout=20)
            self.assertEqual(0,result.returncode,result.stderr);self.assertEqual('False',result.stdout.strip())
    def test_unknown_and_oversized_ipc_refused_before_inference(self):
        base={'messages':[{'role':'human','content':'public'}],'tools':[],'schema':None}
        for payload in [{**base,'tools':[{'name':'shell','parameters':{}}]},{**base,'messages':[{'role':'human','content':'x'*250001}]}]:
            with self.assertRaises(ValueError):request_prompt('vibe-trading',payload)
        with self.assertRaises(ValueError):parse_reply('{"content":"x","content":"y","tool_calls":[]}',[],None)
        with self.assertRaises(ValueError):parse_reply('{"content":"","tool_calls":[{"name":"shell","args":{}}]}',[],None)
    def test_bounded_worker_deadline_and_hostile_output(self):
        for name,script,seconds in [('oversize',"print('x'*1048577)",5),('timeout','import time;time.sleep(10)',.2),('duplicate',"print('RESULT: {}');print('RESULT: {}')",5)]:
            with self.subTest(name=name),self.assertRaises((ValueError,TimeoutError)):
                run_worker([sys.executable,'-c',script],dict(os.environ),self.root,self.root/name,lambda _:self.fail('no inference allowed'),seconds)
    def test_cancellation_no_new_automatic_attempt(self):
        s=self.service();s.cancel.set();self.assertEqual('CANCELLED',s.refresh('BTC-EUR',CUTOFF,self.raw).reason)
        self.assertIsNone(s.budget.attempt(CUTOFF[:10],'BTC-EUR','vibe-trading'))
    def test_native_store_refuses_reparse_and_broad_acl(self):
        if os.name!='nt':self.skipTest('native Windows security only')
        from store import StoreLease
        broad=self.root/'broad';broad.mkdir()
        subprocess.run(['icacls',str(broad),'/grant','*S-1-1-0:(OI)(CI)F'],check=True,capture_output=True)
        with self.assertRaises(ValueError):StoreLease(broad)
        target=self.root/'target';target.mkdir();junction=self.root/'junction'
        result=subprocess.run(['cmd','/c','mklink','/J',str(junction),str(target)],capture_output=True)
        self.assertEqual(0,result.returncode,result.stderr)
        try:
            with self.assertRaises(ValueError):StoreLease(junction)
        finally:os.rmdir(junction)

if __name__=='__main__':unittest.main()
