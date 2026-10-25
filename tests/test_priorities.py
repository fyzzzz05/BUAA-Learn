import pytest

from buaa_agent.learning import LearningError, curate_priorities


class Model:
    def __init__(self, output):
        self.output = output
    def complete(self, *args, **kwargs):
        return self.output

def test_selection_preserves_evidence():
    notices = [{"text": t, "evidence_ids": [str(i)]} for i, t in enumerate(["休息", "期末考", "签到", "小测"])]
    result = {"notices": notices, "review_suggestions": [str(i) for i in range(72)]}
    curate_priorities(Model({"notice_indices": [1, 2, 3, 1], "review_suggestions": ["建模", "推导"]}), result,
                      {str(i): {"text": n["text"]} for i, n in enumerate(notices)})
    assert result["notices"] == notices[1:]
    assert len(result["review_suggestions"]) == 2

@pytest.mark.parametrize('output', [
    {"notice_indices": [8], "review_suggestions": []},
    {"notice_indices": [], "review_suggestions": ["x"] * 4},
])
def test_invalid_output_preserves_result(output):
    result = {"notices": [], "review_suggestions": ["old"]}
    with pytest.raises(LearningError):
        curate_priorities(Model(output), result, {})
    assert result["review_suggestions"] == ["old"]
