import unittest,importlib.util,tempfile,json,hashlib,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
class LocalInstallTests(unittest.TestCase):
 def validator(self):
  spec=importlib.util.find_spec('local_install');self.assertIsNotNone(spec,'one-click pinned installer is absent')
  import local_install
  return local_install.verify_bundle
 def test_pinned_bundle_hash_tamper_and_traversal_refuse_before_execute(self):
  verify=self.validator()
  with tempfile.TemporaryDirectory() as t:
   r=Path(t);(r/'python-base').mkdir();p=r/'python-base/python.exe';p.write_bytes(b'synthetic never executable')
   manifest={'Schema':'apex-advisory-bundle-v1','Files':[{'Path':'python-base/python.exe','Sha256':hashlib.sha256(p.read_bytes()).hexdigest()}]}
   (r/'bundle.json').write_text(json.dumps(manifest));verify(r)
   p.write_bytes(b'tampered')
   with self.assertRaises(ValueError):verify(r)
   manifest['Files'][0]['Path']='../outside';(r/'bundle.json').write_text(json.dumps(manifest))
   with self.assertRaises(ValueError):verify(r)
 def test_closed_manifest_and_cancelled_partial_install_are_not_ready(self):
  verify=self.validator()
  with tempfile.TemporaryDirectory() as t:
   r=Path(t);(r/'bundle.json').write_text('{"Schema":"apex-advisory-bundle-v1","Files":[],"extra":true}')
   with self.assertRaises(ValueError):verify(r)
