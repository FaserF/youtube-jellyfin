#!/usr/bin/env python3
"""
Generates a structured, deduplicated changelog from git commit history for YouTube Jellyfin Plugin.

- Reads commits between FROM_TAG and HEAD (or all commits if no tag)
- Filters noise: lazy commits, merge commits, version bumps, bot commits
- Groups by conventional commit type with friendly emoji headers
- Deduplicates: exact + near-duplicate collapsing
- Scope-aware grouping: scope overrides type bucket
- Breaking changes bubble to the top in a CAUTION callout
- Collapses large categories with expandable <details> block
- Outputs clean GitHub-flavored Markdown

Usage:
    python generate_changelog.py [--from-tag TAG] [--total-commits N] [--repo owner/repo]
"""

import argparse
import re
import subprocess

NOISE_PATTERNS = [
    r"^\s*$",
    r"^(Update|Aktualisier[et]?|Add|Adds|Adde|Delete|Deletes|Remove|Removes|Rename|Renames|Move|Moves|Fix|Edit|Change|Modify)\s+[\w\-\.\/]+\.\w{1,10}\s*$",
    r"^Merge (pull request|branch|remote-tracking branch)\b",
    r"^Merge from\b",
    r"^(chore|build)(\([^)]*\))?:\s*(bump|release|version)\b",
    r"^(bump|release)(\s+version)?\s+v?\d",
    r"^v?\d+\.\d+\.\d+\s*$",
    r"^\[skip[- ]ci\]",
    r"^chore: regenerate (manifest|connections|changelog)\b",
    r"^chore: update (project_manifest|project_connections|contributors)\b",
    r"^(auto.?generated?|automated?|bot:)\b",
    r'^Revert "Revert',
    r"^Initial commit\s*$",
    r"^WIP\b",
    r"^wip\b",
    r"^.{1,3}$",
    r"\[skip[- ]ci\]\s*$",
]
_NOISE_RE = [re.compile(p, re.IGNORECASE) for p in NOISE_PATTERNS]

CATEGORY_ORDER = [
    "breaking",
    "feat",
    "fix",
    "security",
    "perf",
    "refactor",
    "api",
    "ui",
    "docs",
    "test",
    "ci",
    "chore",
    "other",
]
CATEGORY_EMOJI = {
    "breaking": "💥 Breaking Changes",
    "feat": "✨ New Features",
    "fix": "🐛 Bug Fixes",
    "security": "🔒 Security",
    "perf": "⚡ Performance",
    "refactor": "♻️ Code Improvements",
    "api": "🔌 API & Streaming",
    "ui": "🎨 UI & Television",
    "docs": "📚 Documentation",
    "test": "🧪 Tests",
    "ci": "🔄 CI / CD",
    "chore": "🔧 Maintenance",
    "other": "📦 Other Changes",
}

TYPE_MAP = {
    "feat": "feat",
    "feature": "feat",
    "fix": "fix",
    "bugfix": "fix",
    "hotfix": "fix",
    "security": "security",
    "sec": "security",
    "perf": "perf",
    "optim": "perf",
    "refactor": "refactor",
    "refact": "refactor",
    "api": "api",
    "streaming": "api",
    "stream": "api",
    "ui": "ui",
    "style": "ui",
    "ux": "ui",
    "tv": "ui",
    "docs": "docs",
    "doc": "docs",
    "test": "test",
    "tests": "test",
    "ci": "ci",
    "cd": "ci",
    "build": "ci",
    "chore": "chore",
    "maint": "chore",
    "infra": "chore",
    "deps": "chore",
    "dep": "chore",
    "bump": "chore",
    "revert": "fix",
}

SCOPE_MAP = {
    "api": "api",
    "stream": "api",
    "streaming": "api",
    "innertube": "api",
    "hls": "api",
    "dash": "api",
    "strm": "api",
    "library": "feat",
    "channel": "feat",
    "sponsorblock": "feat",
    "ryd": "feat",
    "ui": "ui",
    "tv": "ui",
    "player": "ui",
    "web": "ui",
    "security": "security",
    "auth": "security",
    "oauth": "security",
    "profile": "security",
    "ci": "ci",
    "cd": "ci",
    "workflow": "ci",
    "release": "ci",
}

