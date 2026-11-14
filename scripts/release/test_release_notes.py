import unittest

from release_notes import render


class ReleaseNotesTests(unittest.TestCase):
    def test_qualified_page_leads_with_installation_and_five_explicit_assets(self):
        page = render('owner/repo', 'v1.2.3-rc.1', 'a' * 40, 'mame0289.tar.gz', qualified=True)
        self.assertLess(page.index('Download and install'), page.index('Verification and source'))
        for name in ('romd-v1.2.3-rc.1-deployment.tar.gz', 'romd-v1.2.3-rc.1-deployment.tar.gz.sha256',
                     'romd-v1.2.3-rc.1-verification.tar.gz', 'romd-v1.2.3-rc.1-verification.tar.gz.sha256',
                     'mame0289.tar.gz'):
            self.assertIn('/releases/download/v1.2.3-rc.1/' + name + ')', page)
        self.assertIn('Qualified prerelease', page)
        self.assertIn('fresh-installation baseline', page)
        self.assertIn('source-only and experimental', page)
        self.assertIn('Stable promotion remains a separate', page)

    def test_unqualified_page_never_claims_completed_qualification(self):
        page = render('owner/repo', 'v1.2.3', 'a' * 40, 'mame0289.tar.gz')
        self.assertIn('Unqualified prerelease', page)
        self.assertIn('has not passed all release gates', page)
        self.assertNotIn('This exact release passed', page)
        self.assertNotIn('/romd-v1.2.3-verification.tar.gz)', page)
        self.assertIn('Full verification details will be linked here after qualification passes.', page)

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
