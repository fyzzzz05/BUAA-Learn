"""Provider transport contracts; no paid API calls or real course materials."""
import json
from types import SimpleNamespace

import httpx
import pytest
from pydantic import ValidationError

from buaa_agent.learning import (
    Extraction,
    LearningError,
    Model,
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
    model.configure(base, "deepseek-flash", "synthetic-key")
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


def test_classroom_analysis_supplies_its_output_contract():
    seen = []
    def complete(system, prompt, *, output_model):
        seen.append(output_model)
        return {"summary": "synthetic", "topics": [{
            "title": "synthetic", "summary": "synthetic", "start_id": "s0"}], "covered_until_id": "s0"}

    model = SimpleNamespace(complete=complete)
    extract_chunk(model, "source", {"s0": {"text": "article", "start": 1, "end": 2}}, lambda _: None)
    assert seen == [Extraction]


def test_key_is_kept_only_for_the_same_endpoint(model, monkeypatch):
    model.configure("https://api.deepseek.com/", "updated-model")
    assert model.public()["has_key"]
    model.configure("https://other.example.invalid/v1", "other-model")

    def post(url, **kwargs):
        assert url == "https://other.example.invalid/v1/chat/completions"
        assert "Authorization" not in kwargs["headers"]
        return httpx.Response(200, json={"choices": [{
            "finish_reason": "stop", "message": {"content": '{"summary":"synthetic"}'}}]})

    monkeypatch.setattr("buaa_agent.learning.httpx.post", post)
    assert model.complete("system", "synthetic") == {"summary": "synthetic"}


def test_blank_environment_preserves_settings_and_unscoped_key_is_ignored(monkeypatch):
    monkeypatch.setenv("BUAA_LLM_BASE_URL", "")
    monkeypatch.setenv("BUAA_LLM_MODEL", "")
    monkeypatch.setenv("BUAA_LLM_API_KEY", "synthetic-unscoped-key")
    store = SimpleNamespace(setting=lambda *_: {"base_url": "https://saved.example.invalid/v1", "model": "saved"})
    model = Model(store)
    assert model.ready and model.model == "saved" and not model.public()["has_key"]
    monkeypatch.setenv("BUAA_LLM_BASE_URL", "https://env.example.invalid/v1/")
    model = Model(store)
    assert model.base_url == "https://env.example.invalid/v1" and model.public()["has_key"]


def test_protected_key_persistence_and_removal(tmp_path, monkeypatch):
    from buaa_agent.store import Store

    monkeypatch.setenv("BUAA_LLM_BASE_URL", "https://example.invalid/v1")
    monkeypatch.setenv("BUAA_LLM_API_KEY", "environment-fallback")
    endpoint = "https://example.invalid/v1"

    def protect(value, address):
        assert value == "synthetic-key" and address == endpoint
        return "protected-test-token"

    def unprotect(value, address):
        assert value == "protected-test-token" and address == endpoint
        return "synthetic-key"

    protector = SimpleNamespace(protect=protect, unprotect=unprotect)
    store = Store(tmp_path)
    model = Model(store, protector)
    model.configure(endpoint + "/", "example", "synthetic-key", save=True)
    assert model.public()["key_state"] == "saved"
    assert store.setting("model")["protected_keys"] == {endpoint: "protected-test-token"}
    assert "synthetic-key" not in json.dumps(model.public())
    store.close()

    store = Store(tmp_path)
    model = Model(store, protector)
    assert model.key == "synthetic-key"
    model.configure("https://other.invalid/v1", "other", save=True)
    assert not model.public()["has_key"]
    model.configure(endpoint, "example", save=True)
    assert model.key == "synthetic-key"

    def fail(*args):
        raise OSError("synthetic encryption failure")

    protector.protect = fail
    with pytest.raises(LearningError, match="原有配置已保留"):
        model.configure(endpoint, "changed", "replacement", save=True)
    assert model.model == "example" and model.key == "synthetic-key"
    model.configure(endpoint, "example", "", save=True)
    assert not Model(store, protector).public()["has_key"]
    store.close()


def test_chunk_retries_timeout_with_the_same_prompt(model, monkeypatch):
    prompts = []

    def post(_url, **kwargs):
        prompts.append(kwargs["json"]["input"])
        if len(prompts) == 1:
            raise httpx.ReadTimeout("internal details")
        return httpx.Response(200, json=response_output({"summary": "synthetic"}))

    monkeypatch.setattr("buaa_agent.learning.httpx.post", post)
    progress = []
    notes, _ = extract_chunk(model, "source", {"s0": {"text": "source"}}, progress.append)
    assert notes.summary == "synthetic"
    assert prompts == ["source", "source"]
    assert progress == ["等待模型响应超时，正在自动重试 1/2"]


@pytest.mark.parametrize(("status", "attempts"), [(401, 1), (429, 1), (503, 3)])
def test_chunk_retries_temporary_service_errors_only(model, monkeypatch, status, attempts):
    calls = []

    def post(_url, **kwargs):
        calls.append(kwargs)
        return httpx.Response(status)

    monkeypatch.setattr("buaa_agent.learning.httpx.post", post)
    with pytest.raises(LearningError, match=f"HTTP {status}"):
        extract_chunk(model, "source", {"s0": {"text": "source"}}, lambda _: None)
    assert len(calls) == attempts
