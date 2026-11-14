import unittest
import json
from pathlib import Path
import tempfile

from nuget_locks import IMAGE_PROJECTS, IMAGE_RIDS, restore_commands
from toolchain import check_sdk_servicing


class NugetLockTests(unittest.TestCase):
    def test_sdk_reference_and_runtime_servicing_must_match_before_restore(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'global.json').write_text(json.dumps({'sdk': {'version': '9.8.700'}}))
            (root / 'Dockerfile').write_text('FROM mcr.microsoft.com/dotnet/aspnet:9.8.3-noble@sha256:abc AS runtime\n')
            metadata = root / 'sdk.props'
            for reference, runtime, accepted in (('9.8.3', '9.8.3', True), ('9.8.1', '9.8.3', False),
                                                  ('9.8.3', '9.8.1', False)):
                metadata.write_text('<Project><KnownFrameworkReference Include="Microsoft.AspNetCore.App" '
                    f'TargetFramework="net9.8" TargetingPackVersion="{reference}" '
                    f'LatestRuntimeFrameworkVersion="{runtime}" /></Project>')
                with self.subTest(reference=reference, runtime=runtime):
                    if accepted:
                        check_sdk_servicing(root, metadata)
                    else:
                        with self.assertRaisesRegex(ValueError, 'same servicing release'):
                            check_sdk_servicing(root, metadata)
            metadata.write_text('<Project/>')
            with self.assertRaises(ValueError):
                check_sdk_servicing(root, metadata)

    def test_verification_covers_image_rids_and_projects_outside_the_solution(self):
        commands = restore_commands({'in-solution.csproj', 'standalone.csproj'}, {'in-solution.csproj'})
        self.assertEqual(len(IMAGE_PROJECTS) * len(IMAGE_RIDS) + 2, len(commands))
        for command in commands:
            self.assertIn('--locked-mode', command)
            self.assertNotIn('--force-evaluate', command)
            if '--runtime' in command:
                rid = command[command.index('--runtime') + 1]
                self.assertEqual(f'packages.{rid}.lock.json', command[command.index('--lock-file-path') + 1])
        self.assertEqual('Romd.sln', commands[-2][-1])
        self.assertEqual('standalone.csproj', commands[-1][-1])

    def test_only_explicit_updates_allow_graph_re_evaluation(self):
        commands = restore_commands(set(), set(), update=True)
        for command in commands:
            self.assertNotIn('--locked-mode', command)
            self.assertIn('--force-evaluate', command)
            self.assertIn('-p:RestoreLockedMode=false', command)
