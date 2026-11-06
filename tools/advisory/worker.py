"""Isolated upstream workers. Only public sealed data and JSON IPC cross boundary."""
import sys, os, json, hashlib, math
from pathlib import Path
from datetime import datetime, timezone
ROOT=Path(__file__).parent
sys.path.insert(0,str(ROOT))
from reports import native_vibe_decision
from market_contract import instrument_context,decode_snapshot,dataset_digest,INSTRUMENTS,TOOLS,strict_json
from runtime_contract import RuntimeContract,validate_contract
import argparse
parser=argparse.ArgumentParser()
parser.add_argument('tool',choices=TOOLS)
parser.add_argument('--instrument',choices=tuple(INSTRUMENTS),required=True)
parser.add_argument('--cutoff',required=True)
parser.add_argument('--dataset',type=Path,required=True)
parser.add_argument('--work-root',type=Path,required=True)
parser.add_argument('--runtime-contract',type=Path,required=True)
parser.add_argument('--preflight',action='store_true')
arguments=parser.parse_args()
contract=RuntimeContract(**strict_json(arguments.runtime_contract.read_bytes()))
validate_contract(contract)
TOOL=arguments.tool
source=Path(contract.runtimes[TOOL]['source'])
sys.path.insert(0,str(source/'agent' if TOOL=='vibe-trading' else source))
ROOT=arguments.work_root.resolve()
requested=arguments.instrument
CONTEXT=instrument_context(requested)
raw=arguments.dataset.read_bytes()
DATA=decode_snapshot(raw,requested,arguments.cutoff)
DIGEST=dataset_digest(raw)
import pandas as pd
FRAME=pd.DataFrame([{'Date':pd.to_datetime(int(r[0]),unit='ms'),'Open':float(r[1]),'High':float(r[2]),'Low':float(r[3]),'Close':float(r[4]),'Volume':float(r[5])} for r in DATA['Bars']])
SYMBOL=DATA['Instrument']; DATE=str(FRAME.iloc[-1]['Date'].date())
BASE=CONTEXT['base']; QUOTE=CONTEXT['quote']
EVIDENCE=ROOT/"evidence";EVIDENCE.mkdir(parents=True,exist_ok=True)
TOOL_LOG=[]

def log_tool(name,args,result):
    TOOL_LOG.append({'name':name,'args':args,'result':result,'DatasetSha256':DIGEST})
    suffix='-preflight' if '--preflight' in sys.argv else ''
    (EVIDENCE/f'{TOOL}{suffix}-tools.json').write_text(json.dumps(TOOL_LOG,indent=2,allow_nan=False))
    return result

def exchange(messages,tools=(),schema=None):
    payload={'messages':messages,'tools':list(tools),'schema':schema}
    print('REQUEST: '+json.dumps(payload,default=str),flush=True)
    line=sys.stdin.buffer.readline(65538)
    if not line.endswith(b'\n') or len(line)>65536:raise ValueError('bounded IPC response required')
    reply=strict_json(line,65536)
    if 'error' in reply: raise RuntimeError(reply['error'])
    return reply['reply'],reply.get('tokens',{})

def lc_model():
    from langchain_core.language_models.chat_models import BaseChatModel
    from langchain_core.outputs import ChatResult,ChatGeneration
    from langchain_core.messages import AIMessage
    from langchain_core.runnables import RunnableLambda
    from langchain_core.utils.function_calling import convert_to_openai_tool
    class HermesPublicModel(BaseChatModel):
        @property
        def _llm_type(self): return 'hermes-public-openai-codex'
        def bind_tools(self,tools,**kwargs):
            definitions=[convert_to_openai_tool(t)['function'] for t in tools]
            return self.bind(public_tools=definitions)
        def _generate(self,messages,stop=None,run_manager=None,**kwargs):
            written=[]
            for m in messages:
                x={'role':m.type,'content':m.content}
                if getattr(m,'tool_calls',None): x['tool_calls']=m.tool_calls
                if getattr(m,'tool_call_id',None): x['tool_call_id']=m.tool_call_id
                written.append(x)
            reply,tokens=exchange(written,kwargs.get('public_tools',[]))
            calls=[{'name':c['name'],'args':c['args'],'id':f'public_call_{len(TOOL_LOG)}_{i}','type':'tool_call'} for i,c in enumerate(reply['tool_calls'])]
            ai=AIMessage(content=reply['content'],tool_calls=calls)
            return ChatResult(generations=[ChatGeneration(message=ai)])
        def with_structured_output(self,schema,**kwargs):
            def invoke(prompt):
                messages=prompt.to_messages() if hasattr(prompt,'to_messages') else [{'role':'human','content':str(prompt)}]
                written=[{'role':getattr(m,'type','human'),'content':getattr(m,'content',m.get('content') if isinstance(m,dict) else str(m))} for m in messages]
                shape=schema.model_json_schema(); shape['additionalProperties']=False
                reply,_=exchange(written,schema=shape)
                return schema.model_validate(reply)
            return RunnableLambda(invoke)
    return HermesPublicModel()

