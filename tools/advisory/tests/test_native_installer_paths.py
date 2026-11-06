"""Same-root native causal control for the retained pinned-source long-path refusal."""
import ctypes,hashlib,json,os,shutil,subprocess,sys,unittest,uuid
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from store import StoreLease,StoreRefused
RELATIVE='installed/sources/hermes/website/i18n/zh-Hans/docusaurus-plugin-content-docs/current/user-guide/skills/bundled/software-development/software-development-requesting-code-review.md'
@unittest.skipUnless(os.name=='nt','native Windows only')
class NativeInstallerPaths(unittest.TestCase):
 def test_same_public_long_path_old_refuses_new_admits_without_weakening_links_or_acl(self):
  old=Path(os.environ['APEX_INSTALLER_OLD_GUARD']);new=Path(os.environ['APEX_INSTALLER_NEW_GUARD'])
  root=Path(os.environ['LOCALAPPDATA'])/('apex-ai-install-test-'+uuid.uuid4().hex)
  path=root/RELATIVE;assert len(str(path))>=260
  proof={'relativePath':RELATIVE,'pathLength':len(str(path)),'oldGuardSha256':hashlib.sha256(old.read_bytes()).hexdigest(),'newGuardSha256':hashlib.sha256(new.read_bytes()).hexdigest()}
  try:
   with_store=StoreLease(root,old,timeout=180)
   try:
    (root/'control.json').write_text('{}');with_store.scan();proof['oldShortControl']=True
    path.parent.mkdir(parents=True);path.write_bytes(b'public long-path control\n');before=hashlib.sha256(path.read_bytes()).hexdigest()
    # Raw native API calibration, same flags/rights as the production inspector.
    k=ctypes.WinDLL('kernel32',use_last_error=True);create=k.CreateFileW;create.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p];create.restype=ctypes.c_void_p
    close=k.CloseHandle;close.argtypes=[ctypes.c_void_p]
    h=create(str(path),0x00020081,3,None,3,0x02200000,None);error=ctypes.get_last_error()
    if h==ctypes.c_void_p(-1).value:proof['pythonRawCreateFileError']=error
    else:close(h);proof['pythonRawCreateFileAccepted']=True # Python carries its own longPathAware manifest.
    extended='\\\\?\\'+str(path);h=create(extended,0x00020081,3,None,3,0x02200000,None)
    self.assertNotEqual(ctypes.c_void_p(-1).value,h);close(h);proof['extendedNativeOpen']=True
    with self.assertRaises(StoreRefused):with_store.scan()
    proof['oldLongRefused']=True
   finally:with_store.close()
   admitted=StoreLease(root,new,timeout=180)
   try:admitted.scan();proof['newLongAdmitted']=True
   finally:admitted.close()
   self.assertEqual(before,hashlib.sha256(path.read_bytes()).hexdigest());proof['sameBytes']=True
   alias=root/'hardlink-negative.md';os.link(path,alias)
   with self.assertRaises(StoreRefused):StoreLease(root,new,timeout=180)
   proof['hardlinkRefused']=True;alias.unlink()
   link=root/'reparse-negative';os.symlink(path.parent,link,target_is_directory=True)
   try:
    with self.assertRaises(StoreRefused):StoreLease(root,new,timeout=180)
    proof['reparseRefused']=True
   finally:link.unlink()
   subprocess.run(['icacls',extended,'/grant','*S-1-1-0:R'],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
   with self.assertRaises(StoreRefused):StoreLease(root,new,timeout=180)
   proof['broadAclRefused']=True
   Path(os.environ['APEX_INSTALLER_PATH_EVIDENCE']).write_text(json.dumps(proof,indent=2))
  finally:
   shutil.rmtree(root)
if __name__=='__main__':unittest.main()
