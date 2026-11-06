"""Tool-free Hermes canonical gateway transport, adapted from advisory-bridge.

Hermes alone owns local subscription auth. This module never reads/copies tokens,
uses provider fallbacks, or imports third-party analysts into the trusted process.
"""
import asyncio
import contextlib
import io
import json
import math
from pathlib import Path
import uuid
from runtime_contract import MODEL, PROVIDER


def check_policy(config):
    model=config.get('model',{})
    if not isinstance(model,dict) or (model.get('default'),model.get('provider'))!=(MODEL,PROVIDER):
        raise ValueError('unsupported inference route configuration')
    if config.get('fallback_providers') or config.get('fallback_model'):
        raise ValueError('fallback forbidden')
    if model.get('base_url') or model.get('api_key') or model.get('openai_runtime') not in (None,'','codex_responses'):
        raise ValueError('custom transport forbidden')
    if MODEL in config.get('model_aliases',{}) or MODEL in model.get('aliases',{}):
        raise ValueError('model alias forbidden')
    return True


def check_route(info):
    if info.get('model') != MODEL or info.get('provider') != PROVIDER:
        raise ValueError('model/provider mismatch')
    if info.get('enabled_toolsets') != [] or type(info.get('tool_count')) is not int or info['tool_count'] != 0:
        raise ValueError('tools must be disabled')
    launch = info.get('launch_request', {})
    if launch.get('safe_mode') is not True or launch.get('toolsets') != [] or launch.get('max_turns') != 1:
        raise ValueError('unsafe launch policy')
    return {'model':MODEL,'provider':PROVIDER,'enabled_toolsets':[], 'tool_count':0,
            'launch_request':{'safe_mode':True,'toolsets':[],'max_turns':1}}


def is_missing_auth(error):
    return (getattr(error, 'provider', None) == PROVIDER and
            getattr(error, 'code', None) == 'codex_auth_missing' and
            getattr(error, 'relogin_required', None) is True)


def parse_stream(text):
    if len(text.encode('utf-8')) > 1048576: raise ValueError('oversized gateway stream')
    events = [json.loads(line) for line in text.splitlines() if line.strip()]
    if any(e.get('type') in ('tool_use','tool_result') for e in events):
        raise ValueError('Hermes tool authority forbidden')
    init = [e for e in events if e.get('type')=='system' and e.get('subtype')=='init']
    results = [e for e in events if e.get('type')=='result']
    if len(init)!=1 or init[0].get('model')!=MODEL: raise ValueError('model mismatch')
    if len(results)!=1 or results[0].get('exit_code')!=0 or results[0].get('error'):
        raise ValueError('inference unavailable')
    if not isinstance(results[0].get('text'), str) or not results[0]['text'].strip():
        raise ValueError('empty response')
    return results[0]


async def request(prompt: str, seconds: float) -> dict:
    if not isinstance(prompt, str) or not 1 <= len(prompt) <= 16000:
        raise ValueError('bounded prompt required')
    if isinstance(seconds, bool) or not isinstance(seconds,(int,float)) or not math.isfinite(seconds) or not 0 < seconds <= 120:
        raise ValueError('bounded timeout required')
    # Lazy canonical imports: offline validation cannot accidentally bootstrap.
    from hermes_cli.config import load_config
    check_policy(load_config())
    from hermes_cli.gateway_client import connect_gateway
    from hermes_cli.gateway_chat_view import GatewayChatView
    from hermes_cli.stream_json import StreamJsonEmitter
    client = None
    session = None
    async def invoke():
        nonlocal client, session
        async with connect_gateway() as connected:
            client = connected
            snapshot = await client.rpc('session.create', request_id=uuid.uuid4().hex,
                source='tool', cwd=str(Path(__file__).parent), safe_mode=True,
                model=MODEL, provider=PROVIDER, reasoning='low', toolsets=[], max_turns=1)
            session = snapshot['stored_session_id']
            before = check_route(snapshot['info'])
            output = io.StringIO()
            with contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
                view = GatewayChatView(client, snapshot, quiet=True,
                    emitter=StreamJsonEmitter(model=MODEL, session_id=session))
                view.unattended = True
                try:
                    code = await asyncio.wait_for(view.run(prompt, oneshot=True), seconds)
                except (asyncio.TimeoutError, asyncio.CancelledError):
                    await asyncio.wait_for(client.rpc('session.interrupt', session_id=session), 3)
                    raise
            after = await client.rpc('session.resume', session_id=session)
            route = check_route(after['info'])
            if code: raise ValueError('inference failed')
            result = parse_stream(output.getvalue())
            return {'text':result['text'], 'tokens':result.get('tokens'),
                    'duration_ms':result.get('duration_ms'), 'route':route, 'before':before,
                    'usage':{k:after['info'].get('usage',{}).get(k) for k in ['api_call_count','input_tokens','output_tokens']},
                    'transport':'Hermes canonical gateway RPC'}
    # Includes bootstrap; no unbounded connect before the inference deadline.
    return await asyncio.wait_for(invoke(), seconds + 3)
