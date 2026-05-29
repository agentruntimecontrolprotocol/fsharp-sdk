#!/usr/bin/env python3
"""Generate Markdown API docs for the F# SDK by scraping source.

Walks src/<Project>/**/*.fs, extracts namespace/module declarations and
`///` doc-comment blocks attached to public `let` / `type` / `module` /
`member` / `val` / `abstract` / `new` declarations, then writes one
Markdown file per project plus a top-level index.

Output: docs/api/<Project>.md and docs/api/index.md.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
OUT = ROOT / "docs" / "api"

DECL_RE = re.compile(
    r"^(?P<indent>\s*)"
    r"(?P<kw>let|type|module|member|val|abstract|new|and|interface)\b"
    r"(?P<rest>.*)$"
)
ATTR_RE = re.compile(r"^\s*\[<[^>]+>\]\s*$")
NS_RE = re.compile(r"^namespace\s+(?P<ns>\S+)")
TOP_MODULE_RE = re.compile(r"^module\s+(?P<m>[A-Za-z0-9_.]+)\s*=?\s*$")
# `let private name` / `member private name` is private. A `type Foo
# private () =` is NOT — that only marks the primary ctor private.
LET_MEMBER_PRIV_RE = re.compile(r"^\s+(private|internal)\b")
TYPE_MODULE_PRIV_RE = re.compile(r"^\s+(private|internal)\b\s+[A-Za-z_]")


@dataclass
class Decl:
    kind: str
    signature: str
    doc: list[str] = field(default_factory=list)
    scope: str = ""


@dataclass
class FileDoc:
    path: Path
    namespace: str = ""
    top_module: str = ""
    decls: list[Decl] = field(default_factory=list)


def strip_doc(line: str) -> str:
    s = line.lstrip()
    return s[3:].lstrip() if s.startswith("///") else ""


def is_continuation(line: str) -> bool:
    t = line.rstrip()
    return bool(t) and t.endswith(("(", ",", "->", ":", "*", "<", "="))


def is_private(kw: str, rest: str) -> bool:
    if kw in ("let", "member", "val", "abstract"):
        return bool(LET_MEMBER_PRIV_RE.match(rest))
    return bool(TYPE_MODULE_PRIV_RE.match(rest))


def gather_signature(lines: list[str], i: int) -> str:
    parts = [lines[i].rstrip()]
    j = i
    while is_continuation(parts[-1]) and j + 1 < len(lines):
        j += 1
        nxt = lines[j].rstrip()
        if nxt.lstrip().startswith("///"):
            break
        parts.append(nxt)
        if len(parts) >= 6:
            break
    return "\n".join(parts).strip()


def compute_scope(fd: FileDoc, module_stack: list[tuple[int, str]], kw: str) -> str:
    parts: list[str] = []
    if fd.namespace:
        parts.append(fd.namespace)
    if fd.top_module:
        parts.append(fd.top_module)
    if kw == "module":
        parts.extend(n for _, n in module_stack[:-1])
    else:
        parts.extend(n for _, n in module_stack)
    return ".".join(parts)


def parse_file(path: Path) -> FileDoc:
    """Extract documented declarations from a single .fs file."""
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    fd = FileDoc(path=path)
    pending: list[str] = []
    module_stack: list[tuple[int, str]] = []
    i = 0
    while i < len(lines):
        raw = lines[i]
        stripped = raw.strip()

        if not stripped:
            i += 1
            continue

        m_ns = NS_RE.match(stripped)
        if m_ns:
            fd.namespace = m_ns.group("ns")
            pending = []
            i += 1
            continue

        # Capture the file-level top module only when there is no
        # namespace declaration (i.e. file starts with `module X.Y.Z`).
        # When a namespace is present, col-0 `module Foo =` lines are
        # sibling modules, not file-level wrappers.
        m_top = TOP_MODULE_RE.match(stripped)
        if (
            m_top
            and not raw.startswith(" ")
            and not fd.top_module
            and not fd.namespace
        ):
            fd.top_module = m_top.group("m")

        if stripped.startswith("///"):
            pending.append(strip_doc(raw))
            i += 1
            continue

        if ATTR_RE.match(raw):
            i += 1
            continue

        m = DECL_RE.match(raw)
        if m:
            indent = len(m.group("indent").expandtabs(4))
            kw = m.group("kw")
            rest = m.group("rest")

            while module_stack and indent <= module_stack[-1][0]:
                module_stack.pop()

            if kw == "module":
                nm = re.match(r"\s*([A-Za-z_][A-Za-z0-9_]*)", rest)
                if nm:
                    module_stack.append((indent, nm.group(1)))

            if pending and not is_private(kw, rest):
                fd.decls.append(
                    Decl(
                        kind=kw,
                        signature=gather_signature(lines, i),
                        doc=list(pending),
                        scope=compute_scope(fd, module_stack, kw),
                    )
                )

            pending = []
            i += 1
            continue

        # Non-doc/attr/decl line drops any pending docs.
        pending = []
        i += 1
    return fd


def iter_projects() -> Iterator[Path]:
    for d in sorted(SRC.iterdir()):
        if d.is_dir():
            yield d


def render_project(project_dir: Path) -> tuple[str, str, str]:
    """Return (name, markdown, summary) for one project."""
    name = project_dir.name
    out: list[str] = [f"# {name}", ""]
    summary = ""
    for f in sorted(project_dir.rglob("*.fs")):
        if f.name == "AssemblyInfo.fs":
            continue
        fd = parse_file(f)
        if not fd.decls:
            continue
        rel = f.relative_to(project_dir)
        out.append(f"## `{rel.as_posix()}`")
        out.append("")
        if fd.namespace:
            out.append(f"_namespace_ `{fd.namespace}`")
            out.append("")
        if fd.top_module:
            out.append(f"_module_ `{fd.top_module}`")
            out.append("")
        if not summary and fd.decls[0].doc:
            summary = fd.decls[0].doc[0]
        for d in fd.decls:
            heading = d.scope or fd.namespace or name
            out.append(f"### `{d.kind}` in `{heading}`")
            out.append("")
            out.append("```fsharp")
            out.append(d.signature)
            out.append("```")
            out.append("")
            for line in d.doc:
                out.append(line)
            out.append("")
    return name, "\n".join(out).rstrip() + "\n", summary


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    index: list[tuple[str, str]] = []
    written = 0
    for project in iter_projects():
        name, md, summary = render_project(project)
        if md.strip() == f"# {name}":
            continue
        (OUT / f"{name}.md").write_text(md, encoding="utf-8")
        index.append((name, summary))
        written += 1

    idx = [
        "# ARCP F# SDK API Reference",
        "",
        "Auto-generated from F# `///` doc comments. Regenerate with `make docs-api`.",
        "",
        "## Projects",
        "",
    ]
    for name, summary in index:
        line = f"- [{name}]({name}.md)"
        if summary:
            line += f" — {summary}"
        idx.append(line)
    idx.append("")
    (OUT / "index.md").write_text("\n".join(idx), encoding="utf-8")
    print(f"Wrote {written} project page(s) + index.md to {OUT}")


if __name__ == "__main__":
    main()
