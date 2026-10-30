import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseCounter, parseDecimal, compareCounter, isZero } from '../src/read/decimal.ts';
import { decodeSnapshot, decodeEvents, decodeCursor, ContractError } from '../src/read/decode.ts';
const capture = () => JSON.parse(readFileSync(new URL('./fixtures/README.md',import.meta.url),'utf8').split('```json\n')[1].split('```')[0]);
test('decodePreservesInt64AndDecimalStrings',()=>{
 assert.equal(parseCounter('9007199254740993'),'9007199254740993');
 assert.equal(compareCounter(parseCounter('9007199254740993'),parseCounter('9007199254740992')),1);
 assert.equal(parseDecimal('10039.160000000000000000000000'),'10039.160000000000000000000000');
 assert.ok(isZero(parseDecimal('-0.000')));
 const s=decodeSnapshot(capture().snapshot); assert.equal(s.checkpoint?.accounts[0].cash.amount,'10039.16'); assert.ok(Object.isFrozen(s.checkpoint?.accounts));
});
test('counterAndDecimalRejectLossyOrNonCanonicalValues',()=>{
 for(const v of [1,'01','-1','9223372036854775808','1e3',null])assert.throws(()=>parseCounter(v));
 for(const v of [1,'1e3','1,1','NaN','0.00000000000000000000000000001','79228162514264337593543950336','1.00000000000000000000000000001'])assert.throws(()=>parseDecimal(v));
});
test('decodeRejectsUnknownKindOrNumericMoney',()=>{
 const d=capture(); d.snapshot.checkpoint.accounts[0].cash.amount=10000; assert.throws(()=>decodeSnapshot(d.snapshot),ContractError);
 const e=capture().events;e.batches[0].facts[0].kind='BrokerFilled';assert.throws(()=>decodeEvents(e),ContractError);
});
test('decodeChecksPageMirrorsCursorAndPayload',()=>{
 const {snapshot,events}=capture();const s=decodeSnapshot(snapshot),e=decodeEvents(events);
 assert.equal(decodeCursor(s.cursor!).sequence,s.asOfSequence);assert.equal(e.batches.length,18);
 const bad=capture().events;bad.page.hasMore='yes';assert.throws(()=>decodeEvents(bad));
 const mirror=capture().events;mirror.batches=[];assert.throws(()=>decodeEvents(mirror));
 assert.throws(()=>decodeCursor(s.cursor!+'='));
});
test('decoderRejectsMissingFieldsMembershipAndUnsupportedEconomics',()=>{
 for(const edit of [(s:any)=>delete s.broker.coverage,(s:any)=>s.checkpoint.accounts[0].account.environment='Live',(s:any)=>s.checkpoint.fleets[0].reservations[0].reservation.account.accountId='other',(s:any)=>s.broker.realizedPnl='123',(s:any)=>s.asOfSequence='999']){const s=capture().snapshot;edit(s);assert.throws(()=>decodeSnapshot(s));}
});
