"""Trusted local Hermes process only; upstream analysts never receive its HOME."""
import asyncio,json,sys
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE));sys.path.insert(0,str(Path(sys.argv[2]).resolve()))
from hermes_transport import request
prompt=sys.stdin.buffer.read(1000002)
if len(prompt)>1000000:raise ValueError('prompt byte bound')
async def controlled():
    task=asyncio.create_task(request(prompt.decode('utf-8'),float(sys.argv[1])))
    cancel=Path(sys.argv[3]) if len(sys.argv)>3 else None
    while not task.done():
        if cancel is not None and cancel.exists():
            task.cancel()
            break
        await asyncio.sleep(.05)
    return await task
result=asyncio.run(controlled())
print(json.dumps(result,allow_nan=False))
