// Review (docs/06 M5): one Review, laid out by the screen. A phone-width screen (an iPhone, a narrow browser) walks the rows that
// need you one at a time (#833); anything wider keeps the list. This is a layout rule, not a mode: nobody picks it.
import { useState } from 'react';
import { useWindowDimensions } from 'react-native';
import { ReviewList, type ReviewProps } from './ReviewList';
import { ReviewWalk } from './ReviewWalk';
import { useReview } from './useReview';

/**
 * Below this width Review is the walk. Every iPhone is under 450 pt. It sits below WIDE_BREAKPOINT (768) so a tablet-width window,
 * and Jest's default 750-pt window that the locked CAM-3b Review tests render in, keep the list.
 */
export const WALK_MAX_WIDTH = 600;

export function ReviewScreen(props: ReviewProps) {
  const { width } = useWindowDimensions();
  const review = useReview({ libraryId: props.libraryId, sessionId: props.sessionId, undoMs: props.undoMs });
  // "See all" from the walk opens the list over the same Review, so nothing chosen is lost on the way.
  const [seeAll, setSeeAll] = useState(false);
  // A viewer cannot answer a card, so there is nothing to walk: they look at the list.
  const viewer = !!review.role && !review.canAdd;
  if (width >= WALK_MAX_WIDTH || viewer) return <ReviewList {...props} review={review} />;
  if (seeAll) return <ReviewList {...props} review={review} onBackToWalk={() => setSeeAll(false)} />;
  return <ReviewWalk {...props} review={review} onSeeAll={() => setSeeAll(true)} />;
}
