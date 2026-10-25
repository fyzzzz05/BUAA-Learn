"""Content-based slide deduplication and image-only PDF export."""
import hashlib
import io
import json
import math
import os
import warnings
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from tempfile import TemporaryDirectory
from urllib.parse import urlparse

import httpx
from PIL import Image, ImageChops, ImageOps, ImageStat, UnidentifiedImageError
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen.canvas import Canvas

from .materials import time_label
from .portal import PortalError

MAX_IMAGE_BYTES = 12 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024
MAX_PIXELS = 20_000_000


def slides_digest(material):
    return hashlib.sha256(json.dumps(material.get("slides", []), sort_keys=True).encode()).hexdigest()


def validate_url(url):
    p = urlparse(url)
    if (p.scheme != "https" or p.hostname != "resource.msa.buaa.edu.cn" or
            p.port not in (None, 443) or p.username or p.password or p.query or p.fragment):
        raise PortalError("PPT 图片地址不属于允许的学校资源，请刷新课堂材料")
    return url


def download_image(client, row):
    url = validate_url(row["url"])
    try:
        with client.stream("GET", url, follow_redirects=False) as response:
            if response.status_code != 200:
                raise PortalError(f"PPT 图片读取失败（HTTP {response.status_code}），请刷新课堂材料后重试")
            body = bytearray()
            for chunk in response.iter_bytes():
                body.extend(chunk)
                if len(body) > MAX_IMAGE_BYTES:
                    raise PortalError("单张 PPT 图片过大，导出已停止")
            return bytes(body)
    except httpx.HTTPError:
        raise PortalError("PPT 图片下载失败或超时，请稍后重试") from None


def decode_image(body):
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(io.BytesIO(body)) as source:
                if source.width * source.height > MAX_PIXELS or source.format not in ("PNG", "JPEG", "WEBP"):
                    raise PortalError("PPT 图片格式或尺寸不支持")
                image = ImageOps.exif_transpose(source).convert("RGB")
                image.load()
                return image
    except (UnidentifiedImageError, OSError, ValueError, Image.DecompressionBombError,
            Image.DecompressionBombWarning):
        raise PortalError("PPT 图片无法解码，导出未完成，请刷新材料后重试") from None


def fingerprint(image):
    thumb = image.resize((640, 360), Image.Resampling.LANCZOS)
    small = thumb.convert("L").resize((17, 16), Image.Resampling.LANCZOS)
    pixels = small.tobytes()
    dhash = 0
    for y in range(16):
        for x in range(16):
            dhash = (dhash << 1) | (pixels[y * 17 + x] > pixels[y * 17 + x + 1])
    exact = hashlib.sha256(str(image.size).encode() + image.tobytes()).digest()
    return {"exact": exact, "hash": dhash, "thumb": thumb, "ratio": image.width / image.height}


def same_slide(left, right):
    if left["exact"] == right["exact"]:
        return True
    if abs(left["ratio"] - right["ratio"]) > .005 or (left["hash"] ^ right["hash"]).bit_count() > 12:
        return False
    diff = ImageChops.difference(left["thumb"], right["thumb"])
    if max(ImageStat.Stat(diff).mean) > 2.5:
        return False
    # Keep local text/annotation/colour changes, even when their global difference is tiny.
    red, green, blue = diff.split()
    changed = ImageChops.lighter(ImageChops.lighter(red, green), blue).point(lambda v: 255 if v > 24 else 0)
    blocks = changed.resize((40, 23), Image.Resampling.BOX)
    return blocks.getextrema()[1] <= 3


def write_pdf(path, pages, lesson):
    # Embed full-resolution lossless RGB images; the footer never covers the screenshot.
    canvas = Canvas(str(path), pageCompression=1)
    canvas.setTitle(f"{lesson['course_name']} · {lesson['title']} · 去重课件")
    canvas.setAuthor("BUAA Learn")
    canvas.setSubject("课堂 PPT 截图去重导出，图片型 PDF，非教师原始课件")
    for index, page in enumerate(pages, 1):
        width, height = page["size"]
        page_width = 960
        image_height = page_width * height / width
        canvas.setPageSize((page_width, image_height + 22))
        canvas.drawImage(ImageReader(str(page["path"])), 0, 22, width=page_width, height=image_height)
        canvas.setFillColorRGB(.45, .45, .45)
        canvas.setFont("Helvetica", 9)
        canvas.drawString(12, 7, f"{index} / {len(pages)}")
        times = ", ".join(time_label(t) for t in page["times"])
        if len(times) > 110:
            times = time_label(page["times"][0]) + f"  (+{len(page['times'])-1})"
        canvas.drawRightString(page_width - 12, 7, times)
        bookmark = f"slide-{index}"
        canvas.bookmarkPage(bookmark)
        canvas.addOutlineEntry(f"{index}. {time_label(page['times'][0])}", bookmark)
        canvas.showPage()
    canvas.save()


def export_slides(material, lesson, destination, progress=lambda _: None, client=None):
    rows = sorted(material.get("slides", []), key=lambda row: row["start"])
    if not rows:
        raise PortalError("这节课暂无 PPT 截图，请先获取或刷新课堂材料")
    if len(rows) > 1000:
        raise PortalError("截图数量超过单次导出上限（1000张）")
    for row in rows:
        validate_url(row["url"])
        if not isinstance(row["start"], (int, float)) or not math.isfinite(row["start"]) or row["start"] < 0:
            raise PortalError("PPT 截图时间异常，请刷新课堂材料")
    owns_client = client is None
    client = client or httpx.Client(timeout=30, follow_redirects=False)
    pages, fingerprints, total_bytes, decoded_bytes = [], [], 0, 0
    try:
        with TemporaryDirectory(prefix="ppt-", dir=destination.parent) as temp, ThreadPoolExecutor(max_workers=4) as pool:
            for start in range(0, len(rows), 4):
                batch = rows[start:start + 4]
                bodies = pool.map(lambda row: download_image(client, row), batch)
                for offset, (row, body) in enumerate(zip(batch, bodies), start + 1):
                    total_bytes += len(body)
                    if total_bytes > MAX_TOTAL_BYTES:
                        raise PortalError("截图总大小超过导出上限（256MB）")
                    image = decode_image(body)
                    fp = fingerprint(image)
                    duplicate = next((i for i, kept in enumerate(fingerprints) if same_slide(fp, kept)), None)
                    if duplicate is not None:
                        pages[duplicate]["times"].append(row["start"])
                    else:
                        path = Path(temp) / f"{len(pages)}.png"
                        image.save(path, "PNG")
                        decoded_bytes += path.stat().st_size
                        if decoded_bytes > MAX_TOTAL_BYTES:
                            raise PortalError("解码后的课件总大小超过导出上限（256MB）")
                        if path.stat().st_size > MAX_IMAGE_BYTES:
                            raise PortalError("PPT 图片解码后过大，导出已停止")
                        pages.append({"path": path, "size": image.size, "times": [row["start"]], "url": row["url"]})
                        fingerprints.append(fp)
                    progress({"message": f"正在去重 PPT 截图 {offset}/{len(rows)}，保留 {len(pages)} 页",
                              "progress": {"completed": offset, "total": len(rows), "unit": "张"}})
            draft = Path(temp) / "slides.pdf"
            write_pdf(draft, pages, lesson)
            draft.chmod(0o600)
            os.replace(draft, destination)
    finally:
        if owns_client:
            client.close()
    return {"slide_count": len(pages), "original_count": len(rows),
            "duplicate_count": len(rows) - len(pages), "digest": slides_digest(material)}
