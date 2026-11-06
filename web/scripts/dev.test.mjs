import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { setImmediate, setTimeout } from 'node:timers/promises';
import { runDevelopment } from './dev.mjs';

function session(application, appArguments = []) {
  const children = [];
  const signals = new EventEmitter();
  const killed = [];
  const result = runDevelopment(application, {
    pnpmPath: '/tools/pnpm.cjs',
    appArguments,
    signalSource: signals,
    groupExists: () => false,
    dependenciesInstalled: () => true,
    spawnProcess: (executable, args, options) => {
      const child = new EventEmitter();
      child.pid = children.length + 100;
      children.push({
        child,
        executable,
        args,
        options,
      });
      return child;
    },
    killGroup: (pid, signal) => {
      killed.push({
        pid,
        signal,
      });
      queueMicrotask(() =>
        children.find(({ child }) => child.pid === pid).child.emit('exit', null),
      );
    },
  });
  return {
    children,
    signals,
    killed,
    result,
  };
}

for (const application of [
  'romd-admin-app',
  'romd-consumer-app',
  'romd-player-app',
]) {
  test(`${application} prepares checked-in dependencies before starting watchers and Vite`, async () => {
    const { children, signals, result, killed } = session(application);
    assert.equal(children.length, 1);
    assert.deepEqual(children[0].args, [
      '/tools/pnpm.cjs',
      '--recursive',
      '--filter',
      `${application}^...`,
      'exec',
      'tsc',
    ]);
    assert.equal(children[0].executable, process.execPath);
    assert.equal(children[0].options.shell, undefined);
    children[0].child.emit('exit', 0);
    await setImmediate();
    assert.equal(children.length, 3);
    assert.deepEqual(children[1].args, [
      '/tools/pnpm.cjs',
      '--recursive',
      '--filter',
      `${application}^...`,
      '--parallel',
      'exec',
      'tsc',
      '--watch',
      '--preserveWatchOutput',
    ]);
    assert.deepEqual(children[2].args, [
      '/tools/pnpm.cjs',
      '--filter',
      application,
      'dev',
    ]);
    signals.emit('SIGTERM');
    assert.equal(await result, 143);
    assert.deepEqual(
      killed.map(({ pid }) => pid),
      [
        101,
        102,
      ],
    );
    assert.equal(signals.listenerCount('SIGTERM'), 0);
  });
}

test('a dependency compilation failure blocks the app and watch startup', async () => {
  const { children, result } = session('romd-admin-app');
  children[0].child.emit('exit', 2);
  assert.equal(await result, 2);
  assert.equal(children.length, 1);
});

test('a failed watcher stops its app and reports failure', async () => {
  const { children, result, killed } = session('romd-consumer-app');
  children[0].child.emit('exit', 0);
  await setImmediate();
  children[1].child.emit('exit', 2);
  assert.equal(await result, 2);
  assert.deepEqual(killed, [
    {
      pid: 101,
      signal: 'SIGTERM',
    },
    {
      pid: 102,
      signal: 'SIGTERM',
    },
  ]);
});

test('an unexpected clean app exit still stops its watcher and fails', async () => {
  const { children, result, killed } = session('romd-player-app');
  children[0].child.emit('exit', 0);
  await setImmediate();
  children[2].child.emit('exit', 0);
  assert.equal(await result, 1);
  assert.deepEqual(killed, [
    {
      pid: 101,
      signal: 'SIGTERM',
    },
    {
      pid: 102,
      signal: 'SIGTERM',
    },
  ]);
});

test('interrupting dependency preparation does not start development processes', async () => {
  const { children, signals, result, killed } = session('romd-admin-app');
  signals.emit('SIGINT');
  assert.equal(await result, 130);
  assert.equal(children.length, 1);
  assert.deepEqual(killed, [
    {
      pid: 100,
      signal: 'SIGTERM',
    },
  ]);
});

test('unknown applications cannot be interpolated into pnpm commands', async () => {
  await assert.rejects(() => runDevelopment('arbitrary-app'), /Unknown development app/);
});

