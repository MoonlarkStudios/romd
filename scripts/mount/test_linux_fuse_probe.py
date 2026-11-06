import json
import unittest
import linux_fuse_probe
import linux_fuse_probe_container as container


def passing_report():
    owner = dict(reads=dict(exact=True), sequential_sha256_matches=True, copy_exact=True,
                 mutations=[dict(action='create', denied=True)])
    return dict(mounted={'/private': True, '/open': True}, owner={'/private': owner, '/open': dict(owner)},
                access=[dict(user='bob', denied=True), dict(user='root', denied=True)],
                widen=dict(refused=True), cleanup_verified=True)


class LinuxFuseProbeTests(unittest.TestCase):
    def test_verdict_passes_only_when_every_expectation_holds(self):
        self.assertTrue(container.verdict(passing_report()))
        for breakage in (
                lambda r: r['access'][0].update(denied=False),
                lambda r: r['owner']['/open']['reads'].update(exact=False),
                lambda r: r['owner']['/open']['mutations'][0].update(denied=False),
                lambda r: r['widen'].update(refused=False),
                lambda r: r['mounted'].update({'/open': False}),
                lambda r: r.update(cleanup_verified=False),
                lambda r: r['owner'].pop('/open'),
                lambda r: r.update(access=[])):
            report = passing_report()
            breakage(report)
            self.assertFalse(container.verdict(report))

    def test_verdict_fails_on_partial_report_after_error(self):
        self.assertFalse(container.verdict(dict(error='TimeoutError: fixture', cleanup_verified=True)))

    def test_parse_report_reads_marker_line_among_container_output(self):
        output = 'apt noise\n' + container.MARKER + json.dumps(dict(passed=True)) + '\ntrailing\n'
        self.assertEqual(dict(passed=True), linux_fuse_probe.parse_report(output))
        with self.assertRaisesRegex(ValueError, 'no report'):
            linux_fuse_probe.parse_report('no marker here')


if __name__ == '__main__':
    unittest.main()
