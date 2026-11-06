"""One cross-process daily budget: reserve fully, settle verified new successes.

Original milliseconds are immutable. Historical requests retain their full
charge; only elapsed-success-v1 requests can settle to measured successful time.
Failures and crashes keep full charges. These are not provider billing counters.
"""
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime,timezone,timedelta
import math,sqlite3,uuid
from pathlib import Path
from market_contract import instrument_context,TOOLS

@dataclass(frozen=True)
class Refusal:
    reason:str

@dataclass(frozen=True)
class Reservation:
    id:str
    day:str
    pair:str
    tool:str
    deadline:str
    digest:str

@dataclass(frozen=True)
class RequestReservation:
    id:str
    milliseconds:int
    attempt:str
    day:str

class Budget:
    def __init__(self,path,clock=None,*,store=None):
        self.path=Path(path); self.clock=clock or (lambda:datetime.now(timezone.utc));self.store=store
        if store:store.stable_file(self.path)
        self.path.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
        with self._transaction() as db:
            # execute(), not executescript(): migration stays in BEGIN IMMEDIATE.
            db.execute('CREATE TABLE IF NOT EXISTS attempts(id TEXT PRIMARY KEY,day TEXT NOT NULL,pair TEXT NOT NULL,tool TEXT NOT NULL,deadline TEXT NOT NULL,digest TEXT NOT NULL,status TEXT NOT NULL,UNIQUE(day,pair,tool))')
            db.execute('CREATE TABLE IF NOT EXISTS requests(id TEXT PRIMARY KEY,attempt TEXT NOT NULL,day TEXT NOT NULL,milliseconds INTEGER NOT NULL CHECK(milliseconds>0),observed_ms INTEGER, outcome TEXT NOT NULL)')
            columns={row[1] for row in db.execute('PRAGMA table_info(requests)')}
            if 'charge_policy' not in columns:
                db.execute("ALTER TABLE requests ADD COLUMN charge_policy TEXT NOT NULL DEFAULT 'legacy-full-v1'")
            if 'charged_ms' not in columns:
                db.execute('ALTER TABLE requests ADD COLUMN charged_ms INTEGER CHECK(charged_ms>0 AND charged_ms<=milliseconds)')
    @contextmanager
    def _transaction(self):
        # Main DB identity stays leased; journal/WAL/SHM must remain deletable by SQLite.
        if self.store:
            for suffix in ('','-journal','-wal','-shm'):self.store.inspect(Path(str(self.path)+suffix))
        db=sqlite3.connect(self.path,timeout=10,isolation_level=None)
        try:
            db.execute('PRAGMA synchronous=FULL');db.execute('BEGIN IMMEDIATE')
            yield db
            if db.in_transaction:db.execute('COMMIT')
        except BaseException:
            if db.in_transaction:db.execute('ROLLBACK')
            raise
        finally:db.close()
    def reserve(self,day,pair,tool,deadline,digest=''):
        instrument_context(pair)
        if tool not in TOOLS:raise ValueError('tool refused')
        now=self.clock()
        if now.utcoffset()!=timedelta(0) or day!=now.strftime('%Y-%m-%d') or not now<deadline<=datetime.combine(now.date()+timedelta(days=1),datetime.min.time(),timezone.utc):return Refusal('DAY_OR_DEADLINE_REFUSED')
        row=Reservation(uuid.uuid4().hex,day,pair,tool,deadline.isoformat(),digest)
        with self._transaction() as db:
            if db.execute('SELECT 1 FROM attempts WHERE day=? AND pair=? AND tool=?',(day,pair,tool)).fetchone():return Refusal('ATTEMPT_ALREADY_RESERVED')
            db.execute('INSERT INTO attempts VALUES(?,?,?,?,?,?,?)',(row.id,day,pair,tool,row.deadline,digest,'reserved'))
        return row
    def reserve_request(self,attempt,milliseconds):
        if type(milliseconds) is not int or not 1<=milliseconds<=120000:raise ValueError('request slice refused')
        with self._transaction() as db:
            stored=db.execute('SELECT day,pair,tool,deadline,digest,status FROM attempts WHERE id=?',(attempt.id,)).fetchone()
            if stored!= (attempt.day,attempt.pair,attempt.tool,attempt.deadline,attempt.digest,'reserved'):return Refusal('ATTEMPT_INACTIVE')
            now=self.clock()
            if now.strftime('%Y-%m-%d')!=attempt.day or now+timedelta(milliseconds=milliseconds)>datetime.fromisoformat(attempt.deadline):return Refusal('DEADLINE_EXHAUSTED')
            if db.execute("SELECT 1 FROM requests WHERE day=? AND outcome IN ('failed','cancelled') LIMIT 1",(attempt.day,)).fetchone():return Refusal('GLOBAL_INFERENCE_HALTED')
            count,total=db.execute('SELECT COUNT(*),COALESCE(SUM(COALESCE(charged_ms,milliseconds)),0) FROM requests WHERE day=?',(attempt.day,)).fetchone()
            if count>=48 or total+milliseconds>900000:return Refusal('DAILY_BUDGET_EXHAUSTED')
            row=RequestReservation(uuid.uuid4().hex,milliseconds,attempt.id,attempt.day)
            db.execute("INSERT INTO requests(id,attempt,day,milliseconds,observed_ms,outcome,charge_policy,charged_ms) VALUES(?,?,?,?,NULL,'uncertain','elapsed-success-v1',?)",(row.id,attempt.id,attempt.day,milliseconds,milliseconds))
        return row
    def complete_request(self,reservation,observed_ms,outcome):
        if type(observed_ms) is not int or observed_ms<0 or outcome not in ('ok','failed','cancelled'):raise ValueError('usage refused')
        with self._transaction() as db:
            stored=db.execute('SELECT milliseconds,attempt,day,observed_ms,outcome,charge_policy FROM requests WHERE id=?',(reservation.id,)).fetchone()
            if stored is None or stored[:5]!=(reservation.milliseconds,reservation.attempt,reservation.day,None,'uncertain'):
                raise ValueError('unknown/wrong identity/already completed request')
            if outcome=='ok' and observed_ms>reservation.milliseconds:raise ValueError('successful usage exceeds reservation')
            charge=max(1,observed_ms) if outcome=='ok' and stored[5]=='elapsed-success-v1' else reservation.milliseconds
            db.execute('UPDATE requests SET observed_ms=?,outcome=?,charged_ms=? WHERE id=?',(observed_ms,outcome,charge,reservation.id))
    def finish(self,attempt,status):
        if status not in ('ok','failed'):raise ValueError('status refused')
        with self._transaction() as db:
            db.execute('UPDATE attempts SET status=? WHERE id=? AND status=?',(status,attempt.id,'reserved'))
    def attempt(self,day,pair,tool):
        with self._transaction() as db:
            return db.execute('SELECT digest,status FROM attempts WHERE day=? AND pair=? AND tool=?',(day,pair,tool)).fetchone()
    def usage(self,day):
        with self._transaction() as db:
            count,reserved,charged,observed,unknown=db.execute('SELECT COUNT(*),COALESCE(SUM(milliseconds),0),COALESCE(SUM(COALESCE(charged_ms,milliseconds)),0),COALESCE(SUM(observed_ms),0),SUM(CASE WHEN observed_ms IS NULL THEN 1 ELSE 0 END) FROM requests WHERE day=?',(day,)).fetchone()
        return {'supervised_requests':count,'reserved_ms':reserved,'charged_ms':charged,'observed_ms':observed,'uncertain_requests':unknown or 0,'provider_internal_retries':None}