test('missing web dependencies fail actionably before pnpm can run', async (context) => {
  const spawned = [];
  const errors = [];
  context.mock.method(process.stderr, 'write', (text) => {
    errors.push(String(text));
    return true;
  });
  const signals = new EventEmitter();
  const result = await runDevelopment('romd-admin-app', {
    pnpmPath: '/tools/pnpm.cjs',
    signalSource: signals,
    dependenciesInstalled: () => false,
    spawnProcess: (...args) => {
      spawned.push(args);
      throw new Error('pnpm must not start without installed dependencies');
    },
  });
  context.mock.restoreAll();
  assert.equal(result, 1);
  assert.deepEqual(spawned, []);
  assert.equal(signals.listenerCount('SIGINT'), 0);
  assert.match(errors.join(''), /Web dependencies are not installed.*mise run bootstrap/);
  assert.doesNotMatch(errors.join(''), /undefined/);
});

test('Vite arguments are forwarded only to the selected app', async () => {
  const { children, signals, result } = session('romd-player-app', [
    '--port',
    '5185',
  ]);
  children[0].child.emit('exit', 0);
  await setImmediate();
  assert.deepEqual(children[2].args.slice(-3), [
    'dev',
    '--port',
    '5185',
  ]);
  assert.equal(children[0].args.includes('--port'), false);
  assert.equal(children[1].args.includes('--port'), false);
  signals.emit('SIGINT');
  assert.equal(await result, 130);
});

test(
  'native package-manager entrypoints execute without being parsed as JavaScript',
  {
    skip: process.platform === 'win32' ? 'POSIX native executable fixture' : false,
  },
  async () => {
    const exits = [];
    const commands = [];
    const result = await runDevelopment('romd-player-app', {
      // A real native executable accepts the package-manager arguments and exits.
      // Preparation must succeed before the short-lived watcher stops the session.
      pnpmPath: '/usr/bin/true',
      dependenciesInstalled: () => true,
      appArguments: [
        '--port',
        '5185',
      ],
      spawnProcess: (executable, args, options) => {
        commands.push({
          executable,
          args,
        });
        const child = spawn(executable, args, {
          ...options,
          stdio: 'ignore',
        });
        child.once('exit', (code) => exits.push(code));
        return child;
      },
    });
    assert.equal(exits[0], 0, 'native dependency preparation succeeded');
    assert.equal(exits.length, 3, 'preparation started both persistent children');
    assert.equal(result, 1, 'an unexpected watcher exit still fails the session');
    assert.deepEqual(
      commands.map(({ executable }) => executable),
      [
        '/usr/bin/true',
        '/usr/bin/true',
        '/usr/bin/true',
      ],
    );
    assert.deepEqual(
      commands.map(({ args }) => args),
      [
        [
          '--recursive',
          '--filter',
          'romd-player-app^...',
          'exec',
          'tsc',
        ],
        [
          '--recursive',
          '--filter',
          'romd-player-app^...',
          '--parallel',
          'exec',
          'tsc',
          '--watch',
          '--preserveWatchOutput',
        ],
        [
          '--filter',
          'romd-player-app',
          'dev',
          '--port',
          '5185',
        ],
      ],
    );
  },
);

