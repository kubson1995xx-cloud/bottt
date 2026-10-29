#!/usr/bin/env python3
"""Public OKX EEA SPOT research only. No auth, orders or provider fallback.

CSV: timestamp,open,high,low,close,volume (base units), original decimals.
Cache keys bind exact GET URLs; raw response bytes and every attempt survive.
Run again to resume from hash-verified successful request checkpoints.
"""
import argparse
import csv
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
import hashlib
import io
import json
from pathlib import Path
import re
import time
import urllib.error
import urllib.parse
import urllib.request

BASES = ('BTC', 'ETH', 'SOL', 'XRP', 'DOGE', 'ADA', 'LTC', 'LINK', 'BCH', 'AVAX')
HOST = 'https://eea.okx.com'
SOURCE = 'OKX_EEA_PUBLIC'
DAY = 86400000
COLUMNS = ('timestamp', 'open', 'high', 'low', 'close', 'volume')
START = '2020-01-01T00:00:00Z'
CUTOFF = '2026-10-29T00:00:00Z'
DEFAULT_ROOT = '/home/ubuntu/projects/apex-data/naval-okx'
DECIMAL = re.compile(r'^(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$')


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z')


def iso(ms):
    return datetime.fromtimestamp(ms // 1000, timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')


def milliseconds(value):
    dt = datetime.fromisoformat(value.replace('Z', '+00:00'))
    offset = dt.utcoffset()
    if offset is None or offset.total_seconds() != 0:
        raise ValueError('timestamp must have UTC offset')
    return int(dt.timestamp()) * 1000


def digest(data):
    return hashlib.sha256(data).hexdigest()


def atomic_bytes(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + '.part')
    temp.write_bytes(data)
    temp.replace(path)


def write_json(path, value):
    atomic_bytes(path, (json.dumps(value, indent=2, ensure_ascii=False) + '\n').encode())


def decimal_value(value, positive=False):
    if not isinstance(value, str) or not DECIMAL.fullmatch(value):
        raise ValueError('expected original unsigned decimal string')
    try:
        number = Decimal(value)
    except InvalidOperation as exc:
        raise ValueError('invalid decimal') from exc
    if not number.is_finite() or number < 0 or (positive and number == 0):
        raise ValueError('invalid OHLCV decimal')
    return number


def validate_candle(row):
    if not isinstance(row, list) or len(row) != 9 or not all(isinstance(x, str) for x in row):
        raise ValueError('candle must have nine original string fields')
    if not row[0].isdigit():
        raise ValueError('invalid opening epoch milliseconds')
    ts = int(row[0])
    if ts % DAY:
        raise ValueError('not UTC daily opening')
    o, h, low, c = [decimal_value(v, positive=True) for v in row[1:5]]
    for v in row[5:8]:
        decimal_value(v)
    if not (low <= o <= h and low <= c <= h):
        raise ValueError('inconsistent OHLC')
    if row[8] not in ('0', '1'):
        raise ValueError('unknown confirm flag')
    return ts


def normalize(raw, start, cutoff):
    unique = {}
    stats = {'duplicates': 0, 'duplicatesByYear': {}, 'unconfirmedExcluded': 0, 'outsideWindowExcluded': 0}
    for row in raw:
        ts = validate_candle(row)
        if row[8] != '1':
            stats['unconfirmedExcluded'] += 1
            continue
        if ts < start or ts + DAY > cutoff:
            stats['outsideWindowExcluded'] += 1
            continue
        if ts in unique:
            if unique[ts] != row:
                raise ValueError('conflicting duplicate candle at ' + iso(ts))
            stats['duplicates'] += 1
            year = iso(ts)[:4]
            stats['duplicatesByYear'][year] = stats['duplicatesByYear'].get(year, 0) + 1
        unique[ts] = row
    return [(iso(ts), *unique[ts][1:6]) for ts in sorted(unique)], stats


def coverage(rows, start, cutoff):
    times = [milliseconds(row[0]) for row in rows]
    observed = set(times)
    expected = list(range(start, cutoff, DAY))
    gaps = []
    if times:
        missing = [ts for ts in range(times[0], times[-1] + DAY, DAY) if ts not in observed]
        for ts in missing:
            if gaps and milliseconds(gaps[-1]['endUtc']) + DAY == ts:
                gaps[-1]['endUtc'] = iso(ts)
                gaps[-1]['days'] += 1
            else:
                gaps.append({'startUtc': iso(ts), 'endUtc': iso(ts), 'days': 1})
    years = {}
    for ts in expected:
        year = iso(ts)[:4]
        record = years.setdefault(year, {'expectedDays': 0, 'rows': 0, 'missingDays': 0})
        record['expectedDays'] += 1
        record['rows'] += int(ts in observed)
        record['missingDays'] += int(ts not in observed)
    for record in years.values():
        record['coveragePercent'] = round(record['rows'] / record['expectedDays'] * 100, 6)
    return {
        'rows': len(rows), 'startUtc': rows[0][0] if rows else None,
        'endUtc': rows[-1][0] if rows else None, 'expectedDays': len(expected),
        'missingDays': len(expected) - len(observed),
        'leadingMissingDays': (times[0] - start) // DAY if times else len(expected),
        'trailingMissingDays': (cutoff - DAY - times[-1]) // DAY if times else 0,
        'internalMissingDays': sum(gap['days'] for gap in gaps), 'gaps': gaps, 'years': years,
    }


def paginate(fetch, start, cutoff, max_pages=100):
    cursor, raw = cutoff, []
    for _ in range(max_pages):
        page = fetch(cursor)
        if not isinstance(page, list):
            raise ValueError('API data is not an array')
        if not page:
            return raw, 'sourceExhausted'
        timestamps = [validate_candle(row) for row in page]
        oldest = min(timestamps)
        if oldest >= cursor or max(timestamps) > cursor:
            raise ValueError('pagination made no backward progress or returned future data')
        raw.extend(page)
        if oldest <= start:
            return raw, 'reachedRequestedStart'
        cursor = oldest  # after means strictly earlier; no subtraction/skipped day
    raise ValueError('maximum pagination bound exceeded')


def select_instruments(catalog):
    mapping = {}
    for item in catalog:
        if item.get('instType') != 'SPOT' or item.get('baseCcy') not in BASES:
            continue
        base, quote = item['baseCcy'], item.get('quoteCcy')
        if not isinstance(quote, str) or not re.fullmatch(r'[A-Z0-9]+', quote):
            raise ValueError('invalid catalog quote')
        if item.get('instId') != base + '-' + quote:
            raise ValueError('catalog identity mismatch')
        key = (base, quote)
        if key in mapping:
            raise ValueError('duplicate catalog identity')
        mapping[key] = item
    # Explicit stable research-only choice. Never mix quote units for missing pairs.
    quotes = {q for _, q in mapping}
    if all((b, 'USDT') in mapping for b in BASES):
        quote = 'USDT'
    else:
        common = sorted(q for q in quotes if all((b, q) in mapping for b in BASES))
        quote = common[0] if common else 'USDT'
    return quote, {b: mapping.get((b, quote)) for b in BASES}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def public_transport(url):
    opener = urllib.request.build_opener(NoRedirect())
    request = urllib.request.Request(url, method='GET', headers={
        'User-Agent': 'apex-naval-public-research/1.0', 'Accept': 'application/json'})
    try:
        with opener.open(request, timeout=30) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()


class PublicClient:
    def __init__(self, root, transport=public_transport, throttle=0.25, sleep=time.sleep):
        self.root, self.transport = Path(root), transport
        self.throttle, self.sleep = throttle, sleep
        self.network_requests = 0
        self.cache_hits = 0

    def get(self, path, params):
        allowed = {
            '/api/v5/public/instruments': {'instType'},
            '/api/v5/market/history-candles': {'instId', 'bar', 'after', 'limit'},
        }
        if path not in allowed or set(params) - allowed[path]:
            raise ValueError('only public instruments/history-candles GET allowed')
        if path.endswith('/instruments') and params != {'instType': 'SPOT'}:
            raise ValueError('SPOT catalog only')
        if path.endswith('/history-candles'):
            if params.get('bar') != '1Dutc' or not re.fullmatch(r'[A-Z0-9]+-[A-Z0-9]+', params.get('instId', '')):
                raise ValueError('only exact SPOT UTC daily history allowed')
        url = HOST + path + '?' + urllib.parse.urlencode(sorted(params.items()))
        key = digest(url.encode())
        checkpoint = self.root / 'checkpoints' / (key + '.json')
        if checkpoint.exists():
            meta = json.loads(checkpoint.read_text())
            raw_path = (self.root / meta['rawPath']).resolve()
            if not raw_path.is_relative_to(self.root.resolve()):
                raise ValueError('checkpoint raw path escapes dataset')
            data = raw_path.read_bytes()
            if meta['url'] != url or meta['status'] != 200 or digest(data) != meta['sha256']:
                raise ValueError('checkpoint hash/url/status mismatch')
            payload = json.loads(data)
            if payload.get('code') != '0' or not isinstance(payload.get('data'), list):
                raise ValueError('checkpoint is not successful machine JSON')
            self.cache_hits += 1
            return payload, meta
        for attempt in range(4):
            self.sleep(self.throttle)  # <=4 GET/s, lower than documented 20/2s
            self.network_requests += 1
            error = None
            try:
                status, data = self.transport(url)
            except (OSError, urllib.error.URLError) as exc:
                status, data, error = None, b'', str(exc)
            stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
            name = key + '-' + stamp + '-' + str(attempt)
            raw_path = 'raw/' + name + '.json'
            meta = {'url': url, 'method': 'GET', 'status': status, 'fetchUtc': utc_now(),
                    'sha256': digest(data), 'bytes': len(data), 'rawPath': raw_path,
                    'attempt': attempt + 1, 'transportError': error}
            atomic_bytes(self.root / raw_path, data)
            write_json(self.root / 'provenance' / (name + '.json'), meta)
            payload = None
            try:
                payload = json.loads(data)
            except (ValueError, UnicodeError):
                pass
            code = payload.get('code') if isinstance(payload, dict) else None
            if status == 200 and code == '0' and isinstance(payload.get('data'), list):
                write_json(checkpoint, meta)
                return payload, meta
            retryable = status is None or status == 429 or (status is not None and 500 <= status < 600) or (status == 200 and code == '50011')
            if retryable and attempt < 3:
                self.sleep(2 ** attempt)
                continue
            raise RuntimeError(f'public GET failed HTTP={status} API={code} url={url}; raw={raw_path}; error={error}')
        raise AssertionError('unreachable')


def run_dataset(root, start, cutoff, client=None):
    root = Path(root)
    if start % DAY or cutoff % DAY or start >= cutoff:
        raise ValueError('expected nonempty UTC daily window')
    client = client or PublicClient(root)
    manifest = {'schemaVersion': 'naval-market-data-v1', 'source': SOURCE,
                'normalizerSha256': digest(Path(__file__).read_bytes()),
                'sourceHost': HOST, 'quoteAsset': None, 'priceBasis': 'TRADE',
                'timeframe': 'UTC1D', 'bar': '1Dutc', 'confirmedOnly': True,
                'requestedStartUtc': iso(start), 'cutoffUtc': iso(cutoff),
                'csvColumns': list(COLUMNS), 'volumeUnit': 'baseAsset',
                'metadataTemporalBasis': 'CURRENT_SNAPSHOT_NOT_HISTORICAL',
                'researchOnly': True, 'accountAvailabilityVerified': False,
                'instruments': []}
    try:
        payload, catalog_meta = client.get('/api/v5/public/instruments', {'instType': 'SPOT'})
        quote, selected = select_instruments(payload['data'])
        manifest['catalogProvenance'] = catalog_meta
    except (ValueError, RuntimeError, OSError) as exc:
        quote, selected = 'USDT', {base: None for base in BASES}
        manifest['catalogFailure'] = str(exc)
    manifest['quoteAsset'] = quote
    for base in BASES:
        metadata = selected[base]
        item = {'baseAsset': base, 'instrument': base + '-' + quote, 'quoteAsset': quote,
                'source': SOURCE, 'priceBasis': 'TRADE', 'timeframe': 'UTC1D',
                'status': 'unavailable', 'csvPath': None, 'sha256': None,
                'startUtc': None, 'endUtc': None, 'rows': 0, 'gaps': [],
                'listTime': metadata.get('listTime') if metadata else None,
                'metadataPath': None, 'lotSize': None, 'minSize': None, 'tickSize': None}
        manifest['instruments'].append(item)
        if metadata is None:
            item['unavailableReason'] = manifest.get('catalogFailure', f'no {base}-{quote} SPOT instrument in public catalog')
            continue
        instrument = item['instrument']
        requests = []
        meta_path = 'metadata/' + instrument + '.json'
        item['metadataPath'] = meta_path
        bound_metadata = {'baseAsset': base, 'quoteAsset': quote, 'instrument': instrument,
                          'source': SOURCE, 'priceBasis': 'TRADE', 'timeframe': 'UTC1D',
                          'bar': '1Dutc', 'confirmRequired': '1', 'volumeUnit': 'baseAsset',
                          'timestampMeaning': 'UTC opening timestamp',
                          'metadataTemporalBasis': 'CURRENT_SNAPSHOT_NOT_HISTORICAL',
                          'catalogProvenance': manifest['catalogProvenance'],
                          'instrumentMetadata': metadata, 'requests': requests}
        try:
            for target, field in [('lotSize', 'lotSz'), ('minSize', 'minSz'), ('tickSize', 'tickSz')]:
                decimal_value(metadata.get(field), positive=True)
                item[target] = metadata[field]
            def fetch(cursor):
                payload, provenance = client.get('/api/v5/market/history-candles', {
                    'instId': instrument, 'bar': '1Dutc', 'after': str(cursor), 'limit': '300'})
                requests.append(provenance)
                return payload['data']
            raw, termination = paginate(fetch, start, cutoff)
            rows, stats = normalize(raw, start, cutoff)
            measured = coverage(rows, start, cutoff)
            for year, year_coverage in measured['years'].items():
                year_coverage['duplicates'] = stats['duplicatesByYear'].get(year, 0)
            listing = int(metadata['listTime']) if metadata.get('listTime', '').isdigit() else None
            measured['observationsBeforeCurrentListTime'] = sum(milliseconds(row[0]) < listing for row in rows) if listing is not None else None
            bound_metadata.update({'paginationTermination': termination, 'rawObservations': len(raw),
                                   'normalization': stats, 'coverage': measured})
            if not rows:
                raise ValueError('no confirmed source observations in requested window')
            stream = io.StringIO(newline='')
            writer = csv.writer(stream, lineterminator='\n')
            writer.writerow(COLUMNS)
            writer.writerows(rows)
            data = stream.getvalue().encode('utf-8')
            csv_path = 'normalized/' + instrument + '-1Dutc.csv'
            atomic_bytes(root / csv_path, data)
            # Read the actual output through the standard CSV parser before publishing.
            with (root / csv_path).open(newline='') as check:
                decoded = list(csv.reader(check))
            if decoded != [list(COLUMNS)] + [list(row) for row in rows]:
                raise ValueError('CSV roundtrip changed source decimals')
            item.update({'status': 'available', 'csvPath': csv_path, 'sha256': digest(data),
                         'rows': measured['rows'], 'startUtc': measured['startUtc'],
                         'endUtc': measured['endUtc'], 'gaps': measured['gaps'],
                         'coverage': measured, 'duplicates': stats['duplicates']})
        except (ValueError, RuntimeError, OSError) as exc:
            item['unavailableReason'] = str(exc)
            bound_metadata['failure'] = str(exc)
        write_json(root / meta_path, bound_metadata)
    manifest['retrieval'] = {'networkRequests': client.network_requests, 'verifiedCacheHits': client.cache_hits}
    write_json(root / 'dataset.json', manifest)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', default=DEFAULT_ROOT)
    args = parser.parse_args()
    root = Path(args.root).resolve()
    repo = Path(__file__).resolve().parents[2]
    scratch = Path('/home/ubuntu/.hermes/cache/scratch')
    if root.is_relative_to(repo) or root.is_relative_to(scratch):
        parser.error('dataset must be durable and outside repository/scratch')
    manifest = run_dataset(root, milliseconds(START), milliseconds(CUTOFF))
    print(json.dumps({'dataset': str(root / 'dataset.json'), 'quoteAsset': manifest['quoteAsset'],
                      'retrieval': manifest['retrieval'], 'instruments': [
                          {k: row.get(k) for k in ('baseAsset', 'status', 'rows', 'startUtc', 'endUtc', 'gaps', 'unavailableReason')}
                          for row in manifest['instruments']]}, indent=2))
    return 0 if all(row['status'] == 'available' for row in manifest['instruments']) else 2


if __name__ == '__main__':
    raise SystemExit(main())
