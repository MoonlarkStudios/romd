import unittest
from unittest.mock import patch

from ci_status import check, evaluate, gate_passed, matching_runs

REPOSITORY = 'owner/repo'
REVISION = 'a' * 40


def run(run_id, status='completed', conclusion='success', **changes):
    item = dict(id=run_id, head_sha=REVISION, event='push', head_branch='main', path='.github/workflows/ci.yml',
                status=status, conclusion=conclusion, repository=dict(full_name=REPOSITORY),
                head_repository=dict(full_name=REPOSITORY))
    item.update(changes)
    return item


def jobs(conclusion='success', status='completed', name='CI gate'):
    return dict(jobs=[dict(name='Web', status='completed', conclusion='success'),
                      dict(name=name, status=status, conclusion=conclusion)])


class FakeApi:
    def __init__(self, *rounds, gate=None):
        self.rounds, self.gate, self.calls = list(rounds), gate or (lambda run_id: jobs()), []

    def __call__(self, path, token):
        self.calls.append(path)
        if '/jobs?' in path:
            return self.gate(int(path.split('/runs/')[1].split('/')[0]))
        runs = self.rounds.pop(0) if len(self.rounds) > 1 else self.rounds[0]
        return dict(workflow_runs=runs)


class Clock:
    def __init__(self):
        self.now = 0

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class CiStatusTests(unittest.TestCase):
    def test_only_main_push_runs_of_this_repositorys_ci_for_the_exact_commit_qualify(self):
        good = run(1)
        impostors = [run(2, head_sha='b' * 40), run(3, event='pull_request'), run(4, event='workflow_dispatch'),
                     run(5, head_branch='feature'), run(6, path='.github/workflows/other.yml'),
                     run(7, repository=dict(full_name='fork/repo')), run(8, head_repository=dict(full_name='fork/repo')),
                     run(9, path='.github/workflows/ci.yml@refs/heads/main')]
        self.assertEqual([good], matching_runs(dict(workflow_runs=[good, *impostors]), REPOSITORY, REVISION, 'main'))
        with self.assertRaisesRegex(ValueError, 'invalid'):
            matching_runs(dict(runs=[]), REPOSITORY, REVISION, 'main')

    def test_successful_run_also_requires_its_latest_ci_gate_to_succeed(self):
        self.assertTrue(gate_passed(jobs()))
        for payload in (jobs('failure'), jobs('skipped'), jobs(status='in_progress', conclusion=None),
                        jobs(name='Other check'), dict(jobs=jobs()['jobs'] * 2), dict(jobs=None)):
            with self.subTest(payload=payload):
                if not isinstance(payload.get('jobs'), list):
                    with self.assertRaises(ValueError):
                        gate_passed(payload)
                else:
                    self.assertFalse(gate_passed(payload))
        self.assertEqual(('failed', 1), evaluate([run(1)], lambda run_id: jobs('skipped')))

    def test_any_successful_attempt_passes_and_running_attempts_wait(self):
        self.assertEqual(('passed', 1), evaluate([run(1), run(2, status='in_progress', conclusion=None)], lambda _: jobs()))
        self.assertEqual(('pending', 2), evaluate([run(1, conclusion='failure'), run(2, status='queued', conclusion=None)],
                                                  lambda _: jobs()))
        for conclusion in ('failure', 'cancelled', 'timed_out', 'skipped'):
            self.assertEqual('failed', evaluate([run(1, conclusion=conclusion)], lambda _: jobs())[0])
        self.assertEqual(('failed', None), evaluate([], lambda _: jobs()))

    def test_waits_for_a_running_main_ci_then_accepts_its_success(self):
        clock = Clock()
        api = FakeApi([run(1, status='in_progress', conclusion=None)], [run(1)])
        with patch('ci_status.api_get', api):
            self.assertEqual(1, check(REPOSITORY, REVISION, 'main', 'token', 600, clock=clock, sleep=clock.sleep))
        self.assertEqual(30, clock.now)
        self.assertIn('head_sha=' + REVISION, api.calls[0])
        self.assertIn('event=push', api.calls[0])
        self.assertIn('branch=main', api.calls[0])

    def test_failed_missing_or_unfinished_ci_blocks_without_publishing(self):
        for rounds, wait, message in (([run(1, conclusion='failure')], 600, 'did not succeed'),
                                      ([], 600, 'was not found'),
                                      ([run(1, status='in_progress', conclusion=None)], 60, 'did not finish in time')):
            with self.subTest(message=message), patch('ci_status.api_get', FakeApi(rounds)):
                clock = Clock()
                with self.assertRaisesRegex(ValueError, message + '.*nothing has been published'):
                    check(REPOSITORY, REVISION, 'main', 'token', wait, clock=clock, sleep=clock.sleep)

    def test_identity_is_validated_before_any_request(self):
        api = FakeApi([run(1)])
        with patch('ci_status.api_get', api):
            for repository, revision, branch in (('owner/repo?x=1', REVISION, 'main'), (REPOSITORY, 'HEAD', 'main'),
                                                 (REPOSITORY, REVISION, 'main&event=pull_request')):
                with self.subTest(identity=(repository, revision, branch)), self.assertRaises(ValueError):
                    check(repository, revision, branch, 'token', 0)
        self.assertEqual([], api.calls)


if __name__ == '__main__':
    unittest.main()