MAX_PER_SECTION = 15
NEVER_COLLAPSE = {"breaking", "security"}

_CONV_RE = re.compile(r"^([A-Za-z][A-Za-z0-9_-]*)(\([^)]*\))?(!)?\:\s*(.+)$")
_NORM_STRIP_TYPE = re.compile(
    r"^(feat|fix|docs|style|refactor|perf|test|chore|ci|security|build|api|ui|ux|revert)(\([^)]*\))?(!)?\:\s*",
    re.IGNORECASE,
)
_NORM_PUNCT = re.compile(r"[.!?,;:\"'`]")
_NORM_ARTICLES = re.compile(r"\b(the|a|an|for|of|in|to|with|from|on|at|by)\b")
_NORM_SPACES = re.compile(r"\s+")


def norm_key(msg: str) -> str:
    n = msg.lower()
    n = _NORM_STRIP_TYPE.sub("", n)
    n = _NORM_PUNCT.sub("", n)
    n = _NORM_ARTICLES.sub("", n)
    n = _NORM_SPACES.sub(" ", n)
    return n.strip()


def is_noise(msg: str) -> bool:
    return any(p.search(msg) for p in _NOISE_RE)


def keyword_bucket(msg: str) -> str:
    m = msg.lower()
    if re.search(r"\b(general[\s_]fix|small[\s_]fix|bug[\s_]fix|fix(es|ed)?\b|fix\s+\w)", m):
        return "fix"
    if re.search(r"\b(ci\b|linter?|pipeline|workflow|github[\s_]action|changelog|dockerfile?|container)\b", m):
        return "ci"
    if re.search(r"\b(update[\s_]depend|bump[\s_]depend|renovate|dependency[\s_]update|upgrade[\s_]dep)\b", m):
        return "chore"
    if re.search(r"\b(add(ed|s)?[\s_]+(missing[\s_]+)?(feature|support|ability)|new[\s_]feature|stream(ing)?|strm|channel|sponsorblock|smarttube)\b", m):
        return "feat"
    if re.search(r"\b(security|vulnerability|cve|auth(en|oriz)?|oauth|profile)\b", m):
        return "security"
    if re.search(r"\b(perf(ormance)?|speed|faster|optim|latency|throughput)\b", m):
        return "perf"
    if re.search(r"\b(refactor(ing)?|restructur|rewrite|clean.?up|improve)\b", m):
        return "refactor"
    if re.search(r"\b(doc(s|ument(ation)?)?|readme|wiki|guide)\b", m):
        return "docs"
    if re.search(r"\b(test(s|ing)?|spec|unit[\s_]test|xunit)\b", m):
        return "test"
    if re.search(r"\b(ui\b|ux\b|tv\b|android[\s_]tv|layout|theme|design|modal|component)\b", m):
        return "ui"
    if re.search(r"\b(api\b|endpoint|route|router|hls|dash|innertube|yt-dlp)\b", m):
        return "api"
    if re.search(r"\b(chore|maint(enance)?|housekeep|dependen|package|infra)\b", m):
        return "chore"
    return "other"


def format_item(display: str, hashes: list, repo: str) -> str:
    if hashes:
        if repo:
            links = [f"[{h}](https://github.com/{repo}/commit/{h})" for h in hashes]
        else:
            links = [f"`{h}`" for h in hashes]
        return f"{display} ({', '.join(links)})"
    return display


