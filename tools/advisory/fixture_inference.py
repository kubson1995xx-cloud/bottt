"""FINITE deterministic protocol fixture, NEVER genuine model output.

Real upstream graphs/native tool nodes execute; only the inference replies below
are fixture data. No network model calls, auth tokens or account operands.
"""
import json
from datetime import timedelta
from market_contract import utc

class FiniteInference:
    def __init__(self,pair,cutoff):self.pair=pair;self.end=(utc(cutoff)-timedelta(days=1)).strftime('%Y-%m-%d');self.calls={};self.prompt_sizes=[]
    def __call__(self,tool,payload,prompt,seconds):
        n=self.calls.get(tool,0)+1;self.calls[tool]=n
        if n>16:raise ValueError('finite inference fixture exhausted')
        self.prompt_sizes.append({'tool':tool,'characters':len(prompt)})
        schema=payload['schema']
        if schema:
            def value(shape):
                if '$ref' in shape:return value(schema['$defs'][shape['$ref'].split('/')[-1]])
                if 'enum' in shape:return 'Hold' if 'Hold' in shape['enum'] else shape['enum'][0]
                if 'anyOf' in shape:return None if any(s.get('type')=='null' for s in shape['anyOf']) else value(shape['anyOf'][0])
                if shape.get('type')=='object':return {key:value(s) for key,s in shape.get('properties',{}).items() if key in shape.get('required',[])}
                if shape.get('type')=='array':return []
                if shape.get('type') in ('number','integer'):return 0
                if shape.get('type')=='boolean':return False
                return 'FIXTURE ONLY. Public sealed EUR computation reviewed; no account or executable quote provided. No action authorized.'
            reply=value(schema)
        elif tool=='vibe-trading':
            if n==1:
                reply={'content':'','tool_calls':[{'name':t['name'],'args':{'symbol':self.pair}} for t in payload['tools']]}
            else:
                evidence=[m for m in payload['messages'] if m.get('role')=='tool']
                indicator=next(m for m in evidence if 'latest_close' in str(m['content']))
                content=json.loads(indicator['content']);close=content['latest_close']
                decision={'Recommendation':'WAIT','Summary':f'{self.pair} closing price {close} EUR from sealed native indicators. Fixture-only inference; holdings, news and executable quotes unknown.'}
                text=json.dumps(decision)+f'\n```figures\n{close} | observed | latest_close | {indicator["tool_call_id"]}\n```'
                reply={'content':text,'tool_calls':[]}
        elif n==1:
            calls=[]
            for t in payload['tools']:
                parameters=t['parameters'];args={}
                for key in parameters.get('properties',{}):
                    if key=='symbol':args[key]=self.pair
                    elif key in ('curr_date','end_date'):args[key]=self.end
                    elif key=='start_date':args[key]=(utc(self.end+'T00:00:00Z')-timedelta(days=210)).strftime('%Y-%m-%d')
                    elif key=='indicator':args[key]='close_200_sma'
                    elif key in ('look_back_days','lookback_days'):args[key]=10
                    elif key in parameters.get('required',[]):raise ValueError('unhandled fixture tool argument '+key)
                calls.append({'name':t['name'],'args':args})
            reply={'content':'','tool_calls':calls}
        else:reply={'content':'FIXTURE ONLY. Native sealed EUR market computation reviewed. Holdings, news and executable quotes unknown. No action authorized.','tool_calls':[]}
        return {'fixture':True,'text':json.dumps(reply),'tokens':None,'route_observed':False}
