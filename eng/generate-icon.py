from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "src" / "windows" / "AgentKick75.App" / "Assets"
CONCEPT_PATH = ROOT / "docs" / "design" / "m4-app-icon-agent-matrix-concept.png"
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
MASTER_SIZE = 512
TARGET_WIDTH = 448
SOURCE_PADDING = 24


def alpha_from_light_background(value: int) -> int:
    if value >= 245:
        return 0
    return round((245 - value) * 255 / 245)


def alpha_from_dark_background(value: int) -> int:
    if value <= 20:
        return 0
    return round((value - 20) * 255 / 235)


def extract_variant(source: Image.Image, light_background: bool) -> Image.Image:
    grayscale = source.convert("L")
    threshold = grayscale.point(
        (lambda value: 255 if value < 235 else 0)
        if light_background
        else (lambda value: 255 if value > 40 else 0)
    )
    bounds = threshold.getbbox()
    if bounds is None:
        raise RuntimeError("Agent Matrix glyph was not found in the concept image.")

    left = max(0, bounds[0] - SOURCE_PADDING)
    top = max(0, bounds[1] - SOURCE_PADDING)
    right = min(source.width, bounds[2] + SOURCE_PADDING)
    bottom = min(source.height, bounds[3] + SOURCE_PADDING)
    cropped = grayscale.crop((left, top, right, bottom))

    alpha = cropped.point(
        alpha_from_light_background if light_background else alpha_from_dark_background
    )
    color = 0 if light_background else 255
    glyph = Image.new("RGBA", cropped.size, (color, color, color, 0))
    glyph.putalpha(alpha)

    target_height = round(glyph.height * TARGET_WIDTH / glyph.width)
    glyph = glyph.resize((TARGET_WIDTH, target_height), Image.Resampling.LANCZOS)
    master = Image.new("RGBA", (MASTER_SIZE, MASTER_SIZE), (0, 0, 0, 0))
    master.alpha_composite(
        glyph,
        ((MASTER_SIZE - TARGET_WIDTH) // 2, (MASTER_SIZE - target_height) // 2),
    )
    return master


def sharpen_small_alpha(image: Image.Image, size: int) -> Image.Image:
    if size > 32:
        return image

    red, green, blue, alpha = image.split()
    maximum = alpha.getextrema()[1]
    floor = max(8, round(maximum * 0.10))
    alpha = alpha.point(
        lambda value: 0
        if value <= floor
        else min(255, round((value - floor) * 255 / (maximum - floor)))
    )
    return Image.merge("RGBA", (red, green, blue, alpha))


def make_frames(master: Image.Image) -> list[Image.Image]:
    return [
        sharpen_small_alpha(master.resize((size, size), Image.Resampling.LANCZOS), size)
        for size in ICON_SIZES
    ]


def save_ico(master: Image.Image, path: Path):
    frames = make_frames(master)
    frames[-1].save(
        path,
        format="ICO",
        sizes=[(size, size) for size in ICON_SIZES],
        append_images=frames[:-1],
    )


concept = Image.open(CONCEPT_PATH).convert("RGB")
split = concept.height // 2
app_master = extract_variant(concept.crop((0, 0, concept.width, split)), light_background=True)
tray_master = extract_variant(
    concept.crop((0, split, concept.width, concept.height)),
    light_background=False,
)

app_master.save(ASSET_DIR / "AgentKick75.png")
tray_master.save(ASSET_DIR / "AgentKick75Tray.png")
save_ico(app_master, ASSET_DIR / "AgentKick75.ico")
save_ico(tray_master, ASSET_DIR / "AgentKick75Tray.ico")
