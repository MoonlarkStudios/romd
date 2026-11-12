"""Publish ROMD deployment decisions, without asserting upstream images safe globally."""
from datetime import datetime, timezone


def document(evaluations, bundle_digest, tag):
    groups = {}
    for evaluation in evaluations:
        for finding in evaluation['findings']:
            groups.setdefault(finding['id'], []).append(finding)
    statements = []
    # Product scope includes the exact deployment bundle, not just a library PURL.
    product = 'urn:romd:deployment:sha256:' + bundle_digest
    for identifier, findings in sorted(groups.items()):
        statement = dict(vulnerability={'name': identifier}, products=[{'@id': product,
            'subcomponents': [{'@id': component} for component in sorted({item['component'] for item in findings})]}])
        risks = [item['riskAcceptance'] for item in findings if item.get('riskAcceptance')]
        reviews = [item['applicability'] for item in findings if item.get('applicability', {}).get('status') == 'not_affected']
        if risks:
            statement.update(status='affected', action_statement=' '.join(sorted({
                f"Risk accepted only for {review['releaseTag']} until {review['expires']} UTC: "
                + review['mitigations'] + ' ' + review['followUp'] for review in risks})))
        elif len(reviews) == len(findings):
            statement.update(status='not_affected', impact_statement=' '.join(sorted({
                review['rationale'] + f" Review expires {review['expires']} UTC; scoped to the standard {tag} deployment."
                for review in reviews})))
        else:
            statement['status'] = 'under_investigation'
        statements.append(statement)
    return {'@context': 'https://openvex.dev/ns/v0.2.0',
            '@id': 'https://github.com/MoonlarkStudios/romd/releases/tag/' + tag + '#vex-' + bundle_digest,
            'author': 'https://github.com/MoonlarkStudios', 'role': 'Document Creator',
            'timestamp': datetime.now(timezone.utc).isoformat(), 'version': 1, 'statements': statements}