def tradingagents(preflight):
    import tradingagents.dataflows.vendors.yahoo.snapshot as snap
    import tradingagents.dataflows.vendors.yahoo.market as market
    import tradingagents.agents.tools as tools
    from tradingagents.graph.setup import GraphSetup
    from tradingagents.graph.conditional_logic import ConditionalLogic
    from tradingagents.graph.propagation import Propagator
    from tradingagents.dataflows.config import run_config
    def sealed_load(symbol,as_of_date,**kwargs):
        if symbol!=SYMBOL or as_of_date>DATE: raise ValueError('unsealed symbol/date')
        return FRAME[FRAME['Date']<=pd.Timestamp(as_of_date)].copy()
    # Scoped in this isolated worker only; native functions still compute/render.
    snap.load_ohlcv=sealed_load; market.load_ohlcv=sealed_load
    def route(method,symbol,*args):
        if symbol!=SYMBOL: raise ValueError('symbol mismatch')
        if method=='get_stock_data':
            start,end=args
            frame=sealed_load(symbol,min(end,DATE))
            result=frame[frame.Date>=pd.Timestamp(start)].to_csv(index=False)
        elif method=='get_indicators':
            result=market.get_stock_stats_indicators_window(symbol,*args)
        else: raise ValueError('vendor method forbidden')
        return log_tool(method,list(args),result)
    tools.route_to_vendor=route
    def snapshot(symbol,curr_date,look_back_days=30):
        result=snap.build_verified_market_snapshot(symbol,curr_date,look_back_days)
        return log_tool('get_verified_market_snapshot',{'curr_date':curr_date,'look_back_days':look_back_days},result)
    tools.build_verified_market_snapshot=snapshot
    computation=snapshot(SYMBOL,DATE,10)
    if preflight:
        return {'Instrument':SYMBOL,'bars':len(FRAME),'module':snap.__file__,'computation':computation,'DatasetSha256':DIGEST}
    model=lc_model()
    setup=GraphSetup(model,model,ConditionalLogic(max_debate_rounds=1,max_risk_discuss_rounds=1),max_tool_rounds=2)
    graph=setup.setup_graph(selected_analysts=('market',),memory_node=None).compile()
    context=f'Exact public OKX spot instrument {SYMBOL}, base {BASE}, quote {QUOTE}, 1Dutc. Latest completed candle date {DATE}, closing cutoff {DATA["AsOfUtc"]}. Not USD/USDT. Volume is base {BASE}. Dataset SHA256 {DIGEST}. No current partial candle, news, holdings or orderbook supplied. All research is advisory only; execution forbidden. No historical OOS or live execution claims. Get stock data, relevant indicators and verified snapshot; batch tool requests where independent. Limit each answer to 250 words.'
    state=Propagator().create_initial_state(SYMBOL,DATE,asset_type='crypto',instrument_context=context,portfolio_context='Holdings unknown; public market-only research, no account or position supplied. APEX alone can execute.')
    # All tool date windows end at the completed candle, never wall-clock today.
    with run_config({'report_language':'english','data_vendors':{'core_stock_apis':'yfinance','technical_indicators':'yfinance'},'data_dir':str(ROOT/'runtime'/SYMBOL/DIGEST/'tradingagents'/'data'),'data_cache_dir':str(ROOT/'runtime'/SYMBOL/DIGEST/'tradingagents'/'cache')}):
        final=graph.invoke(state,config={'recursion_limit':35})
    for key in ('market_report','investment_plan','trader_investment_plan','final_trade_decision','final_rating'):
        if not final.get(key): raise ValueError('incomplete upstream graph '+key)
    rating=final['final_rating']
    recommendation={'Buy':'BUY','Overweight':'BUY','Hold':'WAIT','Underweight':'SELL','Sell':'SELL'}.get(rating)
    if recommendation is None: raise ValueError('unparseable upstream rating')
    evidence={k:final.get(k) for k in ['market_report','investment_debate_state','investment_plan','trader_investment_plan','risk_debate_state','final_trade_decision','final_rating']}
    (EVIDENCE/'tradingagents-analysis.json').write_text(json.dumps(evidence,indent=2,default=str))
    return {'recommendation':recommendation,'summary':final['final_trade_decision'],'native_rating':rating,'module':snap.__file__,'orchestration_module':sys.modules[GraphSetup.__module__].__file__,'tool_calls':len(TOOL_LOG),'DatasetSha256':DIGEST}

