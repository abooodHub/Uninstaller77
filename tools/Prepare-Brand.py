"""Convert the supplied product artwork to offline Windows/web formats; no redraw."""
from pathlib import Path
from PIL import Image
import hashlib
import json
import shutil
import sys

root = Path(__file__).resolve().parents[1]
brand = root / 'app/WinUninstaller/Resources/Brand'
master = brand / 'u77-master.png'
if len(sys.argv) > 1:
    shutil.copyfile(sys.argv[1], master)
image = Image.open(master).convert('RGBA')
if image.width != image.height:
    raise ValueError('The supplied icon must be square; do not distort or crop it.')
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512]
for size in sizes:
    image.resize((size, size), Image.Resampling.LANCZOS).save(brand / f'u77-{size}.png')
image.save(brand / 'Uninstaller77.ico', sizes=[(s, s) for s in sizes if s <= 256])
shutil.copyfile(brand / 'u77-256.png', root / 'app/WinUninstaller/WebUI/src/assets/u77.png')
# Preserve the existing wizard layout/text; replace only its original icon rectangle.
welcome = Image.open(root / 'installer/Artwork/welcome.bmp').convert('RGB')
welcome.paste(image.resize((74, 74), Image.Resampling.LANCZOS).convert('RGB'), (45, 81))
welcome.save(root / 'installer/Artwork/welcome.bmp')
image.resize((55, 55), Image.Resampling.LANCZOS).convert('RGB').save(root / 'installer/Artwork/small.bmp')
(brand / 'source.json').write_text(json.dumps({
    'source': 'User-supplied artwork, 2026-10-25',
    'sha256': hashlib.sha256(master.read_bytes()).hexdigest(),
    'originalSize': list(image.size), 'icoFrames': [s for s in sizes if s <= 256],
    'processing': 'Format conversion and proportional resizing only'
}, indent=2), encoding='utf-8')
print('Updated product ICO, web PNG, Windows icon sizes and installer artwork.')
