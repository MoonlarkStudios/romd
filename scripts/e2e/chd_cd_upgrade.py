#!/usr/bin/env python3
"""Qualify retained PS1 CD decoder upgrades and active verification recovery on shipped Compose.

Requires genuine recipe-1 baseline and current recipe-2 images, built from source.
No SQL writes, shortened execution leases, injected delays, native mounts or entrypoint changes.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path, PurePosixPath
import re
import time
import uuid
import chd
import media_facts
from chd_cd import CdQualification


def active_verification(data, job_id):
    """A bounded permitted source-only run with PID 1 as parent; no phase-only checkpoint."""
    for entry in chd._entries(data / 'temp' / 'media-runs-v1'):
        try:
            if not re.fullmatch(r'run-\d+\.json', entry.name) or not entry.is_file(follow_symlinks=False) or entry.stat(follow_symlinks=False).st_size > 1 << 20:
                continue
            record = json.loads(Path(entry.path).read_bytes())
            if (record.get('Version') != 'romd-media-run/v2' or record.get('JobId') != job_id or record.get('Operation') != 'reverify-chd-cd'
                    or record.get('Phase') != 'permitted' or record.get('Parent', {}).get('ProcessId') != 1
                    or not isinstance(record.get('Identity'), dict) or not isinstance(record.get('Admission'), dict)):
                continue
            bindings = record.get('Staging') or []
            if not 3 <= len(bindings) <= 19 or any(binding.get('Role') != 'source' for binding in bindings):
                continue
            inputs = []
            for binding in bindings:
                relative = PurePosixPath(binding['Path']).relative_to(chd.DATA_TARGET)
                if '..' in relative.parts:
                    raise ValueError('unsafe staged input')
                path = data.joinpath(*relative.parts)
                if path.is_symlink() or not path.is_file() or path.stat().st_size <= 0:
                    raise ValueError('missing staged input')
                inputs.append({'path': str(relative), 'bytes': path.stat().st_size})
            return {'record': entry.name, 'runId': record['RunId'], 'parent': record['Parent'], 'child': record['Identity'],
                    'admission': record['Admission'], 'inputs': inputs}
        except (OSError, ValueError, KeyError, TypeError, AttributeError):
            continue
    return None


def process_cpu(stat):
    # comm can contain spaces or parentheses; the remaining fields start with state (field 3).
    fields = stat.rsplit(b') ', 1)[1].split()
    return int(fields[11]) + int(fields[12])  # utime/stime, fields 14/15


def unmeasured_bin_formats(manifest, wanted_paths):
    selected = [item for item in manifest.get('items') or [] if item.get('relativePath') in wanted_paths]
    originals = [option for item in selected for option in item.get('formats') or [] if option.get('format') == 'original']
    return (bool(wanted_paths) and {item['relativePath'] for item in selected} == wanted_paths and
            len(originals) == len(wanted_paths) and all(not item.get('isAvailable') and not item.get('contentGrant') for item in selected) and
            all(option.get('state') == 'unsupported' and not option.get('contentGrant') for option in originals))


class CdUpgradeQualification(CdQualification):
    def __init__(self, settings, fixture, config, current_refs):
        super().__init__(settings, fixture, config)
        self.current_refs = current_refs
        self.record['checkpoint'] = {'name': 'retained-cd-verification', 'definition': 'permitted fresh native decoder with growing CPU time, then worker SIGKILL and ordinary lease recovery'}

    def observe_native(self, observed):
        pid = observed['child'].get('ProcessId')
        if type(pid) is not int or pid <= 1:
            return None
        try:
            stat = self.stack.exec('romd-worker', 'cat', f'/proc/{pid}/stat').stdout
            fields = stat.rsplit(b') ', 1)[1].split()
            if fields[19].decode() != observed['child'].get('Birth') or int(fields[2]) != observed['child'].get('GroupId'):
                return None
            maps = self.stack.exec('romd-worker', 'cat', f'/proc/{pid}/maps').stdout
            if (chd.LIBCHDR_DIRECTORY + '/native/libmoonlark_chdr.so').encode() not in maps:
                return None
            return {**observed, 'cpuTicks': process_cpu(stat), 'nativeMapped': True}
        except (chd.HarnessError, IndexError, ValueError):
            return None  # A reaped child never arms the checkpoint.

    def assert_retained_inputs(self, observed):
        candidates = []
        for source in observed['inputs']:
            if source['bytes'] <= 512 << 10:
                try:
                    path = self.data / source['path']
                    value = json.loads(path.read_bytes())
                    if isinstance(value, dict) and value.get('profile') == 'chd-cd-retained-files/v1':
                        candidates.append((source, value))
                except (ValueError, UnicodeError):
                    pass
        chd.require(len(candidates) == 1, 'retained native run has no unique bounded request')
        request, spec = candidates[0]
        chd.require(spec['artifactSha256'] == self.cd_artifact['sha256'] and spec['artifactBytes'] == self.cd_artifact['size'],
                    'retained request changed artifact identity')
        stored_paths = {str(PurePosixPath(f['path']).relative_to(chd.DATA_TARGET)) for f in spec['storedFiles']}
        remaining = [f for f in observed['inputs'] if f['path'] != request['path'] and f['path'] not in stored_paths]
        chd.require(len(stored_paths) == 1 and len(remaining) == 1 and remaining[0]['bytes'] == spec['artifactBytes'] and len(observed['inputs']) == 3,
                    'native verification staged original BINs or omitted retained dependencies')
        chd.require(len(spec['members']) == len(self.identities), 'native request omitted whole original members')

    def recover_campaign(self, campaign_id):
        self.step('waiting for active retained-CD native verification before SIGKILL')
        worker = self.stack.container('romd-worker')
        previous = None
        deadline = time.monotonic() + self.settings.convergence_timeout
        while True:
            children = [job for job in chd.all_jobs(self.api, 'media-reconciliation')
                        if job.get('originSelectionJobId') == campaign_id and job.get('requestMode') == 'Subject'
                        and (job.get('subject') or {}).get('kind') == 'Representation']
            chd.require(len(children) <= 1, 'bounded verification selected more than one child')
            if children:
                child_id = children[0]['id']
                break
            chd.require(time.monotonic() < deadline, 'finite selection did not create its verification child')
            time.sleep(0.05)
        self.record['checkpoint']['childJobId'] = child_id
        while True:
            observed = active_verification(self.data, child_id)
            if observed:
                observed = self.observe_native(observed)
            if observed and previous and previous['runId'] == observed['runId'] and observed['cpuTicks'] > previous['cpuTicks']:
                break
            if observed:
                previous = observed
            chd.require(time.monotonic() < deadline, 'missed active native verification checkpoint')
            time.sleep(0.025)
        self.assert_retained_inputs(observed)
        chd.docker('kill', '-s', 'KILL', worker, timeout=60)
        job = self.api.json(chd.ADMIN_HOST, f'/api/jobs/{child_id}')
        held = active_verification(self.data, child_id)
        chd.require(not job.get('isTerminal') and job.get('phase') == 'Verifying' and held and held['runId'] == observed['runId'],
                    'MISSED active verification: item completed or custody changed before SIGKILL')
        facts = self.snapshot()
        chd.require(facts['sourcePins'] == 1 and facts['verifications'] == self.baseline_facts['verifications']
                    and facts['verificationOutputs'] == self.baseline_facts['verificationOutputs'], 'interrupted item lost its pin or published a partial proof')
        self.record['checkpoint'].update(killedUtc=chd.iso(chd.utc_now()), activeVerification=observed, retainedVerification=held, job=job)
        self.stack.forget('romd-worker')
        self.stack.compose('up', '-d', '--wait', '--wait-timeout', '600', '--force-recreate', '--no-deps', 'romd-worker')
        chd.require(self.stack.container('romd-worker') != worker, 'worker was restarted without container recreation')
        self.record['recovery'] = {'restartedUtc': chd.iso(chd.utc_now()), 'ordinaryExecutionLease': True}
        self.step('worker recreated; waiting for ordinary lease redelivery and a fresh native verification')
        deadline = time.monotonic() + self.settings.convergence_timeout
        while True:
            observed_after = active_verification(self.data, child_id)
            if observed_after and observed_after['runId'] != observed['runId']:
                active = self.observe_native(observed_after)
                if active:
                    self.record['recovery']['activeVerification'] = active
            job = self.api.json(chd.ADMIN_HOST, f'/api/jobs/{campaign_id}')
            if job.get('isTerminal'):
                break
            chd.require(time.monotonic() < deadline, 'retained-CD campaign did not recover before timeout')
            time.sleep(0.15)
        chd.require('activeVerification' in self.record['recovery'], 'no fresh permitted native verification observed after recreation')
        self.record['recovery'].update(terminalUtc=chd.iso(chd.utc_now()), reexecuted=True)
        return job

    def run(self):
        failure = None
        try:
            pin, recipe = chd.current_decoder_source()
            chd.require(recipe == 2, 'this qualification is the genuine x64 recipe-1 to recipe-2 upgrade')
            self.prepare(); self.assert_startup_failure(); self.start(); self.assert_activation(decoder_source=(pin, 1))
            self.record['baselineDecoder'] = self.record['decoder']
            chd.require(self.record['decoder']['rid'] == 'linux-x64', 'PS1 production decoder qualification is x64 only')
            self.import_catalog(); self.upload_disc()
            self.final_job = chd.wait_for_job(self.api, self.convergence_id, self.settings.convergence_timeout)
            chd.require(self.final_job.get('phase') == 'Completed' and self.final_job.get('outcome') == 'Completed', 'baseline production encode failed')
            self.wait_for_released_custody()
            before = self.snapshot()
            chd.require(len(before['archiveOwners']) == 1 and len(before['verifications']) == 1 and before['sourcePins'] == 0, 'baseline is not one retained CD archive')
            self.capture_archive(before)
            self.baseline_facts = before
            self.assert_serving_admission(); self.download_formats()
            self.record['baselineDownloads'] = self.record.pop('downloads')
            baseline_manifest = self.api.json(chd.CONSUMER_HOST, f'/api/releases/{self.release_id}/manifest', method='POST')
            wanted_hashes = {identity['sha256'] for name, identity in self.identities.items() if name.lower().endswith('.bin')}
            wanted_paths = {item['relativePath'] for item in baseline_manifest['items'] if any(
                option.get('format') == 'original' and option.get('sha256') in wanted_hashes for option in item.get('formats') or [])}
            chd.require(len(wanted_paths) == len(self.roms), 'baseline consumer did not identify every original member path')
            self.record['baselineFacts'] = before
            self.step('upgrading worker, admin and consumer to current x64 decoder using retained data')
            self.stack.env.update(chd.image_environment(self.current_refs))
            self.stack.compose('up', '-d', '--wait', '--wait-timeout', '600', '--force-recreate', '--no-deps', *chd.HOSTS)
            for host in chd.HOSTS:
                self.stack.forget(host)
            self.assert_activation(); self.api.forget(chd.ADMIN_HOST); self.api.forget(chd.CONSUMER_HOST)
            chd.require(self.record['decoder']['rid'] == self.record['baselineDecoder']['rid'], 'native architecture changed in the x64 upgrade')
            for rom_id, _ in self.roms:
                rom = self.api.json(chd.ADMIN_HOST, f'/api/roms/{rom_id}')
                formats = {entry['format']: entry for entry in rom['formats']}
                chd.require(formats['original']['state'] == 'unsupported' and not formats['original'].get('contentGrant'), 'unmeasured upgraded decoder advertised a BIN')
            release = self.api.json(chd.CONSUMER_HOST, f'/api/releases/{self.release_id}/manifest', method='POST')
            self.record['beforeCampaignConsumerManifest'] = release
            chd.require(unmeasured_bin_formats(release, wanted_paths), 'consumer advertised unmeasured BINs or omitted an original')
            closed = []
            for host, route in ((chd.ADMIN_HOST, f'/api/original-disc-layouts/{self.layout["id"]}/archive'),
                                (chd.CONSUMER_HOST, f'/api/releases/{self.release_id}/cd-archive')):
                status, _, _ = self.api.call(host, route, expect=None)
                chd.require(status in (404, 409, 503), 'unmeasured upgraded decoder exposed a raw CD archive')
                closed.append({'host': host, 'status': status})
            path = '/api/media/reconciliation/selections'
            self.record['beforeReverification'] = {'allBinFormats': 'unsupported', 'rawArchives': closed, 'retainedRepresentations': 1}
            payload = {'requestId': str(uuid.uuid4()), 'filter': 'RetainedRepresentations', 'limit': 1, 'conversionRequested': False}
            response = self.api.json(chd.ADMIN_HOST, path, method='POST', expect=(202,), payload=payload)
            campaign = self.recover_campaign(response['jobId'])
            chd.require_verified_selection(campaign)
            replay = self.api.json(chd.ADMIN_HOST, path, method='POST', expect=(202,), payload=payload)
            chd.require(replay['jobId'] == response['jobId'], 'idempotent acceptance created another campaign')
            after = self.snapshot()
            verification_id = media_facts.assert_verification_campaign(before, after, self.record['decoder'], response['jobId'])
            self.wait_for_released_custody(); self.download_formats(); self.assert_malformed_isolation()
            self.record.update(status='passed', campaign={'jobId': response['jobId'], 'verifiedArchives': 1, 'decoder': self.record['decoder'], 'verificationId': verification_id,
                'immutableCreationEvidence': True, 'sourcePinsReleased': True, 'noOriginalBinsStaged': True}, upgradedFacts=after)
        except BaseException as error:
            failure = error; self.record.update(status='failed', error=f'{type(error).__name__}: {error}'); raise
        finally:
            with chd.interrupts_ignored():
                cleanup_error = self.teardown(failed=failure is not None)
            self.record['finishedUtc'] = chd.iso(chd.utc_now())
            if cleanup_error and failure is None:
                raise chd.HarnessError(cleanup_error)
        return self.record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('config', 'root', 'evidence'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--fixture', required=True)
    parser.add_argument('--baseline-image-prefix', required=True)
    parser.add_argument('--image-prefix', required=True)
    args = parser.parse_args()
    config = json.loads(args.config.read_bytes())
    matches = [item for item in config['Inputs'] if item['Name'] == args.fixture]
    chd.require(len(matches) == 1 and config['FixtureKind'] == 'real', 'select one exact real PS1 fixture')
    args.root.mkdir(mode=0o700, parents=True, exist_ok=True)
    root = args.root.resolve(strict=True)
    chd.require(str(root).startswith('/tmp/') and not args.root.is_symlink(), 'use a private disposable root under /tmp')
    refs = lambda prefix: {name: f'{prefix}:{name}' for name in chd.IMAGE_SERVICES}
    old, new = refs(args.baseline_image_prefix), refs(args.image_prefix)
    chd.require(all(chd.image_id(ref) for ref in (*old.values(), *new.values())), 'genuine baseline/current image sets are required')
    nginx = json.loads((chd.ROOT / 'scripts/deployment/base-images.json').read_text())['ingress']
    settings = chd.Settings(False, root, None, args.evidence.parent, old, 'chd-ps1-canonical/v1', nginx, 0, 1800, 1800, 'encoding', '127.0.0.2', True)
    qualification = CdUpgradeQualification(settings, matches[0], config, new)
    chd.install_interrupt_handlers()
    try:
        qualification.run()
    finally:
        args.evidence.parent.mkdir(parents=True, exist_ok=True)
        args.evidence.write_text(json.dumps({'source': qualification.source_at_start, 'sourceAtFinish': chd.source_revision(), **qualification.record}, indent=2) + '\n')
    print('Retained PS1 CD upgrade/recovery passed:', args.evidence)


if __name__ == '__main__':
    main()
