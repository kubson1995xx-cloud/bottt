export type Counter = string & { readonly __counter: unique symbol };
export type Decimal = string & { readonly __decimal: unique symbol };
export type Utc = string & { readonly __utc: unique symbol };
export type Fleet = 'Alpha' | 'Bravo';
export type Mode = 'STOP' | 'RUNNING';
export type AccountId = Readonly<{
  venue: string; environment: 'Offline' | 'OkxDemo' | 'Live';
  accountId: string; quoteCurrency: string;
}>;
export type PortfolioId = Readonly<{ portfolioId: string; reportingCurrency: string }>;
export type Money = Readonly<{ amount: Decimal; currency: string }>;
export type Intent = Readonly<{
  commandId: string; signalId: string; instrument: string; quantity: Decimal;
  entryPriceCeiling: Decimal; stopPrice: Decimal; takeProfitPrice: Decimal;
}>;
export type Reservation = Readonly<{
  id: string; account: AccountId; fleet: Fleet; intent: Intent;
  quantity: Decimal; pending: Decimal; filled: Decimal; inventory: Decimal;
  allocatedCost: Decimal; entryHold: Decimal; closeFeeHold: Decimal; riskHold: Decimal;
  terminalCoverage: boolean; reductionUnsettled: boolean;
  stage: 'Reserved' | 'Confirmed' | 'Uncertain' | 'Rejected' | 'Terminal';
}>;
export type ReadReservation = Readonly<{
  reservation: Reservation; baseCurrency: string; quoteCurrency: string;
}>;
export type Fill = Readonly<{
  account: AccountId; instrument: string; tradeId: string; reservationId: string;
  reduction: boolean; quantity: Decimal; price: Decimal; fee: Decimal;
  feeCurrency: string; tradeUtc: Utc;
}>;
export type ReadFill = Readonly<{
  key: string; fill: Fill; baseCurrency: string; quoteCurrency: string;
}>;
export type ReadAccount = Readonly<{ account: AccountId; seed: Money; cash: Money }>;
export type ReadFleet = Readonly<{
  fleet: Fleet; reservations: readonly ReadReservation[]; fills: readonly ReadFill[];
}>;
export type Checkpoint = Readonly<{
  financialRevision: Counter; portfolioId: PortfolioId; configDigest: string;
  sourceGeneration: Counter; sourceSequence: Counter; reconciledUtc: Utc | null;
  durableMode: Mode; reasonCode: string;
  dailyLatch: boolean; drawdownLatch: boolean; emergencyLatch: boolean;
  historicalLiquidationEquity: Money | null;
  currentQuality: 'UNKNOWN'; runtimeReadiness: 'UNKNOWN';
  brokerState: 'NOT_CONFIGURED'; coverage: 'UNKNOWN'; brokerAccountId: null;
  brokerEquity: null; realizedPnl: null; unrealizedPnl: null;
  accounts: readonly ReadAccount[]; fleets: readonly ReadFleet[];
}>;
export type Operation = Readonly<{
  resultCode: string; durableMode: Mode;
  dailyLatch: boolean; drawdownLatch: boolean; emergencyLatch: boolean;
}>;
export type Coverage = Readonly<{
  complete: boolean; final: boolean; terminalCoverage: boolean;
  reductionUnsettled: boolean; offlineLifecycleFinal: boolean;
}>;
export type FactKeys = Readonly<{
  factId: string; account: AccountId | null; fleet: Fleet | null;
  reservationId: string | null; fillKey: string | null;
}>;
export type Fact = FactKeys & (
  | Readonly<{ kind: 'OfflineReservationAccepted'; reservation: ReadReservation;
      fill: null; coverage: null; operation: Operation; intent: Intent }>
  | Readonly<{ kind: 'OfflineReservationRefused'; reservation: null;
      fill: null; coverage: null; operation: Operation; intent: Intent }>
  | Readonly<{ kind: 'OfflineFillAccepted'; reservation: null;
      fill: ReadFill; coverage: null; operation: null; intent: null }>
  | Readonly<{ kind: 'OfflineEntryCoverageAccepted' | 'OfflineReductionCoverageAccepted';
      reservation: ReadReservation; fill: null; coverage: Coverage;
      operation: null; intent: null }>
  | Readonly<{ kind: 'OfflineReconciliationAccepted' | 'OfflineSafetyChanged' | 'OfflineModeChanged';
      reservation: null; fill: null; coverage: null; operation: Operation; intent: null }>
);
export type Batch = Readonly<{
  sequence: Counter; eventId: string; financialRevision: Counter;
  captureUtc: Utc | null; receiveUtc: Utc | null; committedUtc: Utc;
  sourceGeneration: Counter; sourceSequence: Counter; facts: readonly Fact[];
}>;
export type Cursor = Readonly<{ epoch: string; scopeId: string; sequence: Counter }>;
export type Page = Readonly<{
  epoch: string; scopeId: string; oldestAvailableSequence: Counter;
  asOfSequence: Counter; nextCursor: Cursor; hasMore: boolean;
  gap: boolean; resetRequired: boolean; batches: readonly Batch[];
}>;
export type Source = Readonly<{
  kind: 'OFFLINE_PUBLICATION'; sourceClockUtc: Utc | null;
  captureUtc: Utc | null; receiveUtc: Utc | null; reconciledUtc: Utc | null;
  committedUtc: Utc | null; ageSeconds: Decimal | null;
  quality: 'UNKNOWN'; coverage: 'UNKNOWN'; freshness: 'HISTORICAL' | 'UNKNOWN';
  stale: true; reasonCode: string;
}>;
export type Broker = Readonly<{
  state: 'NOT_CONFIGURED'; accountId: null; balance: null; equity: null;
  realizedPnl: null; unrealizedPnl: null; statistics: null;
  quality: 'UNKNOWN'; coverage: 'UNKNOWN'; runtimeReadiness: 'UNKNOWN';
}>;
export type HostEnvelope = Readonly<{
  schemaVersion: 'naval-host-read-v1'; hostEpoch: string; scope: string;
  requestGeneration: string | null; servedUtc: Utc; observedUtc: Utc | null;
  availability: 'AVAILABLE' | 'NOT_CONFIGURED' | 'UNKNOWN'; reasonCode: string;
  temporalClassification: 'HISTORICAL_OFFLINE_CHECKPOINT';
  classification: 'OFFLINE_SHARED_CAPITAL / SYNTHETIC_NOT_RESEARCH / NO_BROKER_AUTHORITY';
  epoch: string | null; scopeId: string | null;
  sourceGeneration: Counter | null; sourceSequence: Counter | null;
  snapshotVersion: Counter | null; asOfSequence: Counter | null;
  financialRevision: Counter | null; sources: readonly Source[]; broker: Broker;
}>;
export type Snapshot = HostEnvelope & Readonly<{
  checkpoint: Checkpoint | null; oldestAvailableSequence: Counter | null; cursor: string | null;
}>;
export type Events = HostEnvelope & Readonly<{
  page: Page | null; nextCursorEncoded: string | null;
  gap: boolean; resetRequired: boolean; batches: readonly Batch[];
}>;
