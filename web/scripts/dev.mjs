import { spawn, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const applications = new Set([
  'romd-admin-app',
  'romd-consumer-app',
  'romd-player-app',
]);
const workspaceDirectory = fileURLToPath(new URL('..', import.meta.url));

// pnpm writes this file on every successful install. Without it, `pnpm --recursive
// exec tsc` cannot find tsc and pnpm itself prints a stray "undefined" line per
// package before its error, so fail first with an actionable message.
function webDependenciesInstalled() {
  return existsSync(join(workspaceDirectory, 'node_modules', '.modules.yaml'));
}

function terminateProcessGroup(pid, signal) {
  if (process.platform === 'win32') {
    // Windows has no POSIX process-group signals; include pnpm's descendants.
    const result = spawnSync(
      'taskkill',
      [
        '/PID',
        String(pid),
        '/T',
        '/F',
      ],
      {
        stdio: 'ignore',
      },
    );
    if (result.error) throw result.error;
    return;
  }
  process.kill(-pid, signal);
}

function processGroupExists(pid) {
  try {
    process.kill(process.platform === 'win32' ? pid : -pid, 0);
    return true;
  } catch (error) {
    if (error.code === 'ESRCH') return false;
    if (error.code === 'EPERM') return true;
    throw error;
  }
}

// Keep the package graph in pnpm: ^... selects only the app's workspace dependencies.
// Use tsc directly so development never invokes API-client generation or needs a host.
export async function runDevelopment(
  application,
  {
    pnpmPath = process.env.npm_execpath,
    signalSource = process,
    spawnProcess = spawn,
    killGroup = terminateProcessGroup,
    groupExists = processGroupExists,
    dependenciesInstalled = webDependenciesInstalled,
    appArguments = [],
  } = {},
) {
  if (!applications.has(application)) {
    throw new Error(`Unknown development app: ${application}`);
  }
  if (!pnpmPath) {
    throw new Error('Start development with pnpm dev, pnpm dev:consumer, or pnpm dev:player.');
  }
  if (!dependenciesInstalled()) {
    process.stderr.write(
      'Web dependencies are not installed (web/node_modules is missing). ' +
        'Run `mise run bootstrap` from the repository root, or `pnpm install --frozen-lockfile` in web/.\n',
    );
    return 1;
  }
  // npm_execpath can be pnpm's JavaScript entrypoint or a standalone executable
  // (including the native distribution installed by mise).
  const pnpmIsScript = /\.[cm]?js$/i.test(pnpmPath);

  const children = new Set();
  let stopping = false;
  let complete;
  const finished = new Promise((resolve) => {
    complete = resolve;
  });

  const stop = (exitCode) => {
    if (stopping) return;
    stopping = true;
    const stoppingChildren = [
      ...children,
    ];
    // These IDs outlive pnpm: descendants can keep the group alive after the
    // direct child has exited. Do not cancel cleanup when that child exits.
    const groups = stoppingChildren.map((child) => child.pid).filter((pid) => pid !== undefined);
    for (const pid of groups) {
      try {
        killGroup(pid, 'SIGTERM');
      } catch (error) {
        if (error.code !== 'ESRCH')
          process.stderr.write(`Group ${pid} SIGTERM: ${error.message}\n`);
      }
    }
    const cleanup = async () => {
      const deadline = Date.now() + 2000;
      while (groups.some(groupExists) && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 25));
      }
      for (const pid of groups.filter(groupExists)) {
        try {
          killGroup(pid, 'SIGKILL');
        } catch (error) {
          if (error.code !== 'ESRCH')
            process.stderr.write(`Group ${pid} SIGKILL: ${error.message}\n`);
        }
      }
      await Promise.all(stoppingChildren.map((child) => child.finished));
      const killDeadline = Date.now() + 2000;
      while (groups.some(groupExists) && Date.now() < killDeadline) {
        await new Promise((resolve) => setTimeout(resolve, 25));
      }
      if (groups.some(groupExists)) {
        process.stderr.write('Development process groups did not exit after SIGKILL.\n');
        complete(1);
        return;
      }
      complete(exitCode);
    };
    cleanup().catch((error) => {
      process.stderr.write(`${error.message}\n`);
      complete(1);
    });
  };

  const start = (args, persistent = false) => {
    const child = spawnProcess(
      pnpmIsScript ? process.execPath : pnpmPath,
      pnpmIsScript
        ? [
            pnpmPath,
            ...args,
          ]
        : args,
      {
        cwd: workspaceDirectory,
        stdio: 'inherit',
        detached: process.platform !== 'win32',
      },
    );
    children.add(child);
    child.finished = new Promise((resolve) => {
      child.once('error', (error) => {
        process.stderr.write(`${error.message}\n`);
        children.delete(child);
        resolve(1);
        stop(1);
      });
      child.once('exit', (code) => {
        resolve(code ?? 1);
        // Keep a persistent child's group through stop: pnpm can exit before
        // all of the compiler/server descendants in that group have exited.
        if (persistent && !stopping) stop(code || 1);
        children.delete(child);
      });
    });
    return child;
  };

  const interrupt = () => stop(130);
  const terminate = () => stop(143);
  const hangup = () => stop(129);
  // Keep handlers installed throughout asynchronous group cleanup: terminal
  // and task-runner forwarding can deliver the same signal more than once.
  signalSource.on('SIGINT', interrupt);
  signalSource.on('SIGTERM', terminate);
  signalSource.on('SIGHUP', hangup);
  try {
    const dependencies = [
      '--recursive',
      '--filter',
      `${application}^...`,
    ];
    const preparation = start([
      ...dependencies,
      'exec',
      'tsc',
    ]);
    const preparationCode = await preparation.finished;
    if (!stopping && preparationCode !== 0) stop(preparationCode);
    if (!stopping) {
      start(
        [
          ...dependencies,
          '--parallel',
          'exec',
          'tsc',
          '--watch',
          '--preserveWatchOutput',
        ],
        true,
      );
      start(
        [
          '--filter',
          application,
          'dev',
          ...appArguments,
        ],
        true,
      );
    }
    return await finished;
  } finally {
    signalSource.removeListener('SIGINT', interrupt);
    signalSource.removeListener('SIGTERM', terminate);
    signalSource.removeListener('SIGHUP', hangup);
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = await runDevelopment(process.argv[2], {
      appArguments: process.argv.slice(3),
    });
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
