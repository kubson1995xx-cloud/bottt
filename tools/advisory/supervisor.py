"""Asynchronous caller-owned refresh service; no trading/HTTP/UI integration.

Use refresh in a background executor on startup and UTC-day changes. It never
schedules work after owner close. Native admission lease must surround lifetime.
"""
from dataclasses import dataclass
from datetime import datetime,timezone,timedelta
import json,os,subprocess,sys,threading,time
from pathlib import Path
from contextlib import contextmanager
from budget import Budget,Refusal
from market_contract import *
from runtime_contract import RuntimeContract,validate_contract,child_environment
from reports import publish_pair,read_pair,atomic_write,encoded
from ipc import request_prompt,parse_reply,run_worker
from hermes_transport import check_route
HERE=Path(__file__).resolve().parent

@dataclass(frozen=True)
class ReportPairStatus:
    state:str
    reason:str
    manifest:dict|None=None

@contextmanager
def refresh_lock(path):
    f=open(path,'a+b');locked=False
    try:
        if os.name=='nt':
            import msvcrt
            if path.stat().st_size==0:f.write(b'0');f.flush()
            f.seek(0)
            try:msvcrt.locking(f.fileno(),msvcrt.LK_NBLCK,1);locked=True
            except OSError:pass
        else:
            import fcntl
            try:fcntl.flock(f.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB);locked=True
            except BlockingIOError:pass
        yield locked
    finally:
        if locked:
            if os.name=='nt':f.seek(0);msvcrt.locking(f.fileno(),msvcrt.LK_UNLCK,1)
            else:fcntl.flock(f.fileno(),fcntl.LOCK_UN)
        f.close()

