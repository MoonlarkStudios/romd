#!/usr/bin/env python3
"""Check ROMD's agent entry points, decision records and executable backend test routing.

Uses Git's tracked/non-ignored inventory, including new files in a dirty checkout.
Uses the hash-locked workflow YAML parser prepared by mise run agents:check. This is a structural check,
not the complete Codex config schema or a YAML parser. Local Markdown links, anchors and HTML assets are checked.
"""

from pathlib import Path
import re
import shlex
import subprocess
import sys
import tomllib
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'validation'))
from routes import check_registry
from doc_links import check_document

ROOT = Path(__file__).resolve().parents[2]
SPECIAL_TEST_TASKS = {
    "Romd.Hosting.IntegrationTests": "test:integration",
    "Romd.Mount.Tests": "test:mount-dev",
}
# Backend test routing lives in scripts/validation/checks.json; each task runs one registry group.
REGISTRY_GROUPS = {"test": "backend", "test:integration": "integration", "test:mount-dev": "mount-dev"}
LINK = re.compile(r"\[[^\]\n]*\]\(([^\s)]+)\)")
# Decision-record rules are defined in docs/decisions/README.md.
DECISION_DIRS = ("docs/decisions/", "clients/romd_console/docs/decisions/")
DECISION_INDEX = "docs/decisions/README.md"
DECISION_SECTIONS = ("Context", "Terms", "Decisions", "Trade-offs", "Rejected Alternatives", "Guardrails")
OPTIONAL_DECISION_SECTIONS = {"Terms"}
PLAN_HEADING = re.compile(r"\b(?:follow-up|implementation|deferred|acceptance|roadmap)\b", re.IGNORECASE)
LINE_CITATION = re.compile(r"\.(?:cs|csproj|dart|go|js|json|py|sh|sql|toml|ts|tsx|ya?ml)`?:\d+|`:\d+")
DECISION_CITATION = re.compile(r"(?:clients/romd_console/)?docs/decisions/[\w-]+\.md")
CITING_SUFFIXES = {".cs", ".dart", ".go", ".js", ".py", ".sh", ".toml", ".ts", ".tsx", ".yaml", ".yml"}
# Bounded custom-agent checks, not the complete Codex configuration schema.
# https://learn.chatgpt.com/docs/agent-configuration/subagents#custom-agent-file-schema
ROLE_REQUIRED_FIELDS = {"name", "description", "developer_instructions"}
ROLE_MODEL_FIELDS = {"model", "model_reasoning_effort", "model_reasoning_summary", "model_verbosity"}
ROLE_PARENT_SETTINGS = {"sandbox_mode", "approval_policy", "model_provider", "mcp_servers"}
ROLE_FIELDS = ROLE_REQUIRED_FIELDS | ROLE_MODEL_FIELDS | {"features", "skills"}
ROLE_FEATURES = {"shell_tool", "apps", "plugins", "memories", "request_permissions_tool"}


def read_toml(path, root, errors):
    try:
        return tomllib.loads(path.read_text(encoding="utf-8"))
    except (OSError, tomllib.TOMLDecodeError) as error:
        errors.append(f"{path.relative_to(root)}: {error}")
        return {}


def prose_lines(text):
    """Ignore fenced examples so sample links do not become real dependencies."""
    fence = None
    for line in text.splitlines():
        marker = re.match(r"^\s*(`{3,}|~{3,})(.*)$", line)
        if marker:
            token, suffix = marker.groups()
            if fence is None:
                fence = token
            elif token[0] == fence[0] and len(token) >= len(fence) and not suffix.strip():
                fence = None
            continue
        if fence is None:
            yield line


def check_links(path, root, errors):
    errors.extend(check_document(path, root))
    for line in prose_lines(path.read_text(encoding="utf-8")):
        # Inline paths may use the repo root or scoped document directory;
        # explicit ./ and ../ paths resolve only from the document directory.
        for target in re.findall(r"`([^`\n]+)`", line):
            if not (target.endswith("AGENTS.md") or target.endswith("/SKILL.md")):
                continue
            if any(character in target for character in "*<> "):
                continue
            bases = (path.parent,) if target.startswith(("./", "../")) else (root, path.parent)
            if not any((base / target).is_file() for base in bases):
                errors.append(f"{path.relative_to(root)}: missing instruction target {target}")


def markdown_headings(text):
    return [(len(match[1]), match[2]) for line in prose_lines(text)
            if (match := re.match(r"^(#{1,6})\s+(.+?)\s*$", line))]


def decision_record_errors(relative, text):
    errors = []
    headings = markdown_headings(text)
    sections = [title for level, title in headings if level == 2]
    expected = [section for section in DECISION_SECTIONS
                if section in sections or section not in OPTIONAL_DECISION_SECTIONS]
    if sections != expected:
        errors.append(f"{relative}: sections must be {', '.join(expected)}; found {', '.join(sections) or 'none'}")
    if [level for level, _ in headings].count(1) != 1:
        errors.append(f"{relative}: needs exactly one # title")
    errors.extend(f"{relative}: heading {title!r} names plan work; plans belong in an issue or the pull request"
                  for _, title in headings if PLAN_HEADING.search(title))
    for line in prose_lines(text):
        if re.match(r"(?:Status|Date):", line):
            errors.append(f"{relative}: status and dates belong in the pull request: {line.strip()}")
        if LINE_CITATION.search(line):
            errors.append(f"{relative}: cites a source line; name the contract instead: {line.strip()}")
    return errors


