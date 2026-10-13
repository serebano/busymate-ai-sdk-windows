from pathlib import Path
import hashlib, json, re
root = Path(__file__).resolve().parents[1]
m = json.loads((root / 'release-manifest.json').read_text(encoding='utf-8'))
for name, expected in m['files'].items():
    actual = hashlib.sha256((root / name).read_bytes()).hexdigest()
    assert actual == expected, f"Frozen source changed: {name}"
    print(f"verified {name} {actual}")
assert m['version'] == '1.0.0'
csproj = (root / 'src/Busymate.AI/Busymate.AI.csproj').read_text(encoding='utf-8')
assert re.search(r'<Version>' + re.escape(m['version']) + r'</Version>', csproj), "Package version differs from manifest"
bridge = (root / 'src/Busymate.AI/BusymateBridge.cs').read_text(encoding='utf-8')
assert f'Build = "{m["identityWire"]["v2Build"]}"' in bridge, "Bridge build differs from manifest"
microphone = (root / 'src/Busymate.AI/BusymateMicrophone.cs').read_text(encoding='utf-8')
assert f'AdapterVersion = "{m["microphoneAdapter"]}"' in microphone, "Microphone adapter differs from manifest"
assert (root / 'CONTRACT-v2.md').read_bytes() and hashlib.sha256((root / 'CONTRACT-v2.md').read_bytes()).hexdigest() == m['contract'], "CONTRACT-v2.md changed"
print(f"verified package {m['version']}, identity build {m['identityWire']['v2Build']}, microphone adapter {m['microphoneAdapter']}")
