#!/usr/bin/env python3
"""Publish -> no automatic index work -> offline CLI SQL -> DBA-style execution in an isolated fixture.

Only run against an explicitly authorized isolated local test domain. Generated SQL
is executed by this test harness, never by the CLI/runtime. Existing flows are untouched.
"""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import urllib.error
import urllib.request
import uuid

BASE = os.environ['VNEXT_BASE_URL'].rstrip('/')
DOMAIN = os.environ['VNEXT_TEST_DOMAIN']
PG = os.environ['VNEXT_TEST_PG_CONTAINER']
DATABASE = os.environ['VNEXT_TEST_DATABASE']
CLI = Path(os.environ.get('VNEXT_CLI_ROOT', Path(__file__).resolve().parents[3] / 'vnext-workflow-cli'))
NODE = os.environ.get('NODE_BINARY', 'node')
PREFIX = 'manual-' + uuid.uuid4().hex[:10]
MASTER, FLOW = PREFIX + '-master', PREFIX + '-flow'


def sql(query):
    return subprocess.check_output(['docker', 'exec', '-i', PG, 'psql', '-U', 'postgres', '-d', DATABASE,
                                    '-At', '-v', 'ON_ERROR_STOP=1'], input=query, text=True).strip()


def publish(body):
    request = urllib.request.Request(BASE + '/api/v1/definitions/publish', data=json.dumps(body).encode(),
                                     headers={'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            assert response.status == 200
    except urllib.error.HTTPError as error:
        raise AssertionError(error.read().decode()) from error


def component(kind, key, attributes, version):
    return {'domain': DOMAIN, 'flow': kind, 'flowVersion': '1.0.0', 'key': key, 'version': version,
            'tags': ['manual-index-test'], 'attributes': attributes}


def main():
    if os.environ.get('VNEXT_ALLOW_ISOLATED_TEST_DDL') != 'true':
        raise SystemExit('Requires VNEXT_ALLOW_ISOLATED_TEST_DDL=true and an isolated local test domain.')
    before = sql("SELECT count(*) FROM sys_queues.\"BackgroundJobs\" WHERE \"HandlerName\"='attribute-index.prepare';")
    version = '1.0.0-pkg.1.0.0'
    master = component('sys-schemas', MASTER, {'type': 'workflow', 'schema': {'type': 'object', 'properties': {
        'amount': {'type': 'number', 'x-indexed': True, 'x-filterOperators': ['gte']},
        'name': {'type': 'string', 'x-indexed': True, 'x-filterOperators': ['contains']}}}}, version)
    flow = component('sys-flows', FLOW, {
        'type': 'F', 'labels': [{'language': 'en-US', 'label': 'Manual index test'}],
        'schema': {'domain': DOMAIN, 'flow': 'sys-schemas', 'key': MASTER, 'version': 'latest'},
        'startTransition': {'key': 'start', 'target': 'ready', 'versionStrategy': 'Minor',
                            'labels': [{'language': 'en-US', 'label': 'Start'}]},
        'states': [{'labels': [{'language': 'en-US', 'label': 'Ready'}], 'key': 'ready', 'stateType': 1,
                    'subType': 0, 'versionStrategy': 'Minor', 'onEntries': [], 'onExits': [], 'transitions': []}]
    }, version)
    publish(master)
    publish(flow)
    schema = FLOW.replace('-', '_')
    assert sql(f"SELECT count(*) FROM information_schema.columns WHERE table_schema='{schema}' AND table_name='InstancesData' AND is_generated='ALWAYS';") == '0'
    assert sql("SELECT count(*) FROM sys_queues.\"BackgroundJobs\" WHERE \"HandlerName\"='attribute-index.prepare';") == before
    print('PASS indexed Master/workflow publication creates no index jobs or projections', flush=True)
    with tempfile.TemporaryDirectory(prefix='vnext-manual-indexes-') as folder:
        root = Path(folder)
        (root/'components'/'Schemas').mkdir(parents=True)
        (root/'components'/'Workflows').mkdir(parents=True)
        (root/'vnext.config.json').write_text(json.dumps({'domain': DOMAIN, 'paths': {
            'componentsRoot': 'components', 'workflows': 'Workflows', 'schemas': 'Schemas'}}))
        (root/'components'/'Schemas'/'master.json').write_text(json.dumps(master))
        (root/'components'/'Workflows'/'flow.json').write_text(json.dumps(flow))
        subprocess.run([NODE, str(CLI/'bin'/'workflow.js'), 'indexes', 'generate', '--flow', FLOW,
                        '--output', 'sql'], cwd=root, check=True)
        generated = next((root/'sql').glob('*/*.sql')).read_text()
        assert sql(f"SELECT count(*) FROM information_schema.columns WHERE table_schema='{schema}' AND table_name='InstancesData' AND is_generated='ALWAYS';") == '0'
        print('PASS CLI only writes SQL; database is unchanged', flush=True)
        sql(generated)  # Explicit DBA-style execution by test harness, confined to its unique flow.
        assert sql(f'SELECT count(*) FROM "{schema}"."AttributeIndexCatalog" WHERE "Ready";') == '3'
        oids = sql(f"SELECT indexrelid FROM pg_index WHERE indrelid='\"{schema}\".\"InstancesData\"'::regclass ORDER BY indexrelid;")
        sql(generated)
        assert sql(f"SELECT indexrelid FROM pg_index WHERE indrelid='\"{schema}\".\"InstancesData\"'::regclass ORDER BY indexrelid;") == oids
        print('PASS DBA execution prepares catalog, replay preserves indexes', flush=True)
        master['version'] = '1.0.0-pkg.1.0.1'
        master['attributes']['schema']['properties']['another'] = {'type': 'integer', 'x-indexed': True}
        publish(master)
        assert sql(f'SELECT count(*) FROM "{schema}"."AttributeIndexCatalog" WHERE "Ready";') == '3'
        assert sql("SELECT count(*) FROM sys_queues.\"BackgroundJobs\" WHERE \"HandlerName\"='attribute-index.prepare';") == before
        print('PASS subsequent Master publication leaves index maintenance to DBA', flush=True)
    print(json.dumps({'result': 'passed', 'checks': 4, 'base_url': BASE, 'domain': DOMAIN, 'prefix': PREFIX}))


if __name__ == '__main__':
    main()
