"""Replay exact observed genuine upstream message shape; no inference spent."""
import json,sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from ipc import request_prompt
class NativeMessageTests(unittest.TestCase):
 def payload(self,value=None):
  return {'messages':[{'role':'assistant','content':'analysis','tool_calls':[],'reasoning_content':value}],'tools':[],'schema':None}
 def test_real_native_optional_reasoning_none_preserved(self):
  p=self.payload();self.assertIn('reasoning_content',request_prompt('vibe-trading',p))
 def test_unchanged_retained_genuine_native_payload_replays_offline(self):
  p=json.loads((Path(__file__).parent/'fixtures/vibe-real-native-second-request.json').read_text())
  self.assertIn('reasoning_content',request_prompt('vibe-trading',p))
 def test_reasoning_typed_bound_and_unknown_message_fields_refuse(self):
  for value in ({},[],True,12,'x'*16001):
   with self.subTest(value=type(value).__name__),self.assertRaises(ValueError):request_prompt('vibe-trading',self.payload(value))
  p=self.payload();p['messages'][0]['extra']=None
  with self.assertRaises(ValueError):request_prompt('vibe-trading',p)
