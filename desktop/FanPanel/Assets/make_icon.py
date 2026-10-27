"""Render the original fan mark at Windows icon sizes. Requires Pillow only.
SPDX-License-Identifier: GPL-3.0-only
"""
from pathlib import Path
import math
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
SCALE = 4
SIZE = 256
BLADE = [
    (125, 112),
    ((108, 94), (108, 66), (126, 51)),
    ((140, 40), (160, 44), (168, 58)),
    ((177, 76), (164, 88), (147, 98)),
    ((142, 103), (140, 111), (139, 117)),
]

def blade_points():
    points = [BLADE[0]]
    start = BLADE[0]
    for a, b, end in BLADE[1:]:
        for i in range(1, 25):
            t = i / 24
            points.append(tuple((1-t)**3*start[j] + 3*(1-t)**2*t*a[j] + 3*(1-t)*t*t*b[j] + t**3*end[j] for j in (0, 1)))
        start = end
    return points

canvas = Image.new('RGBA', (SIZE*SCALE, SIZE*SCALE))
mask = Image.new('L', canvas.size)
ImageDraw.Draw(mask).rounded_rectangle((8*SCALE, 8*SCALE, 248*SCALE, 248*SCALE), radius=55*SCALE, fill=255)
pixels = canvas.load()
for y in range(canvas.height):
    for x in range(canvas.width):
        t = min(1, max(0, (x+y)/(2*canvas.width)))
        a, b = (67, 190, 247), (31, 63, 188)
        pixels[x, y] = tuple(round(a[i]*(1-t)+b[i]*t) for i in range(3))+(255,)
canvas.putalpha(mask)
d = ImageDraw.Draw(canvas)
d.rounded_rectangle((9*SCALE, 9*SCALE, 247*SCALE, 247*SCALE), radius=54*SCALE, outline=(168, 225, 255, 130), width=2*SCALE)
for n in range(5):
    angle = n*math.tau/5
    points = [((128+(x-128)*math.cos(angle)-(y-128)*math.sin(angle))*SCALE,
               (128+(x-128)*math.sin(angle)+(y-128)*math.cos(angle))*SCALE) for x,y in blade_points()]
    d.polygon(points, fill=(248, 253, 255, 255))
d.ellipse((108*SCALE,108*SCALE,148*SCALE,148*SCALE), fill=(52,125,220,255))
d.ellipse((115*SCALE,115*SCALE,141*SCALE,141*SCALE), fill=(250,254,255,255))
image = canvas.resize((256,256),Image.Resampling.LANCZOS)
image.save(ROOT/'app.png')
image.resize((128,128),Image.Resampling.LANCZOS).save(ROOT/'preview.png')
image.save(ROOT/'app.ico',sizes=[(n,n) for n in (16,20,24,32,40,48,64,128,256)])
path = 'M125 112 C108 94 108 66 126 51 C140 40 160 44 168 58 C177 76 164 88 147 98 C142 103 140 111 139 117 Z'
rotors = '\n'.join(f'<path d="{path}" transform="rotate({n*72} 128 128)"/>' for n in range(5))
(ROOT/'app.svg').write_text(f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
<!-- Original artwork, GPL-3.0-only. -->
<defs><linearGradient id="blue" x2="1" y2="1"><stop stop-color="#43BEF7"/><stop offset="1" stop-color="#1F3FBC"/></linearGradient></defs>
<rect x="8" y="8" width="240" height="240" rx="55" fill="url(#blue)" stroke="#A8E1FF" stroke-opacity=".5" stroke-width="2"/>
<g fill="#F8FDFF">{rotors}</g>
<circle cx="128" cy="128" r="20" fill="#347DDC"/><circle cx="128" cy="128" r="13" fill="#FAFEFF"/>
</svg>\n''',encoding='utf-8')
print('Created PNG, SVG and 9-size ICO.')
