"""Reopen earlier real native graph outputs; never rerun graphs or infer advice.

The source-bound run42248494410 used SYNTHETIC candles and FIXTURE inference.
This control proves only that final storage admission can consume that existing
native graph tree without mutating its charged budget or republishing/retrying.
"""
import sys,json,zipfile,hashlib,os,unittest,sqlite3
from contextlib import closing
from pathlib import Path
from datetime import timedelta
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import test_store_descendants as common
from test_store_descendants import CUTOFF,NOW
from market_contract import canonical_snapshot,utc,INSTRUMENTS
from supervisor import Supervisor
from runtime_contract import RuntimeContract
from store import StoreLease

class RetainedGraphStoreTests(unittest.TestCase):
    setUp=common.DescendantTests.setUp
    tearDown=common.DescendantTests.tearDown
    service=common.DescendantTests.service
    def test_retained_native_graph_tree_readmission(self):
        folder=Path(__file__).parent/'fixtures'
        manifest=json.loads((folder/'retained-native-store.json').read_bytes())
        archive=folder/'retained-native-store.zip'
        self.assertEqual(manifest['zipSha256'],hashlib.sha256(archive.read_bytes()).hexdigest())
        lease=StoreLease(self.root)
        try:
            with zipfile.ZipFile(archive) as z:
                self.assertEqual(sorted(manifest['files']),sorted(z.namelist()))
                for name in z.namelist():
                    self.assertNotIn('..',Path(name).parts);self.assertFalse(Path(name).is_absolute())
                    raw=z.read(name);self.assertEqual(manifest['files'][name],hashlib.sha256(raw).hexdigest())
                    target=self.root/name;lease.directory(target.parent)
                    fd=os.open(target,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
                    with os.fdopen(fd,'wb') as f:f.write(raw)
        finally:lease.close()
        # Additive policy migration may change DB bytes, not historical charges.
        # The immutable retained archive itself is never modified.
        with closing(sqlite3.connect(self.root/'budget.sqlite')) as db:
            before_requests=db.execute('SELECT id,attempt,day,milliseconds,observed_ms,outcome FROM requests ORDER BY id').fetchall()
            before_attempts=db.execute('SELECT * FROM attempts ORDER BY id').fetchall()
            original_charge=db.execute('SELECT COALESCE(SUM(milliseconds),0) FROM requests').fetchone()[0]
        rows=[];end=utc(CUTOFF)
        for i in range(260):
            close=1000+i+(i%7)*2
            rows.append([str(int((end-timedelta(days=260-i)).timestamp())*1000),str(close-1),str(close+3),str(close-3),str(close),str(1000+i),'1'])
        s=self.service()
        migrated=(self.root/'budget.sqlite').read_bytes()
        with patch.object(s,'_analysis',side_effect=AssertionError('retained attempt MUST NOT rerun inference/graph')):
            for pair in INSTRUMENTS:
                result=s.refresh(pair,CUTOFF,canonical_snapshot(pair,CUTOFF,rows))
                self.assertEqual('REUSED',result.reason);self.assertEqual('fixture',result.state)
        self.assertEqual(migrated,(self.root/'budget.sqlite').read_bytes())
        with closing(sqlite3.connect(self.root/'budget.sqlite')) as db:
            self.assertEqual(before_requests,db.execute('SELECT id,attempt,day,milliseconds,observed_ms,outcome FROM requests ORDER BY id').fetchall())
            self.assertEqual(before_attempts,db.execute('SELECT * FROM attempts ORDER BY id').fetchall())
            self.assertEqual(original_charge,db.execute('SELECT SUM(COALESCE(charged_ms,milliseconds)) FROM requests').fetchone()[0])
            self.assertEqual([('legacy-full-v1',None)]*len(before_requests),db.execute('SELECT charge_policy,charged_ms FROM requests').fetchall())
        print('RETAINED_NATIVE_GRAPH_TREE_ADMITTED_6_REPORTS; NO_GRAPH_RERUN; NO_GENUINE_INFERENCE',flush=True)

if __name__=='__main__':unittest.main()
