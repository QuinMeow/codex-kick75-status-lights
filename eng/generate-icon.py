from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "src" / "windows" / "AgentKick75.App" / "Assets"
SCALE = 4


def scaled(value: int) -> int:
    return value * SCALE


def rounded_rectangle(draw: ImageDraw.ImageDraw, box, radius, fill, outline, width):
    draw.rounded_rectangle(
        tuple(scaled(value) for value in box),
        radius=scaled(radius),
        fill=fill,
        outline=outline,
        width=scaled(width),
    )


def rounded_line(draw: ImageDraw.ImageDraw, points, fill, width):
    scaled_points = [(scaled(x), scaled(y)) for x, y in points]
    scaled_width = scaled(width)
    draw.line(scaled_points, fill=fill, width=scaled_width, joint="curve")
    radius = scaled_width // 2
    for x, y in (scaled_points[0], scaled_points[-1]):
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=fill)


canvas = Image.new("RGBA", (scaled(256), scaled(256)), (0, 0, 0, 0))
draw = ImageDraw.Draw(canvas)


def draw_bracket(color, width):
    rounded_line(draw, [(64, 29), (186, 29)], color, width)
    draw.arc(
        tuple(scaled(value) for value in (150, 29, 222, 101)),
        start=270,
        end=360,
        fill=color,
        width=scaled(width),
    )
    rounded_line(draw, [(222, 65), (222, 191)], color, width)
    draw.arc(
        tuple(scaled(value) for value in (150, 155, 222, 227)),
        start=0,
        end=90,
        fill=color,
        width=scaled(width),
    )
    rounded_line(draw, [(186, 227), (64, 227)], color, width)


draw_bracket("black", 14)
draw_bracket("white", 5)

x_segments = [((86, 49), (134, 97)), ((134, 49), (86, 97))]
for start, end in x_segments:
    rounded_line(draw, [start, end], "black", 22)
for start, end in x_segments:
    rounded_line(draw, [start, end], "white", 9)

for top in (111, 151, 191):
    rounded_rectangle(draw, (87, top, 136, top + 27), 7, None, "black", 8)
    rounded_rectangle(draw, (87, top, 136, top + 27), 7, None, "white", 3)

rounded_rectangle(draw, (161, 108, 183, 221), 11, "white", "black", 8)

resampling = Image.Resampling.LANCZOS
master = canvas.resize((512, 512), resampling)
master.save(ASSET_DIR / "AgentKick75.png")
master.save(
    ASSET_DIR / "AgentKick75.ico",
    format="ICO",
    sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
)
