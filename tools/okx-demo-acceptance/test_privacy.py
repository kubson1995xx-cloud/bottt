#!/usr/bin/env python3
"""Actual Linux PTY/signal tests with synthetic-only inputs and no broker components."""
import argparse,fcntl,hashlib,json,os,pty,select,signal,subprocess,termios,time
from pathlib import Path

def run(dll,mode='--offline-input-check',kill=None,maximum=False,invalid=False,extra_env=False):
    master,slave=pty.openpty();name=os.ttyname(slave);before=termios.tcgetattr(slave)
    env={'HOME':'/home/ubuntu','PATH':'/usr/bin:/bin','DOTNET_ROOT':'/home/ubuntu/.dotnet','DOTNET_EnableDiagnostics':'0','COMPlus_EnableDiagnostics':'0','LANG':'C.UTF-8','SSH_CONNECTION':'192.0.2.1 12345 192.0.2.2 22','SSH_TTY':name,'TMPDIR':'/home/ubuntu/.hermes/cache/scratch'}
    if extra_env:env['SSLKEYLOGFILE']='/home/ubuntu/.hermes/cache/scratch/never-create-tls-keylog'
    def setup():
        os.setsid();fcntl.ioctl(0,termios.TIOCSCTTY,0);os.tcsetpgrp(0,os.getpgrp())
    p=subprocess.Popen(['/home/ubuntu/.dotnet/dotnet',str(dll),mode],stdin=slave,stdout=slave,stderr=slave,env=env,preexec_fn=setup,close_fds=True)
    output=bytearray();values=[(x.encode()*4096 if maximum else ('SYNTHETIC_PRIVATE_'+x+'_WILL_NOT_ECHO').encode()) for x in ('K','S','P')];sent=0;hidden=[];deadline=time.monotonic()+20
    prompts=[b'API Key: ',b'Secret Key: ',b'Passphrase: ']
    try:
        while p.poll() is None and time.monotonic()<deadline:
            if select.select([master],[],[],.05)[0]:
                try:output.extend(os.read(master,65536))
                except OSError:break
            if sent<3 and prompts[sent] in output:
                hidden.append(not bool(termios.tcgetattr(slave)[3]&(termios.ECHO|termios.ECHONL)))
                if kill is not None:
                    os.write(master,values[sent][:12]);os.kill(p.pid,kill);sent=3;continue
                os.write(master,b'\n' if invalid else values[sent]+b'\n');sent+=1
        if p.poll() is None:p.kill();raise AssertionError('PTY_CHILD_TIMEOUT')
        p.wait(timeout=5)
        while select.select([master],[],[],.05)[0]:
            try:output.extend(os.read(master,65536))
            except OSError:break
        assert all(hidden),'ECHO_NOT_DISABLED'
        assert termios.tcgetattr(slave)==before,'TTY_NOT_RESTORED'
        assert not any(v in output for v in values),'CREDENTIAL_ECHO_LEAK'
        assert b'SYNTHETIC_PRIVATE_MESSAGE_MUST_NOT_ESCAPE' not in output,'EXCEPTION_LEAK'
        if kill or invalid or extra_env or mode.endswith('error'):assert p.returncode!=0,'FAILURE_WAS_SUCCESS'
        else:assert p.returncode==0 and b'OFFLINE_INPUT_ACCEPTED_NO_NETWORK_NO_FILES' in output,'INPUT_NOT_ACCEPTED'
        return {'exit':p.returncode,'hidden_prompts':len(hidden),'tty_restored':True,'secret_echo':False,'output_sha256':hashlib.sha256(output).hexdigest()}
    finally:
        if p.poll() is None:p.kill();p.wait()
        os.close(master);os.close(slave)

def main():
    a=argparse.ArgumentParser();a.add_argument('dll',type=Path);a.add_argument('output',type=Path);args=a.parse_args();cases={}
    cases['normal']=run(args.dll)
    cases['max4096']=run(args.dll,maximum=True)
    cases['empty']=run(args.dll,invalid=True)
    cases['sanitized_error']=run(args.dll,mode='--offline-input-error')
    cases['unsafe_environment']=run(args.dll,extra_env=True)
    for sig in (signal.SIGINT,signal.SIGTERM,signal.SIGHUP,signal.SIGQUIT,signal.SIGTSTP):cases[sig.name]=run(args.dll,kill=sig)
    result={'scope':'synthetic credentials; real production executable; offline input modes make no network or state writes','cases':cases,'passed':len(cases),'failed':0}
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2));print(json.dumps(result))
if __name__=='__main__':main()
