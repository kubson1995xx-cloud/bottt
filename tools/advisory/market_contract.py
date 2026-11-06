"""Lossless canonical serialization of the actual strategy-consumed NavalSeries.

Only O/H/L/C/base volume are consumed by ReadSeriesAsync. Provider's unused
volume columns are deliberately not asserted equal. No float enters the seal.
"""
from datetime import datetime, timezone, timedelta
from decimal import Decimal
import hashlib
import json
import re

INSTRUMENTS={'BTC-EUR':('BTC','EUR'),'ETH-EUR':('ETH','EUR'),'SOL-EUR':('SOL','EUR')}
TOOLS=('vibe-trading','tradingagents')
SOURCE='OKX EEA public /market/candles 1Dutc'
ENDPOINT='https://eea.okx.com/api/v5/market/candles'

def instrument_context(pair):
    if type(pair) is not str or pair not in INSTRUMENTS: raise ValueError('instrument refused')
    base,quote=INSTRUMENTS[pair]
    return dict(instrument=pair,base=base,quote=quote,source=SOURCE,bar='1Dutc')

def utc(text):
    if type(text) is not str or not re.fullmatch(r'\d{4}-\d{2}-\d{2}T00:00:00Z',text): raise ValueError('cutoff refused')
    return datetime.strptime(text,'%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=timezone.utc)

def decimal_text(raw):
    if type(raw) is not str or not re.fullmatch(r'(?:0|[1-9][0-9]{0,27})(?:\.[0-9]{1,28})?',raw): raise ValueError('decimal string required')
    value=Decimal(raw)
    # Exact System.Decimal range/scale, not the feed parser's possible rounding.
    trimmed=raw.rstrip('0').rstrip('.') if '.' in raw else raw
    fractional=len(trimmed.split('.')[1]) if '.' in trimmed else 0
    coefficient=int(trimmed.replace('.',''))
    if fractional>28 or coefficient>79228162514264337593543950335: raise ValueError('not lossless System.Decimal')
    return trimmed or '0'

def canonical_snapshot(pair,cutoff,bars):
    context=instrument_context(pair); end=utc(cutoff)
    if type(bars) is not list or not 200<=len(bars)<=300: raise ValueError('200..300 completed bars required')
    canonical=[]; previous=None
    for row in bars:
        if type(row) is not list or len(row)!=7 or any(type(x) is not str for x in row) or row[6]!='1': raise ValueError('canonical strategy bar required')
        if not re.fullmatch(r'[1-9][0-9]{0,15}',row[0]): raise ValueError('timestamp refused')
        timestamp=int(row[0]); opening=datetime.fromtimestamp(timestamp//1000,timezone.utc)
        if timestamp%86400000 or opening+timedelta(days=1)>end or (previous is not None and timestamp-previous!=86400000): raise ValueError('bar boundary/order refused')
        previous=timestamp
        values=[decimal_text(x) for x in row[1:6]]; o,h,l,c,v=map(Decimal,values)
        if min(o,h,l,c)<=0 or v<0 or not l<=o<=h or not l<=c<=h: raise ValueError('OHLCV refused')
        canonical.append([str(timestamp),*values,'1'])
    if datetime.fromtimestamp(previous//1000,timezone.utc)+timedelta(days=1)!=end: raise ValueError('latest completed bar missing')
    data={'Schema':'apex-strategy-candles-v1','Instrument':pair,'BaseCurrency':context['base'],'QuoteCurrency':'EUR','Source':SOURCE,'Bar':'1Dutc','PriceBasis':'trade','VolumeUnit':context['base'],'AsOfUtc':cutoff,'Bars':canonical}
    return json.dumps(data,ensure_ascii=True,separators=(',',':'),allow_nan=False).encode('ascii')

def dataset_digest(data): return hashlib.sha256(data).hexdigest()

def strict_json(data,limit=1048576):
    if not isinstance(data,(str,bytes)) or len(data)>limit: raise ValueError('JSON bound')
    def unique(pairs):
        result={}
        for k,v in pairs:
            if k in result: raise ValueError('duplicate JSON field')
            result[k]=v
        return result
    return json.loads(data,object_pairs_hook=unique,parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite JSON')))

def validate_dataset(data,now=None):
    raw=canonical_snapshot(data.get('Instrument'),data.get('AsOfUtc'),data.get('Bars'))
    if strict_json(raw)!=data: raise ValueError('dataset identity/source/units refused')
    if now is not None and not utc(data['AsOfUtc'])<=now<utc(data['AsOfUtc'])+timedelta(days=1): raise ValueError('stale/future dataset')
    return data

def decode_snapshot(raw,pair,cutoff):
    data=strict_json(raw); validate_dataset(data)
    if data['Instrument']!=pair or data['AsOfUtc']!=cutoff or raw!=canonical_snapshot(pair,cutoff,data['Bars']): raise ValueError('noncanonical snapshot')
    return data
