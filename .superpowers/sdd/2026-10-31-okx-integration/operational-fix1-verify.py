from pathlib import Path
import hashlib,json,subprocess,shutil,xml.etree.ElementTree as ET
root=Path('/home/ubuntu/projects/apex-trading-ai')
plan=root/'.superpowers/sdd/2026-10-31-okx-integration'
base=plan/'operational-fix1-evidence'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
load=lambda p:json.loads(p.read_text())
gates=sorted(p for p in base.iterdir() if p.is_dir() and (p/'summary.json').exists())
assert len(gates)==7
red=gates[0];green=gates[1];full=gates[2]
product=['src/Apex.Core/Naval/NavalBrokerBootstrap.cs','src/Apex.Desktop/Okx/OkxAccountConnectionService.cs']
fixture='tests/Apex.Tests/OkxOperationalAccountFixTests.cs'
peer='tests/Apex.Tests/OkxOperationalAccountTests.cs'
original='b1982a89094fb36b3eecdc3d3584f0d09d1bf265'
redmap=load(red/'inputs-before.json')
for path in product+[peer]:
    original_bytes=subprocess.check_output(['git','show',original+':'+path],cwd=root)
    assert hashlib.sha256(original_bytes).hexdigest()==redmap[path],path
assert sha(root/peer)==redmap[peer]
assert sha(root/fixture)==redmap[fixture]
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def results(g):
    return {x.attrib['testId']:dict(x.attrib) for x in ET.parse(g/'results/results.trx').findall('.//t:UnitTestResult',ns)}
r=results(red);g=results(green);f=results(full)
failed=[x for x in r.values() if x['outcome']=='Failed']
assert len(failed)==4
assert load(red/'summary.json')['counts']=={'Failed':4,'Passed':3}
for x in r.values():
    for dst in [g,f]:
        assert dst[x['testId']]['outcome']=='Passed'
        assert dst[x['testId']]['testName']==x['testName']
assert len([x for x in f.values() if 'OkxOperationalAccountTests.' in x['testName']])==23
# The existing four drain regression identities remain covered in the final integrated gate.
old=load(plan/'operational-evidence/10-drain-red/trx-results.json')
assert len(old)==4
for x in old:assert f[x['testId']]['outcome']=='Passed'
summary=[];binary_store=base/'settled-binaries-by-sha256';binary_store.mkdir(exist_ok=True)
retained={};seen=set()
for gate in gates:
    s=load(gate/'summary.json');assert s['source_delta']==[] and s['dependency_delta']==[]
    before=load(gate/'inputs-before.json');assert before==load(gate/'inputs-after.json')
    assert len(before)==308
    if gate!=red:
        assert s['exit']==0
        assert all(sha(root/p)==h for p,h in before.items())
    deps=load(gate/'dependencies-before.json');assert len(deps)==337
    assert all(sha(Path(p))==h for p,h in deps.items())
    binaries=load(gate/'binaries-after.json')
    if gate!=red:
        assert all(sha(root/p)==h for p,h in binaries.items()),gate.name
        retained[gate.name]=binaries
        for path,h in binaries.items():
            if h not in seen:
                shutil.copyfile(root/path,binary_store/h);assert sha(binary_store/h)==h;seen.add(h)
    summary.append({'gate':gate.name,**s,'input_files':len(before)})
assert load(full/'summary.json')['counts']=={'Passed':1125}
assert load(gates[3]/'summary.json')['counts']=={'Passed':168}
# No runtime/source changes beyond the two authorized product files.
delta=subprocess.check_output(['git','diff',original,'--name-only','--','src'],cwd=root,text=True).splitlines()
assert sorted(delta)==sorted(product)
# Actual consumed Core/Desktop copies agree with final production builds.
for assembly,source in [('Apex.Core.dll','src/Apex.Core/bin/Debug/net6.0/Apex.Core.dll'),('ApexDesktop.dll','src/Apex.Desktop/bin/Debug/net8.0/ApexDesktop.dll')]:
    assert sha(root/'tests/Apex.Tests/bin/Debug/net8.0'/assembly)==sha(root/source)
assert sha(root/'tests/Apex.Backtester.Tests/bin/Debug/net8.0/Apex.Core.dll')==sha(root/'src/Apex.Core/bin/Debug/net6.0/Apex.Core.dll')
(base/'settled-binary-map.json').write_text(json.dumps(retained,indent=2))
proof={'base':original,'current_head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),
 'product_delta':delta,'original_peer_unchanged':True,'original_red_green_test_source_sha256':redmap[fixture],
 'red_to_green':[{'testId':x['testId'],'testName':x['testName'],'red':x['outcome'],'targeted':g[x['testId']]['outcome'],'integrated':f[x['testId']]['outcome']} for x in failed],
 'gates':summary,'retained_unique_final_binary_files':len(seen),'retained_final_binary_bytes':sum(p.stat().st_size for p in binary_store.iterdir()),
 'red_binary_retention':'Hashes retained contemporaneously; RED DLL payloads were not copied before the GREEN build. Original source snapshot is retained.',
 'evidence_helpers':{p.name:sha(p) for p in [plan/'operational-run.py',plan/'operational-fix1-run.py',Path(__file__)]}}
(base/'verification.json').write_text(json.dumps(proof,indent=2))
print(json.dumps(proof,indent=2))
