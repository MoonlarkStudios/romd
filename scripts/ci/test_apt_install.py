import contextlib
import io
import subprocess
import unittest

import apt_install


class Runner:
    def __init__(self, *statuses):
        self.statuses, self.commands = list(statuses), []

    def __call__(self, command, check):
        assert check is False
        self.commands.append(command)
        return subprocess.CompletedProcess(command, self.statuses.pop(0) if self.statuses else 0)


class AptInstallTests(unittest.TestCase):
    def run_main(self, packages, runner):
        sleeps = []
        with contextlib.redirect_stderr(io.StringIO()):
            apt_install.main(packages, runner, sleeps.append)
        return sleeps

    def test_each_call_is_bounded_by_a_root_timeout_and_installs_only_the_named_packages(self):
        runner = Runner()
        self.assertEqual([], self.run_main(['nginx'], runner))
        prefix = ['sudo', 'timeout', '--kill-after=10', '60', 'apt-get', *apt_install.OPTIONS]
        self.assertEqual([prefix + ['update', '-qq'],
                          prefix + ['install', '--yes', '--no-install-recommends', 'nginx']], runner.commands)

    def test_a_stalled_attempt_is_retried_and_exhausted_retries_fail_the_step(self):
        # 124 is timeout's status when it stops apt-get.
        runner = Runner(124, 0, 0)
        self.assertEqual([5], self.run_main(['ripgrep'], runner))
        self.assertEqual(3, len(runner.commands))
        runner = Runner(0, 100, 124, 1)
        with self.assertRaisesRegex(SystemExit, 'install failed after 3 bounded attempts'):
            self.run_main(['nginx'], runner)
        self.assertEqual(4, len(runner.commands))

    def test_missing_or_unexpected_package_names_are_refused_before_apt_runs(self):
        for packages in ([], ['nginx;id'], ['-o'], ['Nginx']):
            runner = Runner()
            with self.subTest(packages=packages), self.assertRaisesRegex(SystemExit, 'usage'):
                self.run_main(packages, runner)
            self.assertEqual([], runner.commands)


if __name__ == '__main__':
    unittest.main()
