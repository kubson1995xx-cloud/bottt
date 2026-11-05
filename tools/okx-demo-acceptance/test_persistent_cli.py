#!/usr/bin/env python3
"""Synthetic real-entry tests. Friend fixture never enters production package."""
import argparse,ctypes,errno,fcntl,hashlib,json,os,pty,select,signal,subprocess,termios,time
from pathlib import Path
D='/home/ubuntu/.dotnet/dotnet'
BASE=Path('/home/ubuntu/apex-deliveries/okx-demo-fee0/persistent-runner')
ENV={'HOME':'/home/ubuntu','PATH':'/usr/bin:/bin','DOTNET_ROOT':'/home/ubuntu/.dotnet','DOTNET_EnableDiagnostics':'0','COMPlus_EnableDiagnostics':'0','LANG':'C.UTF-8','TMPDIR':'/home/ubuntu/.hermes/cache/scratch'}
def field(seed):return bytes(33+(i*31+seed*17+i//19)%94 for i in range(64))
def no_network():
    # Linux x64 seccomp: deny AF_INET/AF_INET6 sockets, including descendants.
    class F(ctypes.Structure):_fields_=[('code',ctypes.c_ushort),('jt',ctypes.c_ubyte),('jf',ctypes.c_ubyte),('k',ctypes.c_uint)]
    class P(ctypes.Structure):_fields_=[('len',ctypes.c_ushort),('filter',ctypes.POINTER(F))]
    filters=(F*8)(F(0x20,0,0,0),F(0x15,0,5,41),F(0x20,0,0,16),F(0x15,2,0,2),F(0x15,1,0,10),F(0x06,0,0,0x7fff0000),F(0x06,0,0,0x50000|errno.EACCES),F(0x06,0,0,0x7fff0000))
    lib=ctypes.CDLL(None,use_errno=True)
    if lib.prctl(38,1,0,0,0)!=0 or lib.prctl(22,2,ctypes.byref(P(8,filters)))!=0:os._exit(97)
def invoke(cmd,tty=False,seed=1,cancel=False):
    env=dict(ENV);out=bytearray();hidden=[];restored=True
    if tty:
        m,s=pty.openpty();before=termios.tcgetattr(s);env.update(SSH_CONNECTION='192.0.2.1 12345 192.0.2.2 22',SSH_TTY=os.ttyname(s))
        def setup():
            os.setsid();fcntl.ioctl(0,termios.TIOCSCTTY,0);os.tcsetpgrp(0,os.getpgrp());no_network()
        p=subprocess.Popen(cmd,stdin=s,stdout=s,stderr=s,env=env,preexec_fn=setup,close_fds=True)
        prompts=[b'API Key: ',b'Secret Key: ',b'Passphrase: '];n=0;deadline=time.monotonic()+40
        try:
            while p.poll() is None and time.monotonic()<deadline:
                if select.select([m],[],[],.03)[0]:
                    try:out.extend(os.read(m,65536))
                    except OSError:break
                if n<3 and prompts[n] in out:
                    hidden.append(not bool(termios.tcgetattr(s)[3]&(termios.ECHO|termios.ECHONL)))
                    if cancel:os.write(m,field(seed+n)[:5]);os.kill(p.pid,signal.SIGINT);n=3
                    else:os.write(m,field(seed+n)+b'\n');n+=1
            p.wait(timeout=5)
            while select.select([m],[],[],.03)[0]:
                try:out.extend(os.read(m,65536))
                except OSError:break
            restored=termios.tcgetattr(s)==before
        finally:
            if p.poll() is None:p.kill();p.wait()
            os.close(m);os.close(s)
    else:
        p=subprocess.run(cmd,input=b'',stdout=subprocess.PIPE,stderr=subprocess.STDOUT,env=env,preexec_fn=no_network,timeout=40);out.extend(p.stdout)
    assert not any(field(i) in out for i in range(1,14)),'SECRET_OUTPUT'
    assert restored and all(hidden),'TTY_NOT_PRIVATE_OR_RESTORED'
    return p.returncode,out.decode(errors='replace'),len(hidden)
def main():
    a=argparse.ArgumentParser();a.add_argument('harness',type=Path);a.add_argument('evidence',type=Path);a.add_argument('--production',type=Path);x=a.parse_args()
    testbase=Path('/home/ubuntu/apex-deliveries/apex-okx-persistent-cli-tests');testbase.mkdir(mode=0o700,exist_ok=True)
    root=testbase/x.evidence.name;root.mkdir(mode=0o700);cases=[]
    def run(r,flag=None,scenario='deny',**kw):return invoke([D,str(x.harness),'cli',str(r),scenario]+([] if flag is None else [flag]),**kw)
    def seed(r):assert invoke([D,str(x.harness),'seed',str(r)])[0]==0
    def verify(r,n=1):assert invoke([D,str(x.harness),'verify',str(r),str(n)])[0]==0
    def check(name,f):
        try:f();cases.append({'name':name,'passed':True});print('PASS',name,flush=True)
        except Exception as e:cases.append({'name':name,'passed':False,'reason':str(e)[:120]});print('FAIL',name,str(e)[:120],flush=True)
    def expect(r,flag,code,text='',**kw):
        c,o,h=run(r,flag,**kw);assert c==code,(c,code,o);assert text in o,o
        if kw.get('scenario','deny')=='deny':assert 'TEST_DISCOVERY_CONSTRUCTIONS=0;GET_SENDS=0' in o,o
        return o,h
    r=root/'lifecycle';r.mkdir(mode=0o700)
    check('missing_default_no_tty_no_network',lambda:expect(r,None,8,'--save-credentials'))
    check('missing_status_no_tty_no_vault_creation',lambda:(expect(r,'--credentials-status',0,'NotStored'),assertion(not (r/'vault').exists())))
    check('real_hidden_native_save_three_fields',lambda:(assertion(expect(r,'--save-credentials',0,'STORED_NO_NETWORK_NO_TRADING',tty=True)[1]==3),verify(r)))
    # Seed independent fallback fixture so a RED save does not cause setup-only failures.
    q=root/'seeded';q.mkdir(mode=0o700);seed(q)
    check('stored_status_no_tty',lambda:expect(q,'--credentials-status',0,'Stored'))
    check('default_reuses_stored_without_tty',lambda:expect(q,None,4,'TEST_DISCOVERY_CONSTRUCTIONS=1;GET_SENDS=3',scenario='parser'))
    check('second_fresh_process_reuses_without_tty',lambda:expect(q,None,4,'TEST_DISCOVERY_CONSTRUCTIONS=1;GET_SENDS=3',scenario='parser'))
    check('save_no_clobber',lambda:(expect(q,'--save-credentials',8,'DEMO_VAULT_ALREADYSTORED',tty=True),verify(q)))
    # Authority/report operands are private synthetic bytes; byte-for-byte preservation.
    runroot=q/'run';runroot.mkdir(mode=0o700,exist_ok=True)
    paths=[runroot/'last-report.json',runroot/'naval_authority_broker_v2.json',runroot/'.naval-broker-initialized-v2']
    for i,p in enumerate(paths):p.write_bytes(('synthetic-preserve-'+str(i)).encode());p.chmod(0o600)
    before={str(p):p.read_bytes() for p in paths}
    def preserved():assertion(all(Path(p).read_bytes()==b for p,b in before.items()))
    cipher=(q/'vault/credentials.cred').read_bytes()
    check('authority_refuses_before_decrypt_or_network',lambda:(expect(q,None,3,'EXISTING_FINANCIAL_STATE'),preserved()))
    def before_decrypt():
        rec=q/'vault/credentials.cred';rec.chmod(0o644)
        try:expect(q,None,3,'EXISTING_FINANCIAL_STATE');preserved()
        finally:rec.chmod(0o600)
    check('financial_refusal_precedes_unsafe_vault_decrypt',before_decrypt)
    check('get_only_diagnostic_preserves_financial_report',lambda:(expect(q,'--check-connection',4,'TEST_DISCOVERY_CONSTRUCTIONS=1;GET_SENDS=3',scenario='parser'),preserved()))
    def diagnostic_success():
        expect(q,'--check-connection',0,'READ_ONLY_DISCOVERY_COMPLETED_STOP_NO_TRADING',scenario='safe');preserved()
        reports=list((q/'run/connection-reports').glob('*.json'));assert len(reports)==2
        docs=[json.loads(p.read_text()) for p in reports];d=next(d for d in docs if d['result']=='READ_ONLY_DISCOVERY_COMPLETED_STOP_NO_TRADING')
        assert d['operation']=='CHECK_CONNECTION_GET_ONLY' and d['complete'] and not d['armed'] and not d['schedulerStarted'] and d['mode']=='STOP'
        assert all(d[k] is None for k in ['baseline','orders','intents','fills','reservations','transport'])
        d=next(d for d in docs if d['result']=='READ_ONLY_DISCOVERY_REFUSED_STOP_NO_TRADING')['discoveryDiagnostic']['Diagnostic']
        assert d['HttpStatus']==201 and d['Payload']['Path']=='$.data[].details[].cashBal' and d['ConfigAuthenticated'] and d['AccountSafetyValidated']
    check('four_signed_GET_only_report_no_owner',diagnostic_success)
    check('reached_safety_diagnostic_two_GET',lambda:(expect(q,'--check-connection',4,'TEST_DISCOVERY_CONSTRUCTIONS=1;GET_SENDS=2',scenario='safety'),preserved()))
    def unsupported(scenario):
        folder=q/'run/connection-reports';before_names=set(folder.glob('*.json'))
        expect(q,'--check-connection',4,'TEST_DISCOVERY_CONSTRUCTIONS=1;GET_SENDS=3',scenario=scenario);preserved()
        created=set(folder.glob('*.json'))-before_names;assert len(created)==1
        text=created.pop().read_text();d=json.loads(text);f=d['discoveryDiagnostic']['Diagnostic']
        assert not any(s in text for s in ['SYNTHETIC_PRIVATE_BALANCE_VALUE','SYNTHETIC_PRIVATE_PROPERTY_NAME','ANOTHER_PRIVATE_PROPERTY_NAME'])
        assert d['complete'] and d['mode']=='STOP' and not d['armed'] and not d['schedulerStarted']
        assert all(d[k] is None for k in ['baseline','orders','intents','fills','reservations','transport'])
        assert f['HttpStatus']==201 and f['OkxCode']=='0' and f['Payload']['Reason']=='UNSUPPORTED_PROPERTY'
        assert f['Payload']['Path']=='$.data[].details[].<unknown>' and f['ConfigAuthenticated'] and f['AccountSafetyValidated']
        summary=f['Payload']['UnsupportedProperties']
        if scenario=='balance-known':assert summary==[{'Name':n,'DocumentedType':'String','Actual':'String','Count':1} for n in ['coinUsdPrice','maxSpotInUseAmt']]
        else:assert summary==[{'Name':'<unknown>','DocumentedType':'Undocumented','Actual':'String','Count':2}]
    check('stored_check_closed_documented_name_diagnostic_retains_refusal',lambda:unsupported('balance-known'))
    check('stored_check_unknown_names_grouped_private_retains_refusal',lambda:unsupported('balance-unknown'))
    check('replace_cancel_preserves_record_and_authority',lambda:(expect(q,'--replace-credentials',6,'CANCELLED',tty=True,cancel=True),assertion((q/'vault/credentials.cred').read_bytes()==cipher),preserved(),verify(q)))
    check('explicit_hidden_replace_native_roundtrip',lambda:(expect(q,'--replace-credentials',0,'STORED_NO_NETWORK_NO_TRADING',tty=True,seed=8),verify(q,8),preserved()))
    def busy():
        p=subprocess.Popen([D,str(x.harness),'hold',str(q)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=ENV,preexec_fn=no_network)
        try:
            assert p.stdout is not None and p.stdout.readline()==b'LOCK_HELD\n'
            for flag in ['--save-credentials','--replace-credentials','--forget-credentials','--credentials-status','--check-connection']:
                expect(q,flag,9,'RUN_LOCK_BUSY_NO_CHANGES')
            preserved();verify(q,8)
        finally:p.terminate();p.wait(timeout=5)
    check('active_run_lock_blocks_all_credential_changes',busy)
    check('forget_only_owned_ciphertext_preserves_financial',lambda:(expect(q,'--forget-credentials',0,'REMOVED_NO_NETWORK_NO_TRADING'),preserved(),assertion(not (q/'vault/credentials.cred').exists())))
    check('forget_idempotent',lambda:expect(q,'--forget-credentials',0,'NOT_STORED'))
    z=root/'tamper';z.mkdir(mode=0o700);seed(z);rec=z/'vault/credentials.cred';b=bytearray(rec.read_bytes());b[len(b)//2]^=1;rec.write_bytes(b)
    check('tampered_default_no_fallback_no_network',lambda:expect(z,None,8,'DEMO_VAULT_'))
    check('tampered_status_unavailable',lambda:expect(z,'--credentials-status',8,'Unavailable'))
    rec.chmod(0o644)
    check('unsafe_default_refuses_before_broker',lambda:expect(z,None,8,'DEMO_VAULT_PATHREFUSED'))
    check('save_without_tty_safe_refusal',lambda:expect(root/'notty','--save-credentials',7,'SAFE_REFUSAL'))
    check('extra_arguments_refused',lambda:assertion(invoke([D,str(x.harness),'cli',str(q),'deny','--credentials-status','extra'])[0]==2))
    if x.production:
        for flag,code in [('--identity',0),('--help',0),('--unknown',2)]:check('production_'+flag,lambda flag=flag,code=code:assertion(invoke([str(x.production),flag])[0]==code))
    result={'passed':sum(c['passed'] for c in cases),'failed':sum(not c['passed'] for c in cases),'total':len(cases),'kernel_ipv4_ipv6_denied':True,'cases':cases}
    x.evidence.joinpath('cli-results.json').write_text(json.dumps(result,indent=2));print(json.dumps({k:v for k,v in result.items() if k!='cases'}));return bool(result['failed'])
def assertion(b):assert b
if __name__=='__main__':raise SystemExit(main())
