#!/usr/bin/env python3
"""Check local documentation links; adapt source links for the source-free release bundle.

Handles the Markdown links, reference definitions and HTML asset links used in
this repository. Fenced examples and inline code are not rendered links.
External availability is a separate network check, not part of this local gate.
"""
import argparse
from collections import Counter
from html import unescape
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import quote, unquote, urlsplit


LINK = re.compile(
    r'\[[^\]\n]*\]\(\s*(?:<(?P<angle>[^>\n]+)>|(?P<inline>[^\s)]+))'
    r'|^\s{0,3}\[[^\]\n]+\]:\s*(?:<(?P<refangle>[^>\n]+)>|(?P<ref>\S+))'
    r'|\b(?:href|src|srcset)=["\'](?P<html>[^"\'\s]+)["\']',
    re.MULTILINE,
)
BUNDLE_SOURCES = {
    'README.md': 'deploy/README.md',
    **{f'docs/{name}.md': f'docs/self-hosting/{name}.md'
       for name in ('production-deployment', 'media-processing', 'configuration')},
}


def prose(text, inline=False):
    """Mask examples without changing offsets or line numbers."""
    fence = None
    result = []
    for line in text.splitlines(keepends=True):
        match = re.match(r'^\s*(`{3,}|~{3,})(.*)', line)
        masked = fence is not None or match is not None
        if match:
            token, suffix = match.groups()
            if fence is None:
                fence = token
            elif token[0] == fence[0] and len(token) >= len(fence) and not suffix.strip():
                fence = None
        if masked:
            line = re.sub(r'[^\n]', ' ', line)
        elif inline:
            line = re.sub(r'(`+).*?\1', lambda m: ' ' * len(m[0]), line)
        result.append(line)
    return ''.join(result)


def links(text):
    for match in LINK.finditer(prose(text, inline=True)):
        group = next(name for name, value in match.groupdict().items() if value is not None)
        yield match.group(group), match.span(group)


def anchors(text):
    text = prose(text)
    result = set(re.findall(r'\b(?:id|name)=["\']([^"\']+)["\']', text))
    counts = Counter()
    for line in text.splitlines():
        match = re.match(r'^#{1,6}\s+(.+?)\s*#*$', line)
        if not match:
            continue
        title = re.sub(r'\[([^]]+)\]\([^)]*\)', r'\1', match[1])
        title = unescape(re.sub(r'<[^>]*>', '', title))
        slug = re.sub(r'[^\w\- ]', '', title.lower()).replace(' ', '-')
        number = counts[slug]
        counts[slug] += 1
        result.add(slug + (f'-{number}' if number else ''))
    return result


def local_target(path, target, root):
    url = urlsplit(unescape(target))
    if url.scheme or url.netloc:
        return None
    destination = ((root / unquote(url.path).lstrip('/')) if url.path.startswith('/')
                   else path.parent / unquote(url.path)) if url.path else path
    return destination.resolve(), unquote(url.fragment)


def check_document(path, root):
    errors = []
    for target, span in links(path.read_text(encoding='utf-8')):
        local = local_target(path, target, root)
        if local is None:
            continue
        destination, fragment = local
        if not destination.is_relative_to(root.resolve()) or not destination.exists():
            errors.append(f'{path.relative_to(root)}: missing link target {target}')
        elif fragment and destination.suffix == '.md' and fragment not in anchors(destination.read_text(encoding='utf-8')):
            errors.append(f'{path.relative_to(root)}: missing link anchor {target}')
    return errors


def prepare_bundle(source, stage, revision):
    """Keep installed references local; pin omitted source references to this commit."""
    if not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise ValueError('Full source revision required for bundle documentation')
    source = source.resolve()
    stage = stage.resolve()
    packaged = {source_name: bundle_name for bundle_name, source_name in BUNDLE_SOURCES.items()}
    for path in sorted(stage.rglob('*.md')):
        relative = path.relative_to(stage).as_posix()
        original = source / BUNDLE_SOURCES.get(relative, relative)
        text = path.read_text(encoding='utf-8')
        edits = []
        for target, span in links(text):
            local = local_target(original, target, source)
            if local is None:
                continue
            destination, fragment = local
            if not destination.is_relative_to(source) or not destination.exists():
                installed_local = local_target(path, target, stage)
                if installed_local and installed_local[0].is_relative_to(stage) and installed_local[0].exists():
                    continue  # Notice packaging can already have relocated a retained license.
                raise ValueError(f'{relative}: invalid source link {target}')
            if fragment and destination.suffix == '.md' and fragment not in anchors(destination.read_text(encoding='utf-8')):
                raise ValueError(f'{relative}: invalid source anchor {target}')
            source_name = destination.relative_to(source).as_posix()
            installed = stage / packaged.get(source_name, source_name)
            if installed.exists():
                replacement = Path(os.path.relpath(installed, path.parent)).as_posix()
                replacement = quote(replacement, safe='/.-_') if destination != original else ''
            else:
                kind = 'tree' if destination.is_dir() else 'blob'
                replacement = f'https://github.com/MoonlarkStudios/romd/{kind}/{revision}/{quote(source_name, safe="/.-_")}'
            url = urlsplit(target)
            if url.query:
                replacement += '?' + url.query
            if fragment:
                replacement += '#' + url.fragment
            edits.append((*span, replacement))
        for start, end, replacement in reversed(edits):
            text = text[:start] + replacement + text[end:]
        path.write_text(text, encoding='utf-8')
    errors = [error for path in sorted(stage.rglob('*.md')) for error in check_document(path, stage)]
    if errors:
        raise ValueError('\n'.join(errors))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', nargs=3, metavar=('SOURCE', 'STAGE', 'REVISION'))
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    if args.bundle:
        source, stage, revision = args.bundle
        prepare_bundle(Path(source).resolve(), Path(stage).resolve(), revision)
        return
    root = args.root.resolve()
    inventory = subprocess.check_output(['git', '-C', str(root), 'ls-files', '--cached', '--others', '--exclude-standard', '-z'])
    documents = sorted({root / name for name in inventory.decode().split('\0')
                        if name.endswith('.md') and (root / name).is_file()})
    errors = [error for path in documents for error in check_document(path, root)]
    if errors:
        raise SystemExit('\n'.join(errors))
    print(f'Documentation links passed: {len(documents)} Markdown files (local files, anchors and HTML assets)')


if __name__ == '__main__':
    main()
