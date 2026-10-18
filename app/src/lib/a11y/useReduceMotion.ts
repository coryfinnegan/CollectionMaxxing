import { useEffect, useState } from 'react';
import { AccessibilityInfo } from 'react-native';

/** True when the person turned Reduce Motion on (iOS) or "Remove animations" (Android); follows changes while the app is open. */
export function useReduceMotion(): boolean {
  const [reduce, setReduce] = useState(false);
  useEffect(() => {
    let alive = true;
    void AccessibilityInfo.isReduceMotionEnabled().then((on) => alive && setReduce(on));
    const sub = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduce);
    return () => {
      alive = false;
      sub.remove();
    };
  }, []);
  return reduce;
}
