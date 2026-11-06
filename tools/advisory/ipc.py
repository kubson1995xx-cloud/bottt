"""Portable bounded subprocess IPC and fail-closed application tool protocol."""
import json,queue,subprocess,threading,time
from pathlib import Path
from market_contract import strict_json
ALLOWED={'vibe-trading':{'technical_indicators','historical_var'},'tradingagents':{'get_stock_data','get_indicators','get_verified_market_snapshot'}}
MAX_PROMPT=250000

def request_prompt(tool,payload):
    if tool not in ALLOWED or type(payload) is not dict or set(payload)!={'messages','tools','schema'} or type(payload['messages']) is not list or not payload['messages'] or type(payload['tools']) is not list:raise ValueError('IPC request schema')
    seen=set()
    for definition in payload['tools']:
        if type(definition) is not dict or set(definition)-{'name','description','parameters','strict'} or definition.get('name') not in ALLOWED[tool] or definition['name'] in seen or type(definition.get('parameters')) is not dict:raise ValueError('unknown computational tool')
        seen.add(definition['name'])
    schema=payload['schema']
    if schema is not None and (type(schema) is not dict or payload['tools']):raise ValueError('structured schema refused')
    for message in payload['messages']:
        if type(message) is not dict or set(message)-{'role','content','tool_calls','tool_call_id','name','reasoning_content'} or message.get('role') not in ('system','human','ai','tool','user','assistant') or not isinstance(message.get('content'),(str,list)):raise ValueError('message schema')
        reasoning=message.get('reasoning_content')
        if reasoning is not None and (type(reasoning)is not str or len(reasoning)>16000):raise ValueError('bounded native reasoning field required')
    formatting=('Return JSON matching this schema, no fences: '+json.dumps(schema)) if schema is not None else 'Return exactly JSON {"content":"analysis","tool_calls":[{"name":"offered name","args":{}}]}. Only offered read-only computational tools. Empty tool_calls for final prose.'
    prompt='Public-only advisory, execution forbidden. No shell/files/broker/auth/web tools. Exact EUR completed 1Dutc inputs; holdings unknown. Application JSON tool requests are NOT Hermes tools. '+formatting+'\n'+json.dumps(payload,ensure_ascii=True,allow_nan=False)
    if len(prompt)>MAX_PROMPT:raise ValueError('oversized public prompt')
    return prompt

def validate_shape(value,schema,root=None,depth=0):
    """Fail-closed subset emitted by pinned LangChain/Pydantic public schemas."""
    import re,math
    if depth>16 or type(schema) is not dict:raise ValueError('schema depth/type refused')
    root=root or schema
    annotations={'title','description','default','$defs','examples'}
    supported=annotations|{'$ref','type','enum','const','anyOf','properties','required','additionalProperties','items','minItems','maxItems','minLength','maxLength','minimum','maximum','exclusiveMinimum','exclusiveMaximum','pattern'}
    if set(schema)-supported:raise ValueError('unsupported schema keyword')
    if '$ref' in schema:
        ref=schema['$ref']
        if not isinstance(ref,str) or not ref.startswith('#/$defs/') or '/' in ref[8:]:raise ValueError('external/invalid schema ref')
        return validate_shape(value,root['$defs'][ref[8:]],root,depth+1)
    if 'anyOf' in schema:
        for candidate in schema['anyOf']:
            try:validate_shape(value,candidate,root,depth+1);return
            except ValueError:pass
        raise ValueError('schema union mismatch')
    kind=schema.get('type')
    matches={'object':type(value) is dict,'array':type(value) is list,'string':type(value) is str,'integer':type(value) is int,'number':type(value) in (int,float) and math.isfinite(value),'boolean':type(value) is bool,'null':value is None}
    if kind is not None and (kind not in matches or not matches[kind]):raise ValueError('schema value type')
    if 'enum' in schema and not any(type(value) is type(x) and value==x for x in schema['enum']):raise ValueError('schema enum')
    if 'const' in schema and (type(value) is not type(schema['const']) or value!=schema['const']):raise ValueError('schema const')
    if type(value) is dict:
        properties=schema.get('properties',{})
        if set(schema.get('required',[]))-set(value) or set(value)-set(properties):raise ValueError('schema object fields')
        for key,item in value.items():validate_shape(item,properties[key],root,depth+1)
    elif type(value) is list:
        if len(value)<schema.get('minItems',0) or len(value)>schema.get('maxItems',10000):raise ValueError('schema array bound')
        for item in value:validate_shape(item,schema.get('items',{}),root,depth+1)
    elif type(value) is str:
        if len(value)<schema.get('minLength',0) or len(value)>schema.get('maxLength',16000):raise ValueError('schema string bound')
        if 'pattern' in schema and re.search(schema['pattern'],value) is None:raise ValueError('schema pattern')
    elif type(value) in (int,float):
        if not math.isfinite(value):raise ValueError('nonfinite')
        for key,fail in [('minimum',lambda x:value<x),('maximum',lambda x:value>x),('exclusiveMinimum',lambda x:value<=x),('exclusiveMaximum',lambda x:value>=x)]:
            if key in schema and fail(schema[key]):raise ValueError('schema numeric bound')