def vibe(preflight):
    from vibe_compat import enable_exact_eur_identity
    enable_exact_eur_identity()
    import src.tools.technical_indicator_tool as indicator
    from src.quantlib.risk import historical_var
    from src.agent.tools import BaseTool,ToolRegistry
    # Only the loader seam is substituted, so 1Dutc values do not hit native 16:00 alias.
    def sealed_fetch(codes,start_date,end_date,**kwargs):
        if codes!=[SYMBOL] or end_date!=DATE or kwargs.get('max_rows')!=0:
            raise ValueError('unsealed Vibe market request')
        rows=[{'date':r['Date'].strftime('%Y-%m-%d'),'open':r['Open'],'high':r['High'],'low':r['Low'],'close':r['Close'],'volume':r['Volume']} for _,r in FRAME.iterrows() if r['Date']>=pd.Timestamp(start_date)]
        return {SYMBOL:rows,'_provenance':{SYMBOL:{'source':'okx','quote_currency':QUOTE,'interval':'1Dutc','volume_unit':BASE,'dataset_sha256':DIGEST}}}
    indicator.fetch_market_data=sealed_fetch
    native=indicator.TechnicalIndicatorTool()
    indicators=json.loads(native.execute(symbol=SYMBOL,interval='1d',lookback=len(FRAME),end_date=DATE))
    if not indicators.get('ok') or indicators['read_identity']['bar_count']!=len(FRAME): raise ValueError('native indicator failure')
    risk=float(historical_var(FRAME.Close.pct_change().dropna(),confidence=0.95,horizon=1))
    computation={'indicators':indicators,'historical_var_95_1d':risk}
    (EVIDENCE/'vibe-computation.json').write_text(json.dumps(computation,indent=2,allow_nan=False))
    class SafeIndicator(BaseTool):
        name='technical_indicators'; description=f'Native Vibe technical indicators for the sealed completed OKX {SYMBOL}, base {BASE}, quote {QUOTE}, 1Dutc snapshot.'
        parameters={'type':'object','properties':{'symbol':{'type':'string','enum':[SYMBOL]}},'required':['symbol'],'additionalProperties':False}; deterministic=True
        def execute(self,symbol,*,run_dir=None):
            # Native AgentLoop injects run_dir; accept but never read/use it.
            # It remains absent from the closed model-visible schema.
            if symbol!=SYMBOL: raise ValueError('unsealed Vibe symbol')
            result=json.loads(native.execute(symbol=SYMBOL,interval='1d',lookback=len(FRAME),end_date=DATE))
            return json.dumps(log_tool(self.name,{'symbol':symbol},result))
    class SafeRisk(BaseTool):
        name='historical_var'; description=f'Native Vibe historical daily empirical VaR at 95% confidence, sealed {SYMBOL}, base {BASE}, quote {QUOTE} public returns, horizon=1. No portfolio or position.'
        parameters=SafeIndicator.parameters; deterministic=True
        def execute(self,symbol,*,run_dir=None):
            # Native AgentLoop injects run_dir; accept but never read/use it.
            # It remains absent from the closed model-visible schema.
            if symbol!=SYMBOL: raise ValueError('unsealed Vibe symbol')
            result={'symbol':SYMBOL,'latest_date':DATE,'read_identity':indicators['read_identity'],'historical_var_95_1d':float(historical_var(FRAME.Close.pct_change().dropna(),confidence=.95,horizon=1)),'observations':len(FRAME)-1,'DatasetSha256':DIGEST}
            return json.dumps(log_tool(self.name,{'symbol':symbol},result))
    if preflight:
        rejected=0
        for tool in (SafeIndicator(),SafeRisk()):
            for args in ({},{'symbol':BASE+'-USDT'},{'symbol':SYMBOL,'extra':True}):
                try:tool.execute(**args)
                except (ValueError,TypeError):rejected+=1
        if rejected!=6:raise ValueError('unsafe Vibe argument acceptance')
        registry=ToolRegistry();registry.register(SafeIndicator());registry.register(SafeRisk())
        probe={name:json.loads(registry.execute(name,{'symbol':SYMBOL,'run_dir':str(ROOT/'runtime/vibe-trading')})) for name in registry.tool_names}
        if any(value.get('status')=='error' for value in probe.values()):raise ValueError('native registry runtime context incompatible: '+json.dumps(probe))
        return {'Instrument':SYMBOL,'bars':len(FRAME),'module':indicator.__file__,'computation':computation,'DatasetSha256':DIGEST,'tool_parameters':{t.name:t.parameters for t in (SafeIndicator(),SafeRisk())},'invalid_arguments_rejected':rejected}
    from src.providers.chat import LLMResponse,ToolCallRequest,LLMRuntimeSnapshot
    class HermesVibe:
        model_name='gpt-6.1-sol'
        runtime_snapshot=LLMRuntimeSnapshot(provider='openai-codex',configured_model='gpt-6.1-sol',reasoning_effort='low')
        def stream_chat(self,messages,tools=None,on_text_chunk=None,**kwargs):
            definitions=[t['function'] for t in tools or []]
            result,tokens=exchange(messages,definitions)
            if on_text_chunk and result['content']: on_text_chunk(result['content'])
            usage={'input_tokens':tokens.get('input',0),'output_tokens':tokens.get('output',0),'total_tokens':tokens.get('total',0)}
            return LLMResponse(content=result['content'],tool_calls=[ToolCallRequest(id=f'public_vibe_{len(TOOL_LOG)}_{i}',name=c['name'],arguments=c['args']) for i,c in enumerate(result['tool_calls'])],finish_reason='tool_calls' if result['tool_calls'] else 'stop',usage_metadata=usage,response_model=None)
        def chat(self,messages,**kwargs): return self.stream_chat(messages,**kwargs)
    registry=ToolRegistry(); registry.register(SafeIndicator()); registry.register(SafeRisk())
    from src.agent.loop import AgentLoop
    from src.agent.memory import WorkspaceMemory
    memory=WorkspaceMemory()
    loop=AgentLoop(registry=registry,llm=HermesVibe(),memory=memory,max_iterations=3)
    prompt=f'Public advisory-only technical research of exact OKX spot {SYMBOL} (quote EUR), completed 1Dutc bars ending {DATE}, cutoff {DATA["AsOfUtc"]}. Dataset SHA256 {DIGEST}. No account or holdings supplied. Execution is forbidden. First call both technical_indicators and historical_var with symbol {SYMBOL}; then analyze their actual results. Never request any other tools. Return final content as a JSON object with exactly Recommendation (BUY/SELL/WAIT) and Summary (plain English <=850 characters), stating evidence and limitations. For every numerical claim follow the native figures contract, placing a fenced figures block AFTER the JSON, using the exact tool call IDs and numeric field paths from this run (no aliases). The native gate will validate and strip that block; the released content must remain valid JSON. Do not repeat the dataset hash in prose. No news/orderbook/fundamentals/history validation or shorting claims. This is not OOS validation.'
    final=loop.run(prompt)
    (EVIDENCE/'vibe-analysis.json').write_text(json.dumps(final,indent=2,default=str))
    import shutil
    shutil.copyfile(Path(memory.run_dir)/'artifacts/grounding_evidence.json',EVIDENCE/'vibe-grounding.json')
    decision=native_vibe_decision(final)
    if {x['name'] for x in TOOL_LOG}!={'technical_indicators','historical_var'}: raise ValueError('native Vibe computations were not called')
    return {'native_rating':decision['Recommendation'],'recommendation':decision['Recommendation'],'summary':decision['Summary'],'module':indicator.__file__,'orchestration_module':sys.modules[AgentLoop.__module__].__file__,'tool_calls':len(TOOL_LOG),'DatasetSha256':DIGEST}

if __name__=='__main__':
    try:
        result=(vibe if TOOL=='vibe-trading' else tradingagents)('--preflight' in sys.argv)
        observed={'platform':sys.platform,'python':sys.executable,'prefix':sys.prefix,'pandas':pd.__version__,'imports':{str(Path(m.__file__).resolve()):hashlib.sha256(Path(m.__file__).read_bytes()).hexdigest() for m in list(sys.modules.values()) if getattr(m,'__file__',None) and Path(m.__file__).is_file()}}
        (EVIDENCE/'runtime-observed.json').write_text(json.dumps(observed,indent=2))
        for key in ('module','orchestration_module'):
            if key in result and not Path(result[key]).resolve().is_relative_to(source.resolve()):raise ValueError('unexpected upstream module path')
        print('RESULT: '+json.dumps(result,default=str,allow_nan=False),flush=True)
    except Exception as exc:
        import traceback
        traceback.print_exc(file=sys.stderr)
        print('RESULT: '+json.dumps({'error':type(exc).__name__+': '+str(exc)}),flush=True)
        sys.exit(1)
