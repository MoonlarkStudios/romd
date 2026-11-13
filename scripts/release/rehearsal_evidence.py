"""Bind a disposable rehearsal to the images and payload publication will use."""
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile

from qualify import image_map, verify_bundle, verify_publisher
from suite import digest, verify_suite


ROLES = ('admin', 'consumer', 'worker', 'player')


def verify_rehearsal(path, revision, tag, image_ids, pins):
    if path is None:
        raise ValueError('Publication requires a passed packaged-release rehearsal')
    path = Path(path)
    root = path.parent
    receipt = json.loads(path.read_text())
    if (receipt.get('schemaVersion') != 1 or receipt.get('result') != 'passed'
            or receipt.get('evidenceType') != 'disposable-rehearsal'
            or receipt.get('sourceCommit') != revision or receipt.get('releaseTag') != tag
            or receipt.get('platform') != 'linux/amd64' or receipt.get('imageIds') != image_ids
            or receipt.get('finalImageIds') != image_ids
            or set(image_ids) != set(ROLES) or receipt.get('dependencies') != pins
            or receipt.get('registryCleanup') != 'passed'
            or receipt.get('registryExit') != dict(exitCode=0, oomKilled=False)):
        raise ValueError('Rehearsal does not match the candidate images/source/version/dependencies')
    bundle = root / 'rehearsal-bundle.tar.gz'
    images = root / 'images.env'
    if (receipt.get('bundleSha256') != digest(bundle)
            or receipt.get('suiteSha256') != digest(root / 'packaged/suite.json')
            or receipt.get('images') != image_map(images)):
        raise ValueError('Rehearsal inputs or reports changed after execution')
    args = SimpleNamespace(bundle=bundle, expected_images=images, expected_revision=revision,
                           expected_tag=tag, rehearsal=True, publisher_state=root / 'candidate.json',
                           checksum=root / 'rehearsal-bundle.tar.gz.sha256')
    verify_suite(root / 'packaged', args)
    verify_publisher(bundle, root / 'candidate.json', images, revision, tag)
    with tempfile.TemporaryDirectory(prefix='romd-rehearsal-binding-') as temporary:
        verified = verify_bundle(bundle, root / 'rehearsal-bundle.tar.gz.sha256', images,
                                 Path(temporary) / 'bundle', revision, tag)
    payload = {name: value for name, value in verified['files'].items() if name != 'images.env'}
    if receipt.get('payloadFiles') != payload:
        raise ValueError('Rehearsed installation payload does not match its receipt')
    positive = json.loads((root / 'packaged/positive/qualification.json').read_text())
    for role, identifier in image_ids.items():
        if positive['runningImages']['romd-' + role]['imageId'] != identifier:
            raise ValueError('Rehearsal did not execute the candidate image ID')
    return receipt


def verify_published_bundle(receipt, bundle, images, revision, tag):
    """Only registry names/images.env may change between rehearsal and publication."""
    actual = image_map(images)
    if set(actual) != set(receipt['images']):
        raise ValueError('Published image roles differ from rehearsal')
    for key, reference in actual.items():
        if reference.rsplit('@', 1)[1] != receipt['images'][key].rsplit('@', 1)[1]:
            raise ValueError('Published image digest differs from rehearsal')
    with tempfile.TemporaryDirectory(prefix='romd-published-rehearsal-binding-') as temporary:
        verified = verify_bundle(bundle, Path(str(bundle) + '.sha256'), images,
                                 Path(temporary) / 'bundle', revision, tag)
    if {name: value for name, value in verified['files'].items() if name != 'images.env'} != receipt['payloadFiles']:
        raise ValueError('Published installation payload differs from rehearsal')
