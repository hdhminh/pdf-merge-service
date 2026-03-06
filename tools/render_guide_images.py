from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(r"f:/AI_Model/pdf-merge-service")
ASSETS = ROOT / "desktop-app-wpf" / "Assets"
SRC = ASSETS / "guide-src"

CANVAS_W = 1600
CANVAS_H = 900
ANNOTATE_COLOR = (44, 114, 229)
SHADOW_COLOR = (255, 255, 255)


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    candidates = []
    if bold:
        candidates.extend(
            [
                r"C:/Windows/Fonts/segoeuib.ttf",
                r"C:/Windows/Fonts/arialbd.ttf",
            ]
        )
    candidates.extend([r"C:/Windows/Fonts/segoeui.ttf", r"C:/Windows/Fonts/arial.ttf"])
    for item in candidates:
        try:
            return ImageFont.truetype(item, size)
        except OSError:
            continue
    return ImageFont.load_default()


def draw_arrow(draw: ImageDraw.ImageDraw, x1: int, y1: int, x2: int, y2: int) -> None:
    import math

    draw.line((x1, y1, x2, y2), fill=ANNOTATE_COLOR, width=6)
    ang = math.atan2(y2 - y1, x2 - x1)
    arm = 20
    a1 = ang + 2.6
    a2 = ang - 2.6
    p1 = (x2 + arm * math.cos(a1), y2 + arm * math.sin(a1))
    p2 = (x2 + arm * math.cos(a2), y2 + arm * math.sin(a2))
    draw.polygon([p1, p2, (x2, y2)], fill=ANNOTATE_COLOR)


def draw_note(
    draw: ImageDraw.ImageDraw,
    x: int,
    y: int,
    text: str,
    size: int = 42,
    use_stroke: bool = True,
) -> None:
    f = font(size, bold=True)
    stroke_width = 2 if use_stroke else 0
    stroke_fill = SHADOW_COLOR if use_stroke else None
    draw.multiline_text(
        (x, y),
        text,
        font=f,
        fill=ANNOTATE_COLOR,
        spacing=8,
        stroke_width=stroke_width,
        stroke_fill=stroke_fill,
    )


def compose(base: Image.Image) -> tuple[Image.Image, tuple[int, int, int, int]]:
    canvas = Image.new("RGB", (CANVAS_W, CANVAS_H), (244, 247, 252))
    draw = ImageDraw.Draw(canvas)

    panel = (20, 20, CANVAS_W - 20, CANVAS_H - 20)
    draw.rounded_rectangle(panel, radius=18, fill=(252, 253, 255), outline=(197, 209, 226), width=2)

    max_w = panel[2] - panel[0] - 16
    max_h = panel[3] - panel[1] - 16
    ratio = min(max_w / base.width, max_h / base.height)
    nw = int(base.width * ratio)
    nh = int(base.height * ratio)
    resized = base.resize((nw, nh), Image.Resampling.LANCZOS)

    x = panel[0] + (max_w - nw) // 2 + 8
    y = panel[1] + (max_h - nh) // 2 + 8
    canvas.paste(resized, (x, y))
    return canvas, (x, y, x + nw, y + nh)


def p(rect: tuple[int, int, int, int], rx: float, ry: float) -> tuple[int, int]:
    x1, y1, x2, y2 = rect
    return int(x1 + (x2 - x1) * rx), int(y1 + (y2 - y1) * ry)


def load(name: str) -> Image.Image:
    return Image.open(SRC / name).convert("RGB")


def render_step_1() -> None:
    src = load("Screenshot 2026-03-06 005235.png")
    # Crop tight to login area to avoid large empty black margins.
    base = src.crop((150, 180, 720, 620))
    canvas, rect = compose(base)
    draw = ImageDraw.Draw(canvas)

    note = p(rect, 0.03, 0.06)
    draw_note(
        draw,
        note[0],
        note[1],
        "Bước 1:\nĐăng ký hoặc\nđăng nhập",
        size=52,
        use_stroke=False,
    )
    a1 = p(rect, 0.31, 0.24)
    a2 = p(rect, 0.50, 0.51)
    draw_arrow(draw, a1[0], a1[1], a2[0], a2[1])

    canvas.save(ASSETS / "guide-step-1.png", optimize=True)


def render_step_2() -> None:
    src = load("Screenshot 2026-03-06 005322.png")
    # Crop wider to keep installer button + add-authtoken command area.
    base = src.crop((0, 220, 975, 520))
    canvas, rect = compose(base)
    draw = ImageDraw.Draw(canvas)

    # Redact only the middle token segment (no label text).
    # Use solid rectangle to avoid white-edge artifacts.
    rx1, ry1 = p(rect, 0.44, 0.752)
    rx2, ry2 = p(rect, 0.84, 0.838)
    draw.rectangle((rx1, ry1, rx2, ry2), fill=(0, 0, 0))

    note = p(rect, 0.62, 0.10)
    draw_note(
        draw,
        note[0],
        note[1],
        "Bước 2:\nCopy AuthToken\nsau lệnh add-authtoken",
        size=48,
        use_stroke=False,
    )
    # Keep arrow short to avoid crossing annotation text.
    a1 = p(rect, 0.77, 0.53)
    a2 = p(rect, 0.63, 0.64)
    draw_arrow(draw, a1[0], a1[1], a2[0], a2[1])

    canvas.save(ASSETS / "guide-step-2.png", optimize=True)


def render_step_3() -> None:
    base = load("Screenshot 2026-03-06 005400.png")
    canvas, rect = compose(base)
    draw = ImageDraw.Draw(canvas)

    draw_note(draw, 140, CANVAS_H - 200, "1) Nhập tên profile")
    draw_note(draw, 620, CANVAS_H - 200, "2) Dán AuthToken")
    draw_note(draw, 1080, CANVAS_H - 200, "3) Bấm Thêm token")

    a1 = (220, CANVAS_H - 220)
    b1 = p(rect, 0.27, 0.54)
    draw_arrow(draw, a1[0], a1[1], b1[0], b1[1])

    a2 = (760, CANVAS_H - 220)
    b2 = p(rect, 0.63, 0.54)
    draw_arrow(draw, a2[0], a2[1], b2[0], b2[1])

    a3 = (1200, CANVAS_H - 220)
    b3 = p(rect, 0.77, 0.78)
    draw_arrow(draw, a3[0], a3[1], b3[0], b3[1])

    canvas.save(ASSETS / "guide-step-3.png", optimize=True)


def render_step_4() -> None:
    base = load("Screenshot 2026-03-06 005417.png")
    canvas, rect = compose(base)
    draw = ImageDraw.Draw(canvas)

    draw_note(draw, 220, CANVAS_H - 200, "1) Bấm Tạo link")
    draw_note(draw, 760, CANVAS_H - 200, "2) App tự cập nhật\nGoogle Sheet")

    a1 = (300, CANVAS_H - 220)
    b1 = p(rect, 0.14, 0.72)
    draw_arrow(draw, a1[0], a1[1], b1[0], b1[1])

    a2 = (900, CANVAS_H - 220)
    b2 = p(rect, 0.50, 0.70)
    draw_arrow(draw, a2[0], a2[1], b2[0], b2[1])

    canvas.save(ASSETS / "guide-step-4.png", optimize=True)


def main() -> None:
    render_step_1()
    render_step_2()
    render_step_3()
    render_step_4()
    print("Generated guide-step-1..4 from guide-src with unified blue style.")


if __name__ == "__main__":
    main()
