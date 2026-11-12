"""Fresh exact-binary evidence for the official PostgreSQL image's gosu helper."""
from datetime import datetime, timezone
import hashlib
import json

from go_applicability import DB, affected_version, digest, stream, timestamp, version_key
from go_reader import fetch


def require(condition, message):
    if not condition:
        raise ValueError('PostgreSQL helper evidence: ' + message)


def primary_matches(scanned, primary):
    # govulncheck v1.9.0's internal/osv.Entry does not serialize OSV `related`.
    # Those are links to different vulnerabilities, not aliases or affected ranges.
    # Retain the complete primary record; permit only this documented projection.
    comparable = dict(primary)
    if 'related' not in scanned and 'related' in comparable:
        related = comparable.pop('related')
        if not isinstance(related, list) or not all(isinstance(value, str) and value for value in related):
            return False
    return comparable == scanned


def validate(raw, binary_sha256, policy, subject_digest, tag, scanner_version, index):
    events = stream(raw)
    configs = [event['config'] for event in events if 'config' in event]
    sboms = [event['SBOM'] for event in events if 'SBOM' in event]
    require(len(configs) == len(sboms) == 1 and set(events[0]) == {'config'}, 'helper config/SBOM coverage missing')
    config, sbom = configs[0], sboms[0]
    require(config.get('protocol_version') == 'v1.0.0' and config.get('scanner_name') == 'govulncheck'
            and config.get('scanner_version') == scanner_version and config.get('db') == DB
            and config.get('scan_level') == 'symbol' and config.get('scan_mode') == 'binary', 'helper scanner configuration mismatch')
    require(timestamp(config.get('db_last_modified')) == timestamp(index.get('modified')), 'helper Go database index mismatch')
    require(sbom.get('roots') == ['github.com/tianon/gosu'] and isinstance(sbom.get('modules'), list), 'helper binary is not gosu')
    versions = {item['path']: item['version'] for item in sbom['modules']}
    require(len(versions) == len(sbom['modules']) and versions.get('stdlib') == sbom.get('go_version'), 'helper module inventory incomplete')
    osvs, findings = {}, []
    for event in events:
        require(len(event) == 1 and set(event) <= {'config', 'SBOM', 'progress', 'osv', 'finding'}, 'unsupported helper scan event')
        if 'osv' in event:
            osv = event['osv']
            require(osv['id'] not in osvs or osvs[osv['id']] == osv, 'conflicting helper primary records')
            osvs[osv['id']] = osv
        if 'finding' in event:
            finding = event['finding']
            require(isinstance(finding.get('trace'), list) and len(finding['trace']) == 1, 'unsupported helper binary finding')
            frame = finding['trace'][0]
            module = frame.get('module')
            require(module in versions and version_key(module, versions[module]) == version_key(module, frame.get('version')),
                    'helper finding module/version mismatch')
            findings.append(finding)
    require(all(finding['osv'] in osvs for finding in findings), 'helper finding lacks a primary record')
    for review in policy['assessments']:
        if review['subjectDigest'] != subject_digest or review['releaseTag'] != tag or not review['component'].startswith('pkg:golang/'):
            continue
        proof = review.get('reviewEvidence', {})
        require(proof.get('binarySha256') == binary_sha256, 'helper review is not bound to the extracted binary')
        osv = osvs.get(proof.get('primaryOsv'), {})
        require(review['id'] in osv.get('aliases', []) and not osv.get('withdrawn')
                and osv.get('database_specific', {}).get('review_status') == 'REVIEWED', 'helper review lacks current reviewed primary evidence')
        module = review['component'].removeprefix('pkg:golang/').split('@')[0]
        require(module in versions and version_key(module, versions[module]) == version_key(module, review['installedVersion']),
                'helper review version differs from the binary')
        affected = [item for item in osv.get('affected', []) if item.get('package', {}).get('name') == module
                    and affected_version(item, versions[module].replace('go', 'v', 1))]
        imports = [item for affected_module in affected for item in affected_module.get('ecosystem_specific', {}).get('imports', [])]
        require(imports and all(item.get('symbols') for item in imports), 'helper primary record lacks affected-symbol coverage')
        matching = [finding for finding in findings if finding['osv'] == osv['id'] and finding['trace'][0]['module'] == module]
        other_platform = all(item.get('goos') and 'linux' not in item['goos'] for item in imports)
        require(other_platform or matching and not any(finding['trace'][0].get('function') for finding in matching),
                'helper review contradicts fresh symbol findings')
    return osvs


def scan(binary, artifact, policy, subject_digest, tag, run):
    index = fetch(DB + '/index/db.json')
    raw = run([artifact / 'govulncheck', '-db=' + DB, '-mode=binary', '-scan=symbol', '-format=json', binary],
              'go-helper-govulncheck-binary-symbol')
    version = json.loads((artifact / 'govulncheck-module.json').read_text())['Version']
    result = {'binarySha256': digest(binary), 'scannerSha256': digest(artifact / 'govulncheck'),
              'checkedAt': datetime.now(timezone.utc).isoformat(), 'database': index, 'rawJsonStream': raw,
              'scanSha256': hashlib.sha256(raw.encode()).hexdigest()}
    # Keep the raw stream even when validation fails; the caller retains result.
    try:
        osvs = validate(raw, result['binarySha256'], policy, subject_digest, tag, version, index)
        result['primaryRecords'] = {}
        for identifier, osv in osvs.items():
            primary = fetch(DB + '/ID/' + identifier + '.json')
            result['primaryRecords'][identifier] = primary
            require(primary_matches(osv, primary), 'helper scanner and fresh primary record differ: ' + identifier)
    except (ValueError, KeyError, TypeError) as error:
        result['failure'] = str(error)
    return result