def parse_reply(text,definitions,schema):
    reply=strict_json(text,16000)
    if schema is not None:
        validate_shape(reply,schema);return reply
    if type(reply) is not dict or set(reply)!={'content','tool_calls'} or type(reply['content']) is not str or type(reply['tool_calls']) is not list or len(reply['tool_calls'])>8 or not(reply['content'] or reply['tool_calls']):raise ValueError('reply schema')
    allowed={t['name']:t for t in definitions}
    for call in reply['tool_calls']:
        if type(call) is not dict or set(call)!={'name','args'} or call['name'] not in allowed:raise ValueError('unknown reply tool')
        parameters={**allowed[call['name']]['parameters'],'additionalProperties':False}
        validate_shape(call['args'],parameters)
    return reply

def run_worker(command,env,cwd,logroot,on_request,seconds=600,cancel=None):
    """One process, bounded lines/total/queue, Windows pipes (no selectors)."""
    root=Path(logroot);root.mkdir(parents=True,exist_ok=True);events=queue.Queue(maxsize=2);end=time.monotonic()+seconds
    process=subprocess.Popen([str(x) for x in command],cwd=cwd,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    def lines(stream,kind):
        total=0
        try:
            while True:
                line=stream.readline(1048578)
                if not line:break
                total+=len(line)
                if len(line)>1048576 or total>8388608:raise ValueError('oversized worker stream')
                events.put((kind,line))
        except BaseException as error:events.put(('error',error))
        finally:events.put((kind+'-end',None))
    threads=[threading.Thread(target=lines,args=(process.stdout,'stdout'),daemon=True),threading.Thread(target=lines,args=(process.stderr,'stderr'),daemon=True)]
    for thread in threads:thread.start()
    native=None;ended=set();failure=None
    with (root/'stdout.log').open('wb') as out,(root/'stderr.log').open('wb') as err:
        try:
            while len(ended)<2:
                if cancel is not None and cancel.is_set():raise ValueError('analysis cancelled')
                remaining=end-time.monotonic()
                if remaining<=0:raise TimeoutError('analysis deadline')
                try:kind,line=events.get(timeout=min(.1,remaining))
                except queue.Empty:continue
                if kind.endswith('-end'):ended.add(kind);continue
                if kind=='error':raise line
                (out if kind=='stdout' else err).write(line)
                (out if kind=='stdout' else err).flush()
                if kind!='stdout':continue
                if line.startswith(b'REQUEST: '):
                    payload=strict_json(line[9:]);response=on_request(payload)
                    raw=json.dumps(response,allow_nan=False).encode()+b'\n'
                    if len(raw)>65536:raise ValueError('response IPC bound')
                    errors=[]
                    def write():
                        try:process.stdin.write(raw);process.stdin.flush()
                        except BaseException as e:errors.append(e)
                    writer=threading.Thread(target=write,daemon=True);writer.start();writer.join(max(0,min(3,end-time.monotonic())))
                    if writer.is_alive():raise TimeoutError('worker stdin blocked')
                    if errors:raise errors[0]
                elif line.startswith(b'RESULT: '):
                    if native is not None:raise ValueError('duplicate worker result')
                    native=strict_json(line[8:])
            code=process.wait(timeout=max(.01,end-time.monotonic()))
            if code or not native or native.get('error'):raise ValueError('upstream graph failed')
            return native
        finally:
            if process.poll() is None:process.kill()
            process.wait(timeout=5)
            for stream in (process.stdin,process.stdout,process.stderr):stream.close()
