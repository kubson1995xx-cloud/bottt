import type { NavalState } from './store.ts';
export const plannedRoster=['BTC','ETH','SOL','XRP','DOGE','ADA','LTC','LINK','BCH','AVAX'] as const;
function common(s:NavalState){return {revision:s.snapshot?.financialRevision??null,classification:s.snapshot?.classification??'OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY',checkpoint:s.snapshot?.checkpoint??null,audit:s.audit,accounts:s.snapshot?.checkpoint?.accounts??[],reservations:s.snapshot?.checkpoint?.fleets.flatMap(f=>f.reservations)??[],fills:s.snapshot?.checkpoint?.fleets.flatMap(f=>f.fills)??[]};}
export function selectCommand(s:NavalState){return {...common(s),result:'UNKNOWN' as const,brokerCapital:'UNKNOWN' as const,engineReadiness:'UNKNOWN' as const,effectsKind:'historical-publication-only' as const};}
export function selectTargets(s:NavalState){return {...common(s),roster:plannedRoster,quotes:'UNKNOWN' as const,pairs:'UNKNOWN' as const};}
export function selectFleets(s:NavalState){return {...common(s),fleets:s.snapshot?.checkpoint?.fleets??[],budget:'UNKNOWN' as const,brokerCoverage:'UNKNOWN' as const};}
export function selectLifecycle(s:NavalState){return {...common(s),brokerSubmitted:'UNKNOWN' as const,executionAuthority:false as const,settlement:'UNKNOWN' as const,protection:'UNKNOWN' as const};}
export function selectAnalytics(s:NavalState){return {...common(s),pnl:'UNKNOWN' as const,winRate:'UNKNOWN' as const,equitySeries:null};}
