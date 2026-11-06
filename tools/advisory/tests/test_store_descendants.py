"""I1 unchanged old/final operands: actual Supervisor, no upstream/model calls."""
import os,sys,tempfile,subprocess,unittest,uuid,shutil,json,hashlib
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from supervisor import Supervisor
from store import StoreLease
from runtime_contract import RuntimeContract
from market_contract import canonical_snapshot,utc
from test_market_contract import fixture
CUTOFF='2026-11-06T00:00:00Z'
from datetime import timedelta
NOW=utc(CUTOFF)+timedelta(hours=12)

class DescendantTests(unittest.TestCase):
    def setUp(self):
        self.base=Path(tempfile.gettempdir()).resolve()/('apex-store-i1-'+uuid.uuid4().hex)
        self.base_lease=StoreLease(self.base);self.services=[];self.links=[]
        self.root=self.base/'store';seed=StoreLease(self.root);seed.close()
        self.outside=self.base/'outside';seed=StoreLease(self.outside);seed.close()
        self.raw=canonical_snapshot('BTC-EUR',CUTOFF,fixture())
    def tearDown(self):
        for s in self.services:s.close()
        self.base_lease.close()
        for p in self.links:
            if os.path.lexists(p):
                if os.name=='nt' and p.is_dir():os.rmdir(p)
                else:p.unlink()
        shutil.rmtree(self.base)
    def service(self):
        s=Supervisor(self.root,RuntimeContract({}),fixture=True,clock=lambda:NOW)
        self.services.append(s);return s
    def junction(self,path):
        if os.name=='nt':
            r=subprocess.run(['cmd','/c','mklink','/J',str(path),str(self.outside)],capture_output=True)
            self.assertEqual(0,r.returncode,r.stderr)
        else:path.symlink_to(self.outside,target_is_directory=True)
        self.links.append(path)
    def broad(self,path):
        if os.name=='nt':subprocess.run(['icacls',str(path),'/grant','*S-1-1-0:F'],check=True,capture_output=True)
        else:path.chmod(0o777 if path.is_dir() else 0o666)
    def refused(self,existing=None):
        before=existing.read_bytes() if existing else None
        pointer_existed=(self.root/'BTC-EUR/CURRENT.json').exists()
        refused=False;reason=None;status=None
        try:
            s=self.service()
            with patch('supervisor.validate_contract'),patch.object(s,'_analysis',side_effect=TimeoutError('fixture failure solely to avoid upstream/model work')):
                status=s.refresh('BTC-EUR',CUTOFF,self.raw)
        except ValueError as e:refused=True;reason=str(e)
        outside_files=[str(p.relative_to(self.outside)) for p in self.outside.rglob('*')]
        print(json.dumps({'case':self.id(),'refused':refused,'reason':reason,'state':getattr(status,'state',None),'status':getattr(status,'reason',None),'outside_files':outside_files}),flush=True)
        self.assertTrue(refused,'unsafe descendant admitted by actual Supervisor')
        self.assertEqual([],outside_files,'outside tree mutated')
        if existing:self.assertEqual(before,existing.read_bytes(),'budget mutated before refusal')
        else:self.assertFalse((self.root/'budget.sqlite').exists(),'budget created before refusal')
        self.assertEqual(pointer_existed,(self.root/'BTC-EUR/CURRENT.json').exists(),'publication before refusal')
    def test_pair_junction_exact_portable_shape(self):
        # Absolute 0700 root, BTC-EUR directory link outside, actual refresh,
        # injected analysis failure only to prevent any graph/model invocation.
        self.junction(self.root/'BTC-EUR');self.refused()
    def test_pair_broad_acl(self):
        p=self.root/'BTC-EUR';p.mkdir(mode=0o777 if os.name=='nt' else 0o700);self.broad(p);self.refused()
    def test_budget_broad_acl_existing_no_mutation(self):
        s=self.service();s.close();p=self.root/'budget.sqlite';self.broad(p);self.refused(p)
    def test_budget_hardlink_existing_no_mutation(self):
        s=self.service();s.close();p=self.root/'budget.sqlite';os.link(p,self.base/'budget-alias');self.refused(p)
    def test_budget_sidecar_junction(self):
        self.junction(self.root/'budget.sqlite-journal');self.refused()
    def test_report_generations_junction(self):
        p=self.root/'BTC-EUR';p.mkdir(mode=0o777 if os.name=='nt' else 0o700);self.junction(p/'generations');self.refused()
    def test_attempt_directory_broad_acl(self):
        p=self.root/'BTC-EUR/attempts/existing';p.mkdir(parents=True,mode=0o777 if os.name=='nt' else 0o700);self.broad(p);self.refused()
    def test_native_report_file_hardlink(self):
        p=self.root/'BTC-EUR/generations'/('1'*32);p.mkdir(parents=True,mode=0o777 if os.name=='nt' else 0o700)
        f=p/'vibe-trading-native.json';f.write_bytes(b'{}');os.link(f,self.base/'native-alias');self.refused()
    def test_current_wrong_type(self):
        (self.root/'BTC-EUR/CURRENT.json').mkdir(parents=True,mode=0o777 if os.name=='nt' else 0o700);self.refused()
    def test_created_after_initial_admission_refuses_before_attempt(self):
        s=self.service();p=self.root/'BTC-EUR';self.junction(p)
        before=(self.root/'budget.sqlite').read_bytes();denied=False
        try:
            with patch('supervisor.validate_contract'),patch.object(s,'_analysis',side_effect=TimeoutError('fixture failure')):s.refresh('BTC-EUR',CUTOFF,self.raw)
        except ValueError:denied=True
        self.assertTrue(denied,'post-admission unsafe child admitted')
        self.assertEqual(before,(self.root/'budget.sqlite').read_bytes());self.assertEqual([],list(self.outside.iterdir()))
    def test_safe_existing_graph_and_repeated_current_publication(self):
        for _ in range(2):
            s=self.service()
            with patch('supervisor.validate_contract'),patch.object(s,'_analysis',side_effect=TimeoutError('fixture failure')):status=s.refresh('BTC-EUR',CUTOFF,self.raw)
            self.assertIn(status.reason,('COMPLETED','REUSED'));s.close()
        self.assertEqual(3,len(list((self.root/'BTC-EUR/generations').iterdir())))
        self.assertTrue((self.root/'BTC-EUR/CURRENT.json').is_file())
    @unittest.skipUnless(os.name=='nt','Windows no-delete-sharing lease only')
    def test_hold_created_pair_directory_blocks_replacement_until_close(self):
        s=self.service()
        with patch('supervisor.validate_contract'),patch.object(s,'_analysis',side_effect=TimeoutError('fixture failure')):s.refresh('BTC-EUR',CUTOFF,self.raw)
        p=self.root/'BTC-EUR';moved=self.root/'moved'
        with self.assertRaises(OSError):p.rename(moved)
        s.close();p.rename(moved);self.assertTrue(moved.is_dir())

if __name__=='__main__':unittest.main()
