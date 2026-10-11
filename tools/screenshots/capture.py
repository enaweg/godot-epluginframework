#!/usr/bin/env python3
"""Captures an X11 window with its window manager frame and saves it as a documentation screenshot.

The frame is the window manager's parent of the client window. On GNOME it is an ARGB window, so the rounded corners
stay transparent. The image is trimmed, gets a soft drop shadow on a transparent background, and is scaled down for
the documentation. Needs python-xlib and Pillow, and works with X11 and Xwayland windows.
"""
import argparse
import os
import sys
import time

from PIL import Image, ImageChops, ImageDraw, ImageFilter
from Xlib import X, display


def frame_of(dpy, window):
    """The window manager frame around a client window, or the window itself when it has none."""
    root = dpy.screen().root
    while True:
        parent = window.query_tree().parent
        if parent.id == root.id:
            return window
        window = parent


def find_by_title(dpy, title, timeout):
    """The client window with this exact title, waiting for it to appear."""
    root = dpy.screen().root
    end = time.time() + timeout
    while time.time() < end:
        stack = [root]
        while stack:
            window = stack.pop()
            try:
                if window.get_wm_name() == title and window.get_attributes().map_state == X.IsViewable:
                    return window
                stack.extend(window.query_tree().children)
            except Exception:
                pass
        time.sleep(0.25)
    sys.exit(f"No window titled {title!r}.")


def grab(window):
    geometry = window.get_geometry()
    raw = window.get_image(0, 0, geometry.width, geometry.height, X.ZPixmap, 0xFFFFFFFF)
    return Image.frombytes("RGBA", (geometry.width, geometry.height), raw.data, "raw",
                           "BGRA" if geometry.depth == 32 else "BGRX")


def client_offset(dpy, client, frame):
    """Where the client window sits inside its frame."""
    if client.id == frame.id:
        return 0, 0
    position = client.translate_coords(frame, 0, 0)
    return -position.x, -position.y


def rounded(image, radius):
    mask = Image.new("L", image.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, image.width - 1, image.height - 1), radius, fill=255)
    image.putalpha(ImageChops.multiply(image.getchannel("A"), mask))
    return image


def with_shadow(image, blur, offset, opacity):
    margin = blur * 2
    canvas = Image.new("RGBA", (image.width + 2 * margin, image.height + 2 * margin + offset), (0, 0, 0, 0))
    alpha = Image.new("L", canvas.size, 0)
    alpha.paste(image.getchannel("A").point(lambda a: int(a * opacity)), (margin, margin + offset))
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    shadow.putalpha(alpha.filter(ImageFilter.GaussianBlur(blur)))
    canvas.alpha_composite(shadow)
    canvas.alpha_composite(image, (margin, margin))
    return canvas


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True)
    parser.add_argument("--window", help="X11 id of the client window")
    parser.add_argument("--title", help="find the client window by its title instead")
    parser.add_argument("--crop", help="x,y,width,height inside the client window; captures without the frame")
    parser.add_argument("--highlight", help="x,y,width,height inside the client window to outline, with --crop")
    parser.add_argument("--scale", type=float, default=float(os.environ.get("EPLUGIN_SCREENSHOTS_SCALE", "0.5")),
                        help="factor for the saved image, e.g. 0.5 for a 200%% display")
    args = parser.parse_args()

    dpy = display.Display()
    client = find_by_title(dpy, args.title, 10) if args.title else dpy.create_resource_object("window", int(args.window, 0))
    frame = frame_of(dpy, client)
    image = grab(frame)
    os.makedirs(os.path.join(os.path.dirname(args.out), "raw"), exist_ok=True)
    image.save(os.path.join(os.path.dirname(args.out), "raw", os.path.basename(args.out)))

    unit = 1 / args.scale
    if args.crop:
        x, y, width, height = (int(v) for v in args.crop.split(","))
        # Stay inside the client window, so none of the frame shows up at the edges.
        size = client.get_geometry()
        x, y = max(x, 0), max(y, 0)
        width, height = min(width, size.width - x), min(height, size.height - y)
        dx, dy = client_offset(dpy, client, frame)
        image = image.crop((x + dx, y + dy, x + dx + width, y + dy + height))
        if args.highlight:
            hx, hy, hw, hh = (int(v) for v in args.highlight.split(","))
            box = (hx - x, hy - y, hx - x + hw - 1, hy - y + hh - 1)
            # Godot's default editor accent color
            ImageDraw.Draw(image).rounded_rectangle(box, int(4 * unit), outline=(86, 158, 255, 255), width=max(1, int(1.5 * unit)))
        image = rounded(image, int(6 * unit))
    else:
        # Drop the transparent border some frames keep around the window.
        box = image.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox()
        image = image.crop(box)
    image = with_shadow(image, int(12 * unit), int(4 * unit), 0.55)
    if args.scale != 1:
        image = image.resize((round(image.width * args.scale), round(image.height * args.scale)), Image.LANCZOS)
    image.save(args.out, optimize=True)
    print(f"{args.out} {image.width}x{image.height}")


if __name__ == "__main__":
    main()
