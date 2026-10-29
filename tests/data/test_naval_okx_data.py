"""Synthetic unit fixtures only; never used as research market data."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from typing import Any

MODULE = Path(__file__).resolve().parents[2] / 'tools/data/naval_okx_data.py'
spec = importlib.util.spec_from_file_location('naval_okx_data', MODULE)
m: Any = None
if MODULE.exists():
    assert spec is not None and spec.loader is not None
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)

DAY = 86400000
START = 1577836800000

def candle(day, confirm='1', close='1.2300'):
    return [str(START + day * DAY), '1.200', '2.00', '1.0', close, '3.000', '4', '4', confirm]


class ParsingTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(m, 'downloader implementation does not exist yet')

    def test_preserves_original_decimal_strings_and_sorts(self):
        rows, stats = m.normalize([candle(2), candle(0)], START, START + 3 * DAY)
        self.assertEqual(rows[0], ('2020-01-01T00:00:00Z', '1.200', '2.00', '1.0', '1.2300', '3.000'))
        self.assertEqual(rows[1][0], '2020-01-03T00:00:00Z')
        self.assertEqual(stats['duplicates'], 0)

    def test_unclosed_and_cutoff_bar_are_excluded(self):
        rows, stats = m.normalize([candle(0), candle(1, '0'), candle(2)], START, START + 2 * DAY)
        self.assertEqual(len(rows), 1)
        self.assertEqual(stats['unconfirmedExcluded'], 1)
        self.assertEqual(stats['outsideWindowExcluded'], 1)

    def test_rejects_malformed_nonfinite_negative_misaligned_ohlcv(self):
        bad = []
        for i, value in [(1, 'NaN'), (1, 'Infinity'), (2, '0.1'), (3, '-1'), (5, '-1'), (8, '2'), (0, str(START + 1)), (1, 1.2)]:
            row = candle(0)
            row[i] = value
            bad.append(row)
        bad += [candle(0)[:-1], ['bad'] * 9]
        for row in bad:
            with self.subTest(row=row), self.assertRaises(ValueError):
                m.normalize([row], START, START + DAY)

    def test_duplicate_count_and_conflicting_duplicate_refusal(self):
        rows, stats = m.normalize([candle(0), candle(0)], START, START + DAY)
        self.assertEqual(len(rows), 1)
        self.assertEqual(stats['duplicates'], 1)
        self.assertEqual(stats['duplicatesByYear'], {'2020': 1})
        with self.assertRaisesRegex(ValueError, 'conflict'):
            m.normalize([candle(0), candle(0, close='1.4')], START, START + DAY)

    def test_coverage_distinguishes_leading_internal_trailing_no_fill(self):
        rows, _ = m.normalize([candle(1), candle(3)], START, START + 5 * DAY)
        report = m.coverage(rows, START, START + 5 * DAY)
        self.assertEqual(report['missingDays'], 3)
        self.assertEqual(report['leadingMissingDays'], 1)
        self.assertEqual(report['trailingMissingDays'], 1)
        self.assertEqual(report['internalMissingDays'], 1)
        self.assertEqual(report['gaps'], [{'startUtc': '2020-01-03T00:00:00Z', 'endUtc': '2020-01-03T00:00:00Z', 'days': 1}])
        self.assertEqual(report['years']['2020']['rows'], 2)
        self.assertEqual(report['years']['2020']['expectedDays'], 5)
        self.assertEqual(len(rows), 2)

    def test_empty_coverage_is_explicit(self):
        result = m.coverage([], START, START + DAY)
        self.assertEqual(result['rows'], 0)
        self.assertEqual(result['missingDays'], 1)
        self.assertIsNone(result['startUtc'])

    def test_pagination_uses_earliest_raw_timestamp_and_does_not_stop_on_short_page(self):
        calls = []
        pages = [[candle(4), candle(3)], [candle(3), candle(1, '0')], [candle(0)]]
        def fetch(cursor):
            calls.append(cursor)
            return pages.pop(0)
        raw, reason = m.paginate(fetch, START, START + 5 * DAY)
        self.assertEqual(calls, [START + 5 * DAY, START + 3 * DAY, START + DAY])
        self.assertEqual(len(raw), 5)
        self.assertEqual(reason, 'reachedRequestedStart')

    def test_pagination_empty_and_stuck_and_future(self):
        self.assertEqual(m.paginate(lambda _: [], START, START + DAY), ([], 'sourceExhausted'))
        with self.assertRaisesRegex(ValueError, 'progress'):
            m.paginate(lambda _: [candle(1)], START, START + DAY)
        with self.assertRaises(ValueError):
            m.paginate(lambda _: [candle(2)], START, START + DAY)

    def test_selects_exact_ten_usdt_from_catalog_not_region_authority(self):
        catalog = [{'instId': b + '-USDT', 'baseCcy': b, 'quoteCcy': 'USDT', 'instType': 'SPOT'} for b in m.BASES]
        quote, selected = m.select_instruments(catalog)
        self.assertEqual(quote, 'USDT')
        self.assertEqual(list(selected), list(m.BASES))
        quote, selected = m.select_instruments(catalog[:-1])
        self.assertEqual(quote, 'USDT')
        self.assertIsNone(selected['AVAX'])


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(m, 'downloader implementation does not exist yet')
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def test_cache_reuse_checks_hash_and_persists_real_attempts(self):
        attempts = []
        def transport(url):
            attempts.append(url)
            return 200, b'{"code":"0","data":[],"msg":""}'
        client = m.PublicClient(Path(self.tmp.name), transport=transport, throttle=0, sleep=lambda _: None)
        payload, provenance = client.get('/api/v5/public/instruments', {'instType': 'SPOT'})
        self.assertEqual(payload['data'], [])
        self.assertEqual(provenance['status'], 200)
        self.assertEqual(len(attempts), 1)
        client.get('/api/v5/public/instruments', {'instType': 'SPOT'})
        self.assertEqual(len(attempts), 1)
        raw = Path(self.tmp.name) / provenance['rawPath']
        raw.write_bytes(b'corrupt')
        with self.assertRaisesRegex(ValueError, 'hash'):
            client.get('/api/v5/public/instruments', {'instType': 'SPOT'})

    def test_retry_429_50011_and_throttle_but_not_regional_403(self):
        responses = [(429, b'throttled'), (200, b'{"code":"50011","data":[]}'), (200, b'{"code":"0","data":[]}')]
        waits = []
        client = m.PublicClient(Path(self.tmp.name), transport=lambda _: responses.pop(0), throttle=0.25, sleep=waits.append)
        client.get('/api/v5/public/instruments', {'instType': 'SPOT'})
        self.assertEqual(len(list((Path(self.tmp.name) / 'raw').glob('*.json'))), 3)
        self.assertGreaterEqual(len(waits), 3)
        blocked = m.PublicClient(Path(self.tmp.name) / 'blocked', transport=lambda _: (403, b'regional refusal'), sleep=waits.append)
        with self.assertRaisesRegex(RuntimeError, '403'):
            blocked.get('/api/v5/public/instruments', {'instType': 'SPOT'})
        self.assertEqual(len(list((Path(self.tmp.name) / 'blocked/raw').glob('*.json'))), 1)

    def test_private_or_foreign_path_refused_before_transport(self):
        client = m.PublicClient(Path(self.tmp.name), transport=lambda _: self.fail('must not call transport'))
        for path in ['/api/v5/account/instruments', 'https://www.okx.com/api/v5/market/history-candles', '/api/v5/trade/order']:
            with self.assertRaises(ValueError):
                client.get(path, {})

    def test_dataset_contract_and_partial_failure_keep_all_ten(self):
        catalog = [{'instId': b + '-USDT', 'baseCcy': b, 'quoteCcy': 'USDT', 'instType': 'SPOT',
                    'lotSz': '0.0100', 'minSz': '0.100', 'tickSz': '0.0001', 'listTime': str(START)}
                   for b in m.BASES[:-1]]
        calls = []
        def transport(url):
            calls.append(url)
            if '/public/instruments?' in url:
                payload = {'code': '0', 'data': catalog}
            elif 'ETH-USDT' in url:
                return 403, b'regional refusal'
            else:
                payload = {'code': '0', 'data': [candle(0)]}
            return 200, json.dumps(payload).encode()
        root = Path(self.tmp.name)
        client = m.PublicClient(root, transport=transport, throttle=0, sleep=lambda _: None)
        manifest = m.run_dataset(root, START, START + DAY, client)
        self.assertEqual(len(manifest['instruments']), 10)
        self.assertEqual(manifest['schemaVersion'], 'naval-market-data-v1')
        self.assertEqual(manifest['quoteAsset'], 'USDT')
        self.assertEqual(manifest['priceBasis'], 'TRADE')
        btc = manifest['instruments'][0]
        self.assertEqual(btc['status'], 'available')
        self.assertEqual((btc['lotSize'], btc['minSize'], btc['tickSize']), ('0.0100', '0.100', '0.0001'))
        self.assertFalse(Path(btc['csvPath']).is_absolute())
        self.assertEqual(btc['rows'], 1)
        self.assertTrue((root / btc['metadataPath']).is_file())
        self.assertEqual(manifest['instruments'][1]['status'], 'unavailable')
        self.assertIn('403', manifest['instruments'][1]['unavailableReason'])
        self.assertEqual(manifest['instruments'][-1]['status'], 'unavailable')
        self.assertIn('catalog', manifest['instruments'][-1]['unavailableReason'])
        self.assertEqual(json.loads((root / 'dataset.json').read_text()), manifest)

    def test_transport_retries_are_bounded_and_api_failure_is_not_success(self):
        calls = []
        def broken(url):
            calls.append(url)
            raise OSError('network down')
        root = Path(self.tmp.name)
        client = m.PublicClient(root, transport=broken, throttle=0, sleep=lambda _: None)
        with self.assertRaisesRegex(RuntimeError, 'network down'):
            client.get('/api/v5/public/instruments', {'instType': 'SPOT'})
        self.assertEqual(len(calls), 4)
        self.assertEqual(len(list((root / 'provenance').glob('*.json'))), 4)
        client = m.PublicClient(root / 'api', transport=lambda _: (200, b'{"code":"51001","data":[]}'), sleep=lambda _: None)
        with self.assertRaisesRegex(RuntimeError, '51001'):
            client.get('/api/v5/public/instruments', {'instType': 'SPOT'})
        self.assertEqual(list((root / 'api/checkpoints').glob('*.json')), [])

    def test_dataset_binds_code_hash_and_current_metadata(self):
        catalog = [{'instId': b + '-USDT', 'baseCcy': b, 'quoteCcy': 'USDT', 'instType': 'SPOT',
                    'lotSz': '0.0100', 'minSz': '0.100', 'tickSz': '0.0001', 'listTime': str(START + DAY)}
                   for b in m.BASES]
        def transport(url):
            return 200, json.dumps({'code': '0', 'data': catalog if '/public/instruments?' in url else [candle(0)]}).encode()
        root = Path(self.tmp.name)
        client = m.PublicClient(root, transport=transport, throttle=0, sleep=lambda _: None)
        manifest = m.run_dataset(root, START, START + DAY, client)
        import hashlib
        self.assertEqual(manifest.get('normalizerSha256'), hashlib.sha256(MODULE.read_bytes()).hexdigest())
        self.assertEqual(manifest['metadataTemporalBasis'], 'CURRENT_SNAPSHOT_NOT_HISTORICAL')
        self.assertEqual(manifest['instruments'][0]['coverage']['observationsBeforeCurrentListTime'], 1)
        self.assertEqual(manifest['instruments'][0]['coverage']['years']['2020'].get('duplicates'), 0)

    def test_redirect_is_not_followed(self):
        import urllib.error
        handler = m.NoRedirect()
        self.assertIsNone(handler.redirect_request(None, None, 302, 'found', {}, 'https://www.okx.com/'))


if __name__ == '__main__':
    unittest.main()
