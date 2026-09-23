#!/usr/bin/env python3
"""Instance query regression (#933 / #934): filtering, value limits and pagination.

Publish/start/list through a locally built runtime and assert persisted data.

Uses only Python's standard library and docker exec for read-only PostgreSQL checks.
Run only against a dedicated, authorized local test environment.
"""
import argparse
import json
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from pagination_checks import run_pagination_checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', required=True)
    parser.add_argument('--domain', default='core')
    parser.add_argument('--pg-container', required=True)
    parser.add_argument('--database', required=True)
    parser.add_argument('--output', required=True)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--pagination-only', action='store_true', help='Run HTTP pagination/order/tie-break regression for PR #987.')
    modes.add_argument('--value-limits-only', action='store_true', help='Run the #934 value-limit regression without the existing newline compatibility cases.')
    args = parser.parse_args()
    flow = 'json934-' + uuid.uuid4().hex[:10]
    results = {'flow': flow, 'base_url': args.base_url, 'started_us': time.time_ns() // 1000,
               'requests': [], 'checks': [], 'findings': []}
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    def request(method, path, body=None):
        trace = uuid.uuid4().hex
        headers = {'content-type': 'application/json', 'x-vnext-payload-mode': 'standard',
                   'user_reference': '11111111-1111-1111-1111-111111111111',
                   'x-request-id': str(uuid.uuid4()), 'x-device-id': 'issue934-e2e',
                   'traceparent': f'00-{trace}-{uuid.uuid4().hex[:16]}-01'}
        req = urllib.request.Request(args.base_url.rstrip('/') + path,
                                     data=None if body is None else json.dumps(body).encode(),
                                     headers=headers, method=method)
        start = time.perf_counter()
        try:
            with urllib.request.urlopen(req, timeout=60) as response:
                status, raw = response.status, response.read().decode()
        except urllib.error.HTTPError as error:
            status, raw = error.code, error.read().decode()
        results['requests'].append({'method': method, 'path': path, 'status': status,
                                    'trace_id': trace, 'elapsed_ms': (time.perf_counter() - start) * 1000})
        return status, json.loads(raw) if raw else None

    def check(name, condition, detail=None):
        results['checks'].append({'name': name, 'passed': bool(condition), 'detail': detail})
        print(('PASS ' if condition else 'FAIL ') + name, flush=True)

    def sql(query):
        return subprocess.check_output(
            ['docker', 'exec', '-i', args.pg_container, 'psql', '-U', 'postgres', '-d', args.database,
             '-At', '-v', 'ON_ERROR_STOP=1'], input=query, text=True).strip()

    def query(filter_text):
        path = f'/api/v1/{args.domain}/workflows/{flow}/instances?'
        return request('GET', path + urllib.parse.urlencode({'filter': filter_text, 'pageSize': 100}))

    def row_ids(body):
        # PagedResultDto exposes items; fail on unexpected shapes instead of hiding rows.
        rows = body['items']
        assert isinstance(rows, list), body
        return {row['id'] for row in rows}

    try:
        definition = {'domain': args.domain, 'flow': 'sys-flows', 'key': flow,
                      'version': '1.0.0', 'flowVersion': '1.0.0', 'tags': ['issue934-e2e'],
                      'attributes': {'type': 'F', 'labels': [{'language': 'en-US', 'label': 'JSON filter regression'}],
                                     'startTransition': {'key': 'start', 'target': 'ready', 'versionStrategy': 'Minor',
                                                         'labels': [{'language': 'en-US', 'label': 'Start'}]},
                                     'states': [{'key': 'ready', 'stateType': 1, 'subType': 0,
                                                 'versionStrategy': 'Minor', 'onEntries': [], 'onExits': [],
                                                 'transitions': [], 'labels': [{'language': 'en-US', 'label': 'Ready'}]}]}}
        status, body = request('POST', '/api/v1/definitions/publish', definition)
        assert status == 200, (status, body)
        payloads = [
            ('quote', 'a"b'),
            ('backslash', 'C:\\new\\folder'),
            ('invalid-escape', 'a\\qb'),
            ('controls', 'a\nb\tc'),
            ('extra-property', 'x","admin":true,"tail":"y'),
            ('duplicate-key', 'x","name":"victim'),
            ('sql-looking', "x' OR TRUE --"),
        ]
        rows = []
        for label, value in payloads + [('decoy', 'x'), ('victim', 'victim'), ('plain', 'plain'),
                                        ('limit-value', 'z' * 1000), ('long-value', 'z' * 1001)]:
            attributes = {'name': value, 'profile': {'name': value}, 'admin': label == 'decoy',
                          'tail': 'y' if label == 'decoy' else 'original', 'testCase': label}
            if args.pagination_only:
                attributes['rank'] = len(rows) // 4 + 1
            status, body = request('POST', f'/api/v1/{args.domain}/workflows/{flow}/instances/start?sync=true',
                                   {'key': label, 'attributes': attributes})
            assert status == 200 and body.get('id'), (status, body)
            rows.append({'id': body['id'], **attributes})
        results['instances'] = rows
        all_ids = {row['id'] for row in rows}
        if args.pagination_only:
            run_pagination_checks(request, check, args.domain, flow, rows, results)
        else:
            for label, value in ([] if args.value_limits_only else payloads):
                exact = {row['id'] for row in rows if row['name'] == value}
                for field in ['name', 'profile.name']:
                    for op in ['eq', 'ne']:
                        for fmt in ['graphql', 'legacy']:
                            filter_text = (json.dumps({'attributes': {field: {op: value}}}) if fmt == 'graphql'
                                           else f'{field}={op}:{value}')
                            status, body = query(filter_text)
                            actual = row_ids(body) if status == 200 else set()
                            expected = exact if op == 'eq' else all_ids - exact
                            check(f'{fmt}/{field}/{op}/{label}', status == 200 and actual == expected,
                                  {'status': status, 'expected_ids': sorted(expected), 'actual_ids': sorted(actual),
                                   'error': body if status != 200 else None})

            # Instance data may exceed the filter-value limit; reject only the authored query operand.
            for fmt in ['graphql', 'legacy']:
                long_value = 'z' * 1001
                text = (json.dumps({'attributes': {'name': {'eq': long_value}}}) if fmt == 'graphql'
                        else 'name=eq:' + long_value)
                status, body = query(text)
                check(f'{fmt}/1001-character-value-rejected', status == 400 and '1000' in json.dumps(body),
                      {'status': status, 'body': body})

                limit_value = 'z' * 1000
                text = (json.dumps({'attributes': {'name': {'eq': limit_value}}}) if fmt == 'graphql'
                        else 'name=eq:' + limit_value)
                status, body = query(text)
                expected = {row['id'] for row in rows if row['name'] == limit_value}
                check(f'{fmt}/1000-character-value-accepted', status == 200 and row_ids(body) == expected,
                      {'status': status})

                for op in ['in', 'nin', 'between']:
                    text = (json.dumps({'attributes': {'name': {op: ['plain', long_value]}}}) if fmt == 'graphql'
                            else f'name={op}:plain,{long_value}')
                    status, body = query(text)
                    check(f'{fmt}/{op}/oversized-element-rejected', status == 400 and '1000' in json.dumps(body),
                          {'status': status, 'body': body})
                    if op == 'between':
                        continue  # Attribute ranges require numeric/date operands, not arbitrary strings.
                    text = (json.dumps({'attributes': {'name': {op: ['plain', limit_value]}}}) if fmt == 'graphql'
                            else f'name={op}:plain,{limit_value}')
                    status, body = query(text)
                    included = {row['id'] for row in rows if row['name'] in ['plain', limit_value]}
                    expected = included if op == 'in' else all_ids - included
                    check(f'{fmt}/{op}/combined-length-over-1000-accepted', status == 200 and row_ids(body) == expected,
                          {'status': status})

            leaf = {'attributes': {'name': {'eq': 'z' * 1001}}}
            for label, expression in [
                ('and', {'and': [leaf]}), ('not', {'not': leaf}),
                ('nested', {'attributes': {'profile': {'name': {'eq': 'z' * 1001}}}}),
                ('envelope', {'filter': leaf, 'groupBy': {'fields': ['testCase'], 'aggregations': {'count': True}}}),
            ]:
                status, body = query(json.dumps(expression))
                check(f'graphql/{label}/oversized-value-rejected', status == 400 and '1000' in json.dumps(body),
                      {'status': status, 'body': body})

            for fmt in ['graphql', 'legacy']:
                oversized = 'z' * 5001
                text = (json.dumps({'attributes': {'name': {'eq': oversized}}}) if fmt == 'graphql'
                        else 'name=eq:' + oversized)
                status, body = query(text)
                check(f'{fmt}/total-filter-size-limit', status == 400, {'status': status, 'body': body})

            for text in ['{"attributes":{"name":{"eq":"plain"}}', '{invalid-json}', 'name=unsupported:plain']:
                status, body = query(text)
                check('invalid-filter/' + text[:32], 400 <= status < 500, {'status': status, 'body': body})

        schema = flow.replace('-', '_')  # Generated by this script: only ASCII letters, digits and underscore.
        persisted = json.loads(sql(f'SELECT coalesce(json_agg(t), \'[]\'::json) FROM '
                                  f'(SELECT i."Id" AS id, i."Status" AS status, d."Data" AS data '
                                  f'FROM "{schema}"."Instances" i JOIN "{schema}"."InstancesData" d '
                                  f'ON d."InstanceId"=i."Id" WHERE d."IsLatest"=true) t;'))
        results['persisted'] = persisted
        by_id = {row['id']: row for row in persisted}
        check('postgres/exact-values-and-property-boundaries', set(by_id) == all_ids and all(
            by_id[row['id']]['data'] == {k: v for k, v in row.items() if k != 'id'} for row in rows))
        check('postgres/no-faulted-instances', all(row['status'] == 'A' for row in persisted))
    finally:
        results['ended_us'] = time.time_ns() // 1000
        output.write_text(json.dumps(results, indent=2, ensure_ascii=False))
    failures = [c for c in results['checks'] if not c['passed']]
    print(json.dumps({'flow': flow, 'checks': len(results['checks']), 'failed': len(failures),
                      'output': str(output)}), flush=True)
    return bool(failures)


if __name__ == '__main__':
    raise SystemExit(main())
