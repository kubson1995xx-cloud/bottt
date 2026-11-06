"""Native six genuine graphs with finite fixture inference; NOT model acceptance."""
import json,sys
from pathlib import Path
from datetime import timedelta
from dataclasses import asdict
from runtime_contract import RuntimeContract,validate_contract
from market_contract import *
from supervisor import Supervisor
from fixture_inference import FiniteInference
from reports import read_pair

def main(root,contract_file):
    contract=RuntimeContract(**strict_json(contract_file.read_bytes()));validate_contract(contract)
    cutoff='2026-11-06T00:00:00Z';end=utc(cutoff);rows=[]
    for i in range(260):
        close=1000+i+(i%7)*2
        rows.append([str(int((end-timedelta(days=260-i)).timestamp())*1000),str(close-1),str(close+3),str(close-3),str(close),str(1000+i),'1'])
    service=None;observed=[]
    try:
        for pair in INSTRUMENTS:
            raw=canonical_snapshot(pair,cutoff,rows);data=decode_snapshot(raw,pair,cutoff)
            inference=FiniteInference(pair,cutoff)
            service=Supervisor(root/'service',contract,inference=inference,fixture=True,clock=lambda:end+timedelta(hours=12),request_ms=18000)
            result=service.refresh(pair,cutoff,raw)
            manifest,entries=read_pair(root/'service'/pair,data,end+timedelta(hours=12))
            reused=service.refresh(pair,cutoff,raw)
            result_row={'pair':pair,'status':asdict(result),'reused_generation':reused.manifest['Generation']==manifest['Generation'],'requests':inference.calls,'prompt_sizes':inference.prompt_sizes,'tools':entries}
            observed.append(result_row)
            (root/'evidence/native-graphs.json').write_text(json.dumps({'fixture_inference':True,'genuine_model_output':False,'public_data':'synthetic canonical series','pairs':observed,'usage':service.budget.usage(cutoff[:10])},indent=2))
            if any(entry['error'] or not entry['native'] for entry in entries.values()):raise AssertionError('real native upstream graph failed; see retained per-attempt IPC/evidence')
            if not result_row['reused_generation']:raise AssertionError('restart generation not reused')
            for tool,entry in entries.items():
                if entry['native']['DatasetSha256']!=dataset_digest(raw) or entry['native']['tool_calls']<2:raise AssertionError('native tool/data binding missing')
            service.close();service=None
        print('NATIVE_SIX_GENUINE_GRAPHS_WITH_FIXTURE_INFERENCE_PASS; NO_GENUINE_MODEL_OUTPUT')
    finally:
        if service:service.close()
if __name__=='__main__':main(Path(sys.argv[1]).resolve(),Path(sys.argv[2]).resolve())