def check_decision_records(root, files, errors):
    records = [relative for relative in files if relative.startswith(DECISION_DIRS)
               and relative.endswith(".md") and Path(relative).name != "README.md"]
    if not records:
        return records
    for relative in records:
        errors.extend(decision_record_errors(relative, (root / relative).read_text(encoding="utf-8")))
    index = root / DECISION_INDEX
    if not index.is_file():
        errors.append(f"{DECISION_INDEX}: missing decision-record index")
        return records
    linked = {(index.parent / unquote(urlsplit(target).path)).resolve()
              for line in prose_lines(index.read_text(encoding="utf-8")) for target in LINK.findall(line)}
    errors.extend(f"{DECISION_INDEX}: index does not link {relative}"
                  for relative in records if (root / relative).resolve() not in linked)
    return records


def cited_headings(tail):
    """Quoted headings after a cited path, rejoined across wrapped comments; None when a heading is unquoted."""
    tail = re.sub(r"\n\s*(?:///?|#|\*|--)?", " ", tail.replace('\\"', '"'))
    quoted = re.match(r'\s*,\s*("[^"]+"(?:\s*(?:,|and)\s*"[^"]+")*)', tail)
    if quoted:
        return re.findall(r'"([^"]+)"', quoted[1])
    return None if re.match(r"\s*,\s*[A-Z]", tail) else []


def check_decision_citations(root, files, errors):
    headings = {}
    count = 0
    for relative in files:
        # The checker's allowlist and test fixtures name records without citing them.
        if Path(relative).suffix not in CITING_SUFFIXES or relative.startswith("scripts/agents/"):
            continue
        try:
            text = (root / relative).read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        for match in DECISION_CITATION.finditer(text):
            count += 1
            record = match[0]
            where = f"{relative}:{text.count(chr(10), 0, match.start()) + 1}"
            if not (root / record).is_file():
                errors.append(f"{where}: cites missing decision record {record}")
                continue
            if record not in headings:
                titles = [title for level, title in markdown_headings((root / record).read_text(encoding="utf-8"))
                          if level in (2, 3)]
                headings[record] = set(titles) | {title.split(":", 1)[0].strip() for title in titles}
            cited = cited_headings(text[match.end():match.end() + 400])
            if cited is None:
                errors.append(f"{where}: quote the heading cited from {record}")
            errors.extend(f"{where}: {record} has no heading {heading!r}"
                          for heading in cited or [] if heading not in headings[record])
    return count


def role_table(value, fields, location, errors):
    if not isinstance(value, dict):
        errors.append(f"{location} must be a table")
        return {}
    errors.extend(f"{location}: unsupported field {field!r}" for field in sorted(value.keys() - fields))
    return value


def role_boolean(value, location, errors):
    if not isinstance(value, bool):
        errors.append(f"{location} must be a boolean")


def check_role_options(role, location, errors):
    for field in sorted(role):
        if field in ROLE_PARENT_SETTINGS:
            errors.append(f"{location}: {field} does not override the parent session in a custom agent; "
                          "configure it in the parent session instead")
        elif field not in ROLE_FIELDS:
            errors.append(f"{location}: unsupported custom-agent field {field!r}; "
                          "check the documented custom-agent schema")
    # Model availability and accepted option values belong to the installed Codex runtime.
    for field in sorted(ROLE_MODEL_FIELDS & role.keys()):
        if not isinstance(role[field], str) or not role[field].strip():
            errors.append(f"{location}: {field} must be a nonempty string")
    if "features" in role:
        features = role_table(role["features"], ROLE_FEATURES, f"{location}: features", errors)
        for field in sorted(ROLE_FEATURES & features.keys()):
            role_boolean(features[field], f"{location}: features.{field}", errors)
    if "skills" not in role:
        return
    skills = role_table(role["skills"], {"config", "bundled", "include_instructions"},
                        f"{location}: skills", errors)
    if "include_instructions" in skills:
        role_boolean(skills["include_instructions"], f"{location}: skills.include_instructions", errors)
    if "bundled" in skills:
        bundled = role_table(skills["bundled"], {"enabled"}, f"{location}: skills.bundled", errors)
        if "enabled" in bundled:
            role_boolean(bundled["enabled"], f"{location}: skills.bundled.enabled", errors)
    if "config" not in skills:
        return
    configs = skills["config"]
    if not isinstance(configs, list):
        errors.append(f"{location}: skills.config must be an array of tables")
        return
    for index, entry in enumerate(configs):
        where = f"{location}: skills.config[{index}]"
        config = role_table(entry, {"path", "name", "enabled"}, where, errors)
        if not isinstance(entry, dict):
            continue
        selectors = {"path", "name"} & config.keys()
        if len(selectors) != 1:
            errors.append(f"{where} requires exactly one path or name selector")
        for field in sorted(selectors):
            if not isinstance(config[field], str) or not config[field].strip():
                errors.append(f"{where}.{field} must be a nonempty string")
        if "enabled" in config:
            role_boolean(config["enabled"], f"{where}.enabled", errors)