test(
  'shutdown joins a stubborn descendant after its direct parent exits',
  {
    skip: process.platform === 'win32' ? 'POSIX process-group regression' : false,
  },
  async (context) => {
    const directory = await mkdtemp(join(tmpdir(), 'romd-dev-process-test-'));
    const fixture = join(directory, 'pnpm.mjs');
    const childMarker = join(directory, 'descendant.pid');
    const parentMarker = join(directory, 'watcher.pid');
    const code = `
    import { spawn } from 'node:child_process';
    import { writeFileSync } from 'node:fs';
    if (process.argv.includes('--watch')) {
      const childCode = ${JSON.stringify(`
        require('node:fs').writeFileSync(${JSON.stringify(childMarker)}, String(process.pid));
        process.on('SIGTERM', () => {});
        setInterval(() => {}, 1000);
      `)};
      spawn(process.execPath, ['-e', childCode], { stdio: 'ignore' });
      writeFileSync(${JSON.stringify(parentMarker)}, String(process.pid));
      process.on('SIGTERM', () => process.exit(0));
      setInterval(() => {}, 1000);
    } else if (process.argv.includes('dev')) {
      setInterval(() => {}, 1000);
    }
  `;
    await writeFile(fixture, code);
    const signals = new EventEmitter();
    let watcherPid;
    let descendantPid;
    const result = runDevelopment('romd-player-app', {
      pnpmPath: fixture,
      signalSource: signals,
      dependenciesInstalled: () => true,
    });
    context.after(async () => {
      signals.emit('SIGTERM');
      await result;
      if (watcherPid) {
        try {
          process.kill(-watcherPid, 'SIGKILL');
        } catch (error) {
          if (error.code !== 'ESRCH') throw error;
        }
      }
      await rm(directory, {
        recursive: true,
        force: true,
      });
    });
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      try {
        descendantPid = Number(await readFile(childMarker, 'utf8'));
        watcherPid = Number(await readFile(parentMarker, 'utf8'));
        break;
      } catch (error) {
        if (error.code !== 'ENOENT') throw error;
      }
      await setTimeout(10);
    }
    assert.ok(descendantPid, 'owned stubborn descendant started');
    signals.emit('SIGTERM');
    assert.equal(await result, 143);
    assert.throws(() => process.kill(descendantPid, 0), {
      code: 'ESRCH',
    });
  },
);

test(
  'repeated real shutdown signals still join owned descendants',
  {
    skip: process.platform === 'win32' ? 'POSIX signal regression' : false,
  },
  async (context) => {
    const directory = await mkdtemp(join(tmpdir(), 'romd-dev-signals-test-'));
    const fixture = join(directory, 'pnpm.mjs');
    const runnerFile = join(directory, 'runner.mjs');
    const marker = join(directory, 'descendant.pid');
    const groupMarker = join(directory, 'group.pid');
    await writeFile(
      fixture,
      `
      import { spawn } from 'node:child_process';
      import { writeFileSync } from 'node:fs';
      if (process.argv.includes('--watch')) {
        spawn(process.execPath, ['-e', ${JSON.stringify(`
          process.on('SIGTERM', () => {});
          require('node:fs').writeFileSync(${JSON.stringify(marker)}, String(process.pid));
          setInterval(() => {}, 1000);
        `)}], { stdio: 'ignore' });
        writeFileSync(${JSON.stringify(groupMarker)}, String(process.pid));
        process.on('SIGTERM', () => process.exit(0));
        setInterval(() => {}, 1000);
      } else if (process.argv.includes('dev')) {
        setInterval(() => {}, 1000);
      }
    `,
    );
    await writeFile(
      runnerFile,
      `
      import { runDevelopment } from ${JSON.stringify(new URL('./dev.mjs', import.meta.url).href)};
      process.exitCode = await runDevelopment('romd-player-app', {
        pnpmPath: ${JSON.stringify(fixture)},
        dependenciesInstalled: () => true,
      });
    `,
    );
    const runner = spawn(
      process.execPath,
      [
        runnerFile,
      ],
      {
        stdio: 'ignore',
      },
    );
    const exited = new Promise((resolve) =>
      runner.once('exit', (code, signal) =>
        resolve({
          code,
          signal,
        }),
      ),
    );
    let group;
    let descendant;
    context.after(async () => {
      runner.kill('SIGKILL');
      await exited;
      if (group) {
        try {
          process.kill(-group, 'SIGKILL');
        } catch (error) {
          if (error.code !== 'ESRCH') throw error;
        }
      }
      await rm(directory, {
        recursive: true,
        force: true,
      });
    });
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      try {
        descendant = Number(await readFile(marker, 'utf8'));
        group = Number(await readFile(groupMarker, 'utf8'));
        break;
      } catch (error) {
        if (error.code !== 'ENOENT') throw error;
      }
      await setTimeout(10);
    }
    assert.ok(descendant, 'owned descendant started');
    runner.kill('SIGTERM');
    await setTimeout(50);
    runner.kill('SIGTERM');
    runner.kill('SIGINT');
    runner.kill('SIGHUP');
    assert.deepEqual(await exited, {
      code: 143,
      signal: null,
    });
    assert.throws(() => process.kill(descendant, 0), {
      code: 'ESRCH',
    });
  },
);
