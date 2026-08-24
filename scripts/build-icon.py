"""Build the NetScope Windows icon assets from the checked-in RGBA source."""

from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "design" / "assets" / "netscope-icon-source-v2.png"
OUTPUT_DIR = ROOT / "src" / "NetScope.App" / "Assets"
PNG_OUTPUT = OUTPUT_DIR / "NetScope.png"
ICO_OUTPUT = OUTPUT_DIR / "NetScope.ico"
ICO_SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]


def main() -> None:
    source = Image.open(SOURCE).convert("RGBA")
    alpha_box = source.getchannel("A").getbbox()
    if alpha_box is None:
        raise RuntimeError("Icon source has no visible pixels")

    mark = source.crop(alpha_box)
    side = max(mark.size)
    padding = round(side * 0.08)
    canvas_side = side + padding * 2
    canvas = Image.new("RGBA", (canvas_side, canvas_side), (0, 0, 0, 0))
    canvas.alpha_composite(mark, ((canvas_side - mark.width) // 2, (canvas_side - mark.height) // 2))

    # 512px keeps the embedded WPF resource compact while retaining ample detail
    # for the 256px maximum Windows ICO frame and documentation previews.
    master = canvas.resize((512, 512), Image.Resampling.LANCZOS)
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    master.save(PNG_OUTPUT, "PNG", optimize=True)
    master.save(ICO_OUTPUT, "ICO", sizes=ICO_SIZES, bitmap_format="png")

    print(f"PNG: {PNG_OUTPUT}")
    print(f"ICO: {ICO_OUTPUT} ({', '.join(f'{w}px' for w, _ in ICO_SIZES)})")


if __name__ == "__main__":
    main()
