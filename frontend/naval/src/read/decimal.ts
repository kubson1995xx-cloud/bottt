import type { Counter, Decimal } from './contracts.ts';
const maxCounter=9223372036854775807n, maxDecimal=79228162514264337593543950335n;
export function parseCounter(value: unknown): Counter {
 if(typeof value!=='string'||value.length>19||!/^(0|[1-9][0-9]*)$/.test(value)||BigInt(value)>maxCounter)throw new Error('COUNTER_INVALID');
 return value as Counter;
}
export function parseDecimal(value: unknown): Decimal {
 if(typeof value!=='string'||value.length>128||!/^[-]?(0|[1-9][0-9]*)(\.[0-9]+)?$/.test(value))throw new Error('DECIMAL_INVALID');
 const unsigned=value.replace(/^-/,''), parts=unsigned.split('.'); let scale=parts[1]?.length??0; let coefficient=BigInt(parts.join(''));
 while(scale>0&&(scale>28||coefficient>maxDecimal)&&coefficient%10n===0n){coefficient/=10n;scale--;}
 if(scale>28||coefficient>maxDecimal)throw new Error('DECIMAL_UNREPRESENTABLE');
 return value as Decimal;
}
export function compareCounter(a: Counter,b: Counter): -1|0|1 { return BigInt(a)<BigInt(b)?-1:BigInt(a)>BigInt(b)?1:0; }
export function isZero(value: Decimal): boolean { return /^-?0(\.0+)?$/.test(value); }
export function formatDecimal(value: Decimal): string { return value; }
