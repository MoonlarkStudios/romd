import copy
import unittest

from media_facts import assert_verification_campaign, assert_verification_growth, cd_snapshot_query, whole_snapshot_query


DECODER = {'rid': 'linux-x64', 'upstreamCommit': 'a' * 40, 'recipeVersion': 2}


def retained_facts():
    facts = {key: [] for key in (
        'logicalRoms', 'files', 'representations', 'outputs', 'manifests', 'tracks', 'members', 'memberTracks',
        'chdParameters', 'rvzParameters', 'chdMetadata', 'creations', 'creationSources', 'cdEncodingObservations',
        'archiveOwners', 'encodingAttempts', 'verificationAttempts', 'selectionItems', 'logicalInspections', 'ps2BootObservations', 'cdBootInspections')}
    facts.update(schemaVersion=72, sourcePins=0, pendingDemand=0)
    identity = {'ContentSize': 2352, 'ContentSha256': '\\x' + 'cc' * 32, 'Sha1': '\\x' + 'dd' * 20,
                'Md5': '\\x' + 'ee' * 16, 'Crc32': '\\x00000000'}
    facts['representations'] = [{'Id': 7, 'FileId': 8, 'Encoding': 'Chd'}]
    facts['files'] = [{'Id': 8, 'Sha256': '\\x' + 'ff' * 32, 'Size': 1024}]
    facts['outputs'] = [dict(Id=11, RepresentationId=7, MemberOrdinal=0, RomId=1, **identity),
                        dict(Id=12, RepresentationId=7, MemberOrdinal=1, RomId=1, **identity)]
    proof = {'Id': 'old', 'RepresentationId': 7, 'DecoderRid': 'linux-x64', 'DecoderUpstreamCommit': '\\x' + 'a' * 40,
             'DecoderRecipeVersion': 1, 'ArtifactSha256': '\\x' + 'ff' * 32, 'ArtifactSize': 1024}
    facts['logicalRoms'] = [{'Id': 1, 'PreferredOutputId': 11}]
    facts['manifests'] = [{'RepresentationId': 7, 'LogicalFrames': 8}]
    facts['members'] = [{'RepresentationId': 7, 'Ordinal': i, 'Path': name} for i, name in enumerate(('one.bin', 'two.bin', 'disc.cue'))]
    facts['memberTracks'] = [{'RepresentationId': 7, 'MemberOrdinal': i, 'TrackNumber': i + 1} for i in range(2)]
    facts['creations'] = [{'Id': 'created', 'RepresentationId': 7, 'VerificationId': 'old', 'RecipeId': 'canonical'}]
    facts['creationSources'] = [{'CreationId': 'created', 'TargetOutputId': row['Id'], 'SourceVerificationId': 'independent-source'} for row in facts['outputs']]
    facts['archiveOwners'] = [{'LayoutId': 3, 'RepresentationId': 7, 'VerificationId': 'old'}]
    facts['encodingAttempts'] = [{'JobId': 'completed-encode', 'Number': 1, 'ResultRepresentationId': 7, 'Operation': 'Encode', 'Outcome': 'Recorded'}]
    facts['verifications'] = [proof]
    facts['verificationOutputs'] = [dict(VerificationId='old', RepresentationId=7, OutputId=row['Id'], **identity)
                                    for row in facts['outputs']]
    return facts


def appended_facts():
    before = retained_facts()
    after = copy.deepcopy(before)
    after['verifications'].append(before['verifications'][0] | {'Id': 'new', 'DecoderRecipeVersion': 2})
    after['verificationOutputs'] += [row | {'VerificationId': 'new'} for row in before['verificationOutputs']]
    after['verificationAttempts'] = [{'JobId': 'child', 'Number': 1, 'Operation': 'Verify', 'Outcome': 'Recorded',
        'ResultRepresentationId': 7, 'ResultVerificationId': 'new', 'DecoderRid': DECODER['rid'],
        'DecoderUpstreamCommit': '\\x' + DECODER['upstreamCommit'], 'DecoderRecipeVersion': DECODER['recipeVersion'],
        'StartedAtUtcTicks': 1, 'StoppedAtUtcTicks': 2, 'CompletedAtUtcTicks': 3}]
    after['selectionItems'] = [{'SelectionJobId': 'campaign', 'SubjectKind': 'Representation', 'SubjectId': 7,
                                'ChildJobId': 'child', 'IsOwned': True}]
    return before, after


