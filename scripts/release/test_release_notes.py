import unittest

from release_notes import render


class ReleaseNotesTests(unittest.TestCase):
    def test_qualified_page_leads_with_installation_and_four_explicit_assets(self):
        page = render('owner/repo', 'v1.2.3-rc.1', 'a' * 40, 'mame0289.tar.gz', qualified=True)
        self.assertLess(page.index('Download and install'), page.index('Verification and source'))
        for name in ('romd-v1.2.3-rc.1-deployment.tar.gz', 'romd-v1.2.3-rc.1-deployment.tar.gz.sha256',
                     'romd-v1.2.3-rc.1-attestations.jsonl', 'mame0289.tar.gz'):
            self.assertIn('/releases/download/v1.2.3-rc.1/' + name + ')', page)
        self.assertNotIn('verification.tar.gz', page)
        self.assertIn('gh attestation verify romd-v1.2.3-rc.1-deployment.tar.gz '
                      '--bundle romd-v1.2.3-rc.1-attestations.jsonl --repo owner/repo', page)
        self.assertIn('Qualified prerelease', page)
        self.assertIn('fresh-installation baseline', page)
        self.assertIn('source-only and experimental', page)
        self.assertIn('Stable promotion remains a separate', page)

    def test_draft_page_never_claims_completed_qualification(self):
        page = render('owner/repo', 'v1.2.3', 'a' * 40, 'mame0289.tar.gz')
        self.assertIn('Draft · qualification in progress', page)
        self.assertIn('has not passed all release gates', page)
        self.assertNotIn('This exact release passed', page)
        self.assertNotIn('Qualified prerelease', page)

    def test_promoted_page_names_its_qualified_candidate(self):
        page = render('owner/repo', 'v1.2.3', 'a' * 40, 'mame0289.tar.gz', qualified=True, promoted_from='v1.2.3-rc.4')
        self.assertIn('Promoted without rebuilding from [v1.2.3-rc.4](https://github.com/owner/repo/releases/tag/v1.2.3-rc.4)', page)
        self.assertIn('/releases/download/v1.2.3/romd-v1.2.3-deployment.tar.gz)', page)
        self.assertNotIn('Promoted', render('owner/repo', 'v1.2.3-rc.4', 'a' * 40, 'mame0289.tar.gz', qualified=True))
        for source in ('v1.2.4-rc.1', 'v1.2.3', 'v1.2.3-rc.1)[x](https://evil.test'):
            with self.subTest(source=source), self.assertRaises(ValueError):
                render('owner/repo', 'v1.2.3', 'a' * 40, 'mame0289.tar.gz', promoted_from=source)

    def test_invalid_identity_cannot_inject_markdown_links_or_asset_paths(self):
        for repository, tag, revision, source in (
            ('owner/repo)[injected](https://evil.test)', 'v1.2.3', 'a' * 40, 'source.tar.gz'),
            ('owner/repo', 'v1.2.3;bad', 'a' * 40, 'source.tar.gz'),
            ('owner/repo', 'v1.2.3', 'not-a-commit', 'source.tar.gz'),
            ('owner/repo', 'v1.2.3', 'a' * 40, '../source.tar.gz'),
        ):
            with self.subTest(identity=(repository, tag, revision, source)), self.assertRaises(ValueError):
                render(repository, tag, revision, source)


if __name__ == '__main__':
    unittest.main()
