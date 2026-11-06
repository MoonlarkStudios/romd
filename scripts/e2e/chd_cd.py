#!/usr/bin/env python3
"""PS1 production recipe qualification through shipped Compose. Private originals/DAT stay outside Git.

Uses the existing strict qualification JSON; select one data-only or mixed disc with --fixture.
Reuses stock ingress, process observations, ordinary lease recovery and project-scoped cleanup from chd.py.
No SQL mutation, accelerated leases, native mounts, worker entrypoint replacement or emulator claims.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import re
import time
import uuid
import xml.etree.ElementTree as ET
import zipfile
import chd
import media_facts


class CdQualification(chd.Qualification):
    encoding_operation = 'encode-chd-cd'

    def __init__(self, settings, fixture, config):
        self.fixture, self.config = fixture, config
        self.source_at_start = chd.source_revision()
        self.identities = {}
        subject = chd.Subject('ps1', 'cd', 'CD', 0, {}, {}, provenance={'fixture': fixture['Name'], 'datSha256': fixture['DatSha256']})
        super().__init__(settings, subject)
        self.record['checkpoint']['definition'] = chd.ENCODING_DESCRIPTION.replace('encode-chd ', 'encode-chd-cd ')
        self.record['scope'] = 'Linux x64 PS1 storage and delivery; no emulator/gameplay qualification.'

    def prepare(self):
        super().prepare()
        fixture = self.fixture
        directory = Path(fixture['FixtureDirectory']).resolve(strict=True)
        dat = Path(fixture['DatPath']).resolve(strict=True)
        chd.require(chd.digest_file(dat)['sha256'] == fixture['DatSha256'], 'official DAT changed identity')
        document = ET.parse(dat)
        entries = [game for game in document.getroot().findall('game') if game.get('name') == fixture['DatEntryName']]
        chd.require(len(entries) == 1, 'fixture has no unique DAT entry')
        game = entries[0]
        cue = fixture['CueName']
        names = re.findall(r'^\s*FILE "([^"\r\n]+)" BINARY\s*$', (directory / cue).read_text(), re.MULTILINE)
        chd.require(names and len(set(names)) == len(names) and len(names) <= 99, 'invalid bounded CUE file set')
        chd.require(not fixture.get('Sidecars'), 'this production capture does not admit sidecars yet')
        mapping = fixture.get('DatBinNames') or {name: name for name in names}
        for name in [*names, cue]:
            chd.require(Path(name).name == name and name not in ('.', '..') and not any(c in name for c in '/\\\x00'), 'unsafe original filename')
            identity = chd.digest_file(directory / name)
            if name != cue:
                expected = [row for row in game.findall('rom') if row.get('name') == mapping.get(name)]
                chd.require(len(expected) == 1, 'BIN lacks an explicit DAT binding')
                wanted = {'size': int(expected[0].get('size')), 'sha1': expected[0].get('sha1'), 'md5': expected[0].get('md5'), 'crc32': expected[0].get('crc')}
                chd.require(not chd.identity_mismatches('original vs DAT', wanted, identity, tuple(wanted)), 'original differs from official DAT')
            self.identities[name] = identity
        inputs = self.folder / 'inputs'
        root = ET.Element('datafile')
        if document.getroot().find('header') is not None:
            root.append(document.getroot().find('header'))
        root.append(game)
        ET.ElementTree(root).write(inputs / 'ps1.dat', encoding='utf-8', xml_declaration=True)
        with zipfile.ZipFile(inputs / 'originals.zip', 'w', compression=zipfile.ZIP_STORED) as archive:
            for name in [*names, cue]:
                archive.write(directory / name, name)
        chd.readable_tree(inputs)
        self.record['originals'] = self.identities

    def import_catalog(self):
        self.step('importing the exact PS1 DAT entry through normal DAT intake')
        job = chd.wait_for_job(self.api, chd.upload(self.api, '/api/upload/dat?systemKey=psx', '/e2e/inputs/ps1.dat', 600), 1800)
        chd.require(not job.get('hasErrors') and job.get('datsSucceeded') == 1, 'PS1 DAT intake failed')

    def upload_disc(self):
        self.step('uploading the complete original CUE/BIN archive through normal intake')
        upload_id = chd.upload(self.api, '/api/upload/rom', '/e2e/inputs/originals.zip', self.settings.upload_timeout)
        job = chd.wait_for_job(self.api, upload_id, self.settings.upload_timeout)
        chd.require(not job.get('hasErrors'), f'original intake failed: {job.get("errors")}')
        self.upload_items = chd.job_items(self.api, upload_id)
        layouts = chd.paged_items(self.api, '/api/original-disc-layouts', systemKey='psx')
        layouts = [row for row in layouts if row.get('sourceJobId') == upload_id]
        chd.require(len(layouts) == 1, 'upload did not retain one original CUE layout')
        self.layout = layouts[0]
        chd.require(all(row.get('sha256') for row in self.layout['dependencies']), 'original dependencies were not journal-bound')
        reply = self.api.json(chd.ADMIN_HOST, f'/api/media/reconciliation/subjects/OriginalLayout/{self.layout["id"]}', method='POST', expect=(202,),
                              payload={'requestId': str(uuid.uuid4()), 'conversionRequested': True})
        self.convergence_id = reply['jobId']
        self.record.update(uploadJobId=upload_id, layoutId=self.layout['id'], convergenceJobId=self.convergence_id)

    def snapshot(self):
        query = media_facts.cd_snapshot_query()
        return json.loads(self.stack.compose('exec', '-T', 'postgres', 'psql', '-U', 'romd_bootstrap', '-d', 'romd',
            '-At', '-v', 'ON_ERROR_STOP=1', '-c', query, capture=True).stdout)

    def capture_archive(self, facts):
        chd.require(len(facts['representations']) == len(facts['archiveOwners']) == len(facts['manifests']) == 1,
                    'publication does not retain one complete layout-owned CD interpretation')
        representation = facts['representations'][0]
        chd.require(representation['Encoding'] == 'Chd', 'retained CD is not a CHD interpretation')
        artifact = [row for row in facts['files'] if row['Id'] == representation['FileId']]
        chd.require(len(artifact) == 1, 'retained CD has no exact physical artifact')
        self.cd_artifact = {'sha256': media_facts.postgres_hash(artifact[0]['Sha256'], 32), 'size': artifact[0]['Size']}
        chd.require(len(facts['creations']) == len(facts['cdEncodingObservations']) == 1,
                    'CD publication lost independent creation provenance')
        creation, encoding = facts['creations'][0], facts['cdEncodingObservations'][0]
        boot = [row for row in facts['cdBootInspections'] if row['Id'] == encoding['BootInspectionId']]
        chd.require(creation['RecipeId'] == self.settings.recipe and encoding['CreationId'] == creation['Id']
                    and facts['archiveOwners'][0]['VerificationId'] == creation['VerificationId']
                    and any(row['Id'] == creation['VerificationId'] for row in facts['verifications']) and len(boot) == 1
                    and boot[0]['Profile'] == 'ps1-cd-root-exe/v1', 'published recipe, boot observation and archive owner disagree')
        members = facts['members']
        chd.require(len(members) == len(self.identities) and {row['Path'] for row in members} == self.identities.keys(),
                    'publication lost or invented original members')
        for member in members:
            actual = {'size': member['ContentSize'], 'sha256': media_facts.postgres_hash(member['ContentSha256'], 32),
                      'sha1': media_facts.postgres_hash(member['Sha1'], 20), 'md5': media_facts.postgres_hash(member['Md5'], 16),
                      'crc32': media_facts.postgres_hash(member['Crc32'], 4)}
            chd.require(not chd.identity_mismatches('published original', self.identities[member['Path']], actual),
                        'published original identity differs from the exact supplied bytes')
        self.retained_facts = facts
        self.record['archive'] = self.cd_artifact | {'recipe': self.settings.recipe}

    def assert_recovered(self):
        job = self.final_job
        chd.require(job.get('phase') == 'Completed' and job.get('outcome') == 'Completed' and not job.get('hasErrors'),
                    f'CD recovery ended in {job.get("phase")}, {job.get("outcome")}, {job.get("outcomeCode")}')
        chd.require(chd.fresh_encoding(self.record['checkpoint']['activeEncoding'], self.record['recovery'].get('activeEncoding')), 'no fresh permitted CD encode observed after recreation')
        self.wait_for_released_custody()
        facts = self.snapshot()
        row = {'archives': len(facts['archiveOwners']), 'outputs': len(facts['outputs']), 'sources': facts['sourcePins'],
               'journal': sum(row['JobId'] == self.convergence_id for row in facts['encodingAttempts'])}
        chd.require(row == {'archives': 1, 'outputs': len(self.identities) - 1, 'sources': 0, 'journal': 1},
                    'publication/source ledger is incomplete or duplicated')
        self.capture_archive(facts)
        self.record['recovery']['reexecuted'] = True
        self.record['ledger'] = row

    def assert_serving_admission(self):
        self.roms = []
        for item in self.upload_items:
            if item.get('kind') != 'rom' or not item.get('romFileId') or Path(item.get('fileName','')).suffix.lower() != '.bin':
                continue
            rom = self.api.json(chd.ADMIN_HOST, f'/api/roms/{item["romFileId"]}')
            formats = {entry['format']: entry for entry in rom['formats']}
            self.record.setdefault('perBinFormats', []).append({'romId':item['romFileId'],'formats':formats})
            original = formats.get('original') or {}
            names = [name for name, identity in self.identities.items() if name.lower().endswith('.bin') and identity['sha256'] == original.get('sha256')]
            chd.require(len(names) == 1 and original.get('state') == 'available' and (formats.get('chd') or {}).get('state') != 'available', 'per-BIN delivery leaked its container or lost the original')
            self.roms.append((item['romFileId'],names[0]))
            self.title_id = next((match['titleId'] for match in rom.get('matches') or [] if match.get('titleId')), None)
        chd.require(len(self.roms) == len(self.identities)-1 and self.title_id, 'not every original BIN has a catalog owner')
        status, _, body = self.api.call(chd.ADMIN_HOST, '/api/libraries', expect=(201,), payload={'name':self.run_id,'configuration':{'includeUntrackedTitles':True,'contentRatingPolicy':{'unknownRatingPolicy':'Allow'}}})
        library = json.loads(body)['id']
        self.api.call(chd.ADMIN_HOST, f'/api/libraries/{library}/materialize', method='POST', expect=(202,))
        user = self.api.json(chd.ADMIN_HOST, '/api/me')['id']
        self.api.call(chd.ADMIN_HOST, f'/api/users/{user}/library', method='PUT', payload={'libraryId':library})
        self.api.forget(chd.ADMIN_HOST)
        deadline = time.monotonic()+600
        while True:
            status, _, body = self.api.call(chd.CONSUMER_HOST, f'/api/titles/{self.title_id}', expect=None)
            if status == 200:
                title = json.loads(body)
                break
            chd.require(time.monotonic()<deadline and status in (403,404,503), 'consumer library did not publish')
            time.sleep(2)
        releases = title.get('releases') or []
        chd.require(len(releases) == 1, 'expected one fixture release')
        self.release_id = releases[0]['id']

    def download_formats(self):
        downloads = []
        for rom_id, name in self.roms:
            observed = self.api.download(chd.ADMIN_HOST, f'/api/roms/{rom_id}/download?format=original')
            chd.require(not chd.identity_mismatches('admin original',self.identities[name],observed), 'admin reconstructed BIN differs from original/DAT')
            downloads.append({'member':name,'lane':'admin-original',**observed})
        manifest = self.api.json(chd.CONSUMER_HOST, f'/api/releases/{self.release_id}/manifest', method='POST')
        count = 0
        for item in manifest['items']:
            option = next((entry for entry in item.get('formats') or [] if entry.get('format') == 'original' and entry.get('state') == 'available'), None)
            if not option:
                continue
            names = [name for name, identity in self.identities.items() if name.lower().endswith('.bin') and identity['sha256'] == option.get('sha256')]
            if not names:
                continue
            grant = option['contentGrant']
            observed = self.api.download(chd.CONSUMER_HOST,grant['downloadUrl'],authorized=False)
            chd.require(not chd.identity_mismatches('consumer original',self.identities[names[0]],observed), 'consumer reconstructed BIN differs from original/DAT')
            count += 1
            downloads.append({'member':names[0],'lane':'consumer-original',**observed})
        chd.require(count == len(self.roms), 'consumer did not grant every original BIN')
        admin = self.api.download(chd.ADMIN_HOST, f'/api/original-disc-layouts/{self.layout["id"]}/archive')
        consumer = self.api.download(chd.CONSUMER_HOST, f'/api/releases/{self.release_id}/cd-archive')
        chd.require(admin['size'] == consumer['size'] == self.cd_artifact['size'] and admin['sha256'] == consumer['sha256'] == self.cd_artifact['sha256'], 'whole-container download differs from independent publication evidence')
        self.record['downloads'] = downloads + [{'lane':'admin-archive',**admin},{'lane':'consumer-archive',**consumer}]
        cue_name = self.fixture['CueName']
        for host, route in ((chd.ADMIN_HOST, f'/api/original-disc-layouts/{self.layout["id"]}/archive/cue'),
                            (chd.CONSUMER_HOST, f'/api/releases/{self.release_id}/cd-archive/cue')):
            original_cue = self.api.download(host, route)
            chd.require(not chd.identity_mismatches('retained original CUE',self.identities[cue_name],original_cue), 'original CUE changed or became a catalog template')
            self.record['downloads'].append({'lane':'original-cue',**original_cue})
        for name, expected in self.identities.items():
            chd.require(chd.digest_file(Path(self.fixture['FixtureDirectory']) / name) == expected, 'supplied original changed during Compose qualification')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',required=True,type=Path)
    parser.add_argument('--fixture',required=True)
    parser.add_argument('--root',required=True,type=Path)
    parser.add_argument('--evidence',required=True,type=Path)
    parser.add_argument('--image-prefix',default='romd-ps1-production')
    args = parser.parse_args()
    config = json.loads(args.config.read_bytes())
    matches = [item for item in config['Inputs'] if item['Name']==args.fixture]
    chd.require(len(matches)==1 and config['FixtureKind']=='real','select one exact real-fixture configuration')
    args.root.mkdir(mode=0o700,parents=True,exist_ok=True)
    root = args.root.resolve(strict=True)
    chd.require(str(root).startswith('/tmp/') and not root.is_symlink(),'use a resolved private disposable root under /tmp')
    refs = {name:f'{args.image_prefix}:{name}' for name in chd.IMAGE_SERVICES}
    nginx = json.loads((chd.ROOT/'scripts/deployment/base-images.json').read_text())['ingress']
    settings = chd.Settings(False,root,None,args.evidence.parent,refs,'chd-ps1-canonical/v1',nginx,0,1800,1800,'encoding','127.0.0.2',True)
    qualification = CdQualification(settings,matches[0],config)
    try:
        qualification.run()
    finally:
        args.evidence.parent.mkdir(parents=True,exist_ok=True)
        args.evidence.write_text(json.dumps({'source':qualification.source_at_start,'sourceAtFinish':chd.source_revision(),**qualification.record},indent=2)+'\n')
    print('PS1 Compose qualification passed:',args.evidence)

if __name__=='__main__':
    main()
