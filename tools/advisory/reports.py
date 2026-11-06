"""Strict unchanged shadow v1 reports plus a separately versioned generation index.

Fixture inference is never published as v1 Status=ok. Native result/evidence is
retained in immutable generation data; the old C# parser remains unchanged.
"""
import hashlib,json,os,re,secrets,unicodedata
from contextlib import nullcontext
from pathlib import Path
from datetime import timedelta
from market_contract import *
from runtime_contract import PINS,MODEL,PROVIDER

FIELDS={'Schema','Status','Tool','Instrument','QuoteCurrency','AsOfUtc','ExpiresUtc','DatasetSha256','Provider','Model','Recommendation','ExecutionAllowed','EvidenceSources','Summary','SourceCommit'}
MAPPING={'Buy':'BUY','Overweight':'BUY','Hold':'WAIT','Underweight':'SELL','Sell':'SELL'}

def native_vibe_decision(final):
    if final.get('status')!='success' or final.get('degraded'):raise ValueError('native grounding refused')
    decision=strict_json(final.get('content',''),16000)
    if set(decision)!={'Recommendation','Summary'} or decision['Recommendation'] not in ('BUY','WAIT','SELL') or type(decision['Summary']) is not str or not 1<=len(decision['Summary'])<=2000:raise ValueError('invalid native decision')
    return decision

def normalize(tool,rating):
    if tool=='vibe-trading' and rating in ('BUY','WAIT','SELL'):return rating
    if tool=='tradingagents' and rating in MAPPING:return MAPPING[rating]
    raise ValueError('native rating refused')

def atomic_write(path,raw,*,store=None):
    path=Path(path)
    if store:store.directory(path.parent);store.inspect(path)
    else:path.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
    if path.is_symlink() or (path.exists() and path.stat().st_nlink!=1):raise ValueError('unsafe target')
    temp=path.parent/('.'+path.name+'.'+secrets.token_hex(12))
    try:
        fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
        with os.fdopen(fd,'wb') as f:
            with store.reading(temp) if store else nullcontext():
                f.write(raw);f.flush();os.fsync(f.fileno())
        # The old pointer is inspected, not held across its legitimate replacement.
        if store:store.inspect(path)
        os.replace(temp,path)
        if os.name!='nt':
            fd=os.open(path.parent,os.O_RDONLY)
            try:os.fsync(fd)
            finally:os.close(fd)
    finally:
        if temp.exists():temp.unlink()

def encoded(obj):return json.dumps(obj,ensure_ascii=True,separators=(',',':'),allow_nan=False).encode('ascii')

def make_report(tool,data,native=None,error=None,fixture=False):
    validate_dataset(data)
    rating=(native or {}).get('native_rating')
    mapped='WAIT';valid=False
    if not error:
        if not native or native.get('DatasetSha256')!=dataset_digest(canonical_snapshot(data['Instrument'],data['AsOfUtc'],data['Bars'])) or not isinstance(native.get('summary'),str):raise ValueError('invalid upstream result')
        mapped=normalize(tool,rating)
        if native.get('recommendation')!=mapped:raise ValueError('rating mapping mismatch')
        valid=True
    summary=' '.join(''.join(' ' if unicodedata.category(c).startswith('C') else c for c in (native or {}).get('summary','Unavailable')).split())
    if fixture:summary='FIXTURE INFERENCE; NOT GENUINE MODEL OUTPUT. '+summary
    report={'Schema':'apex-advisory-v1','Status':'ok' if valid and not fixture else 'unavailable','Tool':tool,'Instrument':data['Instrument'],'QuoteCurrency':'EUR','AsOfUtc':data['AsOfUtc'],'ExpiresUtc':(utc(data['AsOfUtc'])+timedelta(days=1)).strftime('%Y-%m-%dT%H:%M:%SZ'),'DatasetSha256':dataset_digest(canonical_snapshot(data['Instrument'],data['AsOfUtc'],data['Bars'])),'Provider':PROVIDER,'Model':MODEL,'Recommendation':mapped if valid and not fixture else 'WAIT','ExecutionAllowed':False,'EvidenceSources':[ENDPOINT],'Summary':summary[:1000],'SourceCommit':PINS[tool]['commit']}
    return report

