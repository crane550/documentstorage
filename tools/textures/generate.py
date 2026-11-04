"""Generate the seamless kraft and paper textures in src/DocumentStorage.Web/wwwroot/images.

Run from the repository root:  python3 tools/textures/generate.py
Needs numpy and Pillow (with WebP). The output is deterministic for a given seed, so it only changes when this
file does. Periodic (FFT) noise and wrapped strokes make every texture tile without a visible seam.
"""
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw

SIZE = 512
OUT = Path('src/DocumentStorage.Web/wwwroot/images')
rng = np.random.default_rng(7)


def periodic_noise(power, ax=1.0, ay=1.0):
    fx = np.fft.fftfreq(SIZE)[None, :] * ax
    fy = np.fft.fftfreq(SIZE)[:, None] * ay
    f = np.sqrt(fx ** 2 + fy ** 2)
    f[0, 0] = 1
    spectrum = (rng.normal(size=(SIZE, SIZE)) + 1j * rng.normal(size=(SIZE, SIZE))) / f ** power
    spectrum[0, 0] = 0
    value = np.real(np.fft.ifft2(spectrum))
    return (value - value.mean()) / value.std()


def wrapped(draw, shape, box, **style):
    """Draw a shape nine times, offset by the tile size, so it wraps across every edge."""
    for ox in (-SIZE, 0, SIZE):
        for oy in (-SIZE, 0, SIZE):
            moved = [coordinate + (ox if i % 2 == 0 else oy) for i, coordinate in enumerate(box)]
            getattr(draw, shape)(moved, **style)


def make(name, base, fibers=1.0, specks=1.0, contrast=1.0):
    mottle = periodic_noise(1.6)                   # broad pulp clouds
    grain = periodic_noise(0.9, ax=1.8, ay=0.3)    # streaky grain along the sheet
    fine = periodic_noise(0.2)                     # tooth
    light = 1 + contrast * (0.028 * mottle + 0.026 * grain + 0.02 * fine)
    pixels = np.clip(np.array(base)[None, None, :] * light[..., None], 0, 255).astype(np.uint8)
    image = Image.fromarray(pixels, 'RGB').convert('RGBA')
    layer = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    for _ in range(int(2600 * fibers)):
        x, y = rng.uniform(0, SIZE, 2)
        angle, length = rng.normal(0, 0.35), rng.uniform(4, 22)
        colour = (70, 45, 20, int(rng.uniform(14, 38))) if rng.random() < 0.6 else (255, 240, 210, int(rng.uniform(16, 40)))
        wrapped(draw, 'line', [x, y, x + length * np.cos(angle), y + length * np.sin(angle)], fill=colour, width=1)
    for _ in range(int(260 * specks)):
        x, y = rng.uniform(0, SIZE, 2)
        r = rng.uniform(0.4, 1.3)
        wrapped(draw, 'ellipse', [x - r, y - r, x + r, y + r], fill=(60, 38, 18, int(rng.uniform(40, 110))))
    Image.alpha_composite(image, layer).convert('RGB').save(OUT / f'{name}.webp', quality=80, method=6)


OUT.mkdir(parents=True, exist_ok=True)
make('kraft', (197, 160, 112))                                           # cardboard for the frame
make('paper', (246, 241, 231), fibers=0.35, specks=0.15, contrast=0.35)  # the desk under the sheets
print('Textures written to', OUT)
