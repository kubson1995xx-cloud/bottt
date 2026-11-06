"""Trusted local Hermes process only; upstream analysts never receive its HOME."""
import asyncio,json,sys
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE));sys.path.insert(0,str(Path(sys.argv[2]).resolve()))
from hermes_transport import request
prompt=sys.stdin.buffer.read(1000002)
if len(prompt)>1000000:raise ValueError('prompt byte bound')
result=asyncio.run(request(prompt.decode('utf-8'),float(sys.argv[1])))
print(json.dumps(result,allow_nan=False))
