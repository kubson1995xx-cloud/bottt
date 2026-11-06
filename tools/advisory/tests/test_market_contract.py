"""Source-linked harness runs the real ReadSeriesAsync; not a Python imitation."""
import os,sys,subprocess,tempfile,shutil,unittest
from pathlib import Path
from datetime import datetime,timezone,timedelta
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from market_contract import *

def fixture(pair='BTC-EUR',cutoff='2026-11-06T00:00:00Z'):
    end=utc(cutoff)
    return [[str(int((end-timedelta(days=260-i)).timestamp())*1000),'100.0000000000000000000000001','103.00','99.0','101.000','1000.123456789012345678901234','1'] for i in range(260)]

class MarketTests(unittest.TestCase):
    def test_roundtrip_and_mutation(self):
        rows=fixture(); raw=canonical_snapshot('BTC-EUR','2026-11-06T00:00:00Z',rows)
        self.assertEqual(raw,canonical_snapshot('BTC-EUR','2026-11-06T00:00:00Z',strict_json(raw)['Bars']))
        rows[-1][4]='102'; self.assertNotEqual(dataset_digest(raw),dataset_digest(canonical_snapshot('BTC-EUR','2026-11-06T00:00:00Z',rows)))
    def test_source_quote_cutoff_units_refused(self):
        data=strict_json(canonical_snapshot('BTC-EUR','2026-11-06T00:00:00Z',fixture()))
        for key,value in [('Source','www.okx.com history-candles'),('QuoteCurrency','USDT'),('AsOfUtc','2026-11-06T01:00:00Z'),('VolumeUnit','EUR')]:
            with self.subTest(key=key),self.assertRaises(ValueError): validate_dataset({**data,key:value})
    def test_no_float_or_partial_or_gap(self):
        for value in [100.1,'NaN','1e2','0.00000000000000000000000000001']:
            rows=fixture();rows[0][1]=value
            with self.subTest(value=value),self.assertRaises(ValueError):canonical_snapshot('BTC-EUR','2026-11-06T00:00:00Z',rows)
        rows=fixture();rows[-1][6]='0'
        with self.assertRaises(ValueError):canonical_snapshot('BTC-EUR','2026-11-06T00:00:00Z',rows)
    def test_actual_csharp_strategy_export(self):
        repo=Path(__file__).resolve().parents[3]
        dotnet=os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet') or str(Path.home()/'.dotnet/dotnet')
        self.assertTrue(Path(dotnet).exists(),'dotnet required for actual strategy parity')
        with tempfile.TemporaryDirectory() as temp:
            temp=Path(temp); rows=fixture(); provider=[]
            # Real feed's descending response includes a currently incomplete candle.
            provider.append([str(int(utc('2026-11-06T00:00:00Z').timestamp())*1000),'100','103','99','101','200','0','0','0'])
            provider.extend([*r[:6],'987.123','456.789',r[6]] for r in reversed(rows))
            (temp/'response.json').write_text(json.dumps({'code':'0','data':provider}))
            result=subprocess.run([dotnet,'run','--project',str(repo/'tools/advisory/StrategyExport/StrategyExport.csproj'),'--',str(temp/'response.json'),str(temp/'actual.json')],cwd=repo,capture_output=True,text=True,timeout=180)
            self.assertEqual(0,result.returncode,result.stdout+result.stderr)
            expected=canonical_snapshot('BTC-EUR','2026-11-06T00:00:00Z',rows)
            self.assertEqual(expected,(temp/'actual.json').read_bytes())
            destination=os.environ.get('ADVISORY_PARITY_EVIDENCE')
            if destination:
                out=Path(destination);out.mkdir(parents=True,exist_ok=True)
                for p in temp.iterdir():shutil.copy2(p,out/p.name)
                (out/'python.json').write_bytes(expected)
                (out/'digest.txt').write_text(dataset_digest(expected))

if __name__=='__main__':unittest.main()
