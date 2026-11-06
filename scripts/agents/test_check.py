"""Regression tests for missing guidance, decision-record drift and silently unrouted backend suites."""

from pathlib import Path
import subprocess
import tempfile
import unittest

from check import (check_decision_citations, check_decision_records, check_links, check_repository, check_roles,
                   check_test_routes, decision_record_errors)
from doc_links import anchors, check_document, prepare_bundle

CONFORMING_RECORD = """# Example Policy

## Context

Forces.

## Decisions

### One Rule

```markdown
## Not A Section
Status: quoted inside an example
```

### Scoped Contexts: Single-Flight

## Trade-offs

## Rejected Alternatives

## Guardrails
"""


class InfrastructureChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def role(self, filename="scout", name="scout"):
        return self.write(f".codex/agents/{filename}.toml", f'''name = "{name}"
description = "Read source"
developer_instructions = "Answer the question"
''')

    def project(self, name, test=True):
        relative = f"tests/{name}/{name}.csproj"
        sdk = '<PackageReference Include="Microsoft.NET.Test.Sdk" />' if test else ""
        self.write(relative, f"<Project><ItemGroup>{sdk}</ItemGroup></Project>")
        return relative

    def test_missing_local_link_fails_but_remote_links_are_not_network_probes(self):
        path = self.write("docs/README.md", "# Here\n[gone](missing.md) [web](https://example.test/x) [section](#here)")
        errors = []
        check_links(path, self.root, errors)
        self.assertEqual(1, len(errors))
        self.assertIn("missing.md", errors[0])

    def test_relative_links_and_instruction_paths_resolve_from_their_owner(self):
        self.write("src/AGENTS.md", "Backend")
        self.write("docs/guide with spaces.md", "# X")
        self.write("docs/local/AGENTS.md", "Local")
        path = self.write("docs/AGENTS.md", "[backend](../src/AGENTS.md) [guide](guide%20with%20spaces.md#x)\n"
                          "Read `src/AGENTS.md`, `../src/AGENTS.md`, and `local/AGENTS.md`.\n")
        errors = []
        check_links(path, self.root, errors)
        self.assertEqual([], errors)

    def test_missing_instruction_path_fails_and_fenced_examples_are_ignored(self):
        path = self.write("AGENTS.md", "Read `src/AGENTS.md`.\n~~~md\n[example](missing.md)\n~~~\n")
        errors = []
        check_links(path, self.root, errors)
        self.assertEqual(1, len(errors))
        self.assertIn("src/AGENTS.md", errors[0])

    def test_rendered_links_check_anchors_reference_definitions_and_html_assets(self):
        self.write('docs/other.md', '# Heading `code` & punctuation!\n\n## Repeat\n## Repeat\n')
        self.write('docs/image.svg', '<svg/>')
        path = self.write('docs/guide.md', '''# Local
[section](#local) [other](other.md#heading-code--punctuation)
[repeat][details]
[details]: other.md#repeat-1
<img src="image.svg"> <source srcset="absent.png">
`[example](missing.md)`
~~~md
[example](missing.md)
~~~
[missing section](other.md#absent)
''')
        errors = check_document(path, self.root)
        self.assertEqual(2, len(errors), errors)
        self.assertTrue(any('absent.png' in error for error in errors))
        self.assertTrue(any('other.md#absent' in error for error in errors))
        self.assertIn('repeat-1', anchors((self.root / 'docs/other.md').read_text()))

    def test_bundle_relocates_local_guides_and_pins_omitted_files_and_directories(self):
        source = self.root / 'source'
        source.mkdir()
        self.write('source/docs/self-hosting/configuration.md', '# Configuration\n')
        self.write('source/docs/features/start.md', '# Start\n')
        original = self.write('source/docs/self-hosting/production-deployment.md',
                              '# Install\n[settings](configuration.md#configuration) '
                              '[start](../features/start.md#start) [features](../features/)\n')
        stage = self.root / 'stage'
        (stage / 'docs').mkdir(parents=True)
        (stage / 'docs/production-deployment.md').write_text(original.read_text())
        (stage / 'docs/configuration.md').write_text('# Configuration\n')
        revision = 'a' * 40
        prepare_bundle(source, stage, revision)
        installed = stage / 'docs/production-deployment.md'
        self.assertIn('(configuration.md#configuration)', installed.read_text())
        self.assertIn(f'/blob/{revision}/docs/features/start.md#start', installed.read_text())
        self.assertIn(f'/tree/{revision}/docs/features', installed.read_text())
        self.assertEqual([], check_document(installed, stage))
        # An omitted document's anchor must be checked before becoming an external URL.
        installed.write_text('[start](../features/start.md#absent)')
        with self.assertRaisesRegex(ValueError, 'invalid source anchor'):
            prepare_bundle(source, stage, revision)

    def test_bundle_refuses_missing_source_reference_instead_of_publishing_a_bad_link(self):
        source = self.root / 'source'
        source.mkdir()
        stage = self.root / 'stage'
        stage.mkdir()
        self.write('source/deploy/README.md', '# Deployment\n')
        (stage / 'README.md').write_text('[missing](missing.md)')
        with self.assertRaisesRegex(ValueError, 'invalid source link'):
            prepare_bundle(source, stage, 'b' * 40)

    def test_bundle_accepts_symlinked_source_and_stage_roots(self):
        source = self.root / 'source'
        stage = self.root / 'stage'
        self.write('source/deploy/README.md', '# Deployment\n[settings](../docs/self-hosting/configuration.md#configuration)\n')
        self.write('source/docs/self-hosting/configuration.md', '# Configuration\n')
        self.write('stage/README.md', (source / 'deploy/README.md').read_text())
        self.write('stage/docs/configuration.md', '# Configuration\n')
        source_alias = self.root / 'source-alias'
        stage_alias = self.root / 'stage-alias'
        source_alias.symlink_to(source, target_is_directory=True)
        stage_alias.symlink_to(stage, target_is_directory=True)

        prepare_bundle(source_alias, stage_alias, 'c' * 40)

        self.assertIn('(docs/configuration.md#configuration)', (stage / 'README.md').read_text())
        self.assertEqual([], check_document(stage / 'README.md', stage))

    def test_bundle_symlinked_root_still_refuses_links_outside_source(self):
        source = self.root / 'source'
        stage = self.root / 'stage'
        self.write('source/deploy/README.md', '[outside](../outside.md)\n')
        outside = self.write('outside.md', '# Outside\n')
        (source / 'outside.md').symlink_to(outside)
        self.write('stage/README.md', (source / 'deploy/README.md').read_text())
        source_alias = self.root / 'source-alias'
        source_alias.symlink_to(source, target_is_directory=True)

        with self.assertRaisesRegex(ValueError, 'invalid source link'):
            prepare_bundle(source_alias, stage, 'd' * 40)

    def test_decision_record_shape_rejects_status_plans_source_lines_and_template_drift(self):
        self.assertEqual([], decision_record_errors("docs/decisions/policy.md", CONFORMING_RECORD))
        errors = decision_record_errors("docs/decisions/old.md",
                                        "# Old\n\nStatus: accepted\n\n## Decision\n\nSee `src/X.cs:12`.\n\n"
                                        "## Follow-Up Work\n")
        self.assertEqual(4, len(errors))
        self.assertIn("sections must be Context, Decisions, Trade-offs", errors[0])
        self.assertIn("'Follow-Up Work' names plan work", errors[1])
        self.assertIn("status and dates", errors[2])
        self.assertIn("cites a source line", errors[3])

    def test_every_record_is_shape_checked_and_the_index_links_every_record(self):
        self.write("docs/decisions/README.md", "[policy](policy.md)\n")
        self.write("docs/decisions/policy.md", CONFORMING_RECORD)
        self.write("clients/romd_console/docs/decisions/console.md", "# Console\n\nStatus: accepted\n")
        files = ["clients/romd_console/docs/decisions/console.md", "docs/decisions/README.md",
                 "docs/decisions/policy.md"]
        errors = []
        records = check_decision_records(self.root, files, errors)
        self.assertEqual(2, len(records))
        self.assertEqual(3, len(errors))
        self.assertIn("clients/romd_console/docs/decisions/console.md: sections must be", errors[0])
        self.assertIn("status and dates belong in the pull request", errors[1])
        self.assertEqual("docs/decisions/README.md: index does not link "
                         "clients/romd_console/docs/decisions/console.md", errors[2])
        errors = []
        check_decision_records(self.root, files[1:], errors)
        self.assertEqual([], errors)

    def test_code_citations_must_name_existing_records_and_quoted_headings(self):
        self.write("docs/decisions/policy.md", CONFORMING_RECORD)
        self.write("src/Valid.cs", '/// (docs/decisions/policy.md,\n///     "One Rule" and "Scoped Contexts").\n'
                                   '// See docs/decisions/policy.md.\n'
                                   'var message = "(docs/decisions/policy.md, \\"One Rule\\")";\n')
        self.write("src/Broken.cs", '// (docs/decisions/policy.md, "No Such Rule")\n'
                                    '// (docs/decisions/policy.md, One Rule)\n'
                                    '// (docs/decisions/gone.md)\n')
        self.write("scripts/agents/fixture.py", "# docs/decisions/gone.md\n")
        self.write("docs/notes.md", "(docs/decisions/gone.md)\n")
        errors = []
        count = check_decision_citations(self.root, ["docs/notes.md", "scripts/agents/fixture.py", "src/Broken.cs",
                                                     "src/Valid.cs"], errors)
        self.assertEqual(6, count)
        self.assertEqual(["src/Broken.cs:1: docs/decisions/policy.md has no heading 'No Such Rule'",
                          "src/Broken.cs:2: quote the heading cited from docs/decisions/policy.md",
                          "src/Broken.cs:3: cites missing decision record docs/decisions/gone.md"], errors)

    def test_role_schema_errors_and_duplicate_identity_are_reported(self):
        self.role()
        self.role(filename="second")
        self.write(".codex/agents/broken.toml", 'name = "broken"\nsandbox_mode = "read-only"\n')
        errors = []
        check_roles(self.root, errors)
        self.assertTrue(any("duplicate agent name" in error for error in errors))
        self.assertTrue(any("developer_instructions" in error for error in errors))
        self.assertTrue(any("sandbox_mode does not override the parent session" in error for error in errors))

    def test_parent_session_settings_are_rejected_even_with_valid_parent_values(self):
        role = self.role()
        role.write_text(role.read_text() + '''sandbox_mode = "read-only"
approval_policy = "never"
model_provider = "openai"
mcp_servers = { docs = { url = "https://example.test/mcp" } }
''')
        errors = []
        check_roles(self.root, errors)
        self.assertEqual(4, len(errors), errors)
        for field in ("sandbox_mode", "approval_policy", "model_provider", "mcp_servers"):
            self.assertTrue(any(f"{field} does not override the parent session" in error for error in errors))
        self.assertTrue(all("configure it in the parent session" in error for error in errors))

    def test_documented_model_capability_and_skill_options_are_accepted(self):
        role = self.role()
        role.write_text(role.read_text() + '''model = "available-model"
model_reasoning_effort = "high"
model_reasoning_summary = "auto"
model_verbosity = "low"
[features]
shell_tool = false
apps = false
plugins = false
memories = false
request_permissions_tool = false
[skills]
include_instructions = false
[skills.bundled]
enabled = false
[[skills.config]]
path = "/example/skills/read/SKILL.md"
enabled = false
[[skills.config]]
name = "write"
enabled = false
''')
        errors = []
        self.assertEqual(1, check_roles(self.root, errors))
        self.assertEqual([], errors)

    def test_unknown_role_and_nested_option_names_fail(self):
        role = self.role()
        role.write_text(role.read_text() + '''model_reasoning_efort = "high"
[features]
shell_tools = false
[skills]
bundeld = { enabled = false }
''')
        errors = []
        check_roles(self.root, errors)
        self.assertEqual(3, len(errors), errors)
        for typo in ("model_reasoning_efort", "shell_tools", "bundeld"):
            self.assertTrue(any(typo in error for error in errors))

    def test_optional_model_fields_require_nonempty_strings(self):
        for field in ("model", "model_reasoning_effort", "model_reasoning_summary", "model_verbosity"):
            for value in ('" "', 'true', '1'):
                with self.subTest(field=field, value=value):
                    role = self.role()
                    role.write_text(role.read_text() + f"{field} = {value}\n")
                    errors = []
                    check_roles(self.root, errors)
                    self.assertEqual([f".codex/agents/scout.toml: {field} must be a nonempty string"], errors)

    def test_capability_and_skill_boolean_options_reject_strings_and_numbers(self):
        role = self.role()
        role.write_text(role.read_text() + '''[features]
shell_tool = "false"
[skills]
include_instructions = 0
[skills.bundled]
enabled = "false"
[[skills.config]]
name = "write"
enabled = 0
''')
        errors = []
        check_roles(self.root, errors)
        self.assertEqual(4, len(errors), errors)
        self.assertTrue(all("must be a boolean" in error for error in errors))

    def test_skill_configuration_requires_one_nonempty_selector_per_table(self):
        role = self.role()
        role.write_text(role.read_text() + '''[[skills.config]]
path = "/example/skills/read/SKILL.md"
name = "read"
[[skills.config]]
enabled = false
[[skills.config]]
path = " "
''')
        errors = []
        check_roles(self.root, errors)
        self.assertEqual(3, len(errors), errors)
        self.assertTrue(all("exactly one path or name selector" in error for error in errors[:2]))
        self.assertIn("skills.config[2].path must be a nonempty string", errors[2])

    def test_malformed_nested_option_shapes_report_errors_without_crashing(self):
        for options, expected in (
            ('features = false', 'features must be a table'),
            ('skills = []', 'skills must be a table'),
            ('skills = { bundled = [] }', 'skills.bundled must be a table'),
            ('skills = { config = {} }', 'skills.config must be an array of tables'),
            ('skills = { config = [false] }', 'skills.config[0] must be a table'),
        ):
            with self.subTest(options=options):
                role = self.role()
                role.write_text(role.read_text() + options + '\n')
                errors = []
                check_roles(self.root, errors)
                self.assertEqual([f".codex/agents/scout.toml: {expected}"], errors)

    def test_malformed_role_toml_fails_with_its_path(self):
        self.write(".codex/agents/broken.toml", 'name = "unterminated')
        errors = []
        check_roles(self.root, errors)
        self.assertTrue(any(".codex/agents/broken.toml" in error for error in errors))

    def test_new_test_project_must_be_in_default_task_and_fixture_library_is_excluded(self):
        suite = self.project("New.Tests")
        support = self.project("FixtureSupport", test=False)
        errors = []
        count = check_test_routes(self.root, [suite, support], {"tasks": {"test": {"run": []}}}, errors)
        self.assertEqual(1, count)
        self.assertEqual(1, len(errors))
        self.assertIn("tasks.test", errors[0])
        errors = []
        check_test_routes(self.root, [suite, support],
                          {"tasks": {"test": {"run": [f'dotnet test "{suite}" --no-restore']}}}, errors)
        self.assertEqual([], errors)

    def test_niced_test_command_routes_but_other_prefixes_do_not(self):
        suite = self.project("New.Tests")
        errors = []
        check_test_routes(self.root, [suite], {"tasks": {"test": {"run": f"nice -n 10 dotnet test {suite}"}}}, errors)
        self.assertEqual([], errors)
        for command in (f"nice dotnet test {suite}", f"nice -n ten dotnet test {suite}", f"nohup dotnet test {suite}"):
            with self.subTest(command=command):
                errors = []
                check_test_routes(self.root, [suite], {"tasks": {"test": {"run": command}}}, errors)
                self.assertEqual(1, len(errors))

    def test_similar_filename_or_non_test_command_does_not_establish_routing(self):
        suite = self.project("New.Tests")
        for command in (f"dotnet build {suite}", f"dotnet test {suite}.backup"):
            with self.subTest(command=command):
                errors = []
                check_test_routes(self.root, [suite], {"tasks": {"test": {"run": command}}}, errors)
                self.assertEqual(1, len(errors))

    def test_integration_and_mount_require_their_explicit_tasks(self):
        for name, task in (("Romd.Hosting.IntegrationTests", "test:integration"),
                           ("Romd.Mount.Tests", "test:mount-dev")):
            with self.subTest(task=task):
                suite = self.project(name)
                errors = []
                check_test_routes(self.root, [suite], {"tasks": {"test": {"run": f"dotnet test {suite}"}}}, errors)
                self.assertIn(f"tasks.{task}", errors[0])
                errors = []
                check_test_routes(self.root, [suite], {"tasks": {task: {"run": f"dotnet test {suite}"}}}, errors)
                self.assertEqual([], errors)

    def test_inventory_includes_new_nonignored_guidance_and_test_projects(self):
        subprocess.run(["git", "init", "--quiet", str(self.root)], check=True)
        self.write(".codex/config.toml", "[agents]\nmax_concurrent_threads_per_session = 4\n")
        self.role()
        self.write(".mise.toml", '[tasks.test]\nrun = []\n')
        self.write("AGENTS.md", "[broken](missing.md)\n")
        self.project("Untracked.Tests")
        self.write(".gitignore", "ignored/\n")
        self.write("ignored/AGENTS.md", "[also broken](missing.md)\n")
        errors, counts = check_repository(self.root)
        self.assertEqual(1, counts["Markdown files"])
        self.assertEqual(1, counts["test projects"])
        self.assertEqual(2, len(errors))

    def test_registry_routing_error_names_the_registry_group_not_mise_tasks(self):
        subprocess.run(["git", "init", "--quiet", str(self.root)], check=True)
        self.write(".codex/config.toml", "[agents]\n")
        self.role()
        self.write(".mise.toml", '[tasks.test]\nrun = "python3 scripts/validation/run.py --group backend"\n')
        self.write("scripts/validation/checks.json", '{"version": 1, "checks": []}\n')
        for name, group, task in (("New.Tests", "backend", "test"),
                                  ("Romd.Hosting.IntegrationTests", "integration", "test:integration")):
            relative = self.project(name)
            errors, _ = check_repository(self.root)
            routing = [error for error in errors if error.startswith(relative + ": not routed")]
            self.assertEqual([f"{relative}: not routed by scripts/validation/checks.json; add a check in group "
                              f"{group} (run by mise run {task})"], routing)
            self.assertFalse(any(".mise.toml tasks." in error for error in errors))

    def test_removed_guidance_does_not_require_staging_before_validation(self):
        subprocess.run(["git", "init", "--quiet", str(self.root)], check=True)
        self.write(".codex/config.toml", "[agents]\n")
        self.role()
        self.write(".mise.toml", '[tasks.test]\nrun = []\n')
        removed = self.write("obsolete/AGENTS.md", "[historical](missing.md)\n")
        subprocess.run(["git", "-C", str(self.root), "add", "obsolete/AGENTS.md"], check=True)
        removed.unlink()
        errors, counts = check_repository(self.root)
        self.assertEqual([], errors)
        self.assertEqual(0, counts["Markdown files"])

    def test_backend_guide_and_contribution_links_are_part_of_guidance_validation(self):
        subprocess.run(["git", "init", "--quiet", str(self.root)], check=True)
        self.write(".codex/config.toml", "[agents]\n")
        self.role()
        self.write(".mise.toml", '[tasks.test]\nrun = []\n')
        self.write("docs/development/dotnet-development.md", "[example](../../src/Missing.cs)\n")
        self.write("CONTRIBUTING.md", "[guide](docs/gone.md)\n")
        errors, counts = check_repository(self.root)
        self.assertEqual(2, counts["Markdown files"])
        self.assertEqual(2, len(errors))
        self.assertTrue(any("src/Missing.cs" in error for error in errors))
        self.assertTrue(any("docs/gone.md" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
