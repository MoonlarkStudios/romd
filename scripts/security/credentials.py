"""An isolated anonymous environment for registry and attestation verification."""
import json
import os


def anonymous_environment(work):
    # Allow ordinary executable/locale/temp resolution, rather than trying to
    # enumerate every cloud/registry credential variable supported by Go tools.
    # HOME is deliberately absent: no caller home is repurposed or searched.
    allowed = {'PATH', 'PATHEXT', 'SYSTEMROOT', 'WINDIR', 'TMPDIR', 'TMP', 'TEMP',
               'LANG', 'LC_ALL', 'LC_CTYPE', 'TZ', 'SSL_CERT_FILE', 'SSL_CERT_DIR'}
    environment = {key: value for key, value in os.environ.items() if key in allowed}
    base = work / 'anonymous'
    paths = {'DOCKER_CONFIG': base / 'docker', 'XDG_CONFIG_HOME': base / 'config',
             'XDG_CACHE_HOME': base / 'cache', 'XDG_DATA_HOME': base / 'data',
             'XDG_RUNTIME_DIR': base / 'runtime', 'GH_CONFIG_DIR': base / 'github',
             'CLOUDSDK_CONFIG': base / 'gcloud', 'AZURE_CONFIG_DIR': base / 'azure'}
    for key, directory in paths.items():
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        environment[key] = str(directory)
    # A missing Docker config triggers DefaultKeychain's Podman fallback. Supply
    # actual empty auth files at every explicit Docker/containers location.
    auth = json.dumps({'auths': {}}) + '\n'
    (paths['DOCKER_CONFIG'] / 'config.json').write_text(auth)
    for location in (base / 'containers/auth.json', paths['XDG_RUNTIME_DIR'] / 'containers/auth.json',
                     paths['XDG_CONFIG_HOME'] / 'containers/auth.json'):
        location.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        location.write_text(auth)
    environment['REGISTRY_AUTH_FILE'] = str(base / 'containers/auth.json')
    for key in ('AWS_CONFIG_FILE', 'AWS_SHARED_CREDENTIALS_FILE'):
        file = base / key.lower()
        file.touch()
        environment[key] = str(file)
    environment.update(AWS_EC2_METADATA_DISABLED='true', GIT_CONFIG_NOSYSTEM='1',
                       GIT_CONFIG_GLOBAL=os.devnull, GIT_TERMINAL_PROMPT='0')
    return environment
