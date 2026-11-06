import copy
import json
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch
import chd
import chd_cd_upgrade
from chd_cd_upgrade import CdUpgradeQualification, active_verification, process_cpu, unmeasured_bin_formats


class RetainedCdCheckpointTests(unittest.TestCase):
    def test_recovery_tracks_the_owned_child_native_run_and_waits_for_the_selection(self):
        run = object.__new__(CdUpgradeQualification)
        run.step, run.api, run.stack = Mock(), Mock(), Mock()
        run.data, run.settings, run.record = Path('/unused'), SimpleNamespace(convergence_timeout=60), {'checkpoint': {}}
        run.stack.container.side_effect = ['worker-before', 'worker-after']
        run.baseline_facts = {'sourcePins': 1, 'verifications': ['old-complete-proof'], 'verificationOutputs': ['all-old-outputs']}
        run.snapshot = Mock(return_value=run.baseline_facts)
        run.assert_retained_inputs = Mock()
        run.observe_native = Mock(side_effect=lambda observed: observed)
        terminal = {'isTerminal': True, 'phase': 'Completed', 'outcome': 'Completed'}
        run.api.json.side_effect = [{'isTerminal': False, 'phase': 'Verifying'}, terminal]
        child = {'id': 'child', 'jobType': 'media-reconciliation', 'requestMode': 'Subject',
                 'originSelectionJobId': 'selection', 'subject': {'kind': 'Representation', 'id': 'public-representation'}}
        first, growing, fresh = {'runId': 'before', 'cpuTicks': 1}, {'runId': 'before', 'cpuTicks': 2}, {'runId': 'after', 'cpuTicks': 1}
        with patch.object(chd, 'all_jobs', return_value=[child]), \
                patch.object(chd_cd_upgrade, 'active_verification', side_effect=[first, growing, growing, fresh]) as observe, \
                patch.object(chd, 'docker') as docker, patch.object(chd_cd_upgrade.time, 'sleep'):
            self.assertEqual(terminal, run.recover_campaign('selection'))
        self.assertEqual('child', run.record['checkpoint']['childJobId'])
        self.assertTrue(run.record['recovery']['reexecuted'])
        self.assertEqual(fresh, run.record['recovery']['activeVerification'])
        self.assertEqual(['child'] * 4, [call.args[1] for call in observe.call_args_list])
        self.assertEqual(['/api/jobs/child', '/api/jobs/selection'], [call.args[1] for call in run.api.json.call_args_list])
        docker.assert_called_once_with('kill', '-s', 'KILL', 'worker-before', timeout=60)
        run.assert_retained_inputs.assert_called_once_with(growing)

    def test_only_bounded_permitted_source_only_selected_job_arms(self):
        with tempfile.TemporaryDirectory() as root:
            data = Path(root)
            journal = data / 'temp/media-runs-v1/run-0.json'
            journal.parent.mkdir(parents=True)
            staged = data / 'temp/staging-v1'
            staged.mkdir()
            sources = []
            for i in range(3):
                (staged / f'slot-{i}.data').write_bytes(b'held')
                sources.append({'Role': 'source', 'Path': f'/var/lib/romd/temp/staging-v1/slot-{i}.data'})
            record = {'Version': 'romd-media-run/v2', 'JobId': 'job', 'Operation': 'reverify-chd-cd', 'Phase': 'permitted',
                      'RunId': 'run', 'Parent': {'ProcessId': 1}, 'Identity': {'ProcessId': 23}, 'Admission': {'Inode': 41}, 'Staging': sources}
            journal.write_text(json.dumps(record))
            self.assertEqual('run', active_verification(data, 'job')['runId'])
            for override in ({'JobId': 'other'}, {'Operation': 'inspect-chd-cd'}, {'Phase': 'ready'}, {'Parent': None},
                             {'Parent': {'ProcessId': 42}}, {'Identity': None}, {'Admission': None}, {'Staging': sources[:2]}):
                with self.subTest(override=override):
                    journal.write_text(json.dumps(record | override))
                    self.assertIsNone(active_verification(data, 'job'))
            for mutation in ('output', 'traversal', 'symlink', 'oversized'):
                mutated = copy.deepcopy(record)
                if mutation == 'output':
                    mutated['Staging'][0]['Role'] = 'output'
                elif mutation == 'traversal':
                    mutated['Staging'][0]['Path'] = '/var/lib/romd/../foreign'
                elif mutation == 'symlink':
                    (staged / 'alias.data').symlink_to(staged / 'slot-0.data')
                    mutated['Staging'][0]['Path'] = '/var/lib/romd/temp/staging-v1/alias.data'
                journal.write_text(json.dumps(mutated) + (' ' * (1 << 20) if mutation == 'oversized' else ''))
                self.assertIsNone(active_verification(data, 'job'), mutation)

    def test_proc_cpu_does_not_misparse_spaces_or_parentheses_in_comm(self):
        fields = ['R', '1', '23'] + ['0'] * 8 + ['30', '11'] + ['0'] * 8
        self.assertEqual(41, process_cpu(('23 (media (decoder)) ' + ' '.join(fields)).encode()))

    def test_upgrade_requires_each_bin_format_unsupported_without_a_grant(self):
        manifest = {'items': [{'relativePath': 'data', 'isAvailable': False, 'formats': [{'format': 'original', 'state': 'unsupported'}]},
                              {'relativePath': 'audio', 'isAvailable': False, 'formats': [{'format': 'original', 'state': 'unsupported'}]},
                              {'formats': [{'format': 'original', 'state': 'missing'}]}]}
        self.assertTrue(unmeasured_bin_formats(manifest, {'data', 'audio'}))
        for change in ('grant', 'available', 'missing'):
            changed = copy.deepcopy(manifest)
            if change == 'grant':
                changed['items'][0]['formats'][0]['contentGrant'] = {'downloadUrl': 'unsafe'}
            elif change == 'available':
                changed['items'][0]['formats'][0]['state'] = 'available'
            else:
                changed['items'].pop(0)
            self.assertFalse(unmeasured_bin_formats(changed, {'data', 'audio'}))



if __name__ == '__main__':
    unittest.main()