class Supervisor:
    def __init__(self,root,contract,*,inference=None,fixture=False,clock=None,hermes_home=None,request_ms=60000,worker_seconds=600,cancel=None,store_guard=None):
        from store import StoreLease
        self.root=Path(root).absolute();self.store_lease=StoreLease(self.root,store_guard)
        self.contract=contract;self.fixture=fixture;self.clock=clock or (lambda:datetime.now(timezone.utc))
        self.budget=Budget(self.root/'budget.sqlite',self.clock);self.inference=inference or self._infer
        self.hermes_home=Path(hermes_home).resolve() if hermes_home else None
        self.request_ms=request_ms;self.worker_seconds=worker_seconds;self.cancel=cancel or threading.Event();self.lifecycle=threading.Lock()
    def _infer(self,tool,payload,prompt,seconds):
        if self.hermes_home is None:raise ValueError('explicit local Hermes owner required')
        row=self.contract.runtimes['hermes'];home=self.hermes_home
        env=child_environment(home,row['python'],row['source'])
        import uuid
        control=self.root/('cancel-'+uuid.uuid4().hex)
        end=time.monotonic()+seconds
        process=subprocess.Popen([row['python'],'-I',str(HERE/'inference_process.py'),str(seconds-6),row['source'],str(control)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,cwd=home,env=env)
        try:
            incoming=prompt.encode('utf-8');cancel_sent=False;cancel_end=end-3
            while True:
                if self.cancel.is_set() and not cancel_sent:
                    atomic_write(control,b'cancel');cancel_sent=True;cancel_end=min(cancel_end,time.monotonic()+3)
                remaining=cancel_end-time.monotonic()
                if remaining<=0:raise TimeoutError('supervised inference deadline')
                try:
                    out,_=process.communicate(incoming,timeout=min(.1,remaining));break
                except subprocess.TimeoutExpired:incoming=None
            if process.returncode or cancel_sent:raise ValueError('Hermes inference unavailable/cancelled')
            return strict_json(out)
        finally:
            if process.poll() is None:process.kill()
            process.wait(timeout=3)
            if control.exists():control.unlink()
    def _analysis(self,tool,pair,cutoff,dataset,attempt,work):
        work.mkdir(parents=True,exist_ok=True,mode=0o700)
        data_path=work/'dataset.json';atomic_write(data_path,dataset)
        contract_file=work/'runtime-contract.json';atomic_write(contract_file,encoded({'runtimes':self.contract.runtimes,'model':self.contract.model,'provider':self.contract.provider}))
        row=self.contract.runtimes[tool];env=child_environment(work,row['python'])
        for key in ('TEMP','APPDATA','LOCALAPPDATA'):Path(env[key]).mkdir(parents=True,exist_ok=True)
        env.update({'PYTHON_DOTENV_DISABLED':'1','DO_NOT_TRACK':'1','ANONYMIZED_TELEMETRY':'false','LANGCHAIN_TRACING_V2':'false','LANGCHAIN_PROVIDER':'openai-codex','LANGCHAIN_MODEL_NAME':'gpt-6.1-sol','VIBE_TRADING_HOME':str(work),'VIBE_TRADING_ENABLE_SHELL_TOOLS':'0','VIBE_TRADING_ENABLE_SCHEDULER':'0','VIBE_TRADING_ENABLE_ADVISORY':'0','VIBE_TRADING_CHANNELS_AUTO_START':'0'})
        def exchange(payload):
            if self.cancel.is_set():raise ValueError('analysis cancelled')
            prompt=request_prompt(tool,payload)
            reservation=self.budget.reserve_request(attempt,self.request_ms)
            if isinstance(reservation,Refusal):raise ValueError(reservation.reason)
            atomic_write(work/(reservation.id+'-request.json'),encoded({'payload':payload,'promptSha256':dataset_digest(prompt.encode()),'reserved_ms':reservation.milliseconds,'inference':'fixture' if self.fixture else 'genuine'}))
            started=time.monotonic();outcome='failed'
            try:
                response=self.inference(tool,payload,prompt,reservation.milliseconds/1000)
                if self.cancel.is_set():raise ValueError('analysis cancelled')
                if not self.fixture:
                    check_route(response['before']);check_route(response['route'])
                elif response.get('fixture') is not True:raise ValueError('unlabelled fixture')
                reply=parse_reply(response['text'],payload['tools'],payload['schema'])
                atomic_write(work/(reservation.id+'-response.json'),encoded(response))
                outcome='ok';return {'reply':reply,'tokens':response.get('tokens') or {}}
            finally:self.budget.complete_request(reservation,int((time.monotonic()-started)*1000),outcome)
        command=[row['python'],'-I',str(HERE/'worker.py'),tool,'--instrument',pair,'--cutoff',cutoff,'--dataset',str(data_path),'--work-root',str(work),'--runtime-contract',str(contract_file)]
        return run_worker(command,env,work,work/'ipc',exchange,self.worker_seconds,self.cancel)
    def refresh(self,pair,cutoff,dataset):
        with self.lifecycle:
            if self.cancel.is_set():return ReportPairStatus('unavailable','CANCELLED')
            return self._refresh(pair,cutoff,dataset)
    def _refresh(self,pair,cutoff,dataset):
        data=decode_snapshot(dataset,pair,cutoff);validate_dataset(data,self.clock());digest=dataset_digest(dataset)
        pairroot=self.root/pair;pairroot.mkdir(exist_ok=True,mode=0o700)
        with refresh_lock(pairroot/'refresh.lock') as locked:
            if not locked:return ReportPairStatus('unavailable','REFRESH_IN_PROGRESS')
            day=cutoff[:10]
            for tool in TOOLS:
                existing=self.budget.attempt(day,pair,tool)
                if existing and existing[0]!=digest:return ReportPairStatus('unavailable','CANONICAL_PARITY_MISMATCH')
            try:
                old,results=read_pair(pairroot,data,self.clock())
                if old['Inference']!=('fixture' if self.fixture else 'genuine'):raise ValueError('inference mode mismatch')
            except (OSError,ValueError,KeyError,TypeError):old=None;results={}
            for tool in TOOLS:
                # Attempt is not reset by missing/corrupt report or process restart.
                if self.budget.attempt(day,pair,tool):
                    results.setdefault(tool,{'native':None,'error':'PRIOR_ATTEMPT_NO_RETRY'});continue
                if self.cancel.is_set():return ReportPairStatus('unavailable','CANCELLED')
                now=self.clock();deadline=min(now+timedelta(seconds=self.worker_seconds),utc(cutoff)+timedelta(days=1))
                attempt=self.budget.reserve(day,pair,tool,deadline,digest)
                if isinstance(attempt,Refusal):results[tool]={'native':None,'error':attempt.reason};continue
                work=pairroot/'attempts'/attempt.id
                try:
                    validate_contract(self.contract)
                    native=self._analysis(tool,pair,cutoff,dataset,attempt,work)
                    # Validate rating/data before marking completion.
                    from reports import make_report
                    make_report(tool,data,native,fixture=self.fixture)
                    results[tool]={'native':native,'error':None};self.budget.finish(attempt,'ok')
                except Exception as error:
                    results[tool]={'native':None,'error':type(error).__name__};self.budget.finish(attempt,'failed')
                # Preserve each completed partial result before the other graph starts.
                partial={t:results.get(t,{'native':None,'error':'PENDING'}) for t in TOOLS}
                publish_pair(pairroot,data,partial,self.fixture)
            if old is not None and results and all(self.budget.attempt(day,pair,t) for t in TOOLS):
                # Reuse exact current generation if no graph/publication changed it.
                current,_=read_pair(pairroot,data,self.clock())
                state='fixture' if self.fixture else ('ready' if all(not r['error'] for r in results.values()) else 'unavailable')
                return ReportPairStatus(state,'REUSED',current)
            manifest=publish_pair(pairroot,data,results,self.fixture)
            read_pair(pairroot,data,self.clock())
            return ReportPairStatus('fixture' if self.fixture else ('ready' if all(not r['error'] for r in results.values()) else 'unavailable'),'COMPLETED',manifest)
    def close(self):
        self.cancel.set()
        with self.lifecycle:self.store_lease.close()
