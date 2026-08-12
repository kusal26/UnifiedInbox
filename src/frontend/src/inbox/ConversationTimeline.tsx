import type { ActivityItem } from '../api/inbox';

export type TimelineState = 'loading' | 'error' | 'empty' | 'ready';

interface ConversationTimelineProps {
  state: TimelineState;
  items: ActivityItem[];
  onRetry?: () => void;
}

export function ConversationTimeline({ state, items, onRetry }: ConversationTimelineProps) {
  if (state === 'loading') return <p className="timeline-state" role="status">Loading conversation…</p>;
  if (state === 'error') return <section className="timeline-state" role="alert"><p>Could not load this conversation.</p><button onClick={onRetry}>Try again</button></section>;
  if (state === 'empty') return <p className="timeline-state">No activity yet</p>;

  return <ol className="conversation-timeline" aria-label="Conversation activity">
    {items.map((item) => <li className={`timeline-item ${item.kind === 'InternalNote' ? 'is-note' : ''}`} key={item.id}>
      <div className="timeline-meta">
        <strong>{item.kind === 'InternalNote' ? 'Internal note' : 'Message'}</strong>
        {item.kind === 'InternalNote' && <span>Private to staff</span>}
      </div>
      <p>{item.body}</p>
    </li>)}
  </ol>;
}
