"""Run only under a validated native environment; synthetic PUBLIC computation."""
import asyncio
from dataclasses import asdict
from datetime import date, timedelta
import hashlib
import importlib.abc
import importlib.metadata
import importlib.util
import json
import math
import os
from pathlib import Path
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0,str(HERE))
from runtime_contract import RuntimeContract, PINS, validate_contract, sha256, MODEL, PROVIDER


def synthetic_frame():
    import pandas as pd
    rows=[]
    for i in range(260):
        close=100 + i/10 + math.sin(i/5)*3
        rows.append({'Date':pd.Timestamp(date(2025,1,1)+timedelta(days=i)),
                     'Open':close-0.2,'High':close+1,'Low':close-1,'Close':close,'Volume':1000+i})
    return pd.DataFrame(rows)


def vibe(source):
    # Reuse the existing source-hash-pinned isolated EUR compatibility adapter.
    module='src.agent.grounding.identity'
    fork=HERE/'vendor/vibe_identity.py'
    provenance=json.loads((HERE/'vendor/provenance.json').read_text())['vibe_identity.py']
    if sha256(fork)!=provenance['sha256']: raise ValueError('compatibility hash mismatch')
    class Finder(importlib.abc.MetaPathFinder):
        def find_spec(self,fullname,path=None,target=None):
            if fullname==module: return importlib.util.spec_from_file_location(fullname,fork)
    sys.meta_path.insert(0,Finder())
    import pandas as pd
    import src.tools.technical_indicator_tool as indicator
    from src.quantlib.risk import historical_var
    from src.agent.loop import AgentLoop  # Genuine upstream orchestration import, not inference.
    frame=synthetic_frame(); symbol='BTC-EUR'; end=str(frame.iloc[-1].Date.date())
    digest=hashlib.sha256(frame.to_csv(index=False).encode()).hexdigest()
    calls=[]
    def fetch(codes,start_date,end_date,**kwargs):
        if codes!=[symbol] or end_date!=end or kwargs.get('max_rows')!=0: raise ValueError('unsealed request')
        calls.append({'codes':codes,'start':start_date,'end':end_date})
        rows=[{'date':r.Date.strftime('%Y-%m-%d'),'open':r.Open,'high':r.High,'low':r.Low,'close':r.Close,'volume':r.Volume} for r in frame.itertuples() if r.Date>=pd.Timestamp(start_date)]
        return {symbol:rows,'_provenance':{symbol:{'source':'synthetic-public-fixture','quote_currency':'EUR','interval':'1Dutc','volume_unit':'BTC','dataset_sha256':digest}}}
    indicator.fetch_market_data=fetch
    result=json.loads(indicator.TechnicalIndicatorTool().execute(symbol=symbol,interval='1d',lookback=len(frame),end_date=end))
    risk=float(historical_var(frame.Close.pct_change().dropna(),confidence=.95,horizon=1))
    if not calls or result.get('ok') is not True or result['read_identity']['bar_count']!=260 or not math.isfinite(risk):
        raise ValueError('Vibe native computation failed')
    return {'pandas_version':pd.__version__,'computation_ok':True,'synthetic_public_input':True,
            'dataset_sha256':digest,'bars':260,'instrument':symbol,'quote':'EUR',
            'module':indicator.__file__,'orchestration_module':sys.modules[AgentLoop.__module__].__file__,
            'computation':result,'historical_var_95_1d':risk,'loader_calls':calls}


def tradingagents(source):
    import pandas as pd
    import tradingagents.dataflows.vendors.yahoo.snapshot as snap
    from tradingagents.graph.setup import GraphSetup
    frame=synthetic_frame(); symbol='BTC-EUR'; end=str(frame.iloc[-1].Date.date());calls=[]
    def load(symbol_arg,as_of_date,**kwargs):
        if symbol_arg!=symbol or as_of_date!=end or kwargs.get('fill_gaps') is not False: raise ValueError('unsealed request')
        calls.append({'symbol':symbol_arg,'as_of_date':as_of_date})
        return frame.copy()
    snap.load_ohlcv=load
    output=snap.build_verified_market_snapshot(symbol,end,10)
    if not calls or symbol not in output or 'N/A' in output or '| rsi |' not in output or '| close_200_sma |' not in output:
        raise ValueError('TradingAgents native snapshot computation failed')
    return {'pandas_version':pd.__version__,'computation_ok':True,'synthetic_public_input':True,
            'bars':260,'instrument':symbol,'quote':'EUR','dataset_sha256':hashlib.sha256(frame.to_csv(index=False).encode()).hexdigest(),
            'module':snap.__file__,'orchestration_module':sys.modules[GraphSetup.__module__].__file__,
            'computation':output,'loader_calls':calls}


