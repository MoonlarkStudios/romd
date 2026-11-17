"""One recovery sequence for source drills and extracted deployment bundles."""
from pathlib import Path

from container_lifecycle import HOSTS

# The quick start's one start command, word for word after `docker compose`
# (docs/self-hosting/production-deployment.md). A contract test keeps the two equal.
QUICK_START = ('up', '-d', '--wait', '--wait-timeout', '240')


class RecoverySequence:
    def __init__(self, run, compose, gate, source_env, *, wait_timeout=300, first_start=None):
        self.run, self.compose_path, self.gate = run, Path(compose), gate
        self.source = dict(source_env)
        self.wait_timeout = str(wait_timeout)
        self.first_start = first_start

    def compose(self, *arguments, stage, env=None):
        return self.run([self.compose_path, *arguments], stage, env=env or self.source)

    def start(self, *, env=None, phase='initial'):
        selected = env or self.source
        if phase == 'initial':
            # A new installation is started the way the quick start says, in one command, so the drill
            # meets the same startup ordering an operator does. Starting the database and worker
            # separately first would hide a service that starts before its dependency is ready.
            if self.first_start:
                self.first_start()
            else:
                self.compose(*QUICK_START, stage='quick-start', env=selected)
        else:
            # The runbook's restart, upgrade and restore procedures start the worker before the APIs.
            worker, hosts = {'restart': ('restart-worker-first', 'restart-start-hosts'),
                             'restore': ('restore-worker-first', 'restore-hosts')}[phase]
            self.compose('up', '-d', '--wait', '--wait-timeout', self.wait_timeout, 'postgres', 'romd-worker',
                         stage=worker, env=selected)
            self.compose('up', '-d', '--wait', '--wait-timeout', self.wait_timeout, stage=hosts, env=selected)
        self.gate.checkpoint(selected['COMPOSE_PROJECT_NAME'])

    def restart(self):
        with self.gate.stopping(self.source['COMPOSE_PROJECT_NAME'], HOSTS):
            self.compose('stop', 'romd-admin', 'romd-consumer', 'romd-worker', stage='restart-stop-hosts')
        self.start(phase='restart')

    def backup_and_stop(self, backup_script, backup_root):
        project = self.source['COMPOSE_PROJECT_NAME']
        with self.gate.stopping(project, HOSTS):
            self.run([Path(backup_script), Path(backup_root)], 'packaged-backup', env=self.source)
        # The backup trap resumes asynchronously. Admit that startup before a
        # second stop can race the hosts' signal-handler/readiness setup.
        self.compose('up', '-d', '--wait', '--wait-timeout', self.wait_timeout, stage='backup-resume-readiness')
        self.gate.checkpoint(project)
        manifests = list(Path(backup_root).glob('*/manifest.json'))
        if len(manifests) != 1:
            raise RuntimeError('Backup did not produce exactly one complete backup set')
        with self.gate.stopping(project):
            self.compose('stop', stage='stop-source-before-restore')
        return manifests[0].parent

    def restore(self, restore_script, backup, restore_env):
        self.compose('up', '-d', '--wait', '--wait-timeout', self.wait_timeout, 'postgres',
                     stage='restore-empty-postgres', env=restore_env)
        self.compose('create', '--no-recreate', 'romd-worker', stage='restore-data-volume', env=restore_env)
        with self.gate.stopping(restore_env['COMPOSE_PROJECT_NAME'], HOSTS):
            self.run([Path(restore_script), Path(backup)], 'packaged-restore', env=restore_env)
        self.start(env=restore_env, phase='restore')
