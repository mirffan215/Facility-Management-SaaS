import { describe, expect, it } from 'vitest';
import i18n from '../i18n';
import type { NotificationItem } from '../api/types';
import { noticeText } from './Notifications';

const notice = (type: string): NotificationItem => ({ id: '1', type, data: { number: 'WO-000007', title: 'Leak' }, linkPath: '/work-orders/1', createdAt: '2026-11-01T08:00:00Z', isRead: false });

const TYPES = [
  'Created', 'Approved', 'Rejected', 'Assigned', 'Started', 'Held', 'Resumed', 'Completed', 'Closed', 'Reopened', 'Cancelled',
  'sla.ResponseBreached', 'sla.ResolutionAtRisk', 'sla.ResolutionBreached',
].map((t) => `workorder.${t}`);

describe('noticeText', () => {
  it('says something specific, in the reader’s language, for every kind of notice the server sends', async () => {
    for (const lng of ['en', 'ar']) {
      const t = i18n.getFixedT(lng);
      for (const type of TYPES) {
        const text = noticeText(t, notice(type));
        expect(text, `${lng} ${type}`).toContain('WO-000007');
        expect(text, `${lng} ${type}`).not.toBe(t('notify.generic', { number: 'WO-000007' }));
      }
    }
  });

  it('falls back to a generic line for a type this version does not know', () => {
    expect(noticeText(i18n.getFixedT('en'), notice('something.new'))).toBe('WO-000007 was updated');
  });
});
