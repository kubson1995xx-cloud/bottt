import asyncio,sys,json,types,unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from hermes_transport import request

class TransportCompatibilityTests(unittest.TestCase):
    def test_lossless_48008_character_native_graph_prompt(self):
        # The retained genuine upstream call-02 payload is 48008 characters.
        # This fixture checks delivery WITHOUT truncation, not genuine inference.
        prompt='public-native-graph-shape:'+('x'*(48008-len('public-native-graph-shape:')));received=[];calls=[]
        info={'model':'gpt-6.1-sol','provider':'openai-codex','enabled_toolsets':[],'tool_count':0,'launch_request':{'safe_mode':True,'toolsets':[],'max_turns':1}}
        class Client:
            async def rpc(self,method,**kw):calls.append(method);return {'stored_session_id':'fixture','info':info}
        class Connection:
            async def __aenter__(self):return Client()
            async def __aexit__(self,*args):pass
        class View:
            def __init__(self,*args,**kw):pass
            async def run(self,text,oneshot):
                received.append(text)
                print(json.dumps({'type':'system','subtype':'init','model':'gpt-6.1-sol'}));print(json.dumps({'type':'result','exit_code':0,'error':None,'text':'fixture only'}));return 0
        modules={'hermes_cli.config':types.SimpleNamespace(load_config=lambda:{'model':{'default':'gpt-6.1-sol','provider':'openai-codex'}}),'hermes_cli.gateway_client':types.SimpleNamespace(connect_gateway=lambda:Connection()),'hermes_cli.gateway_chat_view':types.SimpleNamespace(GatewayChatView=View),'hermes_cli.stream_json':types.SimpleNamespace(StreamJsonEmitter=lambda **kw:None)}
        error=None
        try:
            with patch.dict(sys.modules,modules):asyncio.run(request(prompt,1))
        except ValueError as caught:error=str(caught)
        self.assertIsNone(error,'native-sized prompt was refused: '+str(error))
        self.assertEqual([prompt],received);self.assertEqual(['session.create','session.resume'],calls)
    def test_child_cancellation_deadline(self):
        import subprocess,tempfile,time,shutil
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);script=root/'inference_process.py'
            shutil.copyfile(Path(__file__).resolve().parents[1]/'inference_process.py',script)
            (root/'hermes_transport.py').write_text('import asyncio\nasync def request(prompt,seconds):\n await asyncio.sleep(10)\n return {}\n')
            cancel=root/'cancel';cancel.write_bytes(b'cancel')
            process=subprocess.Popen([sys.executable,str(script),'10',str(root),str(cancel)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            try:
                try:out,err=process.communicate(b'public fixture prompt',timeout=1)
                except subprocess.TimeoutExpired:self.fail('cancelled trusted child did not interrupt before deadline')
            finally:
                if process.poll() is None:process.kill();process.communicate()
            self.assertNotEqual(0,process.returncode,'cancellation must not publish success')
    def test_prompt_over_ceiling_refused(self):
        with self.assertRaises(ValueError):asyncio.run(request('x'*250001,1))

if __name__=='__main__':unittest.main()