def validate_report(report,data,now):
    if type(report) is not dict or set(report)!=FIELDS:raise ValueError('closed report schema')
    expected=make_report(report['Tool'],data,error='unavailable')
    for key in FIELDS-{'Status','Summary','Recommendation'}:
        if type(report[key]) is not type(expected[key]) or report[key]!=expected[key]:raise ValueError('report binding mismatch')
    if report['Status'] not in ('ok','unavailable') or report['Recommendation'] not in ('BUY','WAIT','SELL') or type(report['Summary']) is not str or len(report['Summary'])>1000 or any(unicodedata.category(c).startswith('C') for c in report['Summary']):raise ValueError('report values refused')
    if not utc(data['AsOfUtc'])<=now<utc(data['AsOfUtc'])+timedelta(days=1):raise ValueError('stale report')
    return report

def publish_pair(root,data,results,fixture=False,*,store=None):
    root=Path(root);generation=secrets.token_hex(16);folder=root/'generations'/generation
    if store:store.directory(folder)
    else:folder.mkdir(parents=True,mode=0o700)
    records={}
    for tool in TOOLS:
        entry=results[tool];native=entry.get('native');error=entry.get('error')
        report=make_report(tool,data,native,error,fixture)
        rb=encoded(report);nb=encoded(entry)
        atomic_write(folder/(tool+'.json'),rb,store=store);atomic_write(folder/(tool+'-native.json'),nb,store=store)
        rating=(native or {}).get('native_rating')
        records[tool]={'ReportSha256':dataset_digest(rb),'NativeSha256':dataset_digest(nb),'OriginalRecommendation':rating,'MappedRecommendation':normalize(tool,rating) if rating is not None else None,'State':'completed' if not error else 'failed'}
    manifest={'Schema':'apex-advisory-pair-v1','Generation':generation,'Instrument':data['Instrument'],'DatasetSha256':dataset_digest(canonical_snapshot(data['Instrument'],data['AsOfUtc'],data['Bars'])),'AsOfUtc':data['AsOfUtc'],'ExpiresUtc':(utc(data['AsOfUtc'])+timedelta(days=1)).strftime('%Y-%m-%dT%H:%M:%SZ'),'Inference':'fixture' if fixture else 'genuine','Tools':records}
    mb=encoded(manifest);atomic_write(folder/'manifest.json',mb,store=store)
    atomic_write(root/'CURRENT.json',encoded({'Schema':'apex-advisory-pointer-v1','Generation':generation,'ManifestSha256':dataset_digest(mb)}),store=store)
    return manifest

def read_pair(root,data,now,*,store=None):
    def read(path):
        with store.reading(path) if store else nullcontext():return path.read_bytes()
    root=Path(root);pointer=strict_json(read(root/'CURRENT.json'),4096)
    if set(pointer)!={'Schema','Generation','ManifestSha256'} or pointer['Schema']!='apex-advisory-pointer-v1' or not re.fullmatch('[a-f0-9]{32}',pointer['Generation']):raise ValueError('invalid generation pointer')
    folder=root/'generations'/pointer['Generation'];raw=read(folder/'manifest.json')
    if dataset_digest(raw)!=pointer['ManifestSha256']:raise ValueError('manifest hash mismatch')
    m=strict_json(raw,16384)
    if set(m)!={'Schema','Generation','Instrument','DatasetSha256','AsOfUtc','ExpiresUtc','Inference','Tools'} or m['Schema']!='apex-advisory-pair-v1' or m['Generation']!=pointer['Generation'] or m['Instrument']!=data['Instrument'] or m['DatasetSha256']!=dataset_digest(canonical_snapshot(data['Instrument'],data['AsOfUtc'],data['Bars'])) or m['AsOfUtc']!=data['AsOfUtc'] or m['ExpiresUtc']!=(utc(data['AsOfUtc'])+timedelta(days=1)).strftime('%Y-%m-%dT%H:%M:%SZ') or m['Inference'] not in ('fixture','genuine') or set(m['Tools'])!=set(TOOLS):raise ValueError('pair binding mismatch')
    results={}
    for tool in TOOLS:
        row=m['Tools'][tool]
        if set(row)!={'ReportSha256','NativeSha256','OriginalRecommendation','MappedRecommendation','State'}:raise ValueError('tool metadata schema')
        rb=read(folder/(tool+'.json'));nb=read(folder/(tool+'-native.json'))
        if dataset_digest(rb)!=row['ReportSha256'] or dataset_digest(nb)!=row['NativeSha256']:raise ValueError('report hash mismatch')
        report=validate_report(strict_json(rb,16384),data,now);entry=strict_json(nb)
        rating=(entry.get('native') or {}).get('native_rating')
        if rating!=row['OriginalRecommendation'] or row['MappedRecommendation']!=(normalize(tool,rating) if rating is not None else None):raise ValueError('native mapping mismatch')
        if m['Inference']=='fixture' and report['Status']!='unavailable':raise ValueError('fixture promotion refused')
        results[tool]=entry
    return m,results
