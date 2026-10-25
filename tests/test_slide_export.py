import io
import re
import sys
import time

import httpx
import pytest
from fastapi.testclient import TestClient
from PIL import Image, ImageDraw, PngImagePlugin

from buaa_agent.app import create_app
from buaa_agent.portal import PortalError
from buaa_agent.slide_export import export_slides, fingerprint, same_slide, validate_url

HOST = "https://resource.msa.buaa.edu.cn"
LESSON = {"id": "1001", "course_id": "demo", "course_name": "合成课程", "title": "第一课"}


def png(colour, label="", metadata=False):
    image = Image.new("RGB", (640, 360), colour)
    if label:
        ImageDraw.Draw(image).text((30, 30), label, fill="black")
    out = io.BytesIO()
    info = PngImagePlugin.PngInfo()
    if metadata:
        info.add_text("Description", "different encoding metadata")
    image.save(out, "PNG", pnginfo=info)
    return out.getvalue()


def test_same_pixels_with_different_metadata_and_small_changes():
    left = Image.open(io.BytesIO(png("white", "slide")))
    right = Image.open(io.BytesIO(png("white", "slide", metadata=True)))
    assert same_slide(fingerprint(left), fingerprint(right))
    changed = left.copy()
    ImageDraw.Draw(changed).text((200, 180), "NEW", fill="black")
    assert not same_slide(fingerprint(left), fingerprint(changed))
    assert not same_slide(fingerprint(Image.new("RGB", (640, 360), "red")),
                          fingerprint(Image.new("RGB", (640, 360), "blue")))


def test_recompression_noise_can_merge_without_losing_text_changes():
    original = Image.open(io.BytesIO(png("white", "Same slide content")))
    encoded = io.BytesIO()
    original.save(encoded, "JPEG", quality=85)
    recompressed = Image.open(io.BytesIO(encoded.getvalue()))
    assert same_slide(fingerprint(original), fingerprint(recompressed))


def test_global_deduplication_preserves_chronological_first_occurrence(tmp_path):
    bodies = {"/a": png("red"), "/b": png("blue"), "/a-again": png("red", metadata=True)}
    client = httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(
        200, content=bodies[request.url.path], headers={"Content-Type": "image/png"})))
    material = {"slides": [{"url": HOST + "/a-again", "start": 30},
                            {"url": HOST + "/b", "start": 20}, {"url": HOST + "/a", "start": 10}]}
    output = tmp_path / "slides.pdf"
    updates = []
    result = export_slides(material, LESSON, output, updates.append, client)
    assert result["slide_count"] == 2 and result["duplicate_count"] == 1
    assert updates[-1]["progress"] == {"completed": 3, "total": 3, "unit": "张"}
    pdf = output.read_bytes()
    assert pdf.startswith(b"%PDF")
    assert len(re.findall(rb"/Type /Page\b", pdf)) == 2
    if sys.platform != "win32":
        # POSIX mode bits are not implemented by Windows; it uses per-user ACLs.
        assert output.stat().st_mode & 0o777 == 0o600
    client.close()


@pytest.mark.parametrize("url", ["http://127.0.0.1/private", "https://evil.example/a.png",
                                "https://resource.msa.buaa.edu.cn@evil.example/a",
                                HOST + ":8443/a", HOST + "/a?token=private"])
def test_arbitrary_urls_are_rejected(url):
    with pytest.raises(PortalError):
        validate_url(url)


def test_corrupt_image_or_http_failure_keeps_previous_export(tmp_path):
    output = tmp_path / "slides.pdf"
    output.write_bytes(b"previous export")
    material = {"slides": [{"url": HOST + "/a", "start": 0}]}
    for response in [httpx.Response(200, content=b"invalid"), httpx.Response(403)]:
        with httpx.Client(transport=httpx.MockTransport(lambda request: response)) as client:
            with pytest.raises(PortalError):
                export_slides(material, LESSON, output, client=client)
        assert output.read_bytes() == b"previous export"


def test_export_job_download_and_account_isolation(tmp_path, monkeypatch):
    app = create_app(tmp_path)
    store, auth = app.state.store, app.state.auth
    auth.profile = {"owner": "alice"}
    store.put("alice", "course", "demo", {"id": "demo"})
    store.put("alice", "lesson", "1001", LESSON)
    store.put("alice", "materials", "1001", {"slides": [{"url": HOST + "/a", "start": 10}]})
    monkeypatch.setattr("buaa_agent.slide_export.download_image", lambda *_: png("white", "Slide"))
    with TestClient(app, base_url="http://127.0.0.1:8765") as client:
        assert client.get("/api/lessons/1001/slides/pdf").status_code == 404
        job = client.post("/api/lessons/1001/slides/export", headers={"X-BUAA-Local": "1"}).json()
        until = time.monotonic() + 4
        while time.monotonic() < until:
            current = store.get("alice", "job", job["id"])
            if current["status"] not in ("queued", "running"):
                break
            time.sleep(.02)
        assert current["status"] == "completed", current
        record = client.get("/api/lessons/1001").json()["slides_pdf"]
        assert record["current"] and record["slide_count"] == 1
        downloaded = client.get("/api/lessons/1001/slides/pdf")
        assert downloaded.headers["content-type"] == "application/pdf"
        assert "attachment" in downloaded.headers["content-disposition"]
        assert downloaded.content.startswith(b"%PDF")
        store.put("alice", "materials", "1001", {"slides": []})
        assert not client.get("/api/lessons/1001").json()["slides_pdf"]["current"]
        auth.profile = {"owner": "bob"}
        assert client.get("/api/lessons/1001/slides/pdf").status_code == 502
