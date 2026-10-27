#!/usr/bin/env python3
"""Converts the macOS app's Dialogue+<Language>.swift tables into Assets/dialogue.json.

Usage: python3 scripts/convert_dialogue.py <path to OllieBoard/Sources/OllieBoard>

Output shape: {"<lang>": {"lines": {"<characterId>": {"shortName": str, "lines": {"<line>": [str]},
"pairGreet": {"<friendId>": [str]}}}, "skills": {"<skill>": str}}}
"""
import json
import re
import sys
from pathlib import Path

FILES = {"vi": "Dialogue+Vietnamese.swift", "en": "Dialogue+English.swift", "ja": "Dialogue+Japanese.swift"}
STRING = r'"((?:[^"\\]|\\.)*)"'


def unescape(s: str) -> str:
    return json.loads('"' + s + '"')


def strings(text: str) -> list:
    return [unescape(m) for m in re.findall(STRING, text)]


def parse(source: str) -> dict:
    lines_part, _, skills_part = source.partition("SkillNames: [Skill: String] = [")
    characters = {}
    # Each character starts with `"<id>": Lines(` at four spaces of indentation.
    blocks = re.split(r'\n    "([a-z0-9]+)": Lines\(\n', lines_part)
    for cid, body in zip(blocks[1::2], blocks[2::2]):
        short = re.search(r'shortName: ' + STRING, body)
        entry = {"shortName": unescape(short.group(1)), "lines": {}, "pairGreet": {}}
        lines_body, _, pair_body = body.partition("pairGreet: [")
        for key, values in re.findall(r'\n\s+\.(\w+): \[(.*?)\],?(?=\n)', lines_body):
            entry["lines"][key] = strings(values)
        for key, values in re.findall(r'\n\s+"(\w+)": \[(.*?)\],?(?=\n)', pair_body):
            entry["pairGreet"][key] = strings(values)
        characters[cid] = entry
    skills = {key: unescape(value) for key, value in re.findall(r'\.(\w+): ' + STRING, skills_part)}
    return {"lines": characters, "skills": skills}


def main() -> None:
    src = Path(sys.argv[1])
    out = {lang: parse((src / name).read_text(encoding="utf-8")) for lang, name in FILES.items()}
    target = Path(__file__).resolve().parent.parent / "Assets" / "dialogue.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    for lang, data in out.items():
        count = sum(len(v) for c in data["lines"].values() for v in c["lines"].values())
        print(f"{lang}: {len(data['lines'])} characters, {count} lines, {len(data['skills'])} skill names")


if __name__ == "__main__":
    main()
