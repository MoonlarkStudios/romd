#!/usr/bin/env python3
"""Qualify a recipe-1 to recipe-2 CHD upgrade on disposable data and stock host entrypoints.

Requires explicit existing baseline/current image tags. Baseline images must carry
genuine recipe-1 builds; no database evidence is fabricated or rewritten.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import time
import uuid

import chd
import media_facts


class UpgradeQualification(chd.Qualification):
    def __init__(self, settings, subject, current_refs):
        super().__init__(settings, subject)
        self.current_refs = current_refs
        self.record['checkpoint'] = {'name': 'decoder-upgrade', 'definition': 'retained recipe-1 CHD, fail-closed recipe-2 reads, explicit bounded re-verification'}

    def snapshot(self):
        query = media_facts.whole_snapshot_query(self.subject.identity['sha1'])
        result = self.stack.compose('exec', '-T', 'postgres', 'psql', '-U', 'romd_bootstrap', '-d', 'romd',
                                    '-v', 'ON_ERROR_STOP=1', '-At', '-c', query, capture=True)
        return json.loads(result.stdout)

    def settle_baseline(self):
        deadline, quiet_since, previous = time.monotonic() + 300, None, None
        while True:
            jobs = chd.all_jobs(self.api)
            active = [job for job in jobs if not job.get('isTerminal')]
            facts = self.snapshot()
            if not active and facts == previous and facts['pendingDemand'] == 0:
                quiet_since = quiet_since or time.monotonic()
                if time.monotonic() - quiet_since >= 15:
                    self.record['baselineJobs'] = jobs
                    return facts
            else:
                quiet_since = None
            chd.require(time.monotonic() < deadline, 'baseline ingestion/convergence did not settle before the decoder upgrade')
            previous = facts
            time.sleep(2)

    def run(self):
        failure = None
        try:
            pin, recipe = chd.current_decoder_source()
            chd.require(recipe == 2, 'this qualifier requires the explicit native recipe 1 -> 2 transition')
            self.prepare()
            self.assert_startup_failure()
            self.start()
            self.assert_activation(decoder_source=(pin, 1))
            self.record['baselineDecoder'] = self.record['decoder']
            self.import_catalog()
            self.upload_disc()
            job = chd.wait_for_job(self.api, self.convergence_id, self.settings.convergence_timeout)
            chd.require(job.get('phase') == 'Completed' and job.get('outcome') == 'Completed', 'baseline convergence failed')
            self.wait_for_released_custody()
            self.assert_serving_admission()
            self.download_formats()
            self.record['baselineDownloads'] = self.record.pop('downloads')
            before = self.settle_baseline()
            self.record['baselineFacts'] = before
            chd.require(len(before['representations']) == 1 and before['representations'][0]['Encoding'] == 'Chd',
                        'baseline must retain exactly one preferred CHD, without an original fallback')
            self.step('recreating worker, admin and consumer with the current decoder and the same retained data')
            self.stack.env.update(chd.image_environment(self.current_refs))
            self.stack.compose('up', '-d', '--wait', '--wait-timeout', '600', '--force-recreate', '--no-deps', *chd.HOSTS)
            for host in chd.HOSTS:
                self.stack.forget(host)
            self.assert_activation()
            chd.require(self.record['baselineDecoder']['rid'] == self.record['decoder']['rid'],
                        'the recipe-upgrade qualifier requires the same native RID on both sides')
            self.record['upgradedImages'] = {host: self.stack.inspect(host)['Image'] for host in chd.HOSTS}
            self.api.forget(chd.ADMIN_HOST)
            self.api.forget(chd.CONSUMER_HOST)
            _, admin_formats = self.admin_formats()
            _, consumer_formats = self.consumer_formats()
            self.record['beforeCampaignJobs'] = chd.all_jobs(self.api)
            self.record['beforeCampaignFacts'] = self.snapshot()
            self.record['beforeCampaignFormats'] = {host: {kind: (formats.get(kind) or {}).get('state') for kind in ('original', 'chd')}
                                                   for host, formats in (('admin', admin_formats), ('consumer', consumer_formats))}
            for formats in (admin_formats, consumer_formats):
                for kind in ('original', 'chd'):
                    chd.require((formats.get(kind) or {}).get('state') == 'unsupported', f'new decoder format {kind} has state {(formats.get(kind) or {}).get("state")!r}, expected unsupported')
                    chd.require(not (formats.get(kind) or {}).get('contentGrant'), 'new decoder granted an unmeasured retained format')
            self.record['beforeReverification'] = {'formats': 'unsupported on admin and consumer', 'retainedRepresentations': 1}
            self.step('starting one finite verify-only retained-representation selection through the normal admin API')
            response = self.api.json(chd.ADMIN_HOST, '/api/media/reconciliation/selections', method='POST',
                payload={'requestId': str(uuid.uuid4()), 'filter': 'RetainedRepresentations', 'limit': 1, 'conversionRequested': False}, expect=(202,))
            campaign = chd.wait_for_job(self.api, response['jobId'], self.settings.convergence_timeout)
            chd.require_verified_selection(campaign)
            self.wait_for_released_custody()
            after = self.snapshot()
            verification_id = media_facts.assert_verification_campaign(before, after, self.record['decoder'], response['jobId'])
            for formats in (self.admin_formats()[1], self.consumer_formats()[1]):
                chd.require(all((formats.get(kind) or {}).get('state') == 'available' for kind in ('original', 'chd')),
                            'current decoder delivery was not restored')
                chd.require(formats['chd'].get('sha256') == self.chd['sha256'], 're-verification changed the advertised artifact')
            self.download_formats()
            self.record['campaign'] = {'jobId': response['jobId'], 'decoder': self.record['decoder'], 'verificationId': verification_id,
                                       'verified': 1, 'pending': 0, 'historicalEvidencePreserved': True,
                                       'artifactAndPreferenceUnchanged': True, 'sourcePinsReleased': True}
            self.record['status'] = 'passed'
        except BaseException as error:
            failure = error
            self.record.update(status='failed', error=f'{type(error).__name__}: {error}')
            raise
        finally:
            with chd.interrupts_ignored():
                cleanup_error = self.teardown(failed=failure is not None)
            self.record['finishedUtc'] = chd.iso(chd.utc_now())
            if cleanup_error and failure is None:
                raise chd.HarnessError(cleanup_error)
        return self.record


def main(argv=None):
    preliminary = argparse.ArgumentParser(add_help=False)
    preliminary.add_argument('--baseline-image-tag')
    baseline, remaining = preliminary.parse_known_args(sys.argv[1:] if argv is None else argv)
    args = chd.parse_args(remaining)
    chd.require(baseline.baseline_image_tag and re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}', baseline.baseline_image_tag),
                '--baseline-image-tag must name an existing genuine recipe-1 image set')
    chd.require(args.image_tag, '--image-tag must name the existing current stock images')
    temporary = None
    if args.synthetic and not os.environ.get('ROMD_E2E_ROOT'):
        temporary = root = Path(tempfile.mkdtemp(prefix='romd-chd-upgrade-')).resolve()
        fixtures = None
    elif args.synthetic:
        root, fixtures = chd.canonical_directory(os.environ, 'ROMD_E2E_ROOT'), None
    else:
        fixtures, root = chd.real_paths(os.environ)
    chd.require(not chd.within(root, chd.ROOT) and not chd.within(chd.ROOT, root),
                "ROMD_E2E_ROOT must be outside the repository checkout")
    evidence = chd.resolve_evidence(args.evidence, None if temporary else root, 'upgrade', fixtures)
    summary = {'schema': 'romd-e2e-chd-upgrade/v1', 'mode': 'synthetic' if args.synthetic else 'real',
               'status': 'running', 'source': chd.source_revision(), 'runs': [], 'startedUtc': chd.iso(chd.utc_now())}
    chd.install_interrupt_handlers()
    result = 1
    try:
        summary['environment'] = chd.docker_environment()
        old_refs, new_refs = chd.image_refs(baseline.baseline_image_tag), chd.image_refs(args.image_tag)
        chd.require(all(chd.image_id(ref) for ref in (*old_refs.values(), *new_refs.values())), 'required baseline/current images are missing')
        settings = chd.Settings(args.synthetic, root, fixtures, evidence.parent if evidence else None, old_refs,
                                chd.expected_recipe(chd.RECIPE_SOURCE.read_text()), chd.test_environment()['ROMD_NGINX_IMAGE'],
                                args.synthetic_mib << 20, 60 * (args.convergence_timeout or 60),
                                60 * (args.upload_timeout or 90), https_bind=args.https_bind, image_defaults=args.image_defaults)
        labels = ['synthetic'] if args.synthetic else (args.disc or ['ffx', 'gt4'])
        for label in labels:
            required = chd.synthetic_required_bytes(settings.synthetic_bytes) if args.synthetic else chd.required_free_bytes(None)
            chd.require(shutil.disk_usage(root).free >= required, 'insufficient free space for the upgrade qualification')
            subject = chd.synthetic_subject() if args.synthetic else chd.real_subject(fixtures, chd.DISCS[label])
            qualification = UpgradeQualification(settings, subject, new_refs)
            summary['runs'].append(qualification.record)
            qualification.run()
            chd.write_evidence(summary, evidence)
        summary['status'], result = 'passed', 0
    except BaseException as error:
        summary.update(status='failed', error=f'{type(error).__name__}: {error}')
        print(summary['error'], file=sys.stderr)
    finally:
        summary['finishedUtc'] = chd.iso(chd.utc_now())
        chd.write_evidence(summary, evidence)
        if temporary:
            # Each run tears down its own project first; never discard a retained failed run folder.
            if not any(temporary.rglob('compose.e2e.yaml')):
                shutil.rmtree(temporary)
        print(f"CHD decoder upgrade: {summary['status']}; evidence: {evidence}", flush=True)
    return result


if __name__ == '__main__':
    raise SystemExit(main())
