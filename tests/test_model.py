"""Provider transport contracts; no paid API calls or real course materials."""
import json
from types import SimpleNamespace

import httpx
import pytest
from pydantic import ValidationError

from buaa_agent.learning import (
    Answer,
    Extraction,
    Keywords,
    LearningError,
    Model,
    ask,
    extract_chunk,
)


@pytest.fixture
def model(monkeypatch):
    for name in ("BUAA_LLM_BASE_URL", "BUAA_LLM_MODEL", "BUAA_LLM_API_KEY"):
        monkeypatch.delenv(name, raising=False)
    instance = Model(SimpleNamespace(setting=lambda *_: {}))
    instance.configure("https://api.deepseek.com", "deepseek-flash", "synthetic-key")
    return instance


def response_output(payload, **extra):
    return {"status": "completed", "error": None, "output": [
        {"type": "reasoning", "content": [{"type": "reasoning_text", "text": "not JSON"}]},
        {"type": "message", "role": "assistant", "content": [
            {"type": "output_text", "text": json.dumps(payload)}]},
    ], **extra}


@pytest.mark.parametrize("base", ["https://api.deepseek.com", "https://api.deepseek.com/v1/"])
def test_deepseek_sends_schema_and_reads_only_final_output(model, monkeypatch, base):
    model.configure(base, "deepseek-flash")
    captured = {}
    def post(url, **kwargs):
        captured.update(url=url, **kwargs)
        return httpx.Response(200, json=response_output({"summary": "synthetic"}))
    monkeypatch.setattr("buaa_agent.learning.httpx.post", post)
    assert model.complete("system", "synthetic source", output_model=Extraction) == {
        "summary": "synthetic"}
    assert captured["url"] == base.rstrip("/") + "/responses"
    assert captured["headers"] == {"Authorization": "Bearer synthetic-key"}
    body = captured["json"]
    assert body["model"] == "deepseek-flash"
    assert body["instructions"] == "system"
    assert body["input"] == "synthetic source"
    assert "messages" not in body and "response_format" not in body
    output_format = body["text"]["format"]
    assert output_format["type"] == "json_schema"
    assert output_format["name"] == "Extraction"
    schema = output_format["schema"]
    assert set(schema["required"]) == set(Extraction.model_fields)
    assignment = schema["properties"]["assignments"]["items"]
    assert assignment["additionalProperties"] is False
    assert set(assignment["required"]) == {
        "text", "evidence_ids", "deadline", "deadline_quote", "audience", "submission"}
    assert assignment["properties"]["evidence_ids"]["items"]["type"] == "string"
    chapter = schema["properties"]["topics"]["items"]
    assert chapter["properties"]["title"]["type"] == "string"
    assert set(chapter["required"]) == {"title", "summary", "start_id"}
    assert "$ref" not in json.dumps(schema)
    # Length limits stay enforced locally even when absent from the wire schema.
    with pytest.raises(ValidationError):
        Extraction.model_validate({"summary": "x" * 4001})


@pytest.mark.parametrize("base", ["http://localhost:12345/v1", "https://example.invalid/v1"])
def test_other_providers_keep_chat_completions(model, monkeypatch, base):
    model.configure(base, "compatible-model")
    def post(url, **kwargs):
        assert url == base + "/chat/completions"
        assert kwargs["json"]["response_format"] == {"type": "json_object"}
        assert kwargs["json"]["messages"][0]["role"] == "system"
        return httpx.Response(200, json={"choices": [{
            "finish_reason": "stop", "message": {"content": '{"summary":"synthetic"}'}}]})
    monkeypatch.setattr("buaa_agent.learning.httpx.post", post)
    assert model.complete("system", "source", output_model=Extraction)["summary"] == "synthetic"


@pytest.mark.parametrize(("result", "message"), [
    (response_output({"summary": "partial"}, status="incomplete"), "未完成"),
    (response_output({}, status="failed", error={"message": "private provider details"}), "未成功"),
    (response_output({}, output=[]), "空内容"),
    (response_output({}, output=[{"type": "message", "content": [{"type": "refusal"}]}]), "拒绝"),
    ({"status": "completed", "output": None}, "响应无效"),
])
def test_incomplete_failed_empty_and_invalid_outputs_are_rejected(model, monkeypatch, result, message):
    monkeypatch.setattr("buaa_agent.learning.httpx.post", lambda *_, **kw: httpx.Response(200, json=result))
    with pytest.raises(LearningError, match=message):
        model.complete("system", "source", output_model=Extraction)


def test_schema_request_error_does_not_silently_fall_back(model, monkeypatch):
    calls = []
    def post(url, **kwargs):
        calls.append(url)
        return httpx.Response(400, json={"error": {"message": "synthetic schema rejection"}})
    monkeypatch.setattr("buaa_agent.learning.httpx.post", post)
    with pytest.raises(LearningError, match="HTTP 400"):
        model.complete("system", "source", output_model=Extraction)
    assert calls == ["https://api.deepseek.com/responses"]


def test_all_learning_requests_supply_their_output_contract():
    seen = []
    def complete(system, prompt, *, output_model):
        seen.append(output_model)
        return {
            Extraction: {"summary": "synthetic", "topics": [{
                "title": "synthetic", "summary": "synthetic", "start_id": "s0"}], "covered_until_id": "s0"},
            Keywords: {"keywords": ["article"]},
            Answer: {"answer": "冠词 [c0]", "evidence_ids": ["c0"]},
        }[output_model]
    model = SimpleNamespace(complete=complete)
    extract_chunk(model, "source", {"s0": {"text": "article", "start": 1, "end": 2}}, lambda _: None)
    materials = [{"lesson_id": "1001", "segments": [
        {"id": "s0", "start": 1, "end": 2, "text": "article"}]}]
    lessons = [{"id": "1001", "title": "synthetic", "date": "2026-10-22", "source_url": "https://example.invalid"}]
    assert ask(model, "article", materials, lessons)["answer"] == "冠词 [c0]"
    assert seen == [Extraction, Keywords, Answer]
