import unittest

from nuget_locks import IMAGE_PROJECTS, IMAGE_RIDS, restore_commands


class NugetLockTests(unittest.TestCase):
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
