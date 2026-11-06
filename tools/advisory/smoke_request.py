"""One real tool-free public smoke, only via local_smoke's scrubbed owner runtime."""
import asyncio
import json
import sys
from hermes_transport import request

if __name__=='__main__':
    result=asyncio.run(request('Reply exactly APEX_ADVISORY_OK. This is a public transport smoke, not trading advice.',float(sys.argv[1])))
    if result['text'].strip()!='APEX_ADVISORY_OK': raise ValueError('smoke response mismatch')
    print(json.dumps({'inference_accepted':True,**result},ensure_ascii=True,allow_nan=False))
