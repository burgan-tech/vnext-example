#!/usr/bin/env python3
"""Real-host regression: publication -> durable Aether job -> Dapr -> generated indexes.

Requires an isolated local worktree runtime. SQL is used for assertions and a held
read lock only; all definitions/data are published through the HTTP API.
"""
import json
import os
import subprocess
import time
import urllib.error
import urllib.request
import uuid

BASE = os.environ['VNEXT_BASE_URL'].rstrip('/')
DOMAIN = os.environ['VNEXT_TEST_DOMAIN']
PG = os.environ['VNEXT_TEST_PG_CONTAINER']
DATABASE = os.environ['VNEXT_TEST_DATABASE']
PREFIX = 'auto-' + uuid.uuid4().hex[:10]
MASTER = PREFIX + '-master'
LATEST = PREFIX + '-latest'
PINNED = PREFIX + '-pinned'


def sql(query):
    return subprocess.check_output(['docker', 'exec', PG, 'psql', '-U', 'postgres', '-d', DATABASE,
                                    '-At', '-v', 'ON_ERROR_STOP=1', '-c', query], text=True).strip()


def publish(kind, key, version, attributes, data=None, allow_failure=False):
    body = dict(domain=DOMAIN, flow=kind, key=key, version=version, flowVersion='1.0.0', tags=[], attributes=attributes)
    if data is not None:
        body['data'] = data
    req = urllib.request.Request(BASE + '/api/v1/definitions/publish', data=json.dumps(body).encode(),
                                 headers={'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=60) as response:
            return response.status
    except urllib.error.HTTPError as error:
        if error.code not in (400, 409) and not allow_failure:
            raise AssertionError(error.read().decode()) from error
        print('HTTP', error.code, error.read().decode(), flush=True)
        return error.code


def master(indexed, description=''):
    return {'type': 'workflow', 'schema': {'type': 'object', 'description': description, 'properties': {
        'amount': {'type': 'number', 'x-indexed': indexed, 'x-sortable': True, 'x-filterOperators': ['gte']},
        'name': {'type': 'string', 'x-indexed': indexed, 'x-filterOperators': ['contains']}}}}


def workflow(version):
    return {'type': 'F', 'labels': [{'language': 'en-US', 'label': 'Auto index test'}],
            'schema': {'domain': DOMAIN, 'flow': 'sys-schemas', 'key': MASTER, 'version': version},
            'startTransition': {'key': 'start', 'target': 'ready', 'versionStrategy': 'Minor',
                                'labels': [{'language': 'en-US', 'label': 'Start'}]},
            'states': [{'labels': [{'language': 'en-US', 'label': 'Ready'}], 'key': 'ready', 'stateType': 1, 'subType': 0, 'versionStrategy': 'Minor',
                        'onEntries': [], 'onExits': [], 'transitions': []}]}


def jobs_finished():
    rows = json.loads(sql('SELECT coalesce(json_agg(t), \'[]\'::json) FROM '
                         '(SELECT "Status", "RetryCount", "LastError" FROM sys_queues."BackgroundJobs" '
                         'WHERE "HandlerName" = \'attribute-index.prepare\') t'))
    assert not any(row['Status'] == 3 for row in rows), rows
    return rows and all(row['Status'] == 2 for row in rows)


def wait(predicate, timeout=45):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if predicate():
            return
        time.sleep(0.25)
    raise AssertionError('Timed out waiting for automatic preparation')


def projections(flow):
    return int(sql("SELECT count(*) FROM information_schema.columns WHERE table_schema='" +
                   flow.replace('-', '_') + "' AND table_name='InstancesData' AND is_generated='ALWAYS'"))


def main():
    v1 = '1.0.0-pkg.1.0.0'
    v2 = '1.0.0-pkg.1.0.1'
    assert publish('sys-schemas', MASTER, v1, master(False)) == 200
    seed = [{'key': 'seed', 'version': '1.0.0', 'attributes': {'amount': 12, 'name': 'İstanbul'}}]
    assert publish('sys-flows', LATEST, v1, workflow('latest')) == 200
    assert publish('sys-flows', PINNED, v1, workflow(v1)) == 200
    wait(jobs_finished)
    assert projections(LATEST) == projections(PINNED) == 0
    print('PASS unindexed publication produces no generated columns')

    assert publish('sys-schemas', MASTER, v2, master(True)) == 200
    wait(jobs_finished)
    assert projections(LATEST) == 3
    assert projections(PINNED) == 0
    physical = LATEST.replace('-', '_')
    assert int(sql(f'SELECT count(*) FROM "{physical}"."AttributeIndexCatalog" WHERE "Ready"')) == 3
    assert int(sql(f"SELECT count(*) FROM pg_indexes WHERE schemaname='{physical}' AND indexname LIKE '%_trgm'")) == 1
    print('PASS Master package update prepares latest reference; full revision stays pinned')

    # Keep a read lock while publishing an equivalent physical definition. Taking ACCESS EXCLUSIVE
    # on this path would time out; a real no-op finishes without retries while the lock remains held.
    lock = subprocess.Popen(['docker', 'exec', '-i', PG, 'psql', '-U', 'postgres', '-d', DATABASE,
                             '-At', '-v', 'ON_ERROR_STOP=1'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True)
    try:
        lock.stdin.write(f'BEGIN; LOCK TABLE "{physical}"."InstancesData" IN ACCESS SHARE MODE; SELECT \'locked\';\n')
        lock.stdin.flush()
        while lock.stdout.readline().strip() != 'locked':
            assert lock.poll() is None
        assert publish('sys-schemas', MASTER, '1.0.0-pkg.1.0.2', master(True, 'description only')) == 200
        wait(jobs_finished, timeout=15)
        assert sql('SELECT count(*) FROM sys_queues."BackgroundJobs" WHERE "HandlerName" = \'attribute-index.prepare\' AND "RetryCount" > 0') == '0'
        print('PASS equivalent publication completes while a conflicting read lock is held (no DDL/retry)')
    finally:
        lock.stdin.write('ROLLBACK;\n'); lock.stdin.close(); lock.wait(timeout=10)

    before = sql('SELECT count(*) FROM sys_queues."BackgroundJobs"')
    assert publish('sys-flows', LATEST, v1, workflow('latest')) == 409
    assert sql('SELECT count(*) FROM sys_queues."BackgroundJobs"') == before
    print('PASS conflicting publication creates no job')

    # The extra-data cast rejects a non-component flow AFTER the definition and seed have been
    # written. Exercise transaction rollback through the real HTTP middleware and two DbContexts.
    failed_flow = PREFIX + '-rollback'
    assert publish('sys-flows', failed_flow, v1, workflow('latest'), seed, allow_failure=True) == 500
    assert sql(f'SELECT count(*) FROM sys_flows."Instances" WHERE "Key" = \'{failed_flow}\'') == '0'
    assert sql('SELECT count(*) FROM sys_queues."BackgroundJobs"') == before
    print('PASS exception after definition writes rolls back publication and leaves no job')

    # New workflow revision moving a pinned reference must also prepare without another Master publish.
    assert publish('sys-flows', PINNED, v2, workflow('latest')) == 200
    wait(jobs_finished)
    assert projections(PINNED) == 3
    print('PASS workflow reference update triggers automatic preparation')
    print(json.dumps({'result': 'passed', 'checks': 6, 'base_url': BASE, 'domain': DOMAIN, 'prefix': PREFIX}))


if __name__ == '__main__':
    raise SystemExit('Historical automatic experiment was removed. Run test_manual_indexes.py for the current contract.')
