#!/usr/bin/env python3
"""row-security-lab smoke (vnext#1107): functional fixture, NOT representative of load or latency.

Seeds the lab population on `rs-a` (Definition A) as an hq-auditor and checks three callers on the list and the
single GET:

  hq-auditor                       -> all 7 rows
  branch-staff + x-branch-code=BR-001 -> r1, r2, r5 (own branch on LATEST data; r5 moved BR-003 -> BR-001)
  identity, no role                -> nothing (deny by default; every GET 404)

The authoritative rows are the .NET suite (tests/Core.IntegrationTests/Tests/RowSecurityLab). Standard library only.

    python3 api-tests/row-security/rs-smoke.py [--base-url http://localhost:4201]
"""
import argparse
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

FLOW = 'rs-a'


def request(base, method, path, headers=None, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base.rstrip('/') + path, data=data, method=method)
    req.add_header('content-type', 'application/json')
    for key, value in (headers or {}).items():
        req.add_header(key, value)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            raw = response.read().decode()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read().decode()
        return error.code, raw


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--base-url', default='http://localhost:4201')
    base = parser.parse_args().base_url

    hq = {'role': 'hq-auditor', 'act_sub': 'rs-seeder', 'sub': 'rs-seeder'}
    test_id = 'rs-py-' + uuid.uuid4().hex[:12]
    rows = [
        ('r1', 'alice', None, 'BR-001'), ('r2', 'carol', None, 'BR-001'), ('r3', 'alice', 'bob', 'BR-002'),
        ('r4', 'carol', None, 'BR-002'), ('r5', 'rs-seeder', None, 'BR-003'), ('r6', 'rs-seeder', None, 'BR-999'),
        ('r7', 'rs-seeder', None, None),
    ]
    ids = {}
    for name, act_sub, sub, branch in rows:
        headers = {'role': 'hq-auditor', 'act_sub': act_sub}
        if sub:
            headers['sub'] = sub
        attributes = {'testId': test_id, 'amount': 1}
        if branch:
            attributes['branchCode'] = branch
        status, body = request(base, 'POST', f'/api/v1/core/workflows/{FLOW}/instances/start?sync=true',
                               headers, {'attributes': attributes})
        assert status < 400, (name, status, body)
        ids[name] = body['id']

    for name, transition, payload in (('r4', 'rs-close', {}), ('r5', 'rs-update', {'attributes': {'branchCode': 'BR-001'}})):
        status, body = request(base, 'PATCH',
                               f'/api/v1/core/workflows/{FLOW}/instances/{ids[name]}/transitions/{transition}?sync=true',
                               hq, payload)
        assert status < 400, (name, transition, status, body)

    names = {value: key for key, value in ids.items()}
    query = urllib.parse.urlencode({'pageSize': 100, 'filter': json.dumps({'attributes': {'testId': {'eq': test_id}}})})

    def visible(headers):
        status, body = request(base, 'GET', f'/api/v1/core/workflows/{FLOW}/instances?{query}', headers)
        assert status == 200, (status, body)
        listed = sorted(names[item['id']] for item in body['items'])
        got = sorted(name for name, instance in ids.items()
                     if request(base, 'GET', f'/api/v1/core/workflows/{FLOW}/instances/{instance}', headers)[0] == 200)
        return listed, got

    cases = [
        ('hq-auditor', hq, ['r1', 'r2', 'r3', 'r4', 'r5', 'r6', 'r7']),
        ('branch-staff BR-001', {'role': 'branch-staff', 'x-branch-code': 'BR-001', 'act_sub': 'dave'}, ['r1', 'r2', 'r5']),
        ('identity, no role', {'act_sub': 'alice', 'sub': 'alice'}, []),
    ]
    failed = 0
    for label, headers, expected in cases:
        listed, got = visible(headers)
        ok = listed == expected and got == expected
        failed += 0 if ok else 1
        print(json.dumps({'case': label, 'ok': ok, 'expected': expected, 'list': listed, 'get200': got}))

    print(json.dumps({'testId': test_id, 'failed': failed}))
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