def check_roles(root, errors):
    names = set()
    paths = sorted((root / ".codex/agents").glob("*.toml"))
    if not paths:
        errors.append(".codex/agents: no custom agent TOMLs found")
    for path in paths:
        role = read_toml(path, root, errors)
        for field in sorted(ROLE_REQUIRED_FIELDS):
            value = role.get(field)
            if not isinstance(value, str) or not value.strip():
                errors.append(f"{path.relative_to(root)}: {field} must be a nonempty string")
        name = role.get("name")
        if isinstance(name, str):
            if name in names:
                errors.append(f"{path.relative_to(root)}: duplicate agent name {name}")
            names.add(name)
        check_role_options(role, path.relative_to(root), errors)
    return len(paths)


def task_test_targets(task):
    run = task.get("run", [])
    commands = [run] if isinstance(run, str) else run
    targets = set()
    for command in commands:
        tokens = shlex.split(command)
        if tokens[:3] == ["nice", "-n", "10"]:
            tokens = tokens[3:]
        # A niceness prefix only lowers the suite's priority; the command is still `dotnet test`.
        if tokens[:2] == ["nice", "-n"] and len(tokens) > 2 and tokens[2].lstrip("-").isdigit():
            tokens = tokens[3:]
        if tokens[:2] == ["dotnet", "test"]:
            targets.update(token.replace("\\", "/").removeprefix("./") for token in tokens[2:])
    return targets


def check_test_routes(root, files, mise, errors, registry=False):
    tasks = mise.get("tasks", {})
    count = 0
    for relative in files:
        if not (relative.startswith("tests/") and relative.endswith(".csproj")):
            continue
        try:
            project = ET.parse(root / relative)
        except (OSError, ET.ParseError) as error:
            errors.append(f"{relative}: {error}")
            continue
        is_test = any(node.text and node.text.strip().lower() == "true"
                      for node in project.iter("IsTestProject"))
        has_test_sdk = any(node.get("Include") == "Microsoft.NET.Test.Sdk"
                           for node in project.iter("PackageReference"))
        if not (is_test or has_test_sdk):
            continue
        count += 1
        task_name = SPECIAL_TEST_TASKS.get(Path(relative).stem, "test")
        try:
            targets = task_test_targets(tasks.get(task_name, {}))
        except (TypeError, ValueError) as error:
            errors.append(f".mise.toml tasks.{task_name}: invalid run commands: {error}")
            continue
        if relative not in targets and str(Path(relative).parent) not in targets:
            if registry:
                errors.append(f"{relative}: not routed by scripts/validation/checks.json; add a check in group "
                              f"{REGISTRY_GROUPS[task_name]} (run by mise run {task_name})")
            else:
                errors.append(f"{relative}: not routed by .mise.toml tasks.{task_name}")
    return count


def check_repository(root):
    inventory = subprocess.check_output(
        ["git", "-C", str(root), "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
    )
    files = sorted(relative for relative in set(inventory.decode().split("\0"))
                   if relative and (root / relative).is_file())
    errors = []
    docs = [relative for relative in files if relative.endswith('.md')]
    for relative in docs:
        try:
            check_links(root / relative, root, errors)
        except OSError as error:
            errors.append(f"{relative}: {error}")
    decision_records = check_decision_records(root, files, errors)
    citation_count = check_decision_citations(root, files, errors)
    read_toml(root / ".codex/config.toml", root, errors)
    role_count = check_roles(root, errors)
    mise = read_toml(root / ".mise.toml", root, errors)
    registry_path = root / 'scripts/validation/checks.json'
    if registry_path.is_file():
        import json
        try:
            registry = json.loads(registry_path.read_text())
            check_registry(root, files, mise, errors, registry)
            # Retain the old direct-command inventory check against executable registry commands.
            tasks = {task: {'run': [shlex.join(c['local']) for c in registry['checks']
                                   if group in c['groups']]}
                     for task, group in REGISTRY_GROUPS.items()}
            test_count = check_test_routes(root, files, {'tasks': tasks}, errors, registry=True)
        except (KeyError, TypeError, ValueError, OSError) as error:
            errors.append(f'validation registry: {error}')
            test_count = 0
    else:
        test_count = check_test_routes(root, files, mise, errors)
    return errors, {"Markdown files": len(docs), "agent roles": role_count, "test projects": test_count,
                    "decision records": len(decision_records), "decision citations": citation_count}


def main():
    errors, counts = check_repository(ROOT)
    for error in errors:
        print(f"ERROR: {error}", file=sys.stderr)
    if errors:
        return 1
    print("Agent infrastructure passed: " + ", ".join(f"{count} {name}" for name, count in counts.items()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
