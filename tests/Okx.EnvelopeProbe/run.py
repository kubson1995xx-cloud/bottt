#!/usr/bin/env python3
"""Run real encoder source with test-only allocation observation and write faults.
Usage: run.py --output NEW_DIRECTORY [--product EXACT_DLL] [--dotnet DOTNET]
The output directory must be new; build/setup errors are not behavioral RED.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--output', required=True, type=Path)
p.add_argument('--product', type=Path)
p.add_argument('--dotnet', default='/home/ubuntu/.dotnet/dotnet')
a = p.parse_args()
r = Path(__file__).resolve().parents[2]
out = a.output.resolve()
out.mkdir(parents=True, exist_ok=False)
product = (a.product or r / 'tests/Apex.Tests/bin/Release/net8.0/ApexDesktop.dll').resolve()
review = r / '.superpowers/sdd/2026-10-31-okx-integration/task-1-review-evidence'
source = r / 'src/Apex.Desktop/Okx/OkxExecutionCredentials.cs'
text = source.read_text()
# Exactly one substitution in Encode; Decode and all writes/cleanup are unchanged.
pattern = r'using var stream = new MemoryStream\((?:|owned, 0, owned.Length, writable: true, publiclyVisible: true)\);'
observed, n = re.subn(pattern, lambda m: m[0].replace('new MemoryStream(', 'new global::TrackingMemoryStream('), text)
assert n == 1, 'Unknown encoder allocation: inspect observer before running'
(out / 'ObservedCredentials.cs').write_text(observed)
(out / 'Probe.cs').write_bytes(Path(__file__).with_name('Probe.cs').read_bytes())
fixtures = json.loads((review / 'probe-inputs.json').read_text())['fixtureInputs']
# Original scope, accepted lengths and generator unchanged; no activation rerun.
(out / 'fixture-inputs.json').write_text(json.dumps({k: fixtures[k] for k in ['sizes', 'scope', 'generator']}, indent=2))
(out / 'global.json').write_text('{"sdk":{"version":"8.0.427","rollForward":"disable"}}')
core = r / 'tests/Apex.Tests/bin/Release/net8.0/Apex.Core.dll'
options = r / 'src/Apex.Desktop/Okx/OkxNavalOptions.cs'
(out / 'EnvelopeProbe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup>
<ItemGroup><Compile Include="{options}" Link="OkxNavalOptions.cs" /><Reference Include="Apex.Core"><HintPath>{core}</HintPath></Reference></ItemGroup>
</Project>''')
paths = [source, options, core, product, Path(__file__).resolve(), Path(__file__).with_name('Probe.cs'), *sorted(x for x in out.iterdir() if x.is_file()),
         *[review / name for name in ['Probe.cs', 'ObservedCredentials.cs', 'probe-inputs.json', 'probe-result.json', 'oracle-check.json', 'prepare-probe.py']]]
record = {'source': subprocess.check_output(['git', '-C', str(r), 'rev-parse', 'HEAD'], text=True).strip(),
          'reason': 'Before build/behavior: all temporary stream buffers, original fixture success and every write/output failure; byte parity and both decoders',
          'instrumentation': 'Exactly one MemoryStream constructor type substituted with TrackingMemoryStream; actual writes/cleanup unchanged. Reference observation only on success; deliberate IOException after each outer write or before ToArray in failure cases. No production seam.',
          'inputs': {str(f): {'bytes': f.stat().st_size, 'sha256': hashlib.sha256(f.read_bytes()).hexdigest()} for f in paths}}
(out / 'input-map.json').write_text(json.dumps(record, indent=2))
(out / 'empty-feed').mkdir()
commands = [[a.dotnet, 'restore', 'EnvelopeProbe.csproj', '--source', str(out / 'empty-feed')],
            [a.dotnet, 'build', 'EnvelopeProbe.csproj', '-c', 'Release', '--no-restore'],
            [a.dotnet, str(out / 'bin/Release/net8.0/EnvelopeProbe.dll'), str(product), str(out / 'fixture-inputs.json'), str(out / 'oracle-result.json')]]
exits = []
for i, command in enumerate(commands):
    result = subprocess.run(command, cwd=out, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    (out / f'{i}-command.log').write_text(result.stdout)
    exits.append({'command': command, 'exit': result.returncode})
    (out / 'commands.json').write_text(json.dumps(exits, indent=2))
    print(result.stdout)
    if result.returncode:
        print('BEHAVIORAL ORACLE FAILED' if i == 2 and (out / 'oracle-result.json').exists() else 'SETUP/BUILD BLOCKER, NOT BEHAVIORAL RED')
        raise SystemExit(result.returncode)
