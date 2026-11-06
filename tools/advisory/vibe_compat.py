"""Isolated import adapter; installed upstream source is never modified."""
import importlib.abc,importlib.util,json,sys
from pathlib import Path
from runtime_contract import sha256

def enable_exact_eur_identity():
    root=Path(__file__).resolve().parent
    fork=root/'vendor/vibe_identity.py'
    expected=json.loads((root/'vendor/provenance.json').read_text())['vibe_identity.py']['sha256']
    if sha256(fork)!=expected:raise ValueError('compatibility hash mismatch')
    class Finder(importlib.abc.MetaPathFinder):
        def find_spec(self,fullname,path=None,target=None):
            if fullname=='src.agent.grounding.identity':return importlib.util.spec_from_file_location(fullname,fork)
    sys.meta_path.insert(0,Finder())