def get_commits(from_tag: str) -> list[tuple[str, str]]:
    cmd = ["git", "log", "--pretty=format:%h %s"]
    if from_tag:
        cmd.append(f"{from_tag}..HEAD")
    else:
        cmd += ["--max-count=2000"]
    try:
        out = subprocess.check_output(cmd, stderr=subprocess.DEVNULL, text=True)
    except subprocess.CalledProcessError:
        return []
    result = []
    for line in out.splitlines():
        m = re.match(r"^([0-9a-fA-F]+)\s+(.*)$", line)
        if m:
            result.append((m.group(1), m.group(2).strip()))
        elif line.strip():
            result.append(("", line.strip()))
    return result


def build_changelog(from_tag: str, total_commits: int, repo: str) -> str:
    raw_commits = get_commits(from_tag)
    buckets: dict[str, list[dict]] = {cat: [] for cat in CATEGORY_ORDER}
    seen_keys: dict[str, dict] = {}

    for commit_hash, subject in raw_commits:
        if is_noise(subject):
            continue

        category = "other"
        display = subject
        is_breaking = False

        m = _CONV_RE.match(subject)
        if m:
            type_part, scope_part, bang, rest = (
                m.group(1).lower(),
                m.group(2),
                m.group(3),
                m.group(4).strip(),
            )
            scope = scope_part[1:-1].lower() if scope_part else None

            if bang == "!":
                is_breaking = True

            if scope and scope in SCOPE_MAP:
                category = SCOPE_MAP[scope]
            elif type_part in TYPE_MAP:
                category = TYPE_MAP[type_part]
            else:
                category = keyword_bucket(rest)

            if scope:
                display = f"**{scope}:** {rest}"
            else:
                display = rest
        else:
            category = keyword_bucket(subject)
            display = subject

        if is_breaking:
            category = "breaking"

        key = norm_key(display)
        if not key:
            key = display.lower()

        if key in seen_keys:
            existing = seen_keys[key]
            if commit_hash and commit_hash not in existing["hashes"]:
                existing["hashes"].append(commit_hash)
            if len(display) > len(existing["display"]):
                existing["display"] = display
        else:
            entry = {
                "display": display,
                "hashes": [commit_hash] if commit_hash else [],
                "category": category,
            }
            seen_keys[key] = entry
            buckets[category].append(entry)

    lines: list[str] = []

    if from_tag:
        lines.append(f"Changes since `{from_tag}`:\n")
    else:
        lines.append("Full Changelog:\n")

    breaking = buckets.get("breaking", [])
    if breaking:
        lines.append("> [!CAUTION]")
        lines.append("> **Breaking Changes**")
        for item in breaking:
            lines.append(f"> - {format_item(item['display'], item['hashes'], repo)}")
        lines.append("")

    for cat in CATEGORY_ORDER:
        if cat == "breaking":
            continue
        items = buckets.get(cat, [])
        if not items:
            continue

        label = CATEGORY_EMOJI.get(cat, cat.title())
        lines.append(f"### {label}\n")

        if len(items) > MAX_PER_SECTION and cat not in NEVER_COLLAPSE:
            shown = items[:MAX_PER_SECTION]
            hidden = items[MAX_PER_SECTION:]
            for item in shown:
                lines.append(f"- {format_item(item['display'], item['hashes'], repo)}")
            lines.append("")
            lines.append(f"<details><summary>Show {len(hidden)} more changes...</summary>\n")
            for item in hidden:
                lines.append(f"- {format_item(item['display'], item['hashes'], repo)}")
            lines.append("\n</details>\n")
        else:
            for item in items:
                lines.append(f"- {format_item(item['display'], item['hashes'], repo)}")
            lines.append("")

    return "\n".join(lines).strip() + "\n"


def main():
    parser = argparse.ArgumentParser(description="Generate conventional changelog.")
    parser.add_argument("--from-tag", default="", help="Previous git tag to compare from")
    parser.add_argument("--total-commits", type=int, default=0, help="Total commit count hint")
    parser.add_argument("--repo", default="FaserF/youtube-jellyfin", help="GitHub repo (owner/repo)")
    args = parser.parse_args()

    changelog = build_changelog(args.from_tag, args.total_commits, args.repo)
    print(changelog)


if __name__ == "__main__":
    main()
