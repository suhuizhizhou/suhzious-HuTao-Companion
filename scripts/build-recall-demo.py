"""Build a standalone HTML visualization of the Agent+RAG recall chain.

WHY A GENERATOR AND NOT A HAND-WRITTEN PAGE
    The demo must show REAL trajectories, not a mock-up. The eval already records
    every step of the chain (query / chosen arm / who chose it / status / the
    evidence actually retrieved / gate path / draft vs answer / judge verdict),
    so the page is generated from a report.json. Regenerating after a new run is
    one command, and nothing in the page can drift from the artifact.

WHY THE OUTPUT IS SELF-CONTAINED
    Opening the page must not require a server, a build step, a CDN or network
    access: `file://` cannot fetch sibling JSON, and this project has a
    zero-dependency rule. So the data is embedded into the HTML.

USAGE
    python scripts/build-recall-demo.py [report.json] [out.html]
    defaults: .cache/demo-run/report.json -> docs/recall-chain-demo.html
"""

import json
import os
import sys

DEFAULT_REPORT = os.path.join(".cache", "demo-run", "report.json")
DEFAULT_OUT = os.path.join("docs", "recall-chain-demo.html")

# Caps keep the page small. They are display limits only, and the page says so
# for the fields where text was shortened.
EVIDENCE_TEXT = 140
ANSWER_TEXT = 520
REASON_TEXT = 240


def cut(value, limit):
    text = value or ""
    return text if len(text) <= limit else text[:limit] + "…"


def trim_step(step):
    return {
        "round": step.get("round", 0),
        "task_id": step.get("task_id", ""),
        "depends_on": step.get("depends_on") or [],
        "required_facts": step.get("required_facts") or [],
        "query": step.get("query", ""),
        "strategy": step.get("strategy") or "default",
        "strategy_source": step.get("strategy_source", "default"),
        "chapter_scope_size": step.get("chapter_scope_size", 0),
        "status": step.get("status", ""),
        "evidence_count": step.get("evidence_count", 0),
        "coverage": step.get("coverage", 0.0),
        "sufficient": bool(step.get("sufficient")),
        "reason": cut(step.get("reason", ""), REASON_TEXT),
        "evidence": [
            {
                "id": item.get("id", ""),
                "speaker": item.get("speaker", ""),
                "text": cut(item.get("text", ""), EVIDENCE_TEXT),
                "score": round(float(item.get("score", 0.0)), 4),
                "coverage": round(float(item.get("coverage", 0.0)), 4),
                "chapter": item.get("chapter", ""),
                "exact": bool(item.get("exact")),
                "perspective": item.get("perspective", ""),
                "match_kind": item.get("match_kind", ""),
            }
            for item in (step.get("evidence") or [])
        ],
    }


def trim_case(row):
    return {
        "id": row.get("id", ""),
        "query": row.get("query", ""),
        "reasoning_types": row.get("reasoning_types") or [],
        "expected_route": row.get("expected_route", ""),
        "actual_route": row.get("actual_route", ""),
        "status": row.get("status", ""),
        "loop_rounds": row.get("loop_rounds", 0),
        "query_count": row.get("query_count", 0),
        "evidence": row.get("evidence", 0),
        "covered_groups": row.get("covered_groups", 0),
        "gold_groups": row.get("gold_groups", 0),
        "gate_passed": row.get("gate_passed"),
        "gate_path": row.get("gate_path", ""),
        "gate_kinds": row.get("gate_kinds", ""),
        "answer_path": row.get("answer_path", ""),
        "answer_issues": row.get("answer_issues") or [],
        "draft": cut(row.get("draft", ""), ANSWER_TEXT),
        "reply": cut(row.get("reply", ""), ANSWER_TEXT),
        "judge_verdict": row.get("judge_verdict", ""),
        "judge_score": round(float(row.get("judge_score", 0.0)), 4),
        "judge_reason": cut(row.get("judge_reason", ""), REASON_TEXT),
        "elapsed_ms": round(float(row.get("elapsed_ms", 0.0)), 1),
        "steps": [trim_step(s) for s in (row.get("steps") or [])],
    }


def build(report_path):
    with open(report_path, encoding="utf-8") as handle:
        report = json.load(handle)
    agent = report.get("agent")
    if not agent:
        raise SystemExit(f"{report_path} has no agent section; run --agent-live first.")

    rows = [trim_case(r) for r in agent.get("rows", [])]
    payload = {
        "meta": {
            "created_utc": str(report.get("created_utc", "")),
            "dataset": ", ".join(report.get("dataset_files") or []),
            "dataset_sha256": report.get("dataset_sha256", ""),
            "mode": report.get("mode", ""),
            "strategy": report.get("strategy", ""),
            "strategy_probe": report.get("strategy_probe", ""),
            "corpus": report.get("corpus") or {},
            "gates": report.get("gates") or {},
        },
        "arm_catalog": report.get("arm_catalog") or [],
        "two_level": report.get("two_level") or {},
        "strata": agent.get("strata") or {},
        "totals": {
            "cases": agent.get("cases", 0),
            "steps_total": agent.get("steps_total", 0),
            "agent_chosen_steps": agent.get("agent_chosen_steps", 0),
            "cases_with_agent_choice": agent.get("cases_with_agent_choice", 0),
            "cases_with_multiple_arms": agent.get("cases_with_multiple_arms", 0),
            "arm_usage": agent.get("arm_usage") or {},
            "arm_sources": agent.get("arm_sources") or {},
            "gate_paths": agent.get("gate_paths") or {},
            "answer_paths": agent.get("answer_paths") or {},
            "judge_immersive": agent.get("judge_immersive", 0),
            "judge_minor_break": agent.get("judge_minor_break", 0),
            "judge_broken": agent.get("judge_broken", 0),
            "judge_failed": agent.get("judge_failed", 0),
            "judge_mean_score": agent.get("judge_mean_score", 0.0),
            "judge_scope": "immersive / context coherence / logic coherence only (no factual verdict)",
        },
        "cases": rows,
    }
    return payload


def main():
    report_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_REPORT
    out_path = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_OUT
    payload = build(report_path)

    here = os.path.dirname(os.path.abspath(__file__))
    template_path = os.path.join(here, "recall-chain-demo.template.html")
    with open(template_path, encoding="utf-8") as handle:
        template = handle.read()

    # ensure_ascii=False keeps Chinese readable in the artifact; "</" is escaped so the
    # JSON can never terminate the surrounding <script> element early.
    data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).replace("</", "<\\/")
    html = template.replace("/*__DATA__*/null", data)
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(html)

    size = os.path.getsize(out_path)
    print(f"wrote {out_path} ({size / 1024:.0f} KB) from {report_path}")
    print(f"cases={len(payload['cases'])} steps={payload['totals']['steps_total']} "
          f"agentChosen={payload['totals']['agent_chosen_steps']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
