"""Private advisory storage admission, not a same-user filesystem sandbox.

Windows pins every traversed directory with the existing native inspector.
Leaves get scoped no-delete handles for reads/stable files, never across atomic
replacement or SQLite's journal deletion. Unknown existing ACLs are not repaired.
"""
import os,shutil,subprocess,queue,threading,json,stat
from pathlib import Path
from contextlib import contextmanager

class StoreRefused(ValueError):pass

class StoreLease:
    def __init__(self,root,guard=None):
        self.process=None;self.root=root=Path(root);self.io=threading.RLock();self.handles={};self.closed=False
        if not root.is_absolute():raise StoreRefused('absolute advisory root required')
        # Do not resolve symlinks before native admission.
        if os.name=='nt':
            guard=Path(guard) if guard else Path(__file__).parent/'StrategyExport/bin/Debug/net8.0/StrategyExport.dll'
            host=shutil.which('dotnet')
            if not host or not guard.is_file():raise StoreRefused('native advisory store admission helper required')
            process=subprocess.Popen([host,str(guard),'--guard',str(root)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
            self.process=process
            line=self._line()
            if line.strip()!=b'ADVISORY_STORE_ADMITTED':
                import re
                reason=line.strip().decode('ascii',errors='replace');self.close()
                safe=reason if re.fullmatch(r'ADVISORY_STORE_REFUSED [0-9]{1,3} HOST_DATA_[A-Z_]+',reason) else 'NATIVE_HELPER_FAILED'
                raise StoreRefused('native store admission refused: '+safe)
        else:
            # Portable offline lane only; production worker refuses non-Windows.
            for p in [root,*root.parents]:
                if p.is_symlink():raise StoreRefused('symlink store refused')
            root.mkdir(parents=True,exist_ok=True,mode=0o700)
            self.scan()
    def _line(self):
        q=queue.Queue();threading.Thread(target=lambda:q.put(self.process.stdout.readline(128)),daemon=True).start()
        try:return q.get(timeout=15)
        except queue.Empty:self.close();raise StoreRefused('native store admission timeout')
    def _command(self,operation,path):
        with self.io:
            if self.closed or self.process.poll() is not None:raise StoreRefused('native store lease unavailable')
            try:self.process.stdin.write((json.dumps([operation,str(path)])+'\n').encode());self.process.stdin.flush()
            except OSError:raise StoreRefused('native store lease unavailable') from None
            line=self._line().decode('ascii',errors='replace').strip()
            if line=='MISSING':raise FileNotFoundError(str(path))
            if not line.startswith('OK '):raise StoreRefused('unsafe advisory descendant refused')
            return int(line[3:])
    def _bound(self,path):
        p=Path(path)
        if not p.is_absolute() or '..' in p.parts or (p!=self.root and self.root not in p.parents):raise StoreRefused('store containment refused')
        if self.closed:raise StoreRefused('closed store lease')
        return p
    def _check(self,path,directory):
        p=self._bound(path);info=p.lstat()
        if stat.S_ISLNK(info.st_mode) or (info.st_mode & 0o077) or (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)) is not True or (not directory and info.st_nlink!=1):raise StoreRefused('unsafe advisory descendant refused')
    def directory(self,path):
        p=self._bound(path)
        if self.process:return self._command('dir',p)
        if p!=self.root:self.directory(p.parent)
        if not os.path.lexists(p):p.mkdir(mode=0o700)
        self._check(p,True)
    def scan(self):
        if self.process:return self._command('scan',self.root)
        def visit(p):
            self._check(p,True)
            for child in p.iterdir():
                info=child.lstat();directory=stat.S_ISDIR(info.st_mode)
                expected_file=child.name in {'CURRENT.json','refresh.lock','budget.sqlite','budget.sqlite-journal','budget.sqlite-wal','budget.sqlite-shm'} or child.name.endswith('.json')
                expected_dir=child.name in {'BTC-EUR','ETH-EUR','SOL-EUR','attempts','generations'}
                self._check(child,False if expected_file else True if expected_dir else directory)
                if directory:visit(child)
        visit(self.root)
    def inspect(self,path):
        p=self._bound(path);self.directory(p.parent)
        if self.process:return self._command('inspect',p)
        if os.path.lexists(p):self._check(p,False)
    def stable_file(self,path):
        p=self._bound(path);self.directory(p.parent)
        if str(p) in self.handles:return
        if self.process:self.handles[str(p)]=self._command('file',p)
        else:
            if not os.path.lexists(p):
                fd=os.open(p,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600);os.close(fd)
            self._check(p,False);self.handles[str(p)]=None
    @contextmanager
    def reading(self,path):
        p=self._bound(path);self.directory(p.parent)
        if self.process:
            key=self._command('hold',p)
            try:yield
            finally:self._command('release',key)
        else:
            self._check(p,False)
            yield
    def close(self):
        with self.io:
            self.closed=True
            if self.process is None:return
            process=self.process;self.process=None
            try:
                if process.poll() is None:
                    try:process.stdin.write(b'close\n');process.stdin.flush()
                    except OSError:pass
                    try:process.wait(timeout=5)
                    except subprocess.TimeoutExpired:process.kill();process.wait(timeout=3)
            finally:
                if process.poll() is None:process.kill();process.wait(timeout=3)
                for stream in (process.stdin,process.stdout):
                    try:stream.close()
                    except OSError:pass
