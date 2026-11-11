"""Generates banner and launcher icons from the art sources. Run by hand: python art/make_icons.py"""
import os
from PIL import Image

ART = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(os.path.dirname(ART), "res")


def cover(img, w, h):
    """Resize to cover w x h, centre-cropping the overflow."""
    s = max(w / img.width, h / img.height)
    r = img.resize((round(img.width * s), round(img.height * s)), Image.LANCZOS)
    x, y = (r.width - w) // 2, (r.height - h) // 2
    return r.crop((x, y, x + w, y + h))


banner = Image.open(os.path.join(ART, "banner-source.jpg")).convert("RGB")
os.makedirs(os.path.join(RES, "drawable-xhdpi"), exist_ok=True)
cover(banner, 320, 180).save(os.path.join(RES, "drawable-xhdpi", "banner.png"), optimize=True)

icon = Image.open(os.path.join(ART, "icon-source.jpg")).convert("RGB")
side = min(icon.size)
left, top = (icon.width - side) // 2, (icon.height - side) // 2
square = icon.crop((left, top, left + side, top + side))
for dens, px in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144)):
    d = os.path.join(RES, "mipmap-" + dens)
    os.makedirs(d, exist_ok=True)
    square.resize((px, px), Image.LANCZOS).save(os.path.join(d, "ic_launcher.png"), optimize=True)
print("icons written")
