from pathlib import Path
import hashlib,json,subprocess,os,sys,xml.etree.ElementTree as ET
root=Path('/home/ubuntu/projects/apex-trading-ai')
plan=root/'.superpowers/sdd/2026-10-31-okx-integration'
# Reuse the retained, reviewed local gate mechanics; isolate outputs and recovery report.
original=(plan/'operational-run.py').read_text()
script=original.replace("operational-evidence'","operational-fix1-evidence'").replace('operational-account-report.md','operational-account-fix1-report.md')
exec(compile(script,str(plan/'operational-fix1-run.py'),'exec'))
# The historical helper records failures but exits zero; propagate the actual child result.
sys.exit(json.loads((plan/'operational-fix1-evidence'/sys.argv[1]/'summary.json').read_text())['exit'])
