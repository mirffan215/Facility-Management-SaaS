const KEYS: Record<string, string> = { 'energy.alarm': 'jobs.source.energyAlarm', 'compliance.failed': 'jobs.source.failedInspection' };

/** The translation key naming what raised a work order automatically; anything unknown reads as "an automatic alert". */
export const workSourceKey = (kind: string): string => KEYS[kind] ?? 'jobs.source.other';