class MediaFactEvidenceTests(unittest.TestCase):
    def test_campaign_binds_whole_proof_to_one_owned_stopped_current_decoder_child(self):
        before, after = appended_facts()
        self.assertEqual('new', assert_verification_campaign(before, after, DECODER, 'campaign'))
        mutations = (
            ('verificationAttempts', 'ResultVerificationId', 'old'),
            ('verificationAttempts', 'ResultRepresentationId', 99),
            ('verificationAttempts', 'DecoderRecipeVersion', 1),
            ('verificationAttempts', 'StoppedAtUtcTicks', 4),
            ('selectionItems', 'ChildJobId', 'other-child'),
            ('selectionItems', 'SelectionJobId', 'other-campaign'),
            ('selectionItems', 'SubjectId', 99),
            ('selectionItems', 'SubjectKind', 'LogicalRom'),
            ('selectionItems', 'IsOwned', False),
        )
        for family, key, value in mutations:
            with self.subTest(family=family, key=key):
                changed = copy.deepcopy(after)
                changed[family][0][key] = value
                with self.assertRaises(ValueError):
                    assert_verification_campaign(before, changed, DECODER, 'campaign')
        for family in ('verificationAttempts', 'selectionItems'):
            for rows in ([], after[family] * 2):
                with self.subTest(family=family, count=len(rows)):
                    changed = copy.deepcopy(after)
                    changed[family] = rows
                    with self.assertRaises(ValueError):
                        assert_verification_campaign(before, changed, DECODER, 'campaign')

    def test_complete_new_decoder_measurement_preserves_both_identical_bin_selectors(self):
        before, after = appended_facts()
        self.assertEqual('new', assert_verification_growth(before, after, DECODER))

    def test_old_measurements_ownership_creation_and_layout_cannot_change(self):
        for family in ('logicalRoms', 'representations', 'outputs', 'members', 'memberTracks', 'creations', 'creationSources', 'archiveOwners', 'encodingAttempts', 'verifications', 'verificationOutputs', 'files'):
            with self.subTest(family=family):
                before, after = appended_facts()
                after[family][0]['changed'] = True
                with self.assertRaises(ValueError):
                    assert_verification_growth(before, after, DECODER)
                after[family].pop(0)
                with self.assertRaises(ValueError):
                    assert_verification_growth(before, after, DECODER)
        for family in ('logicalRoms', 'members', 'memberTracks', 'creations', 'creationSources', 'archiveOwners', 'encodingAttempts'):
            with self.subTest(family=family):
                before, after = appended_facts()
                after[family].append({'unexpected': 'replacement'})
                with self.assertRaises(ValueError):
                    assert_verification_growth(before, after, DECODER)

    def test_incomplete_duplicate_or_wrong_logical_output_proof_cannot_pass(self):
        for mutation in ('missing', 'duplicate', 'identity', 'representation'):
            with self.subTest(mutation=mutation):
                before, after = appended_facts()
                if mutation == 'missing':
                    after['verificationOutputs'].pop()
                elif mutation == 'duplicate':
                    after['verificationOutputs'][-1] = dict(after['verificationOutputs'][-2])
                elif mutation == 'identity':
                    after['verificationOutputs'][-1]['ContentSize'] += 1
                else:
                    after['verificationOutputs'][-1]['RepresentationId'] += 1
                with self.assertRaises(ValueError):
                    assert_verification_growth(before, after, DECODER)

    def test_missing_extra_or_wrong_decoder_measurement_cannot_pass(self):
        for mutation in ('missing', 'extra', 'decoder', 'artifact'):
            with self.subTest(mutation=mutation):
                before, after = appended_facts()
                if mutation == 'missing':
                    after['verifications'].pop()
                elif mutation == 'extra':
                    after['verifications'].append(after['verifications'][-1] | {'Id': 'third'})
                elif mutation == 'decoder':
                    after['verifications'][-1]['DecoderRecipeVersion'] = 9
                else:
                    after['verifications'][-1]['ArtifactSize'] += 1
                with self.assertRaises(ValueError):
                    assert_verification_growth(before, after, DECODER)

    def test_raw_observations_can_be_reused_or_appended_but_never_rewritten(self):
        for family, key in (('logicalInspections', 'Id'), ('ps2BootObservations', 'InspectionId'), ('cdBootInspections', 'Id')):
            with self.subTest(family=family):
                before, after = appended_facts()
                before[family] = [{'Id' if key == 'Id' else key: 'boot', 'bytes': 'original'}]
                after[family] = copy.deepcopy(before[family])
                self.assertEqual('new', assert_verification_growth(before, after, DECODER))
                after[family].append({key: 'another-inspector-build', 'bytes': 'same original'})
                self.assertEqual('new', assert_verification_growth(before, after, DECODER))
                after[family].append({key: 'unexpected-extra-inspector', 'bytes': 'not this inspection'})
                with self.assertRaises(ValueError):
                    assert_verification_growth(before, after, DECODER)
                after[family].pop()
                after[family][0]['bytes'] = 'rewritten'
                with self.assertRaises(ValueError):
                    assert_verification_growth(before, after, DECODER)

    def test_changed_baseline_or_unreleased_source_custody_cannot_pass(self):
        for field in ('schemaVersion', 'sourcePins'):
            with self.subTest(field=field):
                before, after = appended_facts()
                after[field] += 1
                with self.assertRaises(ValueError):
                    assert_verification_growth(before, after, DECODER)

    def test_query_scope_refuses_any_unvalidated_logical_hash(self):
        for invalid in ('', 'a' * 39, 'A' * 40, "'; DROP TABLE romd.Roms;--"):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                whole_snapshot_query(invalid)
        self.assertIn('romd."Roms"', whole_snapshot_query('a' * 40))
        self.assertIn('romd."ReconstructionManifests"', cd_snapshot_query())


if __name__ == '__main__':
    unittest.main()
