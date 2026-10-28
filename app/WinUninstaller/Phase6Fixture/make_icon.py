from pathlib import Path
from PIL import Image, ImageDraw

here = Path(__file__).parent
base = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(base)
draw.rounded_rectangle((10, 10, 246, 246), radius=52, fill="#1A2C28")
draw.rounded_rectangle((18, 18, 238, 238), radius=47, outline="#54D49D", width=5)
draw.line([(65, 77), (94, 181), (128, 108), (162, 181), (191, 77)],
          fill="#E8F8EF", width=21, joint="curve")
draw.ellipse((185, 178, 209, 202), fill="#54D49D")
base.save(here / "fixture.ico", format="ICO", sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
