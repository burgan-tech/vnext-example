"""HTTP list pagination regression for #933 / PR #987, sharing the #934 API fixture."""
import json
import urllib.parse


def run_pagination_checks(request, check, domain, flow, rows, results):
    """Assert ordered IDs and next links on an immutable, API-created 12-row fixture.

    The oracle uses authored values and returned IDs, never the runtime's first-page order.
    Every tie is broken by ascending canonical UUID text (PostgreSQL UUID byte order).
    """
    route = f'/api/v1/{domain}/workflows/{flow}/instances'
    by_id = {row['id']: row for row in rows}
    ids = sorted(by_id)
    results['pagination'] = []

    def next_href(body):
        # HATEOAS is the public hasNext contract; do not invent a hasNext JSON property.
        link = body['links'].get('next')
        assert isinstance(link, str), link
        return link or None

    def fetch(parameters):
        status, body = request('GET', route + '?' + urllib.parse.urlencode(parameters))
        assert status == 200, (status, body)
        assert isinstance(body.get('items'), list) and isinstance(body.get('links'), dict), body
        return body

    def assert_page(name, body, expected, page, size, total):
        actual = [item['id'] for item in body['items']]
        expected_next = page * size < total
        href = next_href(body)
        detail = {'page': page, 'page_size': size, 'expected_ids': expected, 'actual_ids': actual,
                  'expected_has_next': expected_next, 'next': href}
        check(name + '/ordered-items', actual == expected, detail)
        check(name + '/next-link', bool(href) == expected_next, detail)
        check(name + '/hydrated-values', all(
            item['id'] in by_id and item['attributes'] == {k: v for k, v in by_id[item['id']].items() if k != 'id'}
            for item in body['items']), detail)
        if href:
            query = urllib.parse.parse_qs(urllib.parse.urlsplit(href).query)
            check(name + '/next-page-number', query.get('page') == [str(page + 1)] and
                  query.get('pageSize') == [str(size)], query)
        results['pagination'].append({'name': name, **detail})
        return actual, href

    # Status is identical for all 12 records: every page boundary lies inside a tie.
    cases = [
        ('status-asc', {'field': 'status', 'direction': 'asc'}, ids),
        ('status-desc', {'field': 'status', 'direction': 'desc'}, ids),
        ('id-desc', {'field': 'id', 'direction': 'desc'}, list(reversed(ids))),
        ('key-asc', {'field': 'key', 'direction': 'asc'}, sorted(ids, key=lambda i: by_id[i]['testCase'])),
        ('key-desc', {'field': 'key', 'direction': 'desc'}, sorted(ids, key=lambda i: by_id[i]['testCase'], reverse=True)),
        ('attribute-asc', {'field': 'attributes.rank', 'direction': 'asc'}, sorted(ids, key=lambda i: by_id[i]['rank'])),
        ('attribute-desc', {'field': 'attributes.rank', 'direction': 'desc'}, sorted(ids, key=lambda i: -by_id[i]['rank'])),
        ('multi-field', {'fields': [{'field': 'status', 'direction': 'desc'},
                                    {'field': 'attributes.rank', 'direction': 'desc'}]},
         sorted(ids, key=lambda i: -by_id[i]['rank'])),
    ]
    for name, sort, expected_ids in cases:
        # The 3-row pages split each 4-row attribute tie and cross into the next group.
        for mode in ['graphql', 'legacy', 'envelope']:
            # Existing envelope detection requires groupBy/aggregations. Null grouping
            # selects the envelope path without changing an instance list into group summaries.
            if mode == 'envelope':
                parameters = {'filter': json.dumps({'filter': {'status': {'eq': 'A'}}, 'groupBy': None, 'orderBy': sort})}
            else:
                parameters = {'filter': json.dumps({'status': {'eq': 'A'}}) if mode == 'graphql' else 'status=eq:A',
                              'sort': json.dumps(sort)}
            seen = []
            for page in range(1, 6):  # exact final page plus one empty page
                body = fetch({**parameters, 'page': page, 'pageSize': 3})
                actual, _ = assert_page(f'{mode}/{name}/page-{page}', body,
                                       expected_ids[(page - 1) * 3:page * 3], page, 3, len(expected_ids))
                seen.extend(actual)
                if page == 2:
                    repeated = fetch({**parameters, 'page': page, 'pageSize': 3})
                    check(f'{mode}/{name}/repeat-page-stable',
                          [row['id'] for row in repeated['items']] == actual)
            check(f'{mode}/{name}/complete-no-duplicates', seen == expected_ids and len(set(seen)) == len(seen))

    # Boundary sizes: singleton, exact multiple, partial last page, exact fit and oversized page.
    sort = json.dumps({'field': 'status', 'direction': 'desc'})
    for size in [1, 4, 5, 12, 20]:
        for page in range(1, (len(ids) + size - 1) // size + 2):
            body = fetch({'sort': sort, 'page': page, 'pageSize': size})
            assert_page(f'boundaries/{size}/page-{page}', body, ids[(page - 1) * size:page * size], page, size, len(ids))

    # Empty match on the first page must not advertise a next page.
    body = fetch({'filter': json.dumps({'key': {'eq': 'does-not-exist'}}), 'page': 1, 'pageSize': 3})
    assert_page('empty-match', body, [], 1, 3, 0)

    # Filter to eight rows so accidentally dropping the filter on a next link is observable.
    expected = sorted([i for i in ids if by_id[i]['rank'] >= 2], key=lambda i: -by_id[i]['rank'])
    for mode in ['graphql', 'legacy', 'envelope']:
        ordering = {'field': 'attributes.rank', 'direction': 'desc'}
        expression = {'attributes': {'rank': {'ge': 2}}}
        parameters = ({'filter': json.dumps({'filter': expression, 'groupBy': None, 'orderBy': ordering})} if mode == 'envelope'
                      else {'filter': json.dumps(expression) if mode == 'graphql' else 'rank=ge:2',
                            'sort': json.dumps(ordering)})
        path = route + '?' + urllib.parse.urlencode({**parameters, 'page': 1, 'pageSize': 3})
        seen = []
        for page in range(1, 5):
            status, body = request('GET', path)
            assert status == 200, (status, body)
            actual, href = assert_page(f'follow-next/{mode}/page-{page}', body,
                                      expected[(page - 1) * 3:page * 3], page, 3, len(expected))
            seen.extend(actual)
            if not href:
                break
            parsed = urllib.parse.urlsplit(href)
            assert not parsed.scheme and not parsed.netloc, 'Expected relative same-runtime pagination link'
            path = parsed.path + ('?' + parsed.query if parsed.query else '')
        check(f'follow-next/{mode}/complete-filtered-set', seen == expected and len(set(seen)) == len(seen),
              {'expected_ids': expected, 'actual_ids': seen})
