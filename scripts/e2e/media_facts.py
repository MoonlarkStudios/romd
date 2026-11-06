"""Read-only qualification snapshots of the fresh media graph; no legacy model translation."""
from pathlib import Path
import re


IMMUTABLE_FAMILIES = (
    'logicalRoms', 'files', 'representations', 'outputs', 'manifests', 'tracks', 'members', 'memberTracks',
    'chdParameters', 'rvzParameters', 'chdMetadata', 'creations', 'creationSources', 'cdEncodingObservations',
    'archiveOwners', 'encodingAttempts',
)
OBSERVATION_KEYS = {'logicalInspections': 'Id', 'ps2BootObservations': 'InspectionId', 'cdBootInspections': 'Id'}
IDENTITY_FIELDS = ('ContentSize', 'ContentSha256', 'Sha1', 'Md5', 'Crc32')


def _query(scope):
    return Path(__file__).with_name('media_snapshot.sql').read_text().replace('__REPRESENTATION_SCOPE__', scope)


def whole_snapshot_query(sha1):
    if not isinstance(sha1, str) or not re.fullmatch('[0-9a-f]{40}', sha1):
        raise ValueError('A normalized logical SHA-1 is required for the read-only snapshot')
    return _query('SELECT r.* FROM romd."Representations" r WHERE r."Id" IN ('
                  'SELECT o."RepresentationId" FROM romd."RepresentationOutputs" o JOIN romd."Roms" logical ON logical."Id" = o."RomId" '
                  f'WHERE logical."Sha1" = decode(\'{sha1}\',\'hex\'))')


def cd_snapshot_query():
    return _query('SELECT r.* FROM romd."Representations" r JOIN romd."ReconstructionManifests" m ON m."RepresentationId" = r."Id"')


def postgres_hash(value, byte_count):
    if not isinstance(value, str) or not re.fullmatch(r'\\x[0-9a-f]{' + str(byte_count * 2) + '}', value):
        raise ValueError('Snapshot checksum is not the expected PostgreSQL bytea identity')
    return value[2:]


def _rows_by_key(rows, fields):
    result = {}
    for row in rows:
        if not isinstance(row, dict) or any(field not in row or row[field] is None for field in fields):
            raise ValueError('Snapshot row has no complete identity')
        key = tuple(row[field] for field in fields)
        if key in result:
            raise ValueError('Snapshot repeats a row identity')
        result[key] = row
    return result


def _preserved_rows(before, after, fields, family):
    old, current = _rows_by_key(before, fields), _rows_by_key(after, fields)
    if not old.keys() <= current.keys() or any(current[key] != row for key, row in old.items()):
        raise ValueError(f'{family} lost or changed a retained row')
    return old, current


def assert_verification_growth(before, after, decoder):
    """Require one complete resident-build append with all old facts, selectors and custody intact."""
    if before['schemaVersion'] != after['schemaVersion']:
        raise ValueError('Decoder qualification requires the same fresh baseline, not a database upgrade')
    for family in IMMUTABLE_FAMILIES:
        if before[family] != after[family]:
            raise ValueError(f'{family} changed during decoder verification')
    for family, key in OBSERVATION_KEYS.items():
        # A physical observation is keyed by inspector/profile, independently of the decoder tuple.
        # Reuse the exact observation or append its genuinely new inspector result; never erase/rewrite it.
        old_observations, observations = _preserved_rows(before[family], after[family], (key,), family)
        if len(observations) > len(old_observations) + 1:
            raise ValueError(f'{family} appended more than the single fresh inspection')
    old, current = _preserved_rows(before['verifications'], after['verifications'], ('Id',), 'verifications')
    if not old or len(current) != len(old) + 1:
        raise ValueError('Verification did not append exactly one complete measurement')
    proof = current[next(iter(current.keys() - old.keys()))]
    if (proof['DecoderRid'] != decoder['rid'] or postgres_hash(proof['DecoderUpstreamCommit'], 20) != decoder['upstreamCommit']
            or type(proof['DecoderRecipeVersion']) is not int or proof['DecoderRecipeVersion'] != decoder['recipeVersion']):
        raise ValueError('The appended measurement is not the captured resident decoder')
    representations = _rows_by_key(before['representations'], ('Id',))
    representation = representations.get((proof['RepresentationId'],))
    files = _rows_by_key(before['files'], ('Id',))
    artifact = files.get((representation['FileId'],)) if representation else None
    if artifact is None or (proof['ArtifactSha256'], proof['ArtifactSize']) != (artifact['Sha256'], artifact['Size']):
        raise ValueError('Measurement contradicts the retained physical artifact')
    old_outputs, all_outputs = _preserved_rows(before['verificationOutputs'], after['verificationOutputs'],
                                              ('VerificationId', 'OutputId'), 'verificationOutputs')
    expected = {row['Id']: row for row in before['outputs'] if row['RepresentationId'] == proof['RepresentationId']}
    added = [row for key, row in all_outputs.items() if key not in old_outputs]
    if not expected or len(added) != len(expected) or {row['OutputId'] for row in added} != expected.keys():
        raise ValueError('New verification does not cover every distinct output exactly once')
    for row in added:
        output = expected[row['OutputId']]
        if (row['VerificationId'] != proof['Id'] or row['RepresentationId'] != proof['RepresentationId']
                or any(row[field] != output[field] for field in IDENTITY_FIELDS)):
            raise ValueError('New verification output contradicts its exact logical identity or selector')
    if after['sourcePins'] != 0:
        raise ValueError('Completed verification retains active source custody')
    return proof['Id']


def assert_verification_campaign(before, after, decoder, job_id):
    """Bind the new whole measurement to the finite selection's owned, stopped child attempt."""
    proof_id = assert_verification_growth(before, after, decoder)
    old, current = _preserved_rows(before['verificationAttempts'], after['verificationAttempts'],
                                   ('JobId', 'Number'), 'verificationAttempts')
    added = [row for key, row in current.items() if key not in old]
    if len(added) != 1:
        raise ValueError('Campaign did not record exactly one fresh verification attempt')
    attempt = added[0]
    proof = next(row for row in after['verifications'] if row['Id'] == proof_id)
    if (attempt['Operation'] != 'Verify' or attempt['Outcome'] != 'Recorded'
            or attempt['ResultVerificationId'] != proof_id or attempt['ResultRepresentationId'] != proof['RepresentationId']
            or any(attempt[field] != proof[field] for field in ('DecoderRid', 'DecoderUpstreamCommit', 'DecoderRecipeVersion'))
            or not attempt['StartedAtUtcTicks'] <= attempt['StoppedAtUtcTicks'] <= attempt['CompletedAtUtcTicks']):
        raise ValueError('Campaign attempt does not bind the complete proof, captured decoder and stopped custody')
    _preserved_rows(before['selectionItems'], after['selectionItems'],
                    ('SelectionJobId', 'SubjectKind', 'SubjectId'), 'selectionItems')
    selected = [row for row in after['selectionItems'] if row['SelectionJobId'] == job_id]
    if (len(selected) != 1 or selected[0]['ChildJobId'] != attempt['JobId'] or selected[0]['IsOwned'] is not True
            or selected[0]['SubjectKind'] != 'Representation' or selected[0]['SubjectId'] != proof['RepresentationId']):
        raise ValueError('Campaign did not own exactly the retained representation verification child')
    return proof_id
