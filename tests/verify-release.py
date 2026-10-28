"""Read-only package/source integrity verification, not a native application launch."""
from pathlib import Path
import argparse, hashlib, json, re
from PIL import Image

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--report-dir', type=Path, default=root / 'reports/PACKAGE_VERIFICATION')
parser.add_argument('--payload-dir', type=Path, default=root / 'dist/app')
parser.add_argument('--setup', type=Path, default=root / 'dist/Uninstaller77-Setup.exe')
options = parser.parse_args()
report = options.report_dir.resolve()
report.mkdir(parents=True, exist_ok=True)
payload = options.payload_dir.resolve()
manifest = json.loads((payload / 'release-manifest.json').read_text(encoding='utf-8-sig'))
checks = []
def check(condition, name):
    checks.append({'name':name,'passed':bool(condition)})
    if not condition: raise AssertionError(name)
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest().upper()
for file, key in [('Uninstaller77.exe','exeSHA256'),('Uninstaller77.dll','dllSHA256'),('wwwroot/index.html','indexSHA256')]:
    check(sha(payload / file) == manifest[key], 'Manifest hash: '+file)
ui_source = root / 'app/WinUninstaller/WebUI/dist'
source_files = {str(p.relative_to(ui_source)).replace('\\','/'):sha(p) for p in ui_source.rglob('*') if p.is_file()}
destination_files = {str(p.relative_to(payload/'wwwroot')).replace('\\','/'):sha(p) for p in (payload/'wwwroot').rglob('*') if p.is_file()}
check(source_files == destination_files, 'Packaged wwwroot exactly matches the current React build; no stale assets')
index = (payload/'wwwroot/index.html').read_text(encoding='utf-8')
for asset in re.findall(r'(?:src|href)="\./([^\"]+)"',index):
    check((payload/'wwwroot'/asset).is_file(), 'Entry-document asset exists: '+asset)
check("connect-src 'none'" in index and "script-src 'self'" in index and "object-src 'none'" in index, 'Offline CSP remains restrictive')
deps = json.loads((payload/'Uninstaller77.deps.json').read_text(encoding='utf-8'))
check('SQLitePCLRaw.bundle_e_sqlite3/3.0.5' in deps['libraries'], 'Corrected SQLite bundle is in the shipped dependency graph')
check(not any(name.startswith('SQLitePCLRaw.lib.e_sqlite3/') for name in deps['libraries']), 'Old affected native package is absent from shipped dependency graph')
tested_native = root/'tests/ProductIntegrity.Tests/bin/Release/net8.0-windows/runtimes/win-x64/native/e_sqlite3.dll'
shipped_native = payload/'e_sqlite3.dll'
check(sha(tested_native) == sha(shipped_native), 'Shipped Windows SQLite bytes match the runtime-tested 3.53.4 library')
check(all(path.name == 'win-x64' for path in (payload/'runtimes').iterdir()), 'Windows x64 publish excludes unrelated platform runtime trees')
setup = options.setup.resolve()
setup_sha = sha(setup)
check(setup_sha == setup.with_suffix('.sha256').read_text().split()[0], 'Installer SHA256 sidecar matches')
check(not any('EngineTests' in path.name or path.name in ['fixture.db','tests.db'] for path in payload.rglob('*')), 'Test executable/databases excluded from release payload')
for language in ['en','ar']:
    source = json.loads((root/f'app/WinUninstaller/Resources/Strings.{language}.json').read_text(encoding='utf-8-sig'))
    destination = json.loads((payload/f'Resources/Strings.{language}.json').read_text(encoding='utf-8-sig'))
    check(source == destination, 'Packaged translation matches source: '+language)
english=json.loads((payload/'Resources/Strings.en.json').read_text(encoding='utf-8-sig'))
arabic=json.loads((payload/'Resources/Strings.ar.json').read_text(encoding='utf-8-sig'))
check(set(english)==set(arabic), 'Resource translation key parity')
images = list((root/'app/WinUninstaller/Resources/Brand').glob('*.png')) + list((root/'app/WinUninstaller/WebUI/src/assets/browsers').glob('*.png'))
for path in images:
    check(path.read_bytes().startswith(b'\x89PNG\r\n\x1a\n'), 'PNG signature: '+path.name)
    with Image.open(path) as image:
        image.load()
        check(image.width > 0 and image.height > 0, 'PNG decodes: '+path.name)
files = [p for p in payload.rglob('*') if p.is_file()]
result = {'version':manifest['version'],'installerSHA256':setup_sha,'assemblyBytes':(payload/'Uninstaller77.dll').stat().st_size,
          'payloadBytes':sum(p.stat().st_size for p in files),'payloadFileCount':len(files), 'checks':checks,
          'passed':len(checks),'failed':0,'nativeUiVerified':False}
(report/'PACKAGE_VERIFICATION.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(f"{len(checks)} package/image checks passed; {result['payloadFileCount']} payload files, assembly {result['assemblyBytes']} bytes, payload {result['payloadBytes']} bytes")
print('Setup SHA256: '+setup_sha)