async def hermes(source):
    from hermes_cli.gateway_client import connect_gateway
    from hermes_cli.gateway_chat_view import GatewayChatView
    from hermes_cli.stream_json import StreamJsonEmitter
    from gateway.runtime_bootstrap_windows import NativeControlServer
    from hermes_cli.auth import resolve_codex_runtime_credentials, AuthError
    from hermes_transport import is_missing_auth, check_policy
    from hermes_cli.config import load_config
    import subprocess
    check_policy(load_config())
    help_result=subprocess.run([sys.executable,'-m','hermes_cli.main','auth','add','--help'],
                               cwd=source,capture_output=True,text=True,encoding='utf-8',timeout=30)
    if help_result.returncode or '--browser' not in help_result.stdout:
        raise ValueError('supported local subscription login entry unavailable')
    (Path(sys.argv[3]).parent/'hermes-login-help.txt').write_text(help_result.stdout,encoding='utf-8')
    # Actual local canonical client starts/discovers the supported native owner;
    # no fake RPC server, tokens, paid key, inference or provider fallback.
    async with connect_gateway() as client:
        description=await client.rpc('runtime.describe')
        if not isinstance(description,dict): raise ValueError('invalid native gateway description')
        # Only retain bounded capability labels, never whole runtime config/identity.
        bootstrap={'rpc':'runtime.describe','response_keys':sorted(description.keys())}
    try:
        resolve_codex_runtime_credentials(read_only=True)
    except AuthError as error:
        if not is_missing_auth(error): raise
        code=error.code
    else: raise ValueError('CI must not have local subscription auth')
    return {'bootstrap_ok':True,'bootstrap':bootstrap,'no_auth_refused':True,'auth_code':code,
            'inference_accepted':False,'model':MODEL,'provider':PROVIDER,
            'module':sys.modules['hermes_cli.gateway_client'].__file__,
            'native_bootstrap_module':sys.modules['gateway.runtime_bootstrap_windows'].__file__}


def main():
    name,contract_file,output=sys.argv[1:]
    contract=RuntimeContract(**json.loads(Path(contract_file).read_text()))
    validate_contract(contract)
    row=contract.runtimes[name]
    if Path(sys.executable).resolve()!=Path(row['python']).resolve() or str(Path(sys.prefix).resolve())!=row['prefix']:
        raise ValueError('actual executable/prefix mismatch')
    source=Path(row['source'])
    sys.path.insert(0,str(source/'agent' if name=='vibe-trading' else source))
    observed={'python':str(Path(sys.executable).resolve()),'prefix':str(Path(sys.prefix).resolve()),
              'platform':sys.platform,'python_version':list(sys.version_info[:3]),'import_ok':True}
    result=asyncio.run(asyncio.wait_for(hermes(source),100)) if name=='hermes' else (vibe(source) if name=='vibe-trading' else tradingagents(source))
    observed.update(result)
    # Bind imports to the validated source tree, not a similarly named installed package.
    for key in ('module','orchestration_module','native_bootstrap_module'):
        if key in observed and not Path(observed[key]).resolve().is_relative_to(source.resolve()):
            raise ValueError('unexpected native import path')
    observed['packages']={d.metadata['Name']:d.version for d in importlib.metadata.distributions()}
    observed['import_hashes']={str(Path(m.__file__).resolve()):sha256(m.__file__) for m in list(sys.modules.values())
                             if getattr(m,'__file__',None) and Path(m.__file__).is_file()}
    Path(output).write_text(json.dumps(observed,indent=2,allow_nan=False),encoding='utf-8')


if __name__=='__main__': main()
