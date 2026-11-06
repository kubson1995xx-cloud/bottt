"""Native private-directory/reparse admission using existing C# handle inspector.

The native helper holds no-delete-sharing ancestor/root handles for the complete
service lifetime. This is ACL admission, NOT a same-user filesystem sandbox.
"""
import os,shutil,subprocess,queue,threading
from pathlib import Path

class StoreLease:
    def __init__(self,root,guard=None):
        self.process=None;root=Path(root)
        if not root.is_absolute():raise ValueError('absolute advisory root required')
        # Do not resolve symlinks before native admission.
        if os.name=='nt':
            guard=Path(guard) if guard else Path(__file__).parent/'StrategyExport/bin/Debug/net8.0/StrategyExport.dll'
            host=shutil.which('dotnet')
            if not host or not guard.is_file():raise ValueError('native advisory store admission helper required')
            process=subprocess.Popen([host,str(guard),'--guard',str(root)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
            self.process=process;q=queue.Queue()
            thread=threading.Thread(target=lambda:q.put(process.stdout.readline(128)),daemon=True);thread.start()
            try:line=q.get(timeout=15)
            except queue.Empty:self.close();raise ValueError('native store admission timeout')
            if line.strip()!=b'ADVISORY_STORE_ADMITTED':self.close();raise ValueError('native store admission refused')
        else:
            # Portable offline lane only; production worker refuses non-Windows.
            for p in [root,*root.parents]:
                if p.is_symlink():raise ValueError('symlink store refused')
            root.mkdir(parents=True,exist_ok=True,mode=0o700)
            if root.stat().st_mode & 0o077:raise ValueError('private store required')
    def close(self):
        if self.process is None:return
        process=self.process;self.process=None
        try:
            if process.poll() is None:process.stdin.write(b'close\n');process.stdin.flush();process.wait(timeout=5)
        finally:
            if process.poll() is None:process.kill();process.wait(timeout=3)
            for stream in (process.stdin,process.stdout):stream.close()
