"""Cross-process, crash-conservative daily reservations, not billing counters.

Every request permanently charges its entire deadline slice BEFORE invocation.
No refunds, even on success. Observed wall time is separate and may be unknown
following a crash. SQLite FULL synchronous commit serializes all six analyses.
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

class Budget:
    def __init__(self,path,clock=None):
        self.path=Path(path); self.clock=clock or (lambda:datetime.now(timezone.utc))
        self.path.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
        with self._transaction() as db:
            db.executescript('''CREATE TABLE IF NOT EXISTS attempts(id TEXT PRIMARY KEY,day TEXT NOT NULL,pair TEXT NOT NULL,tool TEXT NOT NULL,deadline TEXT NOT NULL,digest TEXT NOT NULL,status TEXT NOT NULL,UNIQUE(day,pair,tool));
CREATE TABLE IF NOT EXISTS requests(id TEXT PRIMARY KEY,attempt TEXT NOT NULL,day TEXT NOT NULL,milliseconds INTEGER NOT NULL CHECK(milliseconds>0),observed_ms INTEGER, outcome TEXT NOT NULL);''')
    @contextmanager
    def _transaction(self):
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
            count,total=db.execute('SELECT COUNT(*),COALESCE(SUM(milliseconds),0) FROM requests WHERE day=?',(attempt.day,)).fetchone()
            if count>=48 or total+milliseconds>900000:return Refusal('DAILY_BUDGET_EXHAUSTED')
            row=RequestReservation(uuid.uuid4().hex,milliseconds)
            db.execute('INSERT INTO requests VALUES(?,?,?,?,?,?)',(row.id,attempt.id,attempt.day,milliseconds,None,'uncertain'))
        return row
    def complete_request(self,reservation,observed_ms,outcome):
        if type(observed_ms) is not int or observed_ms<0 or outcome not in ('ok','failed','cancelled'):raise ValueError('usage refused')
        with self._transaction() as db:
            if db.execute('UPDATE requests SET observed_ms=?,outcome=? WHERE id=? AND observed_ms IS NULL',(observed_ms,outcome,reservation.id)).rowcount!=1:raise ValueError('unknown/already completed request')
    def finish(self,attempt,status):
        if status not in ('ok','failed'):raise ValueError('status refused')
        with self._transaction() as db:
            db.execute('UPDATE attempts SET status=? WHERE id=? AND status=?',(status,attempt.id,'reserved'))
    def attempt(self,day,pair,tool):
        with self._transaction() as db:
            return db.execute('SELECT digest,status FROM attempts WHERE day=? AND pair=? AND tool=?',(day,pair,tool)).fetchone()
    def usage(self,day):
        with self._transaction() as db:
            count,reserved,observed,unknown=db.execute('SELECT COUNT(*),COALESCE(SUM(milliseconds),0),COALESCE(SUM(observed_ms),0),SUM(CASE WHEN observed_ms IS NULL THEN 1 ELSE 0 END) FROM requests WHERE day=?',(day,)).fetchone()
        return {'supervised_requests':count,'reserved_ms':reserved,'observed_ms':observed,'uncertain_requests':unknown or 0,'provider_internal_retries':None}
